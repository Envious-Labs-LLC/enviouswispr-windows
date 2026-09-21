using EnviousWispr.App.Composition;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Stopping the background work establishes completion, or says that it could not: a stop under a
/// deadline reports what is still running and keeps owning it, and nothing a late loop does after
/// the closure reaches the screen.
/// </summary>
/// <remarks>
/// THE OWNERS ARE THE COMPOSED ONES. The preview and streaming controllers are built by
/// <see cref="RuntimeComposition"/> with the production adapters over fakes at the leaves - an engine
/// that can be held, a capture, a window - so the stop that is proved is the stop the app runs.
/// </remarks>
public sealed class BoundedStopTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PreviewStopWaitsForWorkerExit()
    {
        // THE STOP IS OVER WHEN THE ENGINE IS. The loop is inside the engine, which honours its cancel;
        // the stop then stops the engine - held here - and returns only once that has returned.
        var world = World.Create();
        world.PreviewEngine.HoldPreviews = true;
        world.PreviewEngine.HoldStop = true;
        var session = DictationSessionId.Create();
        world.Session = session;
        await world.Runtime.Preview.StartAsync(session);
        await world.PreviewEngine.PreviewEntered.Task.WaitAsync(Patience);

        var stop = world.Runtime.Preview.StopAsync(Patience);
        await world.PreviewEngine.StopEntered.Task.WaitAsync(Patience);
        Assert.False(stop.IsCompleted, "the stop waits for the engine's own stop");
        world.PreviewEngine.AllowStopExit.SetResult();

        Assert.Equal(StopOutcome.Completed, await stop.WaitAsync(Patience));
        Assert.False(world.Runtime.Preview.IsRunning);
        Assert.Equal(1, world.PreviewEngine.Stops);
        Assert.Contains(AppEventCode.LivePreviewStopped, world.Log.Events);
    }

    [Fact]
    public async Task TimedOutJoinRetainsResourceOwnership()
    {
        // THE ENGINE DOES NOT ANSWER ITS CANCEL. The stop's deadline passes on the clock; the stop
        // says so and keeps everything: the loop, its token source undisposed, the engine unstopped
        // under it. When the engine answers at last the loop ends on its cancelled token, and the
        // next stop completes the work and stops the engine once.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.PreviewEngine.HoldPreviews = true;
        world.PreviewEngine.IgnoreCancel = true;
        world.Engine.Hold = true;
        var session = DictationSessionId.Create();
        world.Session = session;
        world.Capture.Take = FakeAudioCapture.Script((false, 200), (true, 3_000), (false, 1_200), (true, 500));
        await world.Runtime.Preview.StartAsync(session);
        await world.PreviewEngine.PreviewEntered.Task.WaitAsync(Patience);
        // Streaming runs only with the preview off; the switch is flipped for its start alone.
        world.PreviewEnabled = false;
        world.Runtime.Streaming.Start(session);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var registered = clock.Registered;
        var previewStop = world.Runtime.Preview.StopAsync(TimeSpan.FromSeconds(1));
        var streamingStop = world.Runtime.Streaming.StopAsync(TimeSpan.FromSeconds(1));
        await clock.WhenRegistered(registered + 2).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(StopOutcome.StillRunning, await previewStop.WaitAsync(Patience));
        Assert.Equal(StopOutcome.StillRunning, await streamingStop.WaitAsync(Patience));
        Assert.True(world.Runtime.Preview.IsRunning, "the preview loop is still owned");
        Assert.True(world.Runtime.Streaming.IsRunning, "the streaming loop is still owned");
        Assert.Equal(0, world.PreviewEngine.Stops);
        Assert.True(world.PreviewEngine.Token!.Value.IsCancellationRequested, "the loop's token was cancelled");
        Assert.True(world.Engine.Token!.Value.IsCancellationRequested);
        // A TOKEN WHOSE SOURCE WAS DISPOSED THROWS HERE; these do not, so the sources are still owned.
        _ = world.PreviewEngine.Token.Value.WaitHandle;
        _ = world.Engine.Token.Value.WaitHandle;
        Assert.Contains(null, world.View.Previews);

        // A NEW RECORDING WHILE THE OLD LOOPS ARE STILL OWNED gets no preview and no head start: the
        // starts are refused rather than run beside the loops that still hold the engine and the
        // accumulator.
        var next = DictationSessionId.Create();
        world.Session = next;
        world.PreviewEnabled = true;
        await world.Runtime.Preview.StartAsync(next);
        world.PreviewEnabled = false;
        world.Runtime.Streaming.Start(next);
        Assert.Equal(0, world.PreviewEngine.Starts - 1);
        Assert.Contains(AppEventCode.StreamingAbandoned, world.Log.Events);

        world.PreviewEngine.ReleasePreviews();
        world.Engine.Release();

        Assert.Equal(StopOutcome.Completed, await world.Runtime.Preview.StopAsync(Patience).WaitAsync(Patience));
        Assert.Equal(StopOutcome.Completed, await world.Runtime.Streaming.StopAsync(Patience).WaitAsync(Patience));
        Assert.False(world.Runtime.Preview.IsRunning);
        Assert.False(world.Runtime.Streaming.IsRunning);
        Assert.Equal(1, world.PreviewEngine.Stops);
    }

    [Fact]
    public async Task LatePreviewCallbackCannotRenderAfterClosure()
    {
        // THE ENGINE ANSWERS AFTER THE SCREEN CLOSED. The stop ran out of patience with the loop
        // inside the engine; the words the engine hands back afterwards are for a screen that is
        // gone, and the loop checks the closure again at the dispatch and renders nothing.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.PreviewEngine.HoldPreviews = true;
        world.PreviewEngine.IgnoreCancel = true;
        world.PreviewEngine.Words = "late words";
        var session = DictationSessionId.Create();
        world.Session = session;
        await world.Runtime.Preview.StartAsync(session);
        await world.PreviewEngine.PreviewEntered.Task.WaitAsync(Patience);

        var registered = clock.Registered;
        var stop = world.Runtime.Preview.StopAsync(TimeSpan.FromSeconds(1));
        await clock.WhenRegistered(registered + 1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(StopOutcome.StillRunning, await stop.WaitAsync(Patience));
        var cleared = world.View.Previews.Count;

        world.PreviewEngine.ReleasePreviews();
        Assert.Equal(StopOutcome.Completed, await world.Runtime.Preview.StopAsync(Patience).WaitAsync(Patience));

        Assert.DoesNotContain("late words", world.View.Previews);
        Assert.True(cleared >= 1 && world.View.Previews[cleared - 1] is null, "the screen was cleared by the bounded stop");
    }

    [Fact]
    public async Task AFrameQueuedForTheWindowBeforeTheClosureIsNotDrawnAfterIt()
    {
        // THE WINDOW DRAWS LATER THAN IT IS TOLD. A frame handed over while the preview was open sits
        // in the window's queue; the preview closes; the frame is drawn after. It knows its screen
        // is gone and draws nothing, and the clear that closed the screen stands.
        var world = World.Create();
        world.View.DeferDraws = true;
        var session = DictationSessionId.Create();
        world.Session = session;
        await world.Runtime.Preview.StartAsync(session);
        await Eventually(() => world.View.Queued >= 1, "a frame to be queued for the window");

        Assert.Equal(StopOutcome.Completed, await world.Runtime.Preview.StopAsync(Patience));
        world.View.Flush();

        Assert.DoesNotContain("preview words", world.View.Previews);
        Assert.Contains(null, world.View.Previews);
        Assert.Null(world.View.Previews[^1]);
    }

    [Fact]
    public async Task AnEngineStopRefusedOrHeldLeavesTheStopIncompleteAndIsJoinedNotRepeated()
    {
        // THE LOOP IS OVER BUT THE ENGINE IS NOT. A stop the engine refuses - its worker still there -
        // is reported still running with the preview still owned; the next stop asks again and
        // completes once the engine agrees. A stop the engine holds past the deadline is kept as a
        // task and joined by the next stop, not issued a second time beside itself.
        var refused = World.Create();
        refused.PreviewEngine.RefuseStop = true;
        var session = DictationSessionId.Create();
        refused.Session = session;
        await refused.Runtime.Preview.StartAsync(session);
        await refused.PreviewEngine.PreviewEntered.Task.WaitAsync(Patience);

        Assert.Equal(StopOutcome.StillRunning, await refused.Runtime.Preview.StopAsync(Patience));
        Assert.True(refused.Runtime.Preview.IsRunning, "an engine whose worker did not go keeps the preview owned");
        Assert.Equal(1, refused.PreviewEngine.Stops);
        await refused.Runtime.Preview.StartAsync(DictationSessionId.Create());
        Assert.Equal(1, refused.PreviewEngine.Starts);
        refused.PreviewEngine.RefuseStop = false;
        Assert.Equal(StopOutcome.Completed, await refused.Runtime.Preview.StopAsync(Patience));
        Assert.Equal(2, refused.PreviewEngine.Stops);
        Assert.False(refused.Runtime.Preview.IsRunning);

        var clock = new Deterministic.ManualClock();
        var held = World.Create(clock);
        held.PreviewEngine.HoldStop = true;
        held.Session = session;
        await held.Runtime.Preview.StartAsync(session);
        await held.PreviewEngine.PreviewEntered.Task.WaitAsync(Patience);
        var registered = clock.Registered;
        var first = held.Runtime.Preview.StopAsync(TimeSpan.FromSeconds(1));
        await held.PreviewEngine.StopEntered.Task.WaitAsync(Patience);
        await clock.WhenRegistered(registered + 1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(StopOutcome.StillRunning, await first.WaitAsync(Patience));
        Assert.True(held.Runtime.Preview.IsRunning);
        var second = held.Runtime.Preview.StopAsync(Patience);
        await Task.Delay(50);
        Assert.False(second.IsCompleted, "the second stop joins the engine's stop still in flight");
        held.PreviewEngine.AllowStopExit.SetResult();
        Assert.Equal(StopOutcome.Completed, await second.WaitAsync(Patience));
        Assert.Equal(1, held.PreviewEngine.Stops);
        Assert.False(held.Runtime.Preview.IsRunning);
    }

    [Fact]
    public async Task AStopQueuedBehindAHeldStopIsBoundedToo()
    {
        // TWO STOPS, ONE GATE. The first is inside a held engine stop; the second waits for the gate
        // and must not wait past its own deadline to do so.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.PreviewEngine.HoldStop = true;
        var session = DictationSessionId.Create();
        world.Session = session;
        await world.Runtime.Preview.StartAsync(session);
        await world.PreviewEngine.PreviewEntered.Task.WaitAsync(Patience);
        var first = world.Runtime.Preview.StopAsync(Patience);
        await world.PreviewEngine.StopEntered.Task.WaitAsync(Patience);

        var registered = clock.Registered;
        var second = world.Runtime.Preview.StopAsync(TimeSpan.FromSeconds(1));
        await clock.WhenRegistered(registered + 1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(StopOutcome.StillRunning, await second.WaitAsync(Patience));
        Assert.False(first.IsCompleted);
        world.PreviewEngine.AllowStopExit.SetResult();
        Assert.Equal(StopOutcome.Completed, await first.WaitAsync(Patience));
    }

    [Fact]
    public async Task TimerStopDoesNotAwaitItsOwnQueuedCommand()
    {
        // THE COMMAND A TIMER POSTS IS QUEUED, NOT RUN INSIDE THE POST, and it is that command that
        // stops the timer. The timers' stops join the loop that posted - which returned as soon as
        // it had - and never the command, which here is never even started; the stops complete.
        var clock = new Deterministic.ManualClock();
        var log = new RecordingLogger();
        var effects = new QueueingTimerEffects();
        var autoStop = new AutoStopMonitor(effects, log, clock);
        var watchdog = new RecordingWatchdog(effects, clock);
        var session = DictationSessionId.Create();
        effects.Audio = new FakeAudioCapture { Take = FakeAudioCapture.SpeechThenSilence(1_000, 3_000), SessionSource = () => session };
        autoStop.Start(session, DictationPreferences.Default with { RecordingMode = DictationRecordingMode.Toggle, AutoStopEnabled = true, AutoStopSilenceSeconds = 2 });
        watchdog.Start(session, TimeSpan.FromSeconds(5));

        await clock.WhenRegistered(2).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await effects.Posted.Task.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(5));
        await effects.TimedOut.Task.WaitAsync(Patience);

        // The queued commands are never run; the stops complete regardless.
        Assert.Equal(StopOutcome.Completed, await autoStop.StopAsync(Patience).WaitAsync(Patience));
        Assert.Equal(StopOutcome.Completed, await watchdog.StopAsync(Patience).WaitAsync(Patience));
        Assert.False(autoStop.IsRunning);
        Assert.False(watchdog.IsArmed);
        Assert.Equal([PushToTalkSignal.Released], effects.Queued);
        Assert.Equal([session], effects.TimedOutSessions);
        Assert.False(effects.Command.Task.IsCompleted, "the command the timers queued was never awaited by their stops");
    }

    /// <summary>Waits, briefly, for something a loop will have done.</summary>
    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    private sealed class World
    {
        public required SessionRuntime Runtime { get; init; }

        public required FakePreviewEngine PreviewEngine { get; init; }

        public required FakeEngine Engine { get; init; }

        public required FakeAudioCapture Capture { get; init; }

        public required FakeRuntimeView View { get; init; }

        public required RecordingLogger Log { get; init; }

        public DictationSessionId? Session { get; set; }

        /// <summary>The preview switch as the shell would read it; streaming stands down while it is on.</summary>
        public bool PreviewEnabled { get; set; } = true;

        public static World Create(TimeProvider? clock = null)
        {
            clock ??= TimeProvider.System;
            var log = new RecordingLogger();
            var previewEngine = new FakePreviewEngine();
            var engine = new FakeEngine("hello world");
            var view = new FakeRuntimeView();
            var capture = new FakeAudioCapture();
            World? world = null;
            capture.SessionSource = () => world?.Session;
            var runtime = RuntimeComposition.Compose(new RuntimeCompositionParts(
                new FakeRecoveryStore(),
                new FakeHistoryStore(),
                PatientPipeline.Create(),
                new FakeAdmission(),
                log,
                new RuntimeShell(
                    view,
                    LivePreviewEnabled: () => world?.PreviewEnabled ?? true,
                    History: () => HistoryPreferences.Default,
                    CustomWords: () => [],
                    Audio: () => capture,
                    Engine: () => engine,
                    PreviewEngine: () => previewEngine,
                    PreviewUnavailableReason: () => null,
                    RecordingSessionId: () => world?.Session,
                    Coordinator: () => null,
                    Leaving: () => false),
                clock));
            world = new World
            {
                Runtime = runtime,
                PreviewEngine = previewEngine,
                Engine = engine,
                Capture = capture,
                View = view,
                Log = log,
            };
            return world;
        }
    }

    /// <summary>Timer effects that queue what they are handed, as the composed queue does, and never run it.</summary>
    private sealed class QueueingTimerEffects : IRecordingTimerEffects
    {
        public IAudioSnapshotSource? Audio { get; set; }

        public List<PushToTalkSignal> Queued { get; } = [];

        public List<DictationSessionId> TimedOutSessions { get; } = [];

        /// <summary>The command the queue would run: never completed here.</summary>
        public TaskCompletionSource Command { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TimedOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Post(PushToTalkSignal signal)
        {
            Queued.Add(signal);
            _ = Command.Task;
            Posted.TrySetResult();
        }

        public void RecordingTimedOut(DictationSessionId sessionId)
        {
            TimedOutSessions.Add(sessionId);
            _ = Command.Task;
            TimedOut.TrySetResult();
        }
    }

    /// <summary>The window as the tests see it: a queue of frames drawn later, each asked at its draw whether it is still current - what the shell's dispatcher does.</summary>
    private sealed class FakeRuntimeView : IRuntimeView
    {
        private readonly List<LivePreviewFrame?> _queued = [];

        public List<string?> Previews { get; } = [];

        /// <summary>Whether draws are held in the queue until <see cref="Flush"/>; off, they are drawn at once.</summary>
        public bool DeferDraws { get; set; }

        public int Queued
        {
            get
            {
                lock (Previews)
                {
                    return _queued.Count;
                }
            }
        }

        public void ShowPreview(LivePreviewFrame? frame)
        {
            lock (Previews)
            {
                if (DeferDraws)
                {
                    _queued.Add(frame);
                }
                else
                {
                    Draw(frame);
                }
            }
        }

        /// <summary>Draws what was queued, in order, as the window's dispatcher would when it gets to it.</summary>
        public void Flush()
        {
            lock (Previews)
            {
                foreach (var frame in _queued)
                {
                    Draw(frame);
                }

                _queued.Clear();
            }
        }

        private void Draw(LivePreviewFrame? frame)
        {
            if (frame is null || frame.IsCurrent())
            {
                Previews.Add(frame?.Text);
            }
        }

        public void ShowRecoveredText(RecoveryTextLoadResult result)
        {
        }

        public void ClearRecoveredText()
        {
        }

        public void NotifyHistoryChanged()
        {
        }

        public void ShowMainWindow()
        {
        }
    }

    /// <summary>A preview engine whose passes can be held - honouring the cancel, or not - and whose stop can be held.</summary>
    private sealed class FakePreviewEngine : ILivePreviewEngine
    {
        private readonly TaskCompletionSource _releasePreviews = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string EngineId => "preview";

        public string Words { get; set; } = "preview words";

        public bool HoldPreviews { get; set; }

        public bool IgnoreCancel { get; set; }

        public bool HoldStop { get; set; }

        /// <summary>Whether the stop answers that the worker did not go, as the production adapter does when its exit was not observed.</summary>
        public bool RefuseStop { get; set; }

        public int Stops { get; private set; }

        public CancellationToken? Token { get; private set; }

        public TaskCompletionSource PreviewEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleasePreviews() => _releasePreviews.TrySetResult();

        public int Starts { get; private set; }

        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            return Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Ready));
        }

        public async Task<LivePreviewUpdate> PreviewAsync(AudioSnapshot snapshot, long sequence, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            PreviewEntered.TrySetResult();
            if (HoldPreviews)
            {
                if (IgnoreCancel)
                {
                    await _releasePreviews.Task;
                }
                else
                {
                    await _releasePreviews.Task.WaitAsync(cancellationToken);
                }
            }

            return new LivePreviewUpdate(snapshot.SessionId.Value, sequence, true, Words);
        }

        public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            StopEntered.TrySetResult();
            if (HoldStop)
            {
                await AllowStopExit.Task;
            }

            return RefuseStop
                ? new RuntimeWorkerResult(false, RuntimeWorkerState.Faulted, new AppError(AppErrorCode.RuntimeWorkerFailed, AppErrorStage.RuntimeWorker, CanRetry: true))
                : new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A speech engine that can be held ignoring its cancel, and released by the test.</summary>
    private sealed class FakeEngine(string spoken) : ITranscriptionEngine
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string EngineId => "whisper";

        public bool Hold { get; set; }

        public CancellationToken? Token { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Entered.TrySetResult();
            if (Hold)
            {
                await _released.Task;
            }

            return new Transcript(audio.SessionId, spoken, EngineId, DetectedLanguage: "en");
        }
    }

    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<AppEventCode> _events = [];

        public IReadOnlyList<AppEventCode> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Write(AppLogEntry entry)
        {
            lock (_events)
            {
                _events.Add(entry.Event);
            }
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
        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult([], HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class FakeAudioCapture : IAudioSnapshotSource
    {
        public float[] Take { get; set; } = new float[16_000];

        public Func<DictationSessionId?>? SessionSource { get; set; }

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) =>
            SessionSource?.Invoke() is { } session ? new AudioSnapshot(session, Take, 16_000, 1) : null;

        public static float[] SpeechThenSilence(int speechMilliseconds, int silenceMilliseconds) =>
            Script((true, speechMilliseconds), (false, silenceMilliseconds));

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
    }
}
