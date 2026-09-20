using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The two timers that end a recording nobody released: the watchdog on virtual time, and the
/// auto-stop monitor on virtual time over deterministic speech-and-silence audio. Each posts through
/// a port that records what was posted and when the shell was asked to recover; neither is ever
/// waited for on the wall clock.
/// </summary>
public sealed class RecordingTimersTests
{
    private const int SampleRate = 16_000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task TheWatchdogAsksForRecoveryWhenTheLimitPassesAndNotATickBefore()
    {
        var world = World.Build();

        world.Watchdog.Start(world.Session, Limit);

        Assert.True(world.Watchdog.IsArmed);
        Assert.Equal(Limit, world.Clock.NextDue);
        world.Clock.Advance(Limit);
        await world.Effects.WhenTimedOut(1).WaitAsync(Patience);

        Assert.Equal([world.Session], world.Effects.TimedOut);
        Assert.Equal([world.Session.Value], world.Effects.TimedOutDictations);
        Assert.Empty(world.Effects.Posted);
        await world.Watchdog.StopAsync();
    }

    [Fact]
    public async Task AStopBeforeTheLimitDisarmsTheWatchdogAndNothingIsAskedFor()
    {
        var world = World.Build();
        world.Watchdog.Start(world.Session, Limit);

        await world.Watchdog.StopAsync();

        Assert.False(world.Watchdog.IsArmed);
        Assert.Null(world.Clock.NextDue);
        world.Clock.Advance(Limit + Limit);
        Assert.Empty(world.Effects.TimedOut);
    }

    [Fact]
    public async Task ArmingAgainBeforeTheLimitCancelsTheEarlierWatchAndOnlyTheNewSessionCanTimeOut()
    {
        // CANCELLATION BEFORE EXPIRY, not a stale expiry. A watch that has already expired and is
        // parked on the session gate when a new recording starts is ended by the same cancel, and if
        // it ever reached the guarded section the shell's identity check would still refuse it; that
        // check lives in the shell until step 11 and is not reached from here.
        var world = World.Build();
        var first = DictationSessionId.Create();
        world.Watchdog.Start(first, Limit);
        world.Clock.Advance(TimeSpan.FromMinutes(4));

        var second = DictationSessionId.Create();
        world.Watchdog.Start(second, Limit);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        // The first watch would have fired in a minute; the second is a full limit away.
        Assert.Equal(Limit, world.Clock.NextDue);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(world.Effects.TimedOut);
        world.Clock.Advance(TimeSpan.FromMinutes(4));
        await world.Effects.WhenTimedOut(1).WaitAsync(Patience);
        Assert.Equal([second], world.Effects.TimedOut);
        await world.Watchdog.StopAsync();
    }

    [Fact]
    public async Task TheWatchdogPostsItsTimeoutAndItsStopDoesNotWaitForWhatItPosted()
    {
        // The timeout is a command on the queue, decided there under the session; the watchdog only
        // posts it. A stop issued from inside that command - the executor stops the watchdog first -
        // therefore finds a watch that has already returned, and cannot wait for itself.
        var world = World.Build();
        world.Watchdog.Start(world.Session, Limit);
        world.Clock.Advance(Limit);
        await world.Effects.WhenTimedOut(1).WaitAsync(Patience);

        await world.Watchdog.StopAsync().WaitAsync(Patience);

        Assert.False(world.Watchdog.IsArmed);
        Assert.Equal([world.Session], world.Effects.TimedOut);
    }

    [Fact]
    public async Task AutoStopReleasesTheKeyOnceTheSpeakerHasStoppedForTheThreshold()
    {
        var world = World.Build();
        var dictation = Toggle(silenceSeconds: 2.0);
        world.Audio.Samples = Build((true, 1000), (false, 500));

        world.AutoStop.Start(world.Session, dictation);
        Assert.True(world.AutoStop.IsRunning);
        Assert.Equal(Poll, world.Clock.NextDue);

        // Half a second of silence after a second of speech: not yet.
        world.Clock.Advance(Poll);
        await world.Audio.WhenSampled(1).WaitAsync(Patience);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);
        Assert.Empty(world.Effects.Posted);
        Assert.Equal(TimeSpan.FromSeconds(4), world.Audio.LastWindow);

