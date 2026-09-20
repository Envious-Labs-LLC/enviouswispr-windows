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

        var start = world.Work.StartAsync(session, new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), world.Preferences));

        // THE EXACT SEQUENCE, READ AT THE LEAVES. The watchdog's timer is the first thing registered
        // on the clock; the preview reads whether it is enabled; the preferences are read, once, at
        // the auto-stop's boundary; the auto-stop reads its audio and registers its poll; streaming
        // reads whether the preview is on and registers its poll. Swapping any two adjacent starts
        // changes this list. And none of it waited on a worker: the engine's start is still held.
        await start.WaitAsync(Patience);
        await world.Engine.StartStarted.Task.WaitAsync(Patience);
        Assert.Equal(
            ["timer:180000ms", "preview:enabled", "preferences", "autostop:audio", "timer:250ms", "streaming:enabled", "timer:500ms"],
            world.Leaves);
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
    public async Task StoppingEndsStreamingThenTheAutoStopThenThePreview()
    {
        // THE STOP ORDER, ONE HOLD AT A TIME. The streaming loop is caught inside its engine with a
        // committed segment (its stop then waits on that loop); while it waits, the auto-stop and the
        // preview are still running. Released, the stop moves on: when the preview's worker is asked
        // to stop, the auto-stop is already gone. Streaming, then the auto-stop, then the preview.
        var world = World.Build();
        var session = DictationSessionId.Create();
        world.Audio.Speak(session);
        await world.Work.StartAsync(session, new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), world.Preferences));
        await world.Engine.StartStarted.Task.WaitAsync(Patience);
        world.Engine.AllowStartExit.SetResult();
        await world.Clock.WhenRegistered(3).WaitAsync(Patience);
        world.Transcriber.Hold = true;
        world.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await world.Transcriber.Entered.Task.WaitAsync(Patience);
        world.Engine.HoldStops = true;

        var stop = world.Work.StopAsync();
        await Task.Delay(50);
        Assert.False(stop.IsCompleted, "the stop is waiting on the streaming loop, which is inside its engine");
        Assert.True(world.AutoStop.IsRunning, "the auto-stop is stopped after streaming, not before");
        Assert.True(world.Preview.IsRunning, "the preview is stopped last");
        Assert.False(world.Engine.StopStarted.Task.IsCompleted);

        world.Transcriber.AllowExit.SetResult();
        await world.Engine.StopStarted.Task.WaitAsync(Patience);
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
    public async Task ThePreferencesAreReadOnceAtTheAutoStopsBoundaryNotWhenTheRecordingStarts()
    {
        // A SAVE THAT LANDS BETWEEN THE RECORDING STARTING AND THE AUTO-STOP STARTING GOVERNS THE
        // RECORDING, as it always did. The preview's start returns without waiting for its worker,
        // so the boundary is close to the start; it is still after it, and it is read exactly once.
        var world = World.Build();
        var reads = 0;
        var settings = new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), () =>
        {
            reads++;
            world.Leaves.Add("preferences");
            return Toggle;
        });

        await world.Work.StartAsync(DictationSessionId.Create(), settings);

        Assert.Equal(1, reads);
        Assert.True(world.Leaves.IndexOf("preferences") > world.Leaves.IndexOf("preview:enabled"));
        Assert.True(world.Leaves.IndexOf("preferences") < world.Leaves.IndexOf("autostop:audio"));
        await world.Work.StopWatchdogAsync();
        await world.Work.StopAsync();
        await world.DisposeAsync();
    }

    [Fact]
    public async Task TheWatchdogStopsOnItsOwnAndLeavesTheLoopsRunning()
    {
        var world = World.Build();
        var session = DictationSessionId.Create();
        await world.Work.StartAsync(session, new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), world.Preferences));
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
        await world.Work.StartAsync(DictationSessionId.Create(), new RecordingBackgroundSettings(TimeSpan.FromMinutes(3), world.Preferences));
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
        public required HeldTranscriber Transcriber { get; init; }
        public required ScriptedAudio Audio { get; init; }
        public required Deterministic.ManualClock Clock { get; init; }
        public required List<string> Leaves { get; init; }

        /// <summary>The preferences, read through the trace so the test can see when.</summary>
        public Func<DictationPreferences> Preferences => () =>
        {
            Leaves.Add("preferences");
            return Toggle;
        };

        public static World Build()
        {
            var leaves = new List<string>();
            var clock = new Deterministic.ManualClock();
            var tracingClock = new TracingClock(clock, leaves);
            var audio = new ScriptedAudio();
            var engine = new FakeEngine();
            var transcriber = new HeldTranscriber();
            var log = new NullLogger();
            var watchdog = new RecordingWatchdog(new TimerEffects(audio, leaves, "watchdog"), tracingClock);
            var preview = new LivePreviewController(new PreviewEffects(engine, audio, leaves), log, tracingClock);
            var autoStop = new AutoStopMonitor(new TimerEffects(audio, leaves, "autostop"), log, tracingClock);
            var streaming = new StreamingTranscriptionController(new StreamingEffects(audio, transcriber, leaves), log, tracingClock);
            engine.OnStopStarted = () => (streaming.IsRunning, autoStop.IsRunning);
            return new World
            {
                Work = new SessionBackgroundWork(watchdog, preview, autoStop, streaming),
                Watchdog = watchdog,
                Preview = preview,
                AutoStop = autoStop,
                Streaming = streaming,
                Engine = engine,
                Transcriber = transcriber,
                Audio = audio,
                Clock = clock,
                Leaves = leaves,
            };
        }

        public async Task DisposeAsync()
        {
            Engine.AllowStartExit.TrySetResult();
            Engine.AllowStopExit.TrySetResult();
            Transcriber.AllowExit.TrySetResult();
            await Preview.DisposeAsync();
            await AutoStop.DisposeAsync();
            await Watchdog.DisposeAsync();
            await Streaming.StopAsync();
        }
    }

    /// <summary>Registers timers on the manual clock and writes each one down by its due time, so the first registration in a synchronous prefix names its owner.</summary>
    private sealed class TracingClock(Deterministic.ManualClock inner, List<string> leaves) : TimeProvider
    {
        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            leaves.Add($"timer:{(long)dueTime.TotalMilliseconds}ms");
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>Silence until told to speak: then a finished sentence with silence after it, which the streaming planner commits.</summary>
    private sealed class ScriptedAudio : IAudioSnapshotSource
    {
        private float[] _samples = new float[16_000];
        private DictationSessionId _session = DictationSessionId.Create();

        public void Speak(DictationSessionId session)
        {
            _session = session;
            _samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        }

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) => new(_session, _samples, 16_000, 1);

        private static float[] Build(params (bool IsSpeech, int Milliseconds)[] parts)
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

    private sealed class TimerEffects(IAudioSnapshotSource audio, List<string> leaves, string owner) : IRecordingTimerEffects
    {
        public IAudioSnapshotSource? Audio
        {
            get
            {
                leaves.Add($"{owner}:audio");
                return audio;
            }
        }

        public void Post(PushToTalkSignal signal)
        {
        }

        public void RecordingTimedOut(DictationSessionId sessionId)
        {
        }
    }

    private sealed class PreviewEffects(ILivePreviewEngine engine, IAudioSnapshotSource audio, List<string> leaves) : ILivePreviewEffects
    {
        public bool Enabled
        {
            get
            {
                leaves.Add("preview:enabled");
                return true;
            }
        }

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

    private sealed class StreamingEffects(IAudioSnapshotSource audio, ITranscriptionEngine engine, List<string> leaves) : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled
        {
            get
            {
                leaves.Add("streaming:enabled");
                return false;
            }
        }

        public ITranscriptionEngine? Engine => engine;

        public IAudioSnapshotSource? Audio => audio;
    }

    /// <summary>A head-start transcriber that can be held inside a segment - and that does NOT honour cancellation while held, so the stop that waits on it is observable.</summary>
    private sealed class HeldTranscriber : ITranscriptionEngine
    {
        public string EngineId => "held";

        public bool Hold { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            if (Hold)
            {
                Entered.TrySetResult();
                await AllowExit.Task;
            }

            return new Transcript(audio.SessionId, "words", EngineId);
        }
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
