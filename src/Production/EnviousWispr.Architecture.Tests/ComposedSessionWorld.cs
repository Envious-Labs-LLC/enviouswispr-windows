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
using EnviousWispr.Services.Reliability;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The production session, composed as the shell composes it - RuntimeComposition and
/// SessionComposition over the real coordinator, executor, controller, runner, finalizer,
/// persistence and background owners - with fakes only at the leaves: the microphone, the engines,
/// the delivery route, the stores, the window, the log.
/// </summary>
/// <remarks>
/// ONE WORLD FOR EVERY PRODUCTION-PATH PROOF (plan-2 step 10). A test that hand-builds the executor's
/// effects or the finalisation is proving its own wiring, not the app's; every proof of what the
/// session does under a press, a release, a lock, an exit or a shutdown builds this world instead, so
/// what is proved is what the shell would have done. Isolated component tests keep their own fakes and
/// say so.
/// </remarks>
internal sealed class ComposedSessionWorld
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

    /// <summary>How many times the session's disposal reached the route: the last of the shell's three parts.</summary>
    public int TearDowns { get; private set; }

    /// <summary>The shell's own reference to its controller: let go of by the teardown, as the shell's field is.</summary>
    private PushToTalkSessionController? _attached;

    public required FakeAudioCapture Capture { get; init; }

    public required FakeRuntimeView RuntimeView { get; init; }

    public required FakeRecoveryStore RecoveryStore { get; init; }

    public required FakeHistoryStore HistoryStore { get; init; }

    public required FakePreviewEngine PreviewEngine { get; init; }

    /// <summary>The dictation preferences the shell would read; settable between commands, as a save is.</summary>
    public DictationPreferences Dictation { get; set; } = DictationPreferences.Default;

    public bool LivePreviewEnabled { get; set; }

    public PolishSetup? Polish { get; set; }

    /// <summary>The engine the shell would hand over; replaceable between commands, as a reload is.</summary>
    public ITranscriptionEngine EngineRef { get; set; } = null!;

    /// <summary>The capture the shell would sample; replaceable between commands, as a hook restart is.</summary>
    public IAudioSnapshotSource Audio { get; set; } = null!;

    public HistoryPreferences History { get; set; } = HistoryPreferences.Default;

    /// <summary>Whether the shell has let go of its coordinator.</summary>
    public bool Detached { get; set; }

    public static ComposedSessionWorld Create(string spoken, TimeProvider? clock = null) => Create(spoken, clock, new FakeRunState(), Guid.NewGuid());

    /// <param name="runState">The run-state store the edges go to: the fake that lists them, or the production store on a file.</param>
    /// <param name="runId">The run the shell would name, as it does through its RunId read.</param>
    /// <param name="recoveryStore">The recovery store the words go to: the fake that lists them, or the production store on a file.</param>
    public static ComposedSessionWorld Create(string spoken, TimeProvider? clock, IApplicationRunStateStore runState, Guid runId, IRecoveryTextStore? recoveryStore = null, ISystemResourceProbe? resources = null)
    {
        var log = new RecordingLogger();
        clock ??= TimeProvider.System;
        var capture = new FakeAudioCapture();
        var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101), minimumHoldDuration: TimeSpan.Zero);
        var engine = new FakeEngine(spoken);
        var previewEngine = new FakePreviewEngine();
        var delivery = new FakeDelivery();
        var view = new FakeView();
        var runtimeView = new FakeRuntimeView();
        recoveryStore ??= new FakeRecoveryStore();
        var historyStore = new FakeHistoryStore();
        var trace = new List<string>();
        var recordingActive = new List<bool>();
        var archived = new List<CapturedAudio>();
        var words = new List<CustomWordEntry>();
        ComposedSessionWorld? world = null;

        // THE LONG-LIVED OWNERS FIRST, AS THE SHELL BUILDS THEM: the reads reach the world the way
        // the shell's reach its fields, at the call, so a preference set between commands is seen.
        var runtime = RuntimeComposition.Compose(new RuntimeCompositionParts(
            recoveryStore,
            historyStore,
            PatientPipeline.Create(),
            new FakeAdmission(),
            log,
            new RuntimeShell(
                runtimeView,
                LivePreviewEnabled: () => world!.LivePreviewEnabled,
                History: () => world!.History,
                CustomWords: () => words.ToArray(),
                Audio: () => world!.Audio,
                Engine: () => world!.EngineRef,
                PreviewEngine: () => previewEngine,
                PreviewUnavailableReason: () => null,
                RecordingSessionId: () => controller.CurrentSession?.Id,
                Coordinator: () => world is { Detached: false } ? world.Coordinator : null,
                Leaving: () => false),
            clock));

        var coordinator = SessionComposition.Compose(new SessionCompositionParts(
            controller,
            capture,
            runtime,
            resources ?? new HealthyMachine(),
            runState,
            log,
            new SessionShell(
                view,
                AttachedSession: () => world!._attached?.CurrentSession,
                Dictation: () => world!.Dictation,
                Engine: () => world!.EngineRef,
                Delivery: () => delivery,
                Options: () => new FinalizationOptions(words.ToArray(), new DeterministicTextOptions(true, true, true, true), world!.Polish),
                CloudPolishProviderName: () => null,
                RunId: () => runId,
                RecordingActive: recordingActive.Add,
                ArchiveAudio: archived.Add,
                // The shell's three parts of the disposal, as the app supplies them: the executor
                // disposes the controller itself between the first and the second.
                DetachCaptureObservers: () => { },
                ReleaseSession: () => world!._attached = null,
                DisposeDeliveryRoute: () => world!.TearDowns++),
            clock));

        world = new ComposedSessionWorld
        {
            Coordinator = coordinator,
            Controller = controller,
            Engine = engine,
            Delivery = delivery,
            View = view,
            RunState = runState as FakeRunState ?? new FakeRunState(),
            RecordingActive = recordingActive,
            Archived = archived,
            CustomWords = words,
            Capture = capture,
            RuntimeView = runtimeView,
            RecoveryStore = recoveryStore as FakeRecoveryStore ?? new FakeRecoveryStore(),
            HistoryStore = historyStore,
            PreviewEngine = previewEngine,
            Runtime = runtime,
            Log = log,
        };
        world._attached = controller;
        world.EngineRef = engine;
        world.Audio = capture;
        world.RecoveryStore.Trace = world.Trace;
        return world;
    }

    public required SessionRuntime Runtime { get; init; }

    public required RecordingLogger Log { get; init; }

    /// <summary>The order of the leaf operations that matter to a proof, appended by the fakes that share it.</summary>
    public List<string> Trace { get; } = [];

    /// <summary>A key through the runtime's queue, the route the hook and the auto-stop share.</summary>
    public Task SubmitAsync(PushToTalkSignal signal) => Runtime.SubmitAsync(signal);

    /// <summary>The composed persistence owner, for what it knows about recovery.</summary>
    public SessionPersistence Persistence => Runtime.Persistence;

    /// <summary>The recording started by <see cref="PressAsync"/>.</summary>
    public DictationSessionId SessionId { get; private set; }

    /// <summary>A press through the coordinator, applied, with the recording it started remembered.</summary>
    public async Task PressAsync()
    {
        var press = await Coordinator.SubmitAsync(PushToTalkSignal.Pressed).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionCommandDisposition.Applied, press.Disposition);
        SessionId = Controller.CurrentSession!.Id;
    }

    /// <summary>What the capture and the preview were doing each time the engine was reached: the order proofs read this.</summary>
    public List<(bool Capturing, bool CaptureCancelled, bool PreviewRunning)> EngineSaw { get; } = [];

    /// <summary>
    /// A recording in flight with the preview's worker still starting: the press has been applied,
    /// the microphone is open, the preview loop is owned, and the worker's start is held until a test
    /// releases it. The engine writes down what it finds when it is reached.
    /// </summary>
    public static async Task<ComposedSessionWorld> StartRecordingWithPreviewStartupHeldAsync(string spoken = "hello world", bool escapeRecovery = false)
    {
        var world = Create(spoken);
        world.LivePreviewEnabled = true;
        world.Dictation = DictationPreferences.Default with { EscapeRecoveryEnabled = escapeRecovery };
        world.PreviewEngine.HoldStarts = true;
        world.Engine.OnEntered = () => world.EngineSaw.Add((world.Capture.IsCapturing, world.Capture.Cancelled.Task.IsCompleted, world.Runtime.Preview.IsRunning));

        var press = await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed).WaitAsync(TimeSpan.FromSeconds(10));
        await world.PreviewEngine.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionCommandDisposition.Applied, press.Disposition);
        Assert.True(world.Capture.IsCapturing);
        Assert.Equal(DictationSessionState.Recording, world.Controller.CurrentSession?.State);
        Assert.True(world.Runtime.Preview.IsRunning);
        Assert.Equal(0, world.PreviewEngine.Starts);
        world.SessionId = world.Controller.CurrentSession!.Id;
        return world;
    }
}

