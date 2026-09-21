using EnviousWispr.App.Composition;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Drives the session the app runs: the coordinator, executor, runner and their effects built by the
/// same <see cref="SessionComposition"/> the shell calls, with fakes only at the leaves - the
/// microphone, the engine, the delivery route, the stores, the window.
/// </summary>
/// <remarks>
/// THE FILES UNDER App/Composition ARE COMPILED INTO THIS PROJECT, not copied, so a join that
/// changes in the app changes here. Until now the production joins were built inline behind a
/// window, and the only proof they held was a journey through the built app; a component test
/// with its own adapters could not tell when the shell handed the runner something else.
/// </remarks>
public sealed class SessionCompositionTests
{
    [Fact]
    public async Task ComposedSessionReachesDeliveryAndReset()
    {
        var world = World.Create("hello world");

        var pressed = await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, pressed.Disposition);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        var released = await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        var request = Assert.Single(world.Delivery.Requests);
        Assert.Equal("hello world", request.Text.Text);
        Assert.Equal(new TargetWindowId(101), request.Target);
        Assert.Null(world.Controller.CurrentSession);
        // The shell's leaves saw what the shell's adapters used to hand them: the hook told the
        // recording is on then off, the run-state edge written at both ends, the window told the
        // outcome through the reported delivery and the status.
        Assert.Equal([true, false], world.RecordingActive);
        Assert.Equal([true, false], world.RunState.Edges);
        Assert.Contains(world.View.Statuses, status => status.State == DictationOverlayState.Recording);
        Assert.Contains(world.View.Statuses, status => status.Text == "Transcribing locally...");
        Assert.Contains(world.View.Statuses, status => status.Text == "Delivering to the app you started in...");
        var (delivered, language) = Assert.Single(world.View.Deliveries);
        Assert.Equal(DictationOverlayState.Success, delivered.State);
        Assert.Equal("en", language);
        Assert.Equal("Delivering to the app you started in...", world.View.Statuses[^1].Text);
        Assert.Single(world.Archived);
    }

    [Fact]
    public async Task ComposedLifecycleCancellationReachesRunner()
    {
        // THE ENGINE IS SLOW AND WINDOWS LOCKS. The interruption goes through the composed
        // coordinator, which cancels the processing in flight before it queues; the token the
        // executor armed is the one the runner handed the engine, so the engine sees the cancel.
        var world = World.Create("hello world");
        world.Engine.Hold = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.Coordinator.IsProcessing);

        var interruption = await world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var released = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(world.Engine.Token!.Value.IsCancellationRequested, "the engine's token is the cancelled deadline");
        Assert.Equal(SessionCommandDisposition.Failed, released.Disposition);
        Assert.Empty(world.Delivery.Requests);
        Assert.Null(world.Controller.CurrentSession);
        Assert.False(world.Coordinator.IsProcessing);
        Assert.NotEqual(SessionCommandDisposition.Failed, interruption.Disposition);
        Assert.Equal([true, false, false], world.RunState.Edges);
    }

    [Fact]
    public async Task ComposedEdgeAfterTheTeardownSaysNoDictationIsInFlight()
    {
        // THE TRANSCRIPTION OUTLIVES BOTH OF THE SHUTDOWN'S WAITS. The teardown disposes the
        // controller beside it - which keeps the session it was disposed under - and the command's
        // finally then writes the run-state edge. The shell used to read its own controller field,
        // nulled by the teardown; the composition must say the same: nothing is in flight.
        var clock = new Deterministic.ManualClock();
        var world = World.Create("hello world", clock);
        world.Engine.HoldIgnoringCancel = true;
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var drain = TimeSpan.FromSeconds(10);
        var shutdown = world.Coordinator.ShutdownAsync(drain);
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(drain);
        await clock.WhenRegistered(2).WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(drain);
        await world.TornDown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(world.Controller.CurrentSession);
        world.Engine.AllowExit.SetResult();

        var released = await release.WaitAsync(TimeSpan.FromSeconds(10));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Failed, released.Disposition);
        Assert.Equal([true, false], world.RunState.Edges);
    }

    [Fact]
    public async Task ComposedOptionsAreReadAfterTranscription()
    {
        // A WORD TAUGHT WHILE THE ENGINE WAS WORKING REACHES THIS DICTATION. The shell's options are
        // a read at the call, not a value captured when the session was built; the composed runner
        // asks after the engine returns.
        var world = World.Create("envy wisper is here");
        world.Engine.BeforeReturning = () => world.CustomWords.Add(new CustomWordEntry("envy wisper", "EnviousWispr"));

        await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        Assert.Equal("EnviousWispr is here", world.Delivery.Requests.Single().Text.Text);
    }

    private sealed class World
    {
        public required DictationSessionCoordinator Coordinator { get; init; }

        public required PushToTalkSessionController Controller { get; init; }

        public required FakeEngine Engine { get; init; }

        public required FakeDelivery Delivery { get; init; }

        public required FakeView View { get; init; }

        public required FakeRunState RunState { get; init; }

        public required List<bool> RecordingActive { get; init; }

        public required List<CapturedAudio> Archived { get; init; }

        public required List<CustomWordEntry> CustomWords { get; init; }

        public required TaskCompletionSource TornDown { get; init; }

        public static World Create(string spoken, TimeProvider? clock = null)
        {
            var log = new NullLogger();
            clock ??= TimeProvider.System;
            var capture = new FakeAudioCapture();
            var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101), minimumHoldDuration: TimeSpan.Zero);
            var engine = new FakeEngine(spoken);
            var delivery = new FakeDelivery();
            var view = new FakeView();
            var runState = new FakeRunState();
            var recordingActive = new List<bool>();
            var archived = new List<CapturedAudio>();
            var words = new List<CustomWordEntry>();
            var quiet = new QuietEffects();
            var persistence = new SessionPersistence(
                new FakeRecoveryStore(),
                new FakeHistoryStore(),
                log,
                clock,
                () => HistoryPreferences.Default,
                quiet);
            var finalizer = new TranscriptFinalizer(
                PatientPipeline.Create(),
                new PolishExecutor(new FakeAdmission(), quiet, () => words.ToArray()),
                new FinalizationLeaves(persistence));
            var timers = new IdleTimerEffects();
            var streaming = new StreamingTranscriptionController(new NoStreaming(), log, clock);
            var background = new SessionBackgroundWork(
                new RecordingWatchdog(timers, clock),
                new LivePreviewController(new NoPreview(), log, clock),
                new AutoStopMonitor(timers, log, clock),
                streaming);
            var runId = Guid.NewGuid();
            var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var coordinator = SessionComposition.Compose(new SessionCompositionParts(
                controller,
                capture,
                background,
                finalizer,
                persistence,
                streaming,
                new HealthyMachine(),
                runState,
                log,
                new SessionShell(
                    view,
                    Dictation: () => DictationPreferences.Default,
                    Engine: () => engine,
                    Delivery: () => delivery,
                    Options: () => new FinalizationOptions(words.ToArray(), new DeterministicTextOptions(true, true, true, true), null),
                    CloudPolishProviderName: () => null,
                    RunId: () => runId,
                    RecordingActive: recordingActive.Add,
                    ArchiveAudio: archived.Add,
                    // The shell's teardown disposes the controller beside a command that outlived
                    // the shutdown's waits; the composed test does the same.
                    TearDownSession: async () =>
                    {
                        await controller.DisposeAsync();
                        tornDown.SetResult();
                    }),
                clock));

            return new World
            {
                Coordinator = coordinator,
                Controller = controller,
                Engine = engine,
                Delivery = delivery,
                View = view,
                RunState = runState,
                RecordingActive = recordingActive,
                Archived = archived,
                CustomWords = words,
                TornDown = tornDown,
            };
        }
    }

    private sealed class FakeView : ISessionView
    {
        public List<DictationStatus> Statuses { get; } = [];

        public List<(string Title, string Message, bool IsError)> Notices { get; } = [];

        public List<(DictationStatus Delivered, string? Language)> Deliveries { get; } = [];

        public int MainWindowShown { get; private set; }

        public void ShowStatus(DictationStatus status) => Statuses.Add(status);

        public void ShowNotice(string title, string message, bool isError = false) => Notices.Add((title, message, isError));

        public void ShowMainWindow() => MainWindowShown++;

        public void ReportDelivery(DictationStatus delivered, string? detectedLanguage) => Deliveries.Add((delivered, detectedLanguage));
    }

    private sealed class FakeRunState : IApplicationRunStateStore
    {
        public List<bool> Edges { get; } = [];

        public Task<ApplicationRunStartResult> BeginRunAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HeartbeatAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SetDictationActiveAsync(Guid runId, bool active, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        {
            Edges.Add(active);
            return Task.FromResult(true);
        }

        public Task<bool> NoteSystemEndingAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> CompleteRunAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>A speech engine that can be held mid-call and honours the cancel it is handed.</summary>
    private sealed class FakeEngine(string spoken) : ITranscriptionEngine
    {
        public string EngineId => "whisper";

        public bool Hold { get; set; }

        /// <summary>Held until released, whatever the token says: a worker that does not answer a cancel.</summary>
        public bool HoldIgnoringCancel { get; set; }

        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken? Token { get; private set; }

        public Action? BeforeReturning { get; set; }

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Entered.TrySetResult();
            if (HoldIgnoringCancel)
            {
                await AllowExit.Task;
            }
            else if (Hold)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            BeforeReturning?.Invoke();
            return new Transcript(audio.SessionId, spoken, EngineId, DetectedLanguage: "en");
        }
    }

    private sealed class FakeDelivery : ITextDelivery
    {
        public List<TextDeliveryRequest> Requests { get; } = [];

        public Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DeliveryResult(request.Text.SessionId, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste));
        }
    }

    /// <summary>The finalisation's own leaves: the recovery write goes to the real persistence owner, the rest is quiet.</summary>
    private sealed class FinalizationLeaves(SessionPersistence persistence) : ITranscriptFinalizationEffects
    {
        public void RecordDeterministicProcessingStarted()
        {
        }

        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly)
        {
        }

        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken) =>
            persistence.SaveRecoveryTextAsync(output, cancellationToken);

        public void RecordPolishRefused()
        {
        }

        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds)
        {
        }

        public void RecordPolishStarted(string providerId)
        {
        }

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds)
        {
        }
    }

    private sealed class QuietEffects : ISessionPersistenceEffects, IPolishAttemptEffects
    {
        public void ShowPendingRecovery(RecoveryTextRecord record)
        {
        }

        public void ClearRecoveredText()
        {
        }

        public void NotifyHistoryChanged()
        {
        }

        public void RecordPolishStarted(string providerId)
        {
        }

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds)
        {
        }
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

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Added { get; } = [];

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult(Added, HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Added.Add(entry);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
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

        public void Post(PushToTalkSignal signal)
        {
        }

        public void RecordingTimedOut(DictationSessionId sessionId)
        {
        }
    }

    private sealed class NoStreaming : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;

        public ITranscriptionEngine? Engine => null;

        public IAudioSnapshotSource? Audio => null;
    }

    private sealed class NoPreview : ILivePreviewEffects
    {
        public bool Enabled => false;

        public ILivePreviewEngine? Engine => null;

        public AppErrorCode? EngineUnavailableReason => null;

        public IAudioSnapshotSource? Audio => null;

        public DictationSessionId? RecordingSessionId => null;

        public void ShowPreview(DictationSessionId sessionId, string text)
        {
        }

        public void ClearPreview()
        {
        }
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Write(AppLogEntry entry)
        {
        }
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

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
