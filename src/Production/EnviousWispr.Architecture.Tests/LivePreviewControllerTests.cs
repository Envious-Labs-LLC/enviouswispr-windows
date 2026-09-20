using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A live preview from the first snapshot to the last, driven through a fake engine that can be held
/// mid-pass, a capture that says how much audio there is, and a surface that keeps what it was shown.
/// Every wait is on a signal the code under test raises, never on the clock.
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
        await world.Effects.Shown.Task.WaitAsync(Patience);

        Assert.Equal([(world.Session, "hello there")], world.Effects.Previews);
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
    public async Task StopWhileTheEngineIsMidPassCancelsThePassAndWaitsForIt()
    {
        var world = World.Build();
        world.Engine.HoldPasses = true;

        await world.Controller.StartAsync(world.Session);
        await world.Engine.PassStarted.Task.WaitAsync(Patience);

        var stop = world.Controller.StopAsync();
        // The stop cannot finish while the pass is still running: it waits for the loop, and the
        // loop is inside the engine.
        Assert.False(stop.IsCompleted);
        await stop.WaitAsync(Patience);

        Assert.True(world.Engine.LastPassToken.IsCancellationRequested);
        Assert.Empty(world.Effects.Previews);
        Assert.Equal([AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewStopped], world.Log.Codes);
        Assert.Equal(1, world.Engine.Stops);
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
    public async Task TooLittleAudioWaitsWithoutAPassAndStopsPromptly()
    {
        var world = World.Build();
        world.Audio.Samples = 100;

        await world.Controller.StartAsync(world.Session);
        await world.Audio.Sampled.Task.WaitAsync(Patience);
        await world.Controller.StopAsync().WaitAsync(Patience);

        Assert.Equal(0, world.Engine.Passes);
        Assert.Equal([AppEventCode.LivePreviewStarted, AppEventCode.LivePreviewStopped], world.Log.Codes);
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
        await world.Controller.StopAsync().WaitAsync(Patience);
    }

    [Fact]
    public async Task StopThenStartRunsAFreshLoopWithItsSequenceRestarted()
    {
        var world = World.Build();
        world.Engine.NextText = "one";

        await world.Controller.StartAsync(world.Session);
        await world.Effects.Shown.Task.WaitAsync(Patience);
        await world.Controller.StopAsync();

        var second = DictationSessionId.Create();
        world.Effects.Shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Audio.Session = second;
        world.Engine.NextText = "two";
        await world.Controller.StartAsync(second);
        await world.Effects.Shown.Task.WaitAsync(Patience);
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
        await world.Effects.Shown.Task.WaitAsync(Patience);
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

        await world.Controller.DisposeAsync().AsTask().WaitAsync(Patience);

        Assert.False(world.Controller.IsRunning);
        Assert.Equal(1, world.Engine.Stops);
        Assert.Equal(AppEventCode.LivePreviewStopped, world.Log.Codes.Last());
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
        public required DictationSessionId Session { get; init; }

        public static World Build()
        {
            var session = DictationSessionId.Create();
            var engine = new FakeEngine();
            var audio = new FakeAudio { Session = session };
            var effects = new FakeEffects { Engine = engine, Audio = audio };
            var log = new FakeLogger();
            return new World
            {
                Controller = new LivePreviewController(effects, log, TimeProvider.System),
                Effects = effects,
                Engine = engine,
                Audio = audio,
                Log = log,
                Session = session,
            };
        }
    }

    private sealed class FakeEffects : ILivePreviewEffects
    {
        public bool Enabled { get; set; } = true;
        public ILivePreviewEngine? Engine { get; set; }
        public AppErrorCode? EngineUnavailableReason { get; set; }
        public IAudioSnapshotSource? Audio { get; set; }
        public DictationSessionId? RecordingSessionId { get; set; }
        public List<(DictationSessionId Session, string Text)> Previews { get; } = [];
        public int Clears { get; private set; }
        public TaskCompletionSource Shown { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ShowPreview(DictationSessionId sessionId, string text)
        {
            Previews.Add((sessionId, text));
            Shown.TrySetResult();
        }

        public void ClearPreview() => Clears++;
    }

    private sealed class FakeAudio : IAudioSnapshotSource
    {
        public DictationSessionId Session { get; set; }
        public int Samples { get; set; } = 16_000;
        public TaskCompletionSource Sampled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
        {
            Sampled.TrySetResult();
            return new AudioSnapshot(Session, new float[Samples], 16_000, 1);
        }
    }

    private sealed class FakeEngine : ILivePreviewEngine
    {
        public string EngineId => "fake";
        public RuntimeWorkerResult StartResult { get; set; } = new(true, RuntimeWorkerState.Ready);
        public string NextText { get; set; } = "";
        public AppError? NextError { get; set; }
        public Exception? ThrowOnPass { get; set; }
        public Guid? TagUpdatesWith { get; set; }
        public bool HoldPasses { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Passes { get; private set; }
        public List<long> Sequences { get; } = [];
        public CancellationToken LastPassToken { get; private set; }
        public TaskCompletionSource PassStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PassFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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

        public Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            return Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped));
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
}