internal sealed class FakeRuntimeView : IRuntimeView
{
    public List<string?> Previews { get; } = [];

    public List<RecoveryTextLoadResult> Recovered { get; } = [];

    public int Cleared { get; private set; }

    public int HistoryChanges { get; private set; }

    public int MainWindowShown { get; private set; }

    public void ShowPreview(LivePreviewFrame? frame) => Previews.Add(frame?.Text);

    public void ShowRecoveredText(RecoveryTextLoadResult result) => Recovered.Add(result);

    public void ClearRecoveredText() => Cleared++;

    public void NotifyHistoryChanged() => HistoryChanges++;

    public void ShowMainWindow() => MainWindowShown++;

    public List<SessionHolder> BusyShown { get; } = [];

    public void ShowSessionBusy(SessionHolder holder) => BusyShown.Add(holder);
}

/// <summary>A preview engine that answers every snapshot with the same words, and counts them.</summary>
internal sealed class FakePreviewEngine : IAbortableLivePreviewEngine
{
    public string EngineId => "preview";

    public string Words { get; set; } = "preview words";

    public int Passes { get; private set; }

    public int Starts { get; private set; }

    /// <summary>When set, a start does not answer until released: a worker still loading its model.</summary>
    public bool HoldStarts { get; set; }

    public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AllowStartExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when a held start saw its token cancelled; the start still waits to be released before it answers, as a worker mid-load does.</summary>
    public TaskCompletionSource StartCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default)
    {
        StartEntered.TrySetResult();
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
        return Task.FromResult(new LivePreviewUpdate(snapshot.SessionId.Value, sequence, true, Words));
    }

    public bool HoldStop { get; set; }

    public int Stops { get; private set; }

    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the stop answers that the worker did not go, as the production adapter does when its exit was not observed.</summary>
    public bool RefuseStop { get; set; }

    /// <summary>What an abort sees; the production adapter releases the resource only on Exited or NoWorker.</summary>
    public RuntimeWorkerAbortOutcome AbortOutcome { get; set; } = RuntimeWorkerAbortOutcome.Exited;

    public int Aborts { get; private set; }

    public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
    {
        Stops++;
        if (HoldStop)
        {
            StopEntered.TrySetResult();
            await AllowStopExit.Task;
        }

        return RefuseStop
            ? new RuntimeWorkerResult(false, RuntimeWorkerState.Faulted, new AppError(AppErrorCode.RuntimeWorkerFailed, AppErrorStage.RuntimeWorker, CanRetry: true))
            : new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped);
    }

    public Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline)
    {
        Aborts++;
        if (AbortOutcome == RuntimeWorkerAbortOutcome.Exited)
        {
            RefuseStop = false;
        }

        return Task.FromResult(new RuntimeWorkerAbortResult(AbortOutcome, 4242));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A polish provider that fails every request the way an offline Ollama does.</summary>
internal sealed class FailingPolish : IPolishProvider
{
    public string ProviderId => "ollama";

    public List<PolishRequest> Requests { get; } = [];

    public List<string>? Trace { get; set; }

    public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        Trace?.Add($"Polish:{request.Input.Text}");
        return Task.FromResult(new PolishResult(
            request.Input,
            PolishAttemptStatus.Unavailable,
            new AppError(AppErrorCode.PolishProviderUnavailable, AppErrorStage.LocalPolish, CanRetry: true)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeView : ISessionView
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

internal sealed class FakeRunState : IApplicationRunStateStore
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

    public Task<bool> CompleteRunAsync(Guid runId, DateTimeOffset timestamp, PublicationFence fence, CancellationToken cancellationToken = default) =>
        Task.FromResult(fence.TryCommit(() => { }));
}

/// <summary>A speech engine that can be held mid-call and honours the cancel it is handed.</summary>
internal sealed class FakeEngine(string spoken) : ITranscriptionEngine
{
    public string EngineId => "whisper";

    public bool Hold { get; set; }

    /// <summary>Held until released, whatever the token says: a worker that does not answer a cancel.</summary>
    public bool HoldIgnoringCancel { get; set; }

    public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets a held call return; a held call cancelled first stays cancelled.</summary>
    public void Release() => _released.TrySetResult();

    public CancellationToken? Token { get; private set; }

    public Action? BeforeReturning { get; set; }

    /// <summary>Asked at the entry of every call, before anything is awaited: what the world looked like when the engine was reached.</summary>
    public Action? OnEntered { get; set; }


    public int Calls { get; private set; }

    public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
    {
        Calls++;
        Token = cancellationToken;
        OnEntered?.Invoke();
        Entered.TrySetResult();
        if (HoldIgnoringCancel)
        {
            await AllowExit.Task;
        }
        else if (Hold)
        {
            await _released.Task.WaitAsync(cancellationToken);
        }

        BeforeReturning?.Invoke();
        return new Transcript(audio.SessionId, spoken, EngineId, DetectedLanguage: "en");
    }
}

internal sealed class FakeDelivery : ITextDelivery
{
    public List<TextDeliveryRequest> Requests { get; } = [];

    public int Deliveries => Requests.Count;

    /// <summary>When set, a delivery does not answer until released: a route inside the target's window.</summary>
    public bool Hold { get; set; }

    /// <summary>When set, the route answers refused (a protected field, say) with the words on the clipboard only.</summary>
    public bool Refuse { get; set; }

    /// <summary>When set, the route answers exactly this (with the request's session id): a fault, say, as ContextAwareTextDelivery would name one.</summary>
    public DeliveryResult? Answer { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        Entered.TrySetResult();
        if (Hold)
        {
            await AllowExit.Task;
        }

        if (Answer is { } answer)
        {
            return answer with { SessionId = request.Text.SessionId };
        }

        return Refuse
            ? new DeliveryResult(request.Text.SessionId, Delivered: false, ClipboardFallback: false, TextDeliveryRoute.ClipboardOnly, TextDeliveryRefusalReason.ProtectedField)
            : new DeliveryResult(request.Text.SessionId, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste);
    }
}

internal sealed class FakeAdmission : IRuntimeResourceAdmission
{
    public Task<RuntimeResourceAcquireResult> AcquireAsync(RuntimeResourceKind resource, RuntimeWorkloadKind workload, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new NoLease()));

    private sealed class NoLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeRecoveryStore : IRecoveryTextStore
{
    public List<string> Saved { get; } = [];

    public List<string>? Trace { get; set; }

    public int Cleared { get; private set; }

    public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

    public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default)
    {
        Saved.Add(record.Text);
        Trace?.Add($"SaveRecovery:{record.Text}");
        return Task.FromResult(true);
    }

    public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        Cleared++;
        return Task.FromResult(true);
    }
}

internal sealed class FakeHistoryStore : IHistoryStore
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

internal sealed class HealthyMachine : ISystemResourceProbe
{
    public SystemResourceSnapshot Probe() => new(
        AvailableDiskBytes: 10L * 1024 * 1024 * 1024,
        AvailablePhysicalMemoryBytes: 8UL * 1024 * 1024 * 1024,
        MemoryLoadPercent: 40);
}

/// <summary>A machine that answers whatever a test says, and counts how often it was asked.</summary>
internal sealed class MachineOf(SystemResourceSnapshot snapshot) : ISystemResourceProbe
{
    public int Probes { get; private set; }

    public SystemResourceSnapshot Probe()
    {
        Probes++;
        return snapshot;
    }
}

internal sealed class RecordingLogger : IAppLogger
{
    private readonly List<AppLogEntry> _entries = [];

    public IReadOnlyList<AppEventCode> Events
    {
        get
        {
            lock (_entries)
            {
                return _entries.Select(entry => entry.Event).ToArray();
            }
        }
    }

    /// <summary>Every line as written, for a proof that reads a line's fields and not only its event.</summary>
    public IReadOnlyList<AppLogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Write(AppLogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

internal sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
{
    public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(window);
}

internal sealed class FakeAudioCapture : IAudioCapture, IAudioSnapshotSource
{
    private static readonly float[] OneSample = [0.2f];
    private DictationSessionId _sessionId;

    /// <summary>What a snapshot returns: silence unless a take is scripted.</summary>
    public float[] Take { get; set; } = new float[16_000];

    public int Snapshots { get; private set; }

    /// <summary>Where a snapshot's session comes from; a capture never started answers for the session it is told about.</summary>
    public Func<DictationSessionId?>? SessionSource { get; set; }

    public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
    {
        Snapshots++;
        return new(SessionSource?.Invoke() ?? _sessionId, Take, 16_000, 1);
    }

    /// <summary>A take of speech followed by the silence the auto-stop waits for.</summary>
    public static float[] SpeechThenSilence(int speechMilliseconds, int silenceMilliseconds) =>
        Script((true, speechMilliseconds), (false, silenceMilliseconds));

    /// <summary>A take built from runs of speech and silence, in order.</summary>
    public static float[] Script(params (bool IsSpeech, int Milliseconds)[] parts)
    {
        var samples = new float[parts.Sum(part => 16_000 * part.Milliseconds / 1000)];
        var cursor = 0;
        foreach (var (isSpeech, milliseconds) in parts)
        {
            var length = 16_000 * milliseconds / 1000;
            for (var i = 0; i < length; i++)
            {
                var amplitude = isSpeech ? 0.2f : 0.001f;
                samples[cursor + i] = (i % 2 == 0) ? amplitude : -amplitude;
            }

            cursor += length;
        }

        return samples;
    }

    public event EventHandler<AudioLevel>? LevelChanged
    {
        add { }
        remove { }
    }

    public bool IsCapturing { get; private set; }

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
        Stopped.TrySetResult();
        return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
    }

    /// <summary>Completed when a recording was stopped: its audio handed on.</summary>
    public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when a recording was cancelled rather than stopped: its audio let go of.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        Cancelled.TrySetResult();
        return Task.FromResult(new AudioOperationResult(Succeeded: true));
    }

    /// <summary>Whether the controller disposed the capture: the session's disposal reached it.</summary>
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"EnviousWispr-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
