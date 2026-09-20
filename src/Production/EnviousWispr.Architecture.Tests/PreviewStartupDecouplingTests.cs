using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The step 8 proof, composed: the real coordinator, executor, session controller and preview
/// controller, a capture that says when it stopped, a preview engine whose start and stop are held
/// on barriers, and a shell adapter shaped like the app's. A key released, cancelled, or cancelled
/// into Escape Recovery while the preview worker is still starting must reach the capture before
/// the worker answers, and the final transcription must not begin until the preview has been torn
/// down - including while its engine is still stopping.
/// </summary>
public sealed class PreviewStartupDecouplingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AReleaseDuringPreviewStartupStopsTheCaptureBeforeTheWorkerAnswers()
    {
        await using var world = await World.StartRecordingWithPreviewStartupHeldAsync();

        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Capture.Stopped.Task.WaitAsync(Patience);

        // The capture has stopped while the engine's start is still held: the release did not wait.
        Assert.False(world.Capture.IsCapturing);
        Assert.False(release.IsCompleted);
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));
        Assert.Equal(0, world.Engine.Stops);

        // The engine has seen its start cancelled; it is let out of that start but held in its stop,
        // and transcription still waits.
        await world.Engine.StartCancellationObserved.Task.WaitAsync(Patience);
        world.Engine.HoldStops = true;
        world.Engine.AllowStartExit.SetResult();
        await world.Engine.StopStarted.Task.WaitAsync(Patience);
        Assert.False(release.IsCompleted);
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));

        world.Engine.AllowStopExit.SetResult();
        var result = await release.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(["Capture stopped", "Preview stopped", "Transcribe:recoveryOnly=False"], world.Effects.Trace.Where(IsOrderedEffect));
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(0, world.Engine.Passes);
        Assert.Equal([AppEventCode.LivePreviewStartupCancelled], world.Log.Codes);
        // The shell's finalisation runner would complete and reset the session from here; this
        // adapter stops at the transcription it was proving the order of.
        Assert.Equal(DictationSessionState.Finalizing, world.Controller.CurrentSession?.State);
    }

    [Fact]
    public async Task ACancelDuringPreviewStartupDropsTheCaptureBeforeTheWorkerAnswersAndNeverTranscribes()
    {
        await using var world = await World.StartRecordingWithPreviewStartupHeldAsync();

        var cancel = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        await world.Capture.Cancelled.Task.WaitAsync(Patience);

        Assert.False(world.Capture.IsCapturing);
        await world.Engine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(cancel.IsCompleted);
        Assert.Equal(0, world.Engine.Stops);

        world.Engine.AllowStartExit.SetResult();
        var result = await cancel.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(["Capture cancelled", "Preview stopped"], world.Effects.Trace.Where(IsOrderedEffect));
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(0, world.Engine.Passes);
        Assert.Equal([AppEventCode.LivePreviewStartupCancelled], world.Log.Codes);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task AnEscapeRecoveryCancelDuringPreviewStartupStopsTheCaptureAndTranscribesForRecoveryOnly()
    {
        await using var world = await World.StartRecordingWithPreviewStartupHeldAsync(escapeRecovery: true);

        var cancel = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        await world.Capture.Stopped.Task.WaitAsync(Patience);

        Assert.False(world.Capture.IsCapturing);
        await world.Engine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(cancel.IsCompleted);
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));

        world.Engine.AllowStartExit.SetResult();
        var result = await cancel.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(["Capture stopped", "Preview stopped", "Transcribe:recoveryOnly=True"], world.Effects.Trace.Where(IsOrderedEffect));
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal([AppEventCode.LivePreviewStartupCancelled], world.Log.Codes);
    }

    [Fact]
    public async Task WindowsLockingDuringPreviewStartupStopsTheCaptureKeepsTheAudioAndFinalisesOnce()
    {
        // STEP 11'S PROOF, ONE: the lock is a command on the same queue as a key. It reaches the capture
        // while the preview worker is still starting, the audio is kept and finalised exactly as a
        // release would finalise it, and a release queued behind the lock finds nothing to end.
        await using var world = await World.StartRecordingWithPreviewStartupHeldAsync();

        var lockCommand = world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked);
        var queuedRelease = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Capture.Stopped.Task.WaitAsync(Patience);

        Assert.False(world.Capture.IsCapturing);
        await world.Engine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(lockCommand.IsCompleted);
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));

        world.Engine.AllowStartExit.SetResult();
        var locked = await lockCommand.WaitAsync(Patience);
        var released = await queuedRelease.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, locked.Disposition);
        Assert.Equal(["Capture stopped", "Preview stopped", "Transcribe:recoveryOnly=False"], world.Effects.Trace.Where(IsOrderedEffect));
        Assert.Contains("ShowInterruptionPreserving:SessionLocked", world.Effects.Trace);
        Assert.Single(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));
        // The release ran after the lock and found the session finalising: the controller answers
        // Ignored and nothing is finalised twice.
        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        Assert.True(released.WasQueued);
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal([AppEventCode.LivePreviewStartupCancelled], world.Log.Codes);
    }

    [Fact]
    public async Task ShutdownDuringPreviewStartupRefusesNewCommandsAndLetsTheRecordingFinishItsOwnStop()
    {
        // STEP 11'S PROOF, TWO: the coordinator's stop is the shell's gate now. A release that arrives
        // after the stop is refused; the release that was queued before it is refused too - the
        // shell's disposal then tears the session down itself. Nothing runs after the stop reports.
        await using var world = await World.StartRecordingWithPreviewStartupHeldAsync();

        var stop = world.Coordinator.StopAsync(Patience);
        var afterStop = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal(SessionCommandDisposition.Stopping, afterStop.Disposition);
        Assert.True(await stop.WaitAsync(Patience), "the press had finished; the consumer was idle and stops at once");
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.InterruptAsync(SystemLifecycleTransition.Suspending)).Disposition);
        Assert.True(world.Capture.IsCapturing, "the stop does not itself end the recording; the shell's disposal does");
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));

        // The shell's disposal after the stop: preview, then the session, in the order it always had.
        var previewStop = world.Preview.StopAsync();
        await world.Engine.StartCancellationObserved.Task.WaitAsync(Patience);
        world.Engine.AllowStartExit.SetResult();
        await previewStop.WaitAsync(Patience);
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal([AppEventCode.LivePreviewStartupCancelled], world.Log.Codes);
    }

    [Fact]
    public async Task ATimeoutDuringPreviewStartupCancelsTheCaptureAndTheStartup()
    {
        // STEP 11'S PROOF, THREE: the watchdog's timeout is a command. If the recording is still the
        // one that was armed, the loops are stopped first - the preview's startup is cancelled - and
        // then it is aborted: capture cancelled, not stopped. The order the watchdog always had.
        await using var world = await World.StartRecordingWithPreviewStartupHeldAsync();
        var session = world.Controller.CurrentSession!.Id;

        var timeout = world.Coordinator.TimeOutAsync(session);
        await world.Engine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(timeout.IsCompleted);
        Assert.True(world.Capture.IsCapturing, "the abort follows the stops, as it always did");

        world.Engine.AllowStartExit.SetResult();
        await world.Capture.Cancelled.Task.WaitAsync(Patience);
        var result = await timeout.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(world.Controller.CurrentSession);
        // The adapter records the capture's state at the stop-background call: still open, because
        // the timeout stops the loops before it aborts the capture. The cancel follows.
        Assert.Equal(["Capture still open", "Preview stopped"], world.Effects.Trace.Where(IsOrderedEffect));
        Assert.True(world.Capture.Cancelled.Task.IsCompleted);
        Assert.Contains("ShowRecordingTimedOut", world.Effects.Trace);
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));
        Assert.Equal(1, world.Engine.Stops);
    }

    private static bool IsOrderedEffect(string effect) =>
        effect.StartsWith("Capture ", StringComparison.Ordinal) ||
        effect.StartsWith("Preview ", StringComparison.Ordinal) ||
        effect.StartsWith("Transcribe", StringComparison.Ordinal);

    private sealed class World : IAsyncDisposable
    {
        public required DictationSessionCoordinator Coordinator { get; init; }
        public required PushToTalkSessionController Controller { get; init; }
        public required LivePreviewController Preview { get; init; }
        public required FakeAudioCapture Capture { get; init; }
        public required FakeEngine Engine { get; init; }
        public required ShellAdapter Effects { get; init; }
        public required FakeLogger Log { get; init; }

        /// <summary>A press has been admitted and run to completion while the preview engine's start is still held.</summary>
        public static async Task<World> StartRecordingWithPreviewStartupHeldAsync(bool escapeRecovery = false)
        {
            var capture = new FakeAudioCapture();
            var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101), minimumHoldDuration: TimeSpan.Zero);
            var engine = new FakeEngine { HoldStarts = true };
            var log = new FakeLogger();
            var previewEffects = new PreviewEffects(engine, capture, controller);
            var preview = new LivePreviewController(previewEffects, log, TimeProvider.System);
            var effects = new ShellAdapter(preview, capture) { EscapeRecoveryEnabled = escapeRecovery };
            var executor = new DictationSessionExecutor(controller, effects);
            var coordinator = new DictationSessionCoordinator(
                executor,
                () => new RecordingStartContext(new TargetWindowId(101), TextDeliveryOptions.Default));
            var world = new World
            {
                Coordinator = coordinator,
                Controller = controller,
                Preview = preview,
                Capture = capture,
                Engine = engine,
                Effects = effects,
                Log = log,
            };

            var press = await coordinator.SubmitAsync(PushToTalkSignal.Pressed).WaitAsync(Patience);
            await engine.StartStarted.Task.WaitAsync(Patience);

            // THE PRESS HAS FINISHED WHILE THE ENGINE HAS NOT: that is the whole of step 8. Before it,
            // the press command held the session gate until the worker answered, and a release was
            // queued behind it.
            Assert.Equal(SessionCommandDisposition.Applied, press.Disposition);
            Assert.True(capture.IsCapturing);
            Assert.Equal(DictationSessionState.Recording, controller.CurrentSession?.State);
            Assert.True(preview.IsRunning);
            Assert.Equal(0, engine.Starts);
            return world;
        }

        public async ValueTask DisposeAsync()
        {
            Engine.AllowStartExit.TrySetResult();
            Engine.AllowStopExit.TrySetResult();
            await Coordinator.DisposeAsync();
            await Preview.DisposeAsync();
            await Controller.DisposeAsync();
        }
    }

    /// <summary>The app's adapter, reduced to what the executor calls and the order it calls it in.</summary>
    private sealed class ShellAdapter(LivePreviewController preview, FakeAudioCapture capture) : IDictationSessionEffects
    {
        private readonly object _lock = new();
        private readonly List<string> _trace = [];

        /// <summary>A snapshot: the executor appends from its own thread while a test reads.</summary>
        public string[] Trace
        {
            get
            {
                lock (_lock)
                {
                    return _trace.ToArray();
                }
            }
        }

        private void Add(string effect)
        {
            lock (_lock)
            {
                _trace.Add(effect);
            }
        }

        public bool HasPendingRecovery => false;

        public bool EscapeRecoveryEnabled { get; init; }

        public bool EscapeRecoveryForSession { get; set; }

        public DictationAdmissionResult EvaluateAdmission() =>
            new(DictationAdmissionStatus.Ready, CanStart: true, CanPersistRecovery: true);

        public void ShowRecoveredTextWaiting() => Add("ShowRecoveredTextWaiting");

        public void ShowMemoryCritical() => Add("ShowMemoryCritical");

        public void ShowDiskLow() => Add("ShowDiskLow");

        public Task StopRecordingWatchdogAsync()
        {
            Add("StopRecordingWatchdog");
            return Task.CompletedTask;
        }

        public void RecordTransition(SessionTransitionResult result) => Add($"RecordTransition:{result.Kind}");

        public async Task OnRecordingStartedAsync(DictationSessionId sessionId)
        {
            Add("OnRecordingStarted");
            await preview.StartAsync(sessionId).ConfigureAwait(false);
            Add("OnRecordingStarted:returned");
        }

        public async Task FinalizeAsync(DictationSessionId sessionId, CapturedAudio audio, bool recoveryOnly, SystemLifecycleTransition? preserving = null)
        {
            Add(capture.IsCapturing ? "Capture still open" : "Capture stopped");
            await preview.StopAsync().ConfigureAwait(false);
            Add("Preview stopped");
            if (preserving is { } transition)
            {
                Add($"ShowInterruptionPreserving:{transition}");
            }

            Add($"Transcribe:recoveryOnly={recoveryOnly}");
        }

        public Task TearDownSessionAsync()
        {
            Add("TearDownSession");
            return Task.CompletedTask;
        }

        public async Task StopBackgroundWorkAsync()
        {
            Add(capture.IsCapturing ? "Capture still open" : "Capture cancelled");
            await preview.StopAsync().ConfigureAwait(false);
            Add("Preview stopped");
        }

        public void ShowTransitionStatus(SessionTransitionResult result) => Add($"ShowTransitionStatus:{result.Kind}");

        public void RecordSessionFailure() => Add("RecordSessionFailure");

        public void ReleaseProcessingDeadline() => Add("ReleaseProcessingDeadline");

        public Task RecoverFailedSessionAsync(AppError failure, SessionFailureKind kind)
        {
            Add($"RecoverFailedSession:{failure.Code}:{kind}");
            return Task.CompletedTask;
        }

        public Task RecordDictationEdgeAsync()
        {
            Add("RecordDictationEdge");
            return Task.CompletedTask;
        }

        public void RecordInterruptionFailure() => Add("RecordInterruptionFailure");

        public void ShowInterruptionPending() => Add("ShowInterruptionPending");

        public void RecordRecordingTimedOut(AppError failure) => Add($"RecordRecordingTimedOut:{failure.Code}");

        public void ShowRecordingTimedOut() => Add("ShowRecordingTimedOut");
    }

    private sealed class PreviewEffects(FakeEngine engine, FakeAudioCapture capture, PushToTalkSessionController controller) : ILivePreviewEffects
    {
        public bool Enabled => true;

        public ILivePreviewEngine? Engine => engine;

        public AppErrorCode? EngineUnavailableReason => null;

        public IAudioSnapshotSource? Audio => capture;

        public DictationSessionId? RecordingSessionId => controller.CurrentSession?.Id;

        public void ShowPreview(DictationSessionId sessionId, string text)
        {
        }

        public void ClearPreview()
        {
        }
    }

    private sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(window);
    }

    private sealed class FakeAudioCapture : IAudioCapture, IAudioSnapshotSource
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            Stopped.TrySetResult();
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            Cancelled.TrySetResult();
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) =>
            new(_sessionId, new float[16_000], 16_000, 1);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A preview engine whose start and stop are held on barriers; a held start honours its cancel.</summary>
    private sealed class FakeEngine : ILivePreviewEngine
    {
        public string EngineId => "fake";
        public bool HoldStarts { get; set; }
        public bool HoldStops { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Passes { get; private set; }
        public TaskCompletionSource StartStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStartExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default)
        {
            StartStarted.TrySetResult();
            if (HoldStarts)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                await Task.WhenAny(AllowStartExit.Task, cancelled.Task);
                if (cancellationToken.IsCancellationRequested)
                {
                    StartCancellationObserved.TrySetResult();
                    await AllowStartExit.Task;
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            Starts++;
            return new RuntimeWorkerResult(true, RuntimeWorkerState.Ready);
        }

        public Task<LivePreviewUpdate> PreviewAsync(AudioSnapshot snapshot, long sequence, CancellationToken cancellationToken = default)
        {
            Passes++;
            return Task.FromResult(new LivePreviewUpdate(snapshot.SessionId.Value, sequence, true, "words"));
        }

        public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
        {
            StopStarted.TrySetResult();
            if (HoldStops)
            {
                await AllowStopExit.Task;
            }

            Stops++;
            return new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLogger : IAppLogger
    {
        private readonly object _lock = new();
        private readonly List<AppLogEntry> _entries = [];

        public IEnumerable<AppEventCode> Codes
        {
            get
            {
                lock (_lock)
                {
                    return _entries.Select(entry => entry.Event).ToArray();
                }
            }
        }

        public void Write(AppLogEntry entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
            }
        }
    }
}
