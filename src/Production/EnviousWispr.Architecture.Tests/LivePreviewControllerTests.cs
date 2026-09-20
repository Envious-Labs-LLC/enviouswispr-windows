using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A live preview from the first snapshot to the last, driven through a fake engine that can be held
/// mid-pass or mid-stop, a capture that says how much audio there is, a surface that keeps what it was
/// shown, and a clock that moves only when a test moves it. Every wait is on a barrier the code under
/// test crosses, never on the wall clock; the ten-second guards exist so a broken controller fails
/// rather than hangs.
/// </summary>
public sealed class LivePreviewControllerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WhenLivePreviewIsOffNothingStartsAndNothingIsWritten()
    {
        var world = World.Build();
        world.Effects.Enabled = false;

        await world.Controller.StartAsync(world.Session);

        Assert.False(world.Controller.IsRunning);
        Assert.Equal(0, world.Engine.Starts);
        Assert.Empty(world.Log.Entries);
    }

    [Fact]
    public async Task AMissingEngineIsReportedWithTheReasonTheShellHeld()
    {
        var world = World.Build();
        world.Effects.Engine = null;
        world.Effects.EngineUnavailableReason = AppErrorCode.ModelPackUnavailable;

        await world.Controller.StartAsync(world.Session);

        var line = Assert.Single(world.Log.Entries);
        Assert.Equal(AppEventCode.LivePreviewFailed, line.Event);
        Assert.Equal(AppFailureCategory.AsrUnavailable, line.Failure);
        Assert.Equal(AppErrorCode.ModelPackUnavailable, line.ErrorCode);
        Assert.Equal(world.Session.Value, world.Log.Dictations[0]);
        Assert.False(world.Controller.IsRunning);
    }

    [Fact]
    public async Task AMissingEngineWithNoReasonIsTheRuntimeProvider()
    {
        var world = World.Build();
        world.Effects.Engine = null;

        await world.Controller.StartAsync(world.Session);

        var line = Assert.Single(world.Log.Entries);
        Assert.Equal(AppFailureCategory.RuntimeProvider, line.Failure);
        Assert.Equal(AppErrorCode.RuntimeProviderUnavailable, line.ErrorCode);
    }

    [Fact]
    public async Task ACaptureThatCannotBeSampledIsAudioUnavailable()
    {
        var world = World.Build();
        world.Effects.Audio = null;

        await world.Controller.StartAsync(world.Session);

        var line = Assert.Single(world.Log.Entries);
        Assert.Equal(AppEventCode.LivePreviewFailed, line.Event);
        Assert.Equal(AppFailureCategory.AudioUnavailable, line.Failure);
        Assert.Equal(AppErrorCode.AudioDeviceUnavailable, line.ErrorCode);
        Assert.Equal(0, world.Engine.Starts);
    }

    [Fact]
    public async Task AnEngineThatRefusesToStartLeavesNoLoopAndSaysWhy()
    {
        var world = World.Build();
        world.Engine.StartResult = new RuntimeWorkerResult(
            false,
            RuntimeWorkerState.Faulted,
            new AppError(AppErrorCode.RuntimeWorkerFailed, AppErrorStage.RuntimeWorker, CanRetry: true));

        await world.Controller.StartAsync(world.Session);

        var line = Assert.Single(world.Log.Entries);
        Assert.Equal(AppEventCode.LivePreviewFailed, line.Event);
        Assert.Equal(AppFailureCategory.RuntimeWorker, line.Failure);
        Assert.False(world.Controller.IsRunning);
        Assert.Equal(0, world.Engine.Passes);
    }

    [Fact]
    public async Task AnUpdateReachesTheScreenTaggedWithItsDictationAndStopClearsIt()
    {
        var world = World.Build();
        world.Engine.NextText = "hello there";

        await world.Controller.StartAsync(world.Session);
        Assert.True(world.Controller.IsRunning);
        await world.Effects.WhenShown(1).WaitAsync(Patience);

        Assert.Equal([(world.Session, "hello there")], world.Effects.Previews);
        Assert.Equal(TimeSpan.FromSeconds(20), world.Audio.LastWindow);
        await world.Controller.StopAsync();

        Assert.False(world.Controller.IsRunning);
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(1, world.Effects.Clears);
        Assert.Equal(
            [AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewUpdated, AppEventCode.LivePreviewStopped],
            world.Log.Codes);
        // The start and the update are joined to the dictation that opened them. The stop is joined
        // only to what the shell says is recording, and this shell says nothing is: see the test below.
        Assert.Equal([world.Session.Value, world.Session.Value, null], world.Log.Dictations);
        Assert.Equal([1L], world.Engine.Sequences);
    }

    [Fact]
    public async Task StopWhileTheEngineIsMidPassCancelsThePassAndWaitsForItToLeave()
    {
        var world = World.Build();
        world.Engine.HoldPasses = true;

        await world.Controller.StartAsync(world.Session);
        await world.Engine.PassStarted.Task.WaitAsync(Patience);

        var stop = world.Controller.StopAsync();
        await world.Engine.CancellationObserved.Task.WaitAsync(Patience);

        // The engine has seen the cancellation but has not left the pass. Nothing that follows the
        // loop may have happened yet: not the engine's stop, not the clear, not the line in the log.
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, world.Engine.Stops);
        Assert.Equal(0, world.Effects.Clears);
        Assert.Equal([AppEventCode.LivePreviewStarted], world.Log.Codes);

        world.Engine.AllowPassExit.SetResult();
        await stop.WaitAsync(Patience);

        Assert.True(world.Engine.LastPassToken.IsCancellationRequested);
        Assert.Empty(world.Effects.Previews);
        Assert.Equal([AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewStopped], world.Log.Codes);
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(1, world.Effects.Clears);
    }

    [Fact]
    public async Task AStartQueuedBehindAStopWaitsForTheEngineToFinishStopping()
    {
        var world = World.Build();
        world.Engine.NextText = "first";
        await world.Controller.StartAsync(world.Session);
        await world.Effects.WhenShown(1).WaitAsync(Patience);

        world.Engine.HoldStops = true;
        var stop = world.Controller.StopAsync();
        await world.Engine.StopStarted.Task.WaitAsync(Patience);

        var second = DictationSessionId.Create();
        world.Audio.Session = second;
        world.Engine.NextText = "second";
        var start = world.Controller.StartAsync(second);

        // The stop holds the gate while the engine is still stopping, so the start is parked before
        // it can touch the engine.
        Assert.False(start.IsCompleted);
        Assert.Equal(1, world.Engine.Starts);
        Assert.Equal(0, world.Effects.Clears);

        world.Engine.AllowStopExit.SetResult();
        await stop.WaitAsync(Patience);
        await start.WaitAsync(Patience);
        await world.Effects.WhenShown(2).WaitAsync(Patience);
        await world.Controller.StopAsync().WaitAsync(Patience);

        Assert.Equal(2, world.Engine.Starts);
        Assert.Equal([(world.Session, "first"), (second, "second")], world.Effects.Previews);
        Assert.Equal(
            [
                AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewUpdated, AppEventCode.LivePreviewStopped,
                AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewUpdated, AppEventCode.LivePreviewStopped,
            ],
            world.Log.Codes);
    }

    [Fact]
    public async Task AFailedPassEndsTheLoopWithoutAWordOnScreen()
    {
        var world = World.Build();
        world.Engine.NextError = new AppError(AppErrorCode.TranscriptionFailed, AppErrorStage.RuntimeWorker, CanRetry: true);

        await world.Controller.StartAsync(world.Session);
        await world.Controller.Loop!.WaitAsync(Patience);

        Assert.Empty(world.Effects.Previews);
        Assert.Equal(1, world.Engine.Passes);
        Assert.Equal([AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewFailed], world.Log.Codes);
        Assert.Equal(AppFailureCategory.AsrUnavailable, world.Log.Entries[1].Failure);

        await world.Controller.StopAsync();

        // The loop had been started, so its stop is still recorded even though it ended on its own.
        Assert.Equal(AppEventCode.LivePreviewStopped, world.Log.Codes.Last());
        Assert.Equal(1, world.Effects.Clears);
    }

    [Fact]
    public async Task AnUpdateTaggedWithAnotherDictationIsDropped()
    {
        var world = World.Build();
        world.Engine.NextText = "not yours";
        world.Engine.TagUpdatesWith = Guid.NewGuid();

        await world.Controller.StartAsync(world.Session);
        await world.Engine.PassFinished.Task.WaitAsync(Patience);
        await world.Controller.StopAsync();

        Assert.Empty(world.Effects.Previews);
        Assert.Contains(AppEventCode.LivePreviewUpdated, world.Log.Codes);
    }

    [Fact]
    public async Task TooLittleAudioIsAskedAgainAQuarterSecondLaterUntilThereIsEnough()
    {
        var world = World.Build();
        world.Audio.Samples = 7_999;

        await world.Controller.StartAsync(world.Session);

        // One snapshot was taken at once and found short; the re-ask is a quarter second out, not
        // the cadence.
        Assert.Equal(1, world.Audio.Snapshots);
        Assert.Equal(0, world.Engine.Passes);
        world.Clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Equal(1, world.Audio.Snapshots);
        world.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, world.Audio.Snapshots);
        Assert.Equal(0, world.Engine.Passes);

        world.Audio.Samples = 8_000;
        world.Clock.Advance(TimeSpan.FromMilliseconds(250));
        await world.Effects.WhenShown(1).WaitAsync(Patience);

        Assert.Equal(1, world.Engine.Passes);
        Assert.Equal(TimeSpan.FromSeconds(20), world.Audio.LastWindow);
        await world.Controller.StopAsync().WaitAsync(Patience);
        Assert.Equal([AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewUpdated, AppEventCode.LivePreviewStopped], world.Log.Codes);
    }

    [Fact]
    public async Task TheCadenceIsAFloorOnTheGapBetweenPassesAndTheSequenceCounts()
    {
        var world = World.Build();
        world.Engine.NextText = "words";

        await world.Controller.StartAsync(world.Session);
        await world.Effects.WhenShown(1).WaitAsync(Patience);

        // A pass that cost nothing waits the whole interval, and not a tick less.
        world.Clock.Advance(LivePreviewCadence.Interval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, world.Engine.Passes);
        world.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await world.Effects.WhenShown(2).WaitAsync(Patience);

        Assert.Equal([1L, 2L], world.Engine.Sequences);
        Assert.Equal(2, world.Effects.Previews.Count);
        await world.Controller.StopAsync().WaitAsync(Patience);
    }

    [Fact]
    public async Task APassSlowerThanTheIntervalIsFollowedAtOnce()
    {
        var world = World.Build();
        world.Engine.NextText = "words";
        world.Engine.HoldPasses = true;

        await world.Controller.StartAsync(world.Session);
        await world.Engine.PassStarted.Task.WaitAsync(Patience);

        // Three seconds pass inside the engine; the cadence owes nothing after that, so the second
        // pass follows without the clock moving again.
        world.Clock.Advance(TimeSpan.FromSeconds(3));
        world.Engine.AllowPassExit.SetResult();
        await world.Effects.WhenShown(2).WaitAsync(Patience);

        Assert.Equal([1L, 2L], world.Engine.Sequences);
        Assert.Equal(3_000, world.Log.Entries[1].ElapsedMilliseconds);
        Assert.Equal(AppEventCode.LivePreviewUpdated, world.Log.Entries[1].Event);
        await world.Controller.StopAsync().WaitAsync(Patience);
    }

    [Fact]
    public async Task ASecondStartFindsTheFirstLoopAndDoesNotStartTheEngineAgain()
    {
        var world = World.Build();
        world.Engine.HoldPasses = true;

        await world.Controller.StartAsync(world.Session);
        await world.Controller.StartAsync(world.Session);

        Assert.Equal(1, world.Engine.Starts);
        Assert.Equal([AppEventCode.LivePreviewStarted], world.Log.Codes);
        world.Engine.AllowPassExit.SetResult();
        await world.Controller.StopAsync().WaitAsync(Patience);
    }

    [Fact]
    public async Task StopThenStartRunsAFreshLoopWithItsSequenceRestarted()
    {
        var world = World.Build();
        world.Engine.NextText = "one";

        await world.Controller.StartAsync(world.Session);
        await world.Effects.WhenShown(1).WaitAsync(Patience);
        await world.Controller.StopAsync();

        var second = DictationSessionId.Create();
        world.Audio.Session = second;
        world.Engine.NextText = "two";
        await world.Controller.StartAsync(second);
        await world.Effects.WhenShown(2).WaitAsync(Patience);
        await world.Controller.StopAsync();

        Assert.Equal([(world.Session, "one"), (second, "two")], world.Effects.Previews);
        Assert.Equal([1L, 1L], world.Engine.Sequences);
        Assert.Equal(2, world.Engine.Starts);
        Assert.Equal(2, world.Engine.Stops);
    }

    [Fact]
    public async Task StopWithNothingRunningStillStopsTheEngineAndClearsTheSurfaceButWritesNothing()
    {
        var world = World.Build();

        await world.Controller.StopAsync();

        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(1, world.Effects.Clears);
        Assert.Empty(world.Log.Entries);
    }

    [Fact]
    public async Task StopIsJoinedToTheRecordingTheShellReports()
    {
        var world = World.Build();
        world.Engine.NextText = "hello";
        await world.Controller.StartAsync(world.Session);
        await world.Effects.WhenShown(1).WaitAsync(Patience);
        world.Effects.RecordingSessionId = world.Session;

        await world.Controller.StopAsync();

        Assert.Equal(world.Session.Value, world.Log.Dictations.Last());
    }

    [Fact]
    public async Task DisposeStopsTheLoopAndReleasesTheEngine()
    {
        var world = World.Build();
        world.Engine.HoldPasses = true;
        await world.Controller.StartAsync(world.Session);
        await world.Engine.PassStarted.Task.WaitAsync(Patience);

        var dispose = world.Controller.DisposeAsync().AsTask();
        await world.Engine.CancellationObserved.Task.WaitAsync(Patience);
        world.Engine.AllowPassExit.SetResult();
        await dispose.WaitAsync(Patience);

        Assert.False(world.Controller.IsRunning);
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(AppEventCode.LivePreviewStopped, world.Log.Codes.Last());
    }

    [Fact]
    public async Task DisposeAfterTheShellHasStoppedAndDroppedTheEngineClearsAgainAndNothingElse()
    {
        // The shell's shutdown: stop the preview, dispose and drop the engine, then dispose the
        // controller where its gate used to be disposed. The second stop finds nothing running and
        // no engine; it clears the surface once more and writes nothing. A second dispose is a no-op.
        var world = World.Build();
        world.Engine.NextText = "hello";
        await world.Controller.StartAsync(world.Session);
        await world.Effects.WhenShown(1).WaitAsync(Patience);
        await world.Controller.StopAsync();
        world.Effects.Engine = null;

        await world.Controller.DisposeAsync();
        await world.Controller.DisposeAsync();

        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(2, world.Effects.Clears);
        Assert.Equal(
            [AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewUpdated, AppEventCode.LivePreviewStopped],
            world.Log.Codes);
    }

    [Fact]
    public async Task AnEngineThatThrowsEndsTheLoopAsARuntimeWorkerFailure()
    {
        var world = World.Build();
        world.Engine.ThrowOnPass = new InvalidOperationException("worker gone");

        await world.Controller.StartAsync(world.Session);
        await world.Controller.Loop!.WaitAsync(Patience);

        Assert.Equal([AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewFailed], world.Log.Codes);
        Assert.Equal(AppFailureCategory.RuntimeWorker, world.Log.Entries[1].Failure);
        await world.Controller.StopAsync();
    }

    private sealed class World
    {
        public required LivePreviewController Controller { get; init; }
        public required FakeEffects Effects { get; init; }
        public required FakeEngine Engine { get; init; }
        public required FakeAudio Audio { get; init; }
        public required FakeLogger Log { get; init; }
        public required ManualClock Clock { get; init; }
        public required DictationSessionId Session { get; init; }

        public static World Build()
        {
            var session = DictationSessionId.Create();
            var engine = new FakeEngine();
            var audio = new FakeAudio { Session = session };
            var effects = new FakeEffects { Engine = engine, Audio = audio };
            var log = new FakeLogger();
            var clock = new ManualClock();
            return new World
            {
                Controller = new LivePreviewController(effects, log, clock),
                Effects = effects,
                Engine = engine,
                Audio = audio,
                Log = log,
                Clock = clock,
                Session = session,
            };
        }
    }

    private sealed class FakeEffects : ILivePreviewEffects
    {
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

        public bool Enabled { get; set; } = true;
        public ILivePreviewEngine? Engine { get; set; }
        public AppErrorCode? EngineUnavailableReason { get; set; }
        public IAudioSnapshotSource? Audio { get; set; }
        public DictationSessionId? RecordingSessionId { get; set; }
        public List<(DictationSessionId Session, string Text)> Previews { get; } = [];
        public int Clears { get; private set; }

        /// <summary>Completes once at least <paramref name="count"/> previews have been shown.</summary>
        public Task WhenShown(int count)
        {
            if (Previews.Count >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task;
        }

        public void ShowPreview(DictationSessionId sessionId, string text)
        {
            Previews.Add((sessionId, text));
            foreach (var (count, reached) in _waiters.Where(waiter => waiter.Count <= Previews.Count).ToArray())
            {
                reached.TrySetResult();
            }
        }

        public void ClearPreview() => Clears++;
    }

    private sealed class FakeAudio : IAudioSnapshotSource
    {
        public DictationSessionId Session { get; set; }
        public int Samples { get; set; } = 16_000;
        public int Snapshots { get; private set; }
        public TimeSpan LastWindow { get; private set; }

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
        {
            Snapshots++;
            LastWindow = maximumDuration;
            return new AudioSnapshot(Session, new float[Samples], 16_000, 1);
        }
    }

    /// <summary>
    /// An engine whose pass and stop can each be held on a barrier, so a test decides when they
    /// leave rather than the scheduler. A held pass that is cancelled says so and then waits to be
    /// let out before it throws, which is the interval a real worker spends acknowledging a cancel.
    /// </summary>
    private sealed class FakeEngine : ILivePreviewEngine
    {
        public string EngineId => "fake";
        public RuntimeWorkerResult StartResult { get; set; } = new(true, RuntimeWorkerState.Ready);
        public string NextText { get; set; } = "";
        public AppError? NextError { get; set; }
        public Exception? ThrowOnPass { get; set; }
        public Guid? TagUpdatesWith { get; set; }
        public bool HoldPasses { get; set; }
        public bool HoldStops { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Passes { get; private set; }
        public List<long> Sequences { get; } = [];
        public CancellationToken LastPassToken { get; private set; }
        public TaskCompletionSource PassStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PassFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowPassExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            return Task.FromResult(StartResult);
        }

        public async Task<LivePreviewUpdate> PreviewAsync(AudioSnapshot snapshot, long sequence, CancellationToken cancellationToken = default)
        {
            Passes++;
            Sequences.Add(sequence);
            LastPassToken = cancellationToken;
            PassStarted.TrySetResult();
            if (HoldPasses)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                await Task.WhenAny(AllowPassExit.Task, cancelled.Task);
                if (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult();
                    await AllowPassExit.Task;
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            if (ThrowOnPass is not null)
            {
                throw ThrowOnPass;
            }

            var update = NextError is { } error
                ? new LivePreviewUpdate(snapshot.SessionId.Value, sequence, false, "", Error: error)
                : new LivePreviewUpdate(TagUpdatesWith ?? snapshot.SessionId.Value, sequence, true, NextText);
            PassFinished.TrySetResult();
            return update;
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
        public List<AppLogEntry> Entries { get; } = [];
        public List<Guid?> Dictations { get; } = [];

        public IEnumerable<AppEventCode> Codes => Entries.Select(entry => entry.Event);

        public void Write(AppLogEntry entry)
        {
            Entries.Add(entry);
            Dictations.Add(DictationScope.Current);
        }
    }

    /// <summary>
    /// A clock that moves only when told to. `Task.Delay` on it becomes a timer this clock owns, fired
    /// in due order as time is advanced, so the loop's quarter-second re-ask and its cadence are
    /// crossed by a test in one call rather than waited for.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_lock)
            {
                return _ticks;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return Start.AddTicks(_ticks);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = GetTimestamp() + by.Ticks;
            while (true)
            {
                ManualTimer? due;
                lock (_lock)
                {
                    due = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                    if (due is null)
                    {
                        _ticks = target;
                        break;
                    }

                    _ticks = due.Due;
                    _timers.Remove(due);
                }

                due.Fire();
            }
        }

        private void Schedule(ManualTimer timer, long due)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
                timer.Due = due;
                _timers.Add(timer);
            }
        }

        private void Cancel(ManualTimer timer)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    clock.Cancel(this);
                }
                else
                {
                    clock.Schedule(this, clock.GetTimestamp() + dueTime.Ticks);
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => clock.Cancel(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
