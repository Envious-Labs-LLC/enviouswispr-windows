using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Shutdown against the real thing: the coordinator, the executor, the session controller, and the
/// real finalisation runner over the real finalizer and persistence, with a speech engine that can be
/// held mid-transcription, a delivery route that counts, and a capture that says when it was let go
/// of. What is asserted is what the plan asked for: the microphone is closed, text is delivered
/// exactly once or not at all, and nothing is called after the teardown.
/// </summary>
public sealed class SessionShutdownTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QuittingDuringATranscriptionWaitsForItDeliversOnceAndThenClosesTheMicrophone()
    {
        var world = World.Build();
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        Assert.False(world.Capture.Disposed);
        Assert.Equal(0, world.Delivery.Deliveries);

        world.Engine.AllowExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        Assert.True(await shutdown.WaitAsync(Patience));

        Assert.Equal(1, world.Delivery.Deliveries);
        Assert.Equal(1, world.Engine.Transcriptions);
        Assert.True(world.Capture.Disposed, "the teardown disposed the session controller, which disposed the capture");
        Assert.Equal(1, world.Effects.TearDowns);

        // Nothing after the teardown: a late timeout and a late lock are refused, and neither the
        // engine nor the delivery route is asked anything again.
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.TimeOutAsync(world.SessionId)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked)).Disposition);
        Assert.Equal(1, world.Delivery.Deliveries);
        Assert.Equal(1, world.Engine.Transcriptions);
    }

    [Fact]
    public async Task QuittingWhileTheMicrophoneIsOpeningRefusesTheQueuedReleaseAndClosesTheMicrophoneWithoutDelivering()
    {
        var world = World.Build(holdMicrophoneOpening: true);
        var press = world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Capture.StartEntered.Task.WaitAsync(Patience);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);

        world.Capture.AllowStartExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await release.WaitAsync(Patience)).Disposition);
        Assert.True(await shutdown.WaitAsync(Patience));

        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(0, world.Engine.Transcriptions);
        Assert.True(world.Capture.Cancelled, "the recording that was open when the teardown came was cancelled, not delivered");
        Assert.True(world.Capture.Disposed);
        Assert.Equal(1, world.Effects.TearDowns);
    }

    [Fact]
    public async Task ATranscriptionThatFinishesInsideTheSecondWaitIsStillDeliveredUnderTheSession()
    {
        // The two waits the shell had: one for the queue, one for the gate. A transcription that
        // outlives the first and finishes inside the second is delivered before the teardown, and the
        // shutdown reports a clean one. Crossed on the manual clock: the first wait's timer is
        // registered and advanced past; the second wait's registration is the milestone that says the
        // shutdown is inside it, and only then is the engine let go.
        var clock = new Deterministic.ManualClock();
        var world = World.Build(clock: clock);
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var drain = TimeSpan.FromSeconds(10);
        var shutdown = world.Coordinator.ShutdownAsync(drain);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        Assert.Equal(drain, clock.NextDue);
        clock.Advance(drain);
        await clock.WhenRegistered(2).WaitAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, world.Effects.TearDowns);
        world.Engine.AllowExit.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        Assert.True(await shutdown.WaitAsync(Patience), "the transcription finished inside the second wait");
        Assert.Equal(1, world.Delivery.Deliveries);
        Assert.True(world.Capture.Disposed);
        Assert.Equal(1, world.Effects.TearDowns);
    }

    [Fact]
    public async Task ATimeoutStillRunningWhenTheTeardownComesEndsAsFailedWithTheMicrophoneClosed()
    {
        // The watchdog's timeout was stopping the loops when the shutdown gave up waiting. The
        // teardown cancels the open recording and disposes the controller beside it; when the timeout
        // resumes it finds the session gone and ends as failed - answered, not a faulted task the
        // watchdog would have discarded.
        var world = World.Build();
        await world.PressAsync();
        world.Background.HoldStop = true;
        var timeout = world.Coordinator.TimeOutAsync(world.SessionId);
        await world.Background.StopEntered.Task.WaitAsync(Patience);

        var clean = await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience);

        Assert.False(clean);
        Assert.Equal(1, world.Effects.TearDowns);
        Assert.True(world.Capture.Cancelled);
        Assert.True(world.Capture.Disposed);

        world.Background.AllowStopExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Failed, (await timeout.WaitAsync(Patience)).Disposition);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(0, world.Engine.Transcriptions);
    }

    [Fact]
    public async Task ATranscriptionThatFailsAfterTheTeardownEndsAsFailedAndDeliversNothing()
    {
        // Not a disposed dependency but an ordinary engine failure, after the teardown: the state
        // decides, and no recovery is attempted into a session that is gone.
        var world = World.Build();
        await world.PressAsync();
        world.Engine.Hold = true;
        world.Engine.ThrowOnExit = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        Assert.False(await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience));
        world.Engine.AllowExit.SetResult();

        Assert.Equal(SessionCommandDisposition.Failed, (await release.WaitAsync(Patience)).Disposition);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.True(world.Capture.Disposed);
    }

    [Fact]
    public async Task ATranscriptionThatOutlivesBothWaitsIsTornDownBesideAndReportedUnclean()
    {
        // THE INHERITED FALLBACK, STATED: the shell never waited longer than its two intervals for a
        // transcription, and the coordinator does not either. The teardown runs beside the work that
        // would not finish, the shutdown says so, and the work then fails safely against a disposed
        // session rather than delivering into a torn-down shell.
        var world = World.Build();
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var clean = await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience);

        Assert.False(clean);
        Assert.Equal(1, world.Effects.TearDowns);
        Assert.True(world.Capture.Disposed);
        Assert.Equal(0, world.Delivery.Deliveries);

        world.Engine.AllowExit.SetResult();
        var result = await release.WaitAsync(Patience);
        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(1, world.Engine.Transcriptions);
    }

    private sealed class World
    {
        public required DictationSessionCoordinator Coordinator { get; init; }
        public required PushToTalkSessionController Controller { get; init; }
        public required FakeAudioCapture Capture { get; init; }
        public required HeldEngine Engine { get; init; }
        public required CountingDelivery Delivery { get; init; }
        public required ShellAdapter Effects { get; init; }
        public required NoBackgroundWork Background { get; init; }
        public DictationSessionId SessionId { get; private set; }

        public async Task PressAsync()
        {
            var press = await Coordinator.SubmitAsync(PushToTalkSignal.Pressed).WaitAsync(Patience);
            Assert.Equal(SessionCommandDisposition.Applied, press.Disposition);
            SessionId = Controller.CurrentSession!.Id;
        }

        public static World Build(bool holdMicrophoneOpening = false, TimeProvider? clock = null)
        {
            var capture = new FakeAudioCapture { HoldStart = holdMicrophoneOpening };
            var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101), minimumHoldDuration: TimeSpan.Zero);
            var engine = new HeldEngine();
            var delivery = new CountingDelivery();
            var runnerEffects = new RunnerEffects { Engine = engine, DeliveryRoute = delivery };
            var persistence = new SessionPersistence(
                new FakeRecoveryStore(),
                new FakeHistoryStore(),
                new NullLogger(),
                new FrozenClock(Now),
                () => HistoryPreferences.Default,
                runnerEffects);
            runnerEffects.Persistence = persistence;
            var finalizer = new TranscriptFinalizer(
                PatientPipeline.Create(),
                new PolishExecutor(new FakeAdmission(), runnerEffects, () => []),
                runnerEffects);
            var streaming = new StreamingTranscriptionController(new NoStreaming(), new NullLogger(), new FrozenClock(Now));
            var runner = new SessionFinalizationRunner(controller, finalizer, persistence, streaming, runnerEffects, new FrozenClock(Now));
            var effects = new ShellAdapter(controller, runnerEffects);
            var background = new NoBackgroundWork();
            var executor = new DictationSessionExecutor(controller, background, runner, persistence, new HealthyMachine(), effects);
            var coordinator = new DictationSessionCoordinator(
                executor,
                () => new RecordingStartContext(new TargetWindowId(101), TextDeliveryOptions.Default),
                clock);
            return new World
            {
                Coordinator = coordinator,
                Controller = controller,
                Capture = capture,
                Engine = engine,
                Delivery = delivery,
                Effects = effects,
                Background = background,
            };
        }
    }

    /// <summary>The app's session adapter, reduced to the real runner and the real teardown.</summary>
    private sealed class ShellAdapter(PushToTalkSessionController controller, RunnerEffects runnerEffects) : IDictationSessionEffects
    {
        public int TearDowns { get; private set; }

        public bool EscapeRecoveryEnabled => false;

        public void RecordResourcePressure(AppError? failure)
        {
        }

        public void ShowRecoveredTextWaiting()
        {
        }

        public void ShowMemoryCritical()
        {
        }

        public void ShowDiskLow()
        {
        }

        public void RecordTransition(SessionTransitionResult result)
        {
        }

        public RecordingBackgroundSettings RecordingSettings() => new(TimeSpan.FromMinutes(5), () => DictationPreferences.Default);

        public void ShowInterruptionPreserving(SystemLifecycleTransition transition)
        {
        }

        public void ShowTransitionStatus(SessionTransitionResult result)
        {
        }

        public void RecordSessionFailure()
        {
        }

        public void RecordInterruptionFailure()
        {
        }

        public void RecordSessionRecovered(AppError failure)
        {
        }

        public void ShowSessionRecovered(SessionFailureKind kind)
        {
        }

        public Task RecordDictationEdgeAsync() => Task.CompletedTask;

        public void ShowInterruptionPending()
        {
        }

        public void RecordRecordingTimedOut(AppError failure)
        {
        }

        public void ShowRecordingTimedOut()
        {
        }

        public async Task TearDownSessionAsync()
        {
            // What the shell's teardown does to the two things this test counts: the controller
            // disposed (and the capture with it), the delivery route let go of.
            TearDowns++;
            await controller.DisposeAsync();
            runnerEffects.DeliveryRoute = null;
        }
    }

    /// <summary>Nothing runs beside these recordings; the timeout test holds the stop to stand in for a slow one.</summary>
    private sealed class NoBackgroundWork : ISessionBackgroundWork
    {
        public bool HoldStop { get; set; }

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(DictationSessionId sessionId, RecordingBackgroundSettings settings) => Task.CompletedTask;

        public async Task StopAsync()
        {
            StopEntered.TrySetResult();
            if (HoldStop)
            {
                await AllowStopExit.Task;
            }
        }

        public async Task<BackgroundStopReport> StopAsync(TimeSpan deadline)
        {
            await StopAsync();
            return BackgroundStopReport.AllCompleted;
        }

        public Task StopWatchdogAsync() => Task.CompletedTask;
    }

    private sealed class HeldEngine : ITranscriptionEngine
    {
        public string EngineId => "held";
        public bool Hold { get; set; }
        public bool ThrowOnExit { get; set; }
        public int Transcriptions { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Transcriptions++;
            Entered.TrySetResult();
            if (Hold)
            {
                await AllowExit.Task;
            }

            if (ThrowOnExit)
            {
                throw new InvalidOperationException("the engine fell over");
            }

            return new Transcript(audio.SessionId, "hello world", EngineId, DetectedLanguage: "en");
        }
    }

    private sealed class CountingDelivery : ITextDelivery
    {
        public int Deliveries { get; private set; }

        public Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Deliveries++;
            return Task.FromResult(new DeliveryResult(request.Text.SessionId, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste));
        }
    }

    private sealed class RunnerEffects : ISessionFinalizationEffects, ITranscriptFinalizationEffects, ISessionPersistenceEffects
    {
        public ITranscriptionEngine? Engine { get; set; }
        public ITextDelivery? DeliveryRoute { get; set; }
        public SessionPersistence? Persistence { get; set; }

        public ITextDelivery? Delivery => DeliveryRoute;

        public FinalizationOptions CurrentOptions() => new([], new DeterministicTextOptions(true, true, true, true), null);

        public void ArchiveAudio(CapturedAudio audio)
        {
        }

        public void RecordTranscriptionUnavailable()
        {
        }

        public void ShowTranscriptionUnavailable()
        {
        }

        public void ShowTranscribing()
        {
        }

        public void RecordTranscriptionStarted()
        {
        }

        public void RecordTranscriptionFinished(Transcript transcript, long elapsedMilliseconds)
        {
        }

        public void RecordTranscriptionFailed(AppError? failure, long elapsedMilliseconds)
        {
        }

        public void ShowTranscriptionFailed()
        {
        }

        public void ShowDelivering()
        {
        }

        public void RecordDeliveryStarted()
        {
        }

        public void RecordDelivery(DeliveryResult delivery, long elapsedMilliseconds)
        {
        }

        public void ReportDelivery(DeliveryResult delivery, string? language)
        {
        }

        public void ShowEscapeRecoveryFinished()
        {
        }

        public void ShowHeldStatus(FinalizationReport report)
        {
        }

        public void RecordDictationCompleted(long waitMilliseconds)
        {
        }

        public void RecordDeterministicProcessingStarted()
        {
        }

        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly)
        {
        }

        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken) =>
            Persistence!.SaveRecoveryTextAsync(output, cancellationToken);

        public void RecordPolishStarted(string providerId)
        {
        }

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds)
        {
        }

        public void RecordPolishRefused()
        {
        }

        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds)
        {
        }

        public void ShowPendingRecovery(RecoveryTextRecord record)
        {
        }

        public void ClearRecoveredText()
        {
        }

        public void NotifyHistoryChanged()
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

    private sealed class FakeAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(RuntimeResourceKind resource, RuntimeWorkloadKind workload, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new NoLease()));

        private sealed class NoLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoStreaming : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;

        public ITranscriptionEngine? Engine => null;

        public IAudioSnapshotSource? Audio => null;
    }

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult([], HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Write(AppLogEntry entry)
        {
        }
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(window);
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }
        public bool HoldStart { get; set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStartExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            StartEntered.TrySetResult();
            if (HoldStart)
            {
                await AllowStartExit.Task;
            }

            IsCapturing = true;
            return new AudioOperationResult(Succeeded: true);
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            Cancelled = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