        // Two seconds of silence after the speech: the speaker has stopped.
        world.Audio.Samples = Build((true, 1000), (false, 2200));
        world.Clock.Advance(Poll);
        await world.AutoStop.Loop!.WaitAsync(Patience);

        Assert.Equal([PushToTalkSignal.Released], world.Effects.Posted);
        Assert.Equal([AppEventCode.AutoStopTriggered], world.Log.Codes);
        Assert.Equal([world.Session.Value], world.Log.Dictations);
        Assert.Empty(world.Effects.TimedOut);
    }

    [Fact]
    public async Task SilenceBeforeAnyoneHasSpokenNeverEndsTheRecording()
    {
        var world = World.Build();
        world.Audio.Samples = Build((false, 4000));

        world.AutoStop.Start(world.Session, Toggle(silenceSeconds: 2.0));
        world.Clock.Advance(Poll);
        await world.Audio.WhenSampled(1).WaitAsync(Patience);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        Assert.Empty(world.Effects.Posted);
        Assert.Equal(Poll, world.Clock.NextDue);
        await world.AutoStop.StopAsync();
    }

    [Fact]
    public async Task HavingHeardSpeechIsRememberedAfterItLeavesTheWindow()
    {
        // The window only holds four seconds. A word said and then a long pause would look, to a
        // single snapshot, like nobody ever spoke - and the policy would keep protecting a silent
        // speaker forever. The monitor remembers.
        var world = World.Build();
        world.Audio.Samples = Build((true, 1000), (false, 500));
        world.AutoStop.Start(world.Session, Toggle(silenceSeconds: 2.0));
        world.Clock.Advance(Poll);
        await world.Audio.WhenSampled(1).WaitAsync(Patience);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        // The window has rolled past the speech entirely.
        world.Audio.Samples = Build((false, 4000));
        world.Clock.Advance(Poll);
        await world.AutoStop.Loop!.WaitAsync(Patience);

        Assert.Equal([PushToTalkSignal.Released], world.Effects.Posted);
    }

    [Fact]
    public void AutoStopDoesNotStartWhenNotAskedForOrOutsideToggleModeOrWithoutAudio()
    {
        var off = World.Build();
        off.AutoStop.Start(off.Session, Toggle(silenceSeconds: 2.0) with { AutoStopEnabled = false });
        Assert.False(off.AutoStop.IsRunning);

        var pushToTalk = World.Build();
        pushToTalk.AutoStop.Start(pushToTalk.Session, Toggle(silenceSeconds: 2.0) with { RecordingMode = DictationRecordingMode.PushToTalk });
        Assert.False(pushToTalk.AutoStop.IsRunning);

        var noAudio = World.Build();
        noAudio.Effects.Audio = null;
        noAudio.AutoStop.Start(noAudio.Session, Toggle(silenceSeconds: 2.0));
        Assert.False(noAudio.AutoStop.IsRunning);

        Assert.Null(off.Clock.NextDue);
    }

    [Fact]
    public async Task AThresholdBelowTheMinimumIsRaisedToIt()
    {
        var world = World.Build();
        world.Audio.Samples = Build((true, 1000), (false, 700));

        // Asked for half a second; the policy's floor is longer, and 700 ms is not enough for it.
        world.AutoStop.Start(world.Session, Toggle(silenceSeconds: 0.5));
        world.Clock.Advance(Poll);
        await world.Audio.WhenSampled(1).WaitAsync(Patience);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        Assert.Empty(world.Effects.Posted);
        Assert.Equal(AutoStopPolicy.MinimumSilence + AutoStopPolicy.MinimumSilence, world.Audio.LastWindow);
        await world.AutoStop.StopAsync();
    }

    [Fact]
    public async Task ASnapshotOfAnotherDictationIsIgnoredByAutoStop()
    {
        var world = World.Build();
        world.Audio.Samples = Build((true, 1000), (false, 2200));
        world.Audio.Session = DictationSessionId.Create();

        world.AutoStop.Start(world.Session, Toggle(silenceSeconds: 2.0));
        world.Clock.Advance(Poll);
        await world.Audio.WhenSampled(1).WaitAsync(Patience);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        Assert.Empty(world.Effects.Posted);
        await world.AutoStop.StopAsync();
    }

    [Fact]
    public async Task AStopEndsTheAutoStopLoopAndNothingIsPostedAfterIt()
    {
        var world = World.Build();
        world.Audio.Samples = Build((true, 1000), (false, 500));
        world.AutoStop.Start(world.Session, Toggle(silenceSeconds: 2.0));
        world.Clock.Advance(Poll);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        await world.AutoStop.StopAsync();

        Assert.False(world.AutoStop.IsRunning);
        Assert.Null(world.Clock.NextDue);
        world.Audio.Samples = Build((true, 1000), (false, 2200));
        world.Clock.Advance(Poll + Poll);
        Assert.Empty(world.Effects.Posted);
    }

    [Fact]
    public async Task AnAutoStopThatLandsWhileARealReleaseIsRunningIsIgnoredAndTheLoopStillStops()
    {
        // THE PLAN'S SIMULTANEOUS RELEASE AND AUTO-STOP, COMPOSED: the real coordinator, an executor
        // holding a key release mid-finalisation, and the monitor deciding the speaker has stopped at
        // that moment. Its Released is refused as Ignored - the recording is already ending - the
        // post did not block the loop, the executor sees one release, and a loop that has posted
        // stops cleanly. The stop is issued from the test, after the loop has ended, not from inside
        // the held release; that the release's own stop cannot wait for the loop is the port's
        // contract (Post returns without waiting), stated on the port and not proved here.
        var executor = new HeldExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);
        var world = World.Build();
        world.Effects.PostTo = coordinator;
        world.Audio.Samples = Build((true, 1000), (false, 2200));
        world.AutoStop.Start(world.Session, Toggle(silenceSeconds: 2.0));

        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        await executor.Started.Task.WaitAsync(Patience);
        world.Clock.Advance(Poll);
        await world.AutoStop.Loop!.WaitAsync(Patience);
        await world.Effects.WhenSubmitted(1).WaitAsync(Patience);

        Assert.Equal([SessionCommandDisposition.Ignored], world.Effects.Submitted);
        await world.AutoStop.StopAsync().WaitAsync(Patience);
        executor.Finish.SetResult();
        await release.WaitAsync(Patience);
        Assert.Equal([PushToTalkSignal.Released], executor.Seen);
        Assert.False(world.AutoStop.IsRunning);
    }

    private sealed class HeldExecutor : ISessionCommandExecutor
    {
        private readonly object _lock = new();
        private readonly List<PushToTalkSignal> _seen = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PushToTalkSignal[] Seen
        {
            get
            {
                lock (_lock)
                {
                    return _seen.ToArray();
                }
            }
        }

        public async Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken)
        {
            lock (_lock)
            {
                _seen.Add(command.Signal);
            }

            Started.TrySetResult();
            await Finish.Task;
            return new SessionCommandResult(SessionCommandDisposition.Applied);
        }
    }

    private static DictationPreferences Toggle(double silenceSeconds) => DictationPreferences.Default with
    {
        RecordingMode = DictationRecordingMode.Toggle,
        AutoStopEnabled = true,
        AutoStopSilenceSeconds = silenceSeconds,
    };

    private static float[] Build(params (bool IsSpeech, int Milliseconds)[] parts)
    {
        var samples = new float[parts.Sum(part => SampleRate * part.Milliseconds / 1000)];
        var cursor = 0;
        foreach (var (isSpeech, milliseconds) in parts)
        {
            var length = SampleRate * milliseconds / 1000;
            for (var i = 0; i < length; i++)
            {
                var amplitude = isSpeech ? 0.2f : 0.001f;
                samples[cursor + i] = (i % 2 == 0) ? amplitude : -amplitude;
            }

            cursor += length;
        }

        return samples;
    }

    private sealed class World
    {
        public required RecordingWatchdog Watchdog { get; init; }
        public required AutoStopMonitor AutoStop { get; init; }
        public required FakeEffects Effects { get; init; }
        public required FakeAudio Audio { get; init; }
        public required FakeLogger Log { get; init; }
        public required Deterministic.ManualClock Clock { get; init; }
        public required DictationSessionId Session { get; init; }

        public static World Build()
        {
            var session = DictationSessionId.Create();
            var audio = new FakeAudio { Session = session };
            var effects = new FakeEffects { Audio = audio };
            var log = new FakeLogger();
            var clock = new Deterministic.ManualClock();
            return new World
            {
                Watchdog = new RecordingWatchdog(effects, clock),
                AutoStop = new AutoStopMonitor(effects, log, clock),
                Effects = effects,
                Audio = audio,
                Log = log,
                Clock = clock,
                Session = session,
            };
        }
    }

    private sealed class FakeEffects : IRecordingTimerEffects
    {
        private readonly object _lock = new();
        private readonly List<PushToTalkSignal> _posted = [];
        private readonly List<(DictationSessionId Session, Guid? Dictation)> _timedOut = [];
        private readonly Deterministic.Milestone _timedOutMilestone = new();

        private readonly List<SessionCommandDisposition> _submitted = [];
        private readonly Deterministic.Milestone _submittedMilestone = new();

        public IAudioSnapshotSource? Audio { get; set; }

        /// <summary>When set, a posted signal goes to this coordinator the way the shell's entry sends it, and the answer is kept.</summary>
        public DictationSessionCoordinator? PostTo { get; set; }

        public SessionCommandDisposition[] Submitted
        {
            get
            {
                lock (_lock)
                {
                    return _submitted.ToArray();
                }
            }
        }

        public Task WhenSubmitted(int count) => _submittedMilestone.WhenAtLeast(count);

        public PushToTalkSignal[] Posted
        {
            get
            {
                lock (_lock)
                {
                    return _posted.ToArray();
                }
            }
        }

        public DictationSessionId[] TimedOut
        {
            get
            {
                lock (_lock)
                {
                    return _timedOut.Select(entry => entry.Session).ToArray();
                }
            }
        }

        public Guid?[] TimedOutDictations
        {
            get
            {
                lock (_lock)
                {
                    return _timedOut.Select(entry => entry.Dictation).ToArray();
                }
            }
        }

        public Task WhenTimedOut(int count) => _timedOutMilestone.WhenAtLeast(count);

        public void Post(PushToTalkSignal signal)
        {
            lock (_lock)
            {
                _posted.Add(signal);
            }

            if (PostTo is { } coordinator)
            {
                // Fire and return, as the shell's entry does; the answer arrives on its own.
                _ = coordinator.SubmitAsync(signal).ContinueWith(
                    answered =>
                    {
                        lock (_lock)
                        {
                            _submitted.Add(answered.Result.Disposition);
                        }

                        _submittedMilestone.Increment();
                    },
                    TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        public void RecordingTimedOut(DictationSessionId sessionId)
        {
            lock (_lock)
            {
                _timedOut.Add((sessionId, DictationScope.Current));
            }

            _timedOutMilestone.Increment();
        }
    }

    private sealed class FakeAudio : IAudioSnapshotSource
    {
        private readonly Deterministic.Milestone _sampled = new();

        public DictationSessionId Session { get; set; }
        public float[] Samples { get; set; } = [];
        public TimeSpan LastWindow { get; private set; }

        public Task WhenSampled(int count) => _sampled.WhenAtLeast(count);

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
        {
            LastWindow = maximumDuration;
            var snapshot = new AudioSnapshot(Session, Samples, SampleRate, 1);
            _sampled.Increment();
            return snapshot;
        }
    }

    private sealed class FakeLogger : IAppLogger
    {
        private readonly object _lock = new();
        private readonly List<(AppLogEntry Entry, Guid? Dictation)> _lines = [];

        public IEnumerable<AppEventCode> Codes
        {
            get
            {
                lock (_lock)
                {
                    return _lines.Select(line => line.Entry.Event).ToArray();
                }
            }
        }

        public Guid?[] Dictations
        {
            get
            {
                lock (_lock)
                {
                    return _lines.Select(line => line.Dictation).ToArray();
                }
            }
        }

        public void Write(AppLogEntry entry)
        {
            lock (_lock)
            {
                _lines.Add((entry, DictationScope.Current));
            }
        }
    }
}
