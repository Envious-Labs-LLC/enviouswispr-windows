using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The order the four things beside a recording start and stop in, proved on the real controllers
/// over fakes at their leaves: the watchdog is armed first; the preview's start does not wait for its
/// worker; streaming and the auto-stop stop before the preview does; the watchdog stops on its own,
/// first, when a terminal arrives.
/// </summary>
public sealed class SessionBackgroundWorkTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DictationPreferences Toggle = DictationPreferences.Default with
    {
        RecordingMode = DictationRecordingMode.Toggle,
        AutoStopEnabled = true,
    };

    [Fact]
    public async Task ARecordingStartsAllFourAndTheWatchdogIsArmedFirstWithoutWaitingForTheWorker()
    {
        var world = World.Build();
        world.Engine.HoldStarts = true;
        var session = DictationSessionId.Create();

        var start = world.Work.StartAsync(session, new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), Toggle));

        // THE WATCHDOG IS THE FIRST THING ASKED - armed in the synchronous prefix, before the first
        // await - and the start does not wait on a worker that has not answered: every loop is
        // running while the preview engine's start is still held.
        Assert.True(world.Watchdog.IsArmed, "the watchdog is armed before anything is awaited");
        await start.WaitAsync(Patience);
        await world.Engine.StartStarted.Task.WaitAsync(Patience);
        Assert.True(world.Watchdog.IsArmed);
        Assert.True(world.Preview.IsRunning);
        Assert.True(world.AutoStop.IsRunning);
        Assert.True(world.Streaming.IsRunning);
        Assert.Equal(0, world.Engine.Starts);

        world.Engine.AllowStartExit.SetResult();
        await world.Work.StopWatchdogAsync();
        await world.Work.StopAsync();
        await world.DisposeAsync();
    }

    [Fact]
    public async Task StoppingEndsStreamingAndTheAutoStopBeforeThePreviewHasFinishedStopping()
    {
        var world = World.Build();
        var session = DictationSessionId.Create();
        await world.Work.StartAsync(session, new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), Toggle));
        await world.Engine.StartStarted.Task.WaitAsync(Patience);
        world.Engine.HoldStops = true;

        var stop = world.Work.StopAsync();
        await world.Engine.StopStarted.Task.WaitAsync(Patience);

        // AT THE INSTANT THE PREVIEW'S WORKER WAS ASKED TO STOP, streaming and the auto-stop were
        // already gone - the order: the head start first, then the watcher, then the slowest thing
        // last - and the stop as a whole waits on that worker.
        Assert.Equal((false, false), world.Engine.LoopsRunningWhenStopStarted);
        Assert.False(stop.IsCompleted);
        Assert.True(world.Watchdog.IsArmed, "the watchdog is not part of this stop; a terminal stops it separately, first");

        world.Engine.AllowStopExit.SetResult();
        await stop.WaitAsync(Patience);
        Assert.False(world.Preview.IsRunning);
        await world.Work.StopWatchdogAsync();
        Assert.False(world.Watchdog.IsArmed);
        await world.DisposeAsync();
    }

    [Fact]
    public async Task TheWatchdogStopsOnItsOwnAndLeavesTheLoopsRunning()
    {
        var world = World.Build();
        var session = DictationSessionId.Create();
        await world.Work.StartAsync(session, new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), Toggle));
        await world.Engine.StartStarted.Task.WaitAsync(Patience);

        await world.Work.StopWatchdogAsync();

        Assert.False(world.Watchdog.IsArmed);
        Assert.True(world.Preview.IsRunning);
        Assert.True(world.AutoStop.IsRunning);
        Assert.True(world.Streaming.IsRunning);

        await world.Work.StopAsync();
        await world.DisposeAsync();
    }

    [Fact]
    public async Task StoppingTwiceIsHarmless()
    {
        var world = World.Build();
        await world.Work.StartAsync(DictationSessionId.Create(), new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), Toggle));
        await world.Engine.StartStarted.Task.WaitAsync(Patience);

        await world.Work.StopWatchdogAsync();
        await world.Work.StopAsync();
        await world.Work.StopWatchdogAsync();
        await world.Work.StopAsync();

        // The preview asks its engine to stop on every stop by design (a worker that came up between
        // a cancel and the check is taken down); nothing else runs twice and nothing throws.
        Assert.False(world.Watchdog.IsArmed);
        Assert.False(world.Preview.IsRunning);
        Assert.False(world.AutoStop.IsRunning);
        Assert.False(world.Streaming.IsRunning);
        await world.DisposeAsync();
    }

    private sealed class World
    {
        public required SessionBackgroundWork Work { get; init; }
        public required RecordingWatchdog Watchdog { get; init; }
        public required LivePreviewController Preview { get; init; }
        public required AutoStopMonitor AutoStop { get; init; }
        public required StreamingTranscriptionController Streaming { get; init; }
        public required FakeEngine Engine { get; init; }
        public required Deterministic.ManualClock Clock { get; init; }

        public static World Build()
        {
            var clock = new Deterministic.ManualClock();
            var audio = new SilentAudio();
            var engine = new FakeEngine();
            var log = new NullLogger();
            var watchdog = new RecordingWatchdog(new TimerEffects(audio), clock);
            var preview = new LivePreviewController(new PreviewEffects(engine, audio), log, clock);
            var autoStop = new AutoStopMonitor(new TimerEffects(audio), log, clock);
            var streaming = new StreamingTranscriptionController(new StreamingEffects(audio), log, clock);
            engine.OnStopStarted = () => (streaming.IsRunning, autoStop.IsRunning);
            return new World
            {
                Work = new SessionBackgroundWork(watchdog, preview, autoStop, streaming),
                Watchdog = watchdog,
                Preview = preview,
                AutoStop = autoStop,
                Streaming = streaming,
                Engine = engine,
                Clock = clock,
            };
        }

        public async Task DisposeAsync()
        {
            Engine.AllowStartExit.TrySetResult();
            Engine.AllowStopExit.TrySetResult();
            await Preview.DisposeAsync();
            await AutoStop.DisposeAsync();
            await Watchdog.DisposeAsync();
            await Streaming.StopAsync();
        }
    }

    private sealed class SilentAudio : IAudioSnapshotSource
    {
        private static readonly float[] Silence = new float[16_000];

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) =>
            new(DictationSessionId.Create(), Silence, 16_000, 1);
    }

    private sealed class TimerEffects(IAudioSnapshotSource audio) : IRecordingTimerEffects
    {
        public IAudioSnapshotSource? Audio => audio;

        public void Post(PushToTalkSignal signal)
        {
        }

        public void RecordingTimedOut(DictationSessionId sessionId)
        {
        }
    }

    private sealed class PreviewEffects(ILivePreviewEngine engine, IAudioSnapshotSource audio) : ILivePreviewEffects
    {
        public bool Enabled => true;

        public ILivePreviewEngine? Engine => engine;

        public AppErrorCode? EngineUnavailableReason => null;

        public IAudioSnapshotSource? Audio => audio;

        public DictationSessionId? RecordingSessionId => null;

        public void ShowPreview(DictationSessionId sessionId, string text)
        {
        }

        public void ClearPreview()
        {
        }
    }

    private sealed class StreamingEffects(IAudioSnapshotSource audio) : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;

        public ITranscriptionEngine? Engine { get; } = new IdleTranscriptionEngine();

        public IAudioSnapshotSource? Audio => audio;
    }

    private sealed class IdleTranscriptionEngine : ITranscriptionEngine
    {
        public string EngineId => "idle";

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Transcript(audio.SessionId, string.Empty, EngineId));
    }

    private sealed class FakeEngine : ILivePreviewEngine
    {
        public string EngineId => "fake";
        public bool HoldStarts { get; set; }
        public bool HoldStops { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public TaskCompletionSource StartStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStartExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<(bool Streaming, bool AutoStop)>? OnStopStarted { get; set; }
        public (bool Streaming, bool AutoStop)? LoopsRunningWhenStopStarted { get; private set; }

        public async Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default)
        {
            StartStarted.TrySetResult();
            if (HoldStarts)
            {
                await AllowStartExit.Task.WaitAsync(cancellationToken);
            }

            Starts++;
            return new RuntimeWorkerResult(true, RuntimeWorkerState.Ready);
        }

        public Task<LivePreviewUpdate> PreviewAsync(AudioSnapshot snapshot, long sequence, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LivePreviewUpdate(snapshot.SessionId.Value, sequence, true, "words"));

        public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
        {
            LoopsRunningWhenStopStarted ??= OnStopStarted?.Invoke();
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

    private sealed class NullLogger : IAppLogger
    {
        public void Write(AppLogEntry entry)
        {
        }
    }
}
