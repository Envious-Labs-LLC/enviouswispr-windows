using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
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

    [Fact]
    public async Task ShutdownWhileTheMicrophoneIsStillOpeningRefusesTheQueuedReleaseAndTearsDownAfterThePress()
    {
        // STEP 11'S PROOF, FOUR: quit while a press is still opening the microphone with a release
        // already queued behind it. The release is refused when its turn comes; the press finishes on
        // its own terms; the teardown runs after it, once, and nothing is transcribed.
        var world = World.BuildWithMicrophoneOpeningHeld();
        var press = world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Capture.StartEntered.Task.WaitAsync(Patience);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, world.Effects.TearDowns);

        world.Capture.AllowStartExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await release.WaitAsync(Patience)).Disposition);
        Assert.True((await shutdown.WaitAsync(Patience)).Clean);

        Assert.Equal(1, world.Effects.TearDowns);
        Assert.DoesNotContain(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));
        Assert.Equal("TearDownSession", world.Effects.Trace.Last());
        await world.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownDuringFinalisationWaitsForItAndTranscribesExactlyOnce()
    {
        // STEP 11'S PROOF, FIVE: quit while a release is transcribing. The teardown waits for the
        // transcription, which is delivered once; nothing is torn down under it and nothing runs after.
        await using var world = await World.StartRecordingWithPreviewStartupHeldAsync();
        world.Engine.AllowStartExit.SetResult();
        world.Effects.HoldTranscription = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Effects.TranscriptionEntered.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, world.Effects.TearDowns);
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked)).Disposition);

        world.Effects.AllowTranscriptionExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        Assert.True((await shutdown.WaitAsync(Patience)).Clean);

        Assert.Single(world.Effects.Trace, effect => effect.StartsWith("Transcribe", StringComparison.Ordinal));
        Assert.Equal(1, world.Effects.TearDowns);
        Assert.True(world.Effects.Trace.ToList().IndexOf("TearDownSession") > world.Effects.Trace.ToList().FindIndex(effect => effect.StartsWith("Transcribe", StringComparison.Ordinal)));
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

        /// <summary>A world whose microphone will not finish opening until told; nothing has been pressed yet.</summary>
        public static World BuildWithMicrophoneOpeningHeld()
        {
            var world = Build(escapeRecovery: false, holdMicrophoneOpening: true);
            world.Engine.AllowStartExit.TrySetResult();
            return world;
        }

        private static World Build(bool escapeRecovery, bool holdMicrophoneOpening)
        {
            var capture = new FakeAudioCapture { HoldStart = holdMicrophoneOpening };
            var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101), minimumHoldDuration: TimeSpan.Zero);
            var engine = new FakeEngine { HoldStarts = true };
            var log = new FakeLogger();
            var previewEffects = new PreviewEffects(engine, capture, controller);
            var preview = new LivePreviewController(previewEffects, log, TimeProvider.System);
            var effects = new ShellAdapter(preview, capture) { EscapeRecoveryEnabled = escapeRecovery };
            // THE REAL ORDERING OWNER, with the timers and the streaming loop idle (no audio to
            // watch, preview on), so what is proved is the executor's order around the preview.
            var timers = new IdleTimerEffects();
            var background = new SessionBackgroundWork(
                new RecordingWatchdog(timers, TimeProvider.System),
                preview,
                new AutoStopMonitor(timers, log, TimeProvider.System),
                new StreamingTranscriptionController(new NoStreaming(), log, TimeProvider.System));
            var executor = new DictationSessionExecutor(
                controller,
                new TracedBackgroundWork(background, capture, preview, effects),
                new HeldFinalization(effects),
                new NoRecoveryState(),
                new HealthyMachine(),
                effects);
            var coordinator = new DictationSessionCoordinator(
                executor,
                () => new RecordingStartContext(new TargetWindowId(101), TextDeliveryOptions.Default));
            return new World
            {
                Coordinator = coordinator,
                Controller = controller,
                Preview = preview,
                Capture = capture,
                Engine = engine,
                Effects = effects,
                Log = log,
            };
        }

        /// <summary>A press has been admitted and run to completion while the preview engine's start is still held.</summary>
        public static async Task<World> StartRecordingWithPreviewStartupHeldAsync(bool escapeRecovery = false)
        {
            var world = Build(escapeRecovery, holdMicrophoneOpening: false);
            var capture = world.Capture;
            var controller = world.Controller;
            var engine = world.Engine;
            var preview = world.Preview;
            var coordinator = world.Coordinator;

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

        public bool EscapeRecoveryEnabled { get; init; }

        public void RecordResourcePressure(AppError? failure) => Add("RecordResourcePressure");

        public void ShowRecoveredTextWaiting() => Add("ShowRecoveredTextWaiting");

        public void ShowMemoryCritical() => Add("ShowMemoryCritical");

        public void ShowDiskLow() => Add("ShowDiskLow");

        public void RecordTransition(SessionTransitionResult result) => Add($"RecordTransition:{result.Kind}");

        public RecordingBackgroundSettings RecordingSettings() =>
            new(TimeSpan.FromMinutes(5), () => DictationPreferences.Default);

        public void ShowInterruptionPreserving(SystemLifecycleTransition transition) => Add($"ShowInterruptionPreserving:{transition}");

        public void Add(string effect)
        {
            lock (_lock)
            {
                _trace.Add(effect);
            }
        }

        public bool HoldTranscription { get; set; }

        public TaskCompletionSource TranscriptionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowTranscriptionExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeAudioCapture Capture => capture;

        public LivePreviewController Preview => preview;

        public int TearDowns { get; private set; }

        public Task TearDownSessionAsync()
        {
            TearDowns++;
            Add("TearDownSession");
            return Task.CompletedTask;
        }

        public void ShowTransitionStatus(SessionTransitionResult result) => Add($"ShowTransitionStatus:{result.Kind}");

        public void RecordSessionFailure() => Add("RecordSessionFailure");

        public void RecordSessionRecovered(AppError failure) => Add($"RecordSessionRecovered:{failure.Code}");

        public void ShowSessionRecovered(SessionFailureKind kind) => Add($"ShowSessionRecovered:{kind}");

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

    /// <summary>The finalisation, reduced to the order it was asked in: after the capture and the preview have stopped, and held when a test says so.</summary>
    private sealed class HeldFinalization(ShellAdapter adapter) : ISessionFinalization
    {
        public async Task<FinalizationReport> RunAsync(DictationSessionId sessionId, CapturedAudio audio, bool recoveryOnly, CancellationToken cancellationToken)
        {
            Assert.False(adapter.Preview.IsRunning, "the finalisation is asked for only after the preview has stopped");
            Assert.False(adapter.Capture.IsCapturing, "the finalisation is asked for only after the capture has stopped");
            adapter.Add($"Transcribe:recoveryOnly={recoveryOnly}");
            if (adapter.HoldTranscription)
            {
                adapter.TranscriptionEntered.TrySetResult();
                await adapter.AllowTranscriptionExit.Task.ConfigureAwait(false);
            }

            return new FinalizationReport(FinalizationOutcome.Held);
        }
    }

    /// <summary>Forwards to the real ordering owner and writes down what the capture and the preview were doing when it was asked.</summary>
    private sealed class TracedBackgroundWork(SessionBackgroundWork inner, FakeAudioCapture capture, LivePreviewController preview, ShellAdapter trace) : ISessionBackgroundWork
    {
        public Task StartAsync(DictationSessionId sessionId, RecordingBackgroundSettings settings) => inner.StartAsync(sessionId, settings);

        public async Task StopAsync()
        {
            trace.Add(capture.IsCapturing ? "Capture still open" : capture.Cancelled.Task.IsCompleted ? "Capture cancelled" : "Capture stopped");
            await inner.StopAsync();
            trace.Add(preview.IsRunning ? "Preview still running" : "Preview stopped");
        }

        public async Task<BackgroundStopReport> StopAsync(TimeSpan deadline)
        {
            await StopAsync();
            return BackgroundStopReport.AllCompleted;
        }

        public Task StopWatchdogAsync() => inner.StopWatchdogAsync();


        public async Task<StopOutcome> StopWatchdogAsync(TimeSpan deadline)
        {
            await StopWatchdogAsync();
            return StopOutcome.Completed;
        }    }

    private sealed class NoRecoveryState : ISessionRecoveryState
    {
        public bool HasPendingRecovery => false;

        public bool CanPersistRecovery { get; set; } = true;

        public void ShowPendingRecovery()
        {
        }
    }

    private sealed class HealthyMachine : ISystemResourceProbe
    {
        public SystemResourceSnapshot Probe() => new(
            AvailableDiskBytes: 10L * 1024 * 1024 * 1024,
            AvailablePhysicalMemoryBytes: 8UL * 1024 * 1024 * 1024,
            MemoryLoadPercent: 40);
    }

    private sealed class IdleTimerEffects : IRecordingTimerEffects
    {
        public IAudioSnapshotSource? Audio => null;

        public void Post(PushToTalkSignal signal, DictationSessionId forSession)
        {
        }

        public void RecordingTimedOut(DictationSessionId sessionId)
        {
        }
    }

    private sealed class NoStreaming : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => true;

        public ITranscriptionEngine? Engine => null;

        public IAudioSnapshotSource? Audio => null;
    }

    private sealed class PreviewEffects(FakeEngine engine, FakeAudioCapture capture, PushToTalkSessionController controller) : ILivePreviewEffects
    {
        public bool Enabled => true;

        public ILivePreviewEngine? Engine => engine;

        public AppErrorCode? EngineUnavailableReason => null;

        public IAudioSnapshotSource? Audio => capture;

        public DictationSessionId? RecordingSessionId => controller.CurrentSession?.Id;

        public void ShowPreview(LivePreviewFrame frame)
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

        public bool HoldStart { get; set; }

        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStartExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            StartEntered.TrySetResult();
            if (HoldStart)
            {
                await AllowStartExit.Task.ConfigureAwait(false);
            }

            IsCapturing = true;
            return new AudioOperationResult(Succeeded: true);
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
