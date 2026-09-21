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
    public async Task QuittingDuringATranscriptionWaitsForItKeepsTheWordsAndThenClosesTheMicrophone()
    {
        // THE APP IS LEAVING, AND A DELIVERY NOT YET ISSUED IS NOT ISSUED. The transcription is
        // waited for - nothing is torn down under it - and its words go to the recovery copy rather
        // than into whatever is in front of an app that is shutting down; the teardown runs under
        // the session once the command is over.
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
        var report = await shutdown.WaitAsync(Patience);

        Assert.True(report.Clean);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(1, world.Engine.Transcriptions);
        Assert.True(world.Persistence.HasPendingRecovery, "the words wait on Home for the next launch");
        Assert.True(world.Capture.Disposed, "the teardown disposed the session controller, which disposed the capture");
        Assert.Equal(1, world.Effects.TearDowns);

        // Nothing after the teardown: a late timeout and a late lock are refused, and neither the
        // engine nor the delivery route is asked anything again.
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.TimeOutAsync(world.SessionId)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked)).Disposition);
        Assert.Equal(0, world.Delivery.Deliveries);
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
        Assert.True((await shutdown.WaitAsync(Patience)).Clean);

        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(0, world.Engine.Transcriptions);
        Assert.True(world.Capture.Cancelled, "the recording that was open when the teardown came was cancelled, not delivered");
        Assert.True(world.Capture.Disposed);
        Assert.Equal(1, world.Effects.TearDowns);
    }

    [Fact]
    public async Task ATranscriptionThatFinishesInsideTheBudgetIsHeldForRecoveryAndTornDownUnderTheSession()
    {
        // ONE BUDGET. A transcription that finishes inside it ends with its words kept - delivery is
        // closed from the moment the shutdown began - and the teardown runs under the session once
        // it is over; the shutdown is clean. Crossed on the manual clock: the budget's timer is
        // registered, the engine let go before it is advanced.
        var clock = new Deterministic.ManualClock();
        var world = World.Build(clock: clock);
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var budget = TimeSpan.FromSeconds(10);
        var shutdown = world.Coordinator.ShutdownAsync(budget);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        Assert.Equal(budget, clock.NextDue);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, world.Effects.TearDowns);
        world.Engine.AllowExit.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        var report = await shutdown.WaitAsync(Patience);
        Assert.True(report.Clean, "the transcription finished inside the budget");
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.True(world.Capture.Disposed);
        Assert.Equal(1, world.Effects.TearDowns);
    }

    [Fact]
    public async Task ATimeoutStillRunningWhenTheBudgetEndsIsReportedOutstandingAndNothingIsTornDown()
    {
        // THE WATCHDOG'S TIMEOUT IS STOPPING THE LOOPS when the budget runs out. Nothing is torn down
        // beside it: the microphone it is closing is its to close, the shutdown says the command is
        // outstanding, and when the timeout resumes it finishes on its own terms.
        var world = World.Build();
        await world.PressAsync();
        world.Background.HoldStop = true;
        var timeout = world.Coordinator.TimeOutAsync(world.SessionId);
        await world.Background.StopEntered.Task.WaitAsync(Patience);

        var report = await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience);

        Assert.Equal(ShutdownOutcome.Unclean, report.Outcome);
        Assert.True(report.CommandOutstanding);
        Assert.Null(report.Teardown);
        Assert.Equal(0, world.Effects.TearDowns);
        Assert.False(world.Capture.Disposed);

        world.Background.AllowStopExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await timeout.WaitAsync(Patience)).Disposition);
        Assert.True(world.Capture.Cancelled, "the timeout closed the microphone itself");
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(0, world.Engine.Transcriptions);
        Assert.Equal(0, world.Effects.TearDowns);
    }

    [Fact]
    public async Task ATranscriptionThatOutlivesTheBudgetIsReportedOutstandingAndNotTornDownBeside()
    {
        // NEVER BESIDE A RESOURCE USER. A transcription that will not finish inside the budget is left
        // what it holds: the shutdown says the command is outstanding and runs no teardown. When the
        // engine answers at last the command ends on its own terms - its words kept, nothing
        // delivered into an app that is leaving - and nothing was disposed under it.
        var world = World.Build();
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var report = await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience);

        Assert.Equal(ShutdownOutcome.Unclean, report.Outcome);
        Assert.True(report.CommandOutstanding);
        Assert.False(report.ExpiriesOutstanding);
        Assert.Equal(0, report.HoldsOutstanding);
        Assert.Null(report.Teardown);
        Assert.Equal(0, world.Effects.TearDowns);
        Assert.False(world.Capture.Disposed);
        Assert.Equal(0, world.Delivery.Deliveries);

        world.Engine.AllowExit.SetResult();
        var result = await release.WaitAsync(Patience);
        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.Equal(1, world.Engine.Transcriptions);
        Assert.Equal(0, world.Effects.TearDowns);
    }

    [Fact]
    public async Task ADeliveryNotYetAdmittedWhenTheShutdownClosesIsNeverIssued()
    {
        // THE CLOSURE AND THE ADMISSION ARE ONE DECISION. The finalisation has read delivery as open
        // and moved the session to Delivering; it is held there, on the transition's own notification,
        // when the shutdown closes delivery. Resumed, it finds delivery closed at the admission that
        // counts - the one taken under the closure's lock, with the issue following at once - so
        // nothing is issued: the words go to the recovery copy and the teardown runs after. A check
        // made only before the transition would have let this delivery through.
        var world = World.Build();
        await world.PressAsync();
        var delivering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Controller.SessionChanged += (_, snapshot) =>
        {
            if (snapshot.State != DictationSessionState.Delivering)
            {
                return;
            }

            delivering.TrySetResult();
            if (!resume.Task.Wait(Patience))
            {
                throw new TimeoutException("the finalisation held at Delivering was never resumed");
            }
        };
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await delivering.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        resume.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        var report = await shutdown.WaitAsync(Patience);
        Assert.True(report.Clean);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery, "the words not delivered are kept for recovery");
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(1, world.Effects.TearDowns);
    }

    [Fact]
    public async Task IssuedDeliveryIsNeverRetriedDuringShutdown()
    {
        // A DELIVERY ALREADY ISSUED WHEN THE SHUTDOWN BEGINS IS LEFT TO SETTLE, and settles once.
        // The route is held mid-delivery; the shutdown waits; the route answers refused; nothing asks
        // it again, the words go to the recovery copy, and the teardown runs after.
        var world = World.Build();
        await world.PressAsync();
        world.Delivery.Hold = true;
        world.Delivery.Refuse = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Delivery.Entered.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        world.Delivery.AllowExit.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        var report = await shutdown.WaitAsync(Patience);
        Assert.True(report.Clean);
        Assert.Equal(1, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery, "the refused delivery left its words for recovery");
        Assert.Equal(1, world.Effects.TearDowns);
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
        public required SessionPersistence Persistence { get; init; }
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
                Persistence = persistence,
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


        public async Task<StopOutcome> StopWatchdogAsync(TimeSpan deadline)
        {
            await StopWatchdogAsync();
            return StopOutcome.Completed;
        }    }

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

        public bool Hold { get; set; }

        public bool Refuse { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Deliveries++;
            Entered.TrySetResult();
            if (Hold)
            {
                await AllowExit.Task;
            }

            return Refuse
                ? new DeliveryResult(request.Text.SessionId, Delivered: false, ClipboardFallback: false, TextDeliveryRoute.None, TextDeliveryRefusalReason.TargetChanged)
                : new DeliveryResult(request.Text.SessionId, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste);
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
