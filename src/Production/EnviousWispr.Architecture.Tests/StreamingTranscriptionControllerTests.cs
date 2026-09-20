using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The head start from the first commit to the join at the release: deterministic audio built from
/// stretches of speech and silence, an engine that records exactly which samples it was handed, a
/// clock the test moves, and a log that keeps its lines. The sample boundaries are the subject: a
/// tail that starts one sample early repeats a word, one sample late loses one.
/// </summary>
public sealed class StreamingTranscriptionControllerTests
{
    private const int SampleRate = 16_000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task ACommittedPrefixIsJoinedToTheTailAndOnlyTheTailIsTranscribedAtTheRelease()
    {
        var world = World.Build();
        // A finished sentence, a pause long enough to end it, and a word still being said.
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.NextText = "the first sentence";

        world.Controller.Start(world.Session);
        Assert.True(world.Controller.IsRunning);
        world.Clock.Advance(Poll);
        // A COMMIT IS COMPLETE WHEN THE NEXT POLL IS REGISTERED: the engine's answer has been
        // appended and logged by then. The engine's own milestone says only that it was entered.
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        var commit = world.Engine.Requests[0];
        Assert.Equal(0, commit.From);
        Assert.True(commit.Length > SampleRate * 3, "the commit covers the sentence");
        Assert.True(commit.Length < world.Audio.Samples.Length, "the commit stops short of the word in progress");
        Assert.Equal([AppEventCode.StreamingSegmentCommitted], world.Log.Codes);

        await world.Controller.StopAsync();
        // The take grew after the last poll: the release transcribes from the committed sample on.
        var whole = Build((false, 200), (true, 3000), (false, 1200), (true, 2000), (false, 600));
        world.Engine.NextText = "and the tail";
        var transcript = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, new CapturedAudio(world.Session, whole, SampleRate, 1), CancellationToken.None);

        var tail = world.Engine.Requests[1];
        Assert.Equal(commit.Length, tail.From);
        Assert.Equal(whole.Length - commit.Length, tail.Length);
        Assert.Equal("the first sentence and the tail", transcript.Text);
        Assert.Equal([AppEventCode.StreamingSegmentCommitted, AppEventCode.StreamingHeadStartUsed], world.Log.Codes);
        Assert.All(world.Log.Dictations, dictation => Assert.Equal(world.Session.Value, dictation));
    }

    [Fact]
    public async Task AFailedSegmentAbandonsTheHeadStartAndTheReleaseTranscribesEverything()
    {
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.ThrowOnTranscribe = new TranscriptionEngineException(
            new AppError(AppErrorCode.RuntimeWorkerFailed, AppErrorStage.RuntimeWorker, CanRetry: true));

        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Controller.Loop!.WaitAsync(Patience);

        var abandoned = Assert.Single(world.Log.Entries);
        Assert.Equal(AppEventCode.StreamingAbandoned, abandoned.Event);
        Assert.Equal(AppFailureCategory.RuntimeWorker, abandoned.Failure);
        Assert.Equal(AppErrorCode.RuntimeWorkerFailed, abandoned.ErrorCode);

        await world.Controller.StopAsync();
        world.Engine.ThrowOnTranscribe = null;
        world.Engine.NextText = "everything";
        var whole = world.Audio.Samples;
        var transcript = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, new CapturedAudio(world.Session, whole, SampleRate, 1), CancellationToken.None);

        var request = world.Engine.Requests.Last();
        Assert.Equal(0, request.From);
        Assert.Equal(whole.Length, request.Length);
        Assert.Equal("everything", transcript.Text);
        Assert.DoesNotContain(AppEventCode.StreamingHeadStartUsed, world.Log.Codes);
    }

    [Fact]
    public async Task AFailureAfterACommitAbandonsTheWholeHeadStartNotJustTheFailedSegment()
    {
        // Half a dictation is worse than a slow one: one good commit followed by a failed one must
        // not become "the good commit plus a tail", because the failed stretch would be missing
        // from the middle. Everything is transcribed again.
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.NextText = "the first sentence";
        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.ThrowOnTranscribe = new TranscriptionEngineException(
            new AppError(AppErrorCode.RuntimeWorkerFailed, AppErrorStage.RuntimeWorker, CanRetry: true));
        world.Clock.Advance(Poll);
        await world.Controller.Loop!.WaitAsync(Patience);
        await world.Controller.StopAsync();

        Assert.Equal([AppEventCode.StreamingSegmentCommitted, AppEventCode.StreamingAbandoned], world.Log.Codes);
        world.Engine.ThrowOnTranscribe = null;
        world.Engine.NextText = "everything";
        var whole = world.Audio.Samples;
        var transcript = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, new CapturedAudio(world.Session, whole, SampleRate, 1), CancellationToken.None);

        var request = world.Engine.Requests.Last();
        Assert.Equal(0, request.From);
        Assert.Equal(whole.Length, request.Length);
        Assert.Equal("everything", transcript.Text);
    }

    [Fact]
    public async Task AnUntypedFailureIsAbandonedAsUnknownAndStillFallsBack()
    {
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.ThrowOnTranscribe = new InvalidOperationException("worker gone");

        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Controller.Loop!.WaitAsync(Patience);

        var abandoned = Assert.Single(world.Log.Entries);
        Assert.Equal(AppEventCode.StreamingAbandoned, abandoned.Event);
        Assert.Equal(AppFailureCategory.Unknown, abandoned.Failure);
        Assert.Null(abandoned.ErrorCode);
    }

    [Fact]
    public async Task AStopDuringASegmentCancelsItAndKeepsWhatWasCommittedBefore()
    {
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.NextText = "the first sentence";

        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);
        var committed = world.Engine.Requests[0].Length;

        // More finished speech arrives; the next segment is held inside the engine when the stop lands.
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.HoldTranscriptions = true;
        world.Clock.Advance(Poll);
        await world.Engine.WhenTranscribed(2).WaitAsync(Patience);

        var stop = world.Controller.StopAsync();
        await world.Engine.CancellationObserved.Task.WaitAsync(Patience);
        Assert.False(stop.IsCompleted);
        world.Engine.AllowExit.SetResult();
        await stop.WaitAsync(Patience);

        Assert.False(world.Controller.IsRunning);
        Assert.Equal([AppEventCode.StreamingSegmentCommitted], world.Log.Codes);

        // The release uses the first commit only: the cancelled segment was never appended.
        world.Engine.HoldTranscriptions = false;
        world.Engine.NextText = "and the rest";
        var whole = world.Audio.Samples;
        var transcript = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, new CapturedAudio(world.Session, whole, SampleRate, 1), CancellationToken.None);

        var tail = world.Engine.Requests.Last();
        Assert.Equal(committed, tail.From);
        Assert.Equal("the first sentence and the rest", transcript.Text);
    }

    [Fact]
    public async Task WithLivePreviewOnStreamingStandsDownAndTheReleaseTranscribesEverything()
    {
        var world = World.Build();
        world.Effects.LivePreviewEnabled = true;
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));

        world.Controller.Start(world.Session);

        Assert.False(world.Controller.IsRunning);
        Assert.Null(world.Clock.NextDue);
        await world.Controller.StopAsync();
        world.Engine.NextText = "everything";
        var whole = world.Audio.Samples;
        var transcript = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, new CapturedAudio(world.Session, whole, SampleRate, 1), CancellationToken.None);

        var request = Assert.Single(world.Engine.Requests);
        Assert.Equal(0, request.From);
        Assert.Equal(whole.Length, request.Length);
        Assert.Equal("everything", transcript.Text);
        Assert.Empty(world.Log.Entries);
    }

    [Fact]
    public void WithoutAnEngineOrASampleableCaptureNothingStarts()
    {
        var noEngine = World.Build();
        noEngine.Effects.Engine = null;
        noEngine.Controller.Start(noEngine.Session);
        Assert.False(noEngine.Controller.IsRunning);

        var noAudio = World.Build();
        noAudio.Effects.Audio = null;
        noAudio.Controller.Start(noAudio.Session);
        Assert.False(noAudio.Controller.IsRunning);
    }

    [Fact]
    public async Task ASnapshotOfAnotherDictationIsIgnored()
    {
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Audio.Session = DictationSessionId.Create();

        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Audio.WhenSampled(1).WaitAsync(Patience);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        Assert.Empty(world.Engine.Requests);
        Assert.Equal(Poll, world.Clock.NextDue);
        await world.Controller.StopAsync();
    }

    [Fact]
    public async Task SpeechStillInProgressIsNotCommittedAndTheLoopKeepsPolling()
    {
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 4000));

        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Audio.WhenSampled(1).WaitAsync(Patience);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);

        Assert.Empty(world.Engine.Requests);
        Assert.Equal(Poll, world.Clock.NextDue);
        Assert.Equal(TimeSpan.MaxValue, world.Audio.LastWindow);
        await world.Controller.StopAsync();
    }

    [Fact]
    public async Task AHeadStartThatReachesTheEndOfTheAudioIsNotUsed()
    {
        // The last place to refuse: a commit that covers everything leaves no tail, and the join is
        // not attempted on a tail of nothing. Everything is transcribed again instead.
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.NextText = "the first sentence";
        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);
        var committed = world.Engine.Requests[0].Length;
        await world.Controller.StopAsync();

        world.Engine.NextText = "everything";
        var shorter = world.Audio.Samples[..committed];
        var transcript = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, new CapturedAudio(world.Session, shorter, SampleRate, 1), CancellationToken.None);

        Assert.Equal(0, world.Engine.Requests.Last().From);
        Assert.Equal("everything", transcript.Text);
        Assert.DoesNotContain(AppEventCode.StreamingHeadStartUsed, world.Log.Codes);
    }

    [Fact]
    public async Task ANewTakeForgetsTheLastOne()
    {
        var world = World.Build();
        world.Audio.Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 500));
        world.Engine.NextText = "old words";
        world.Controller.Start(world.Session);
        world.Clock.Advance(Poll);
        await world.Clock.WhenRegistered(2).WaitAsync(Patience);
        await world.Controller.StopAsync();

        var next = DictationSessionId.Create();
        world.Effects.Engine = null;
        world.Controller.Start(next);
        world.Effects.Engine = world.Engine;
        world.Engine.NextText = "new words";
        var whole = world.Audio.Samples;
        var transcript = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, new CapturedAudio(next, whole, SampleRate, 1), CancellationToken.None);

        Assert.Equal(0, world.Engine.Requests.Last().From);
        Assert.Equal("new words", transcript.Text);
    }

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
        public required StreamingTranscriptionController Controller { get; init; }
        public required FakeEffects Effects { get; init; }
        public required FakeEngine Engine { get; init; }
        public required FakeAudio Audio { get; init; }
        public required FakeLogger Log { get; init; }
        public required Deterministic.ManualClock Clock { get; init; }
        public required DictationSessionId Session { get; init; }

        public static World Build()
        {
            var session = DictationSessionId.Create();
            var engine = new FakeEngine();
            var audio = new FakeAudio { Session = session };
            var effects = new FakeEffects { Engine = engine, Audio = audio };
            var log = new FakeLogger();
            var clock = new Deterministic.ManualClock();
            return new World
            {
                Controller = new StreamingTranscriptionController(effects, log, clock),
                Effects = effects,
                Engine = engine,
                Audio = audio,
                Log = log,
                Clock = clock,
                Session = session,
            };
        }
    }

    private sealed class FakeEffects : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled { get; set; }
        public ITranscriptionEngine? Engine { get; set; }
        public IAudioSnapshotSource? Audio { get; set; }
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

    /// <summary>The engine records where each request's samples sit in the take: the slice's offset into the array it was cut from.</summary>
    private sealed class FakeEngine : ITranscriptionEngine
    {
        private readonly object _lock = new();
        private readonly Deterministic.Milestone _transcribed = new();
        private readonly List<(int From, int Length)> _requests = [];

        public string EngineId => "fake";
        public string NextText { get; set; } = "";
        public Exception? ThrowOnTranscribe { get; set; }
        public bool HoldTranscriptions { get; set; }
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (int From, int Length)[] Requests
        {
            get
            {
                lock (_lock)
                {
                    return _requests.ToArray();
                }
            }
        }

        /// <summary>Completes once the engine has been ENTERED that many times - not once it has answered.</summary>
        public Task WhenTranscribed(int count) => _transcribed.WhenAtLeast(count);

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _requests.Add((Offset(audio.Samples), audio.Samples.Length));
            }

            _transcribed.Increment();
            if (HoldTranscriptions)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                await Task.WhenAny(AllowExit.Task, cancelled.Task);
                if (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult();
                    await AllowExit.Task;
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            if (ThrowOnTranscribe is not null)
            {
                throw ThrowOnTranscribe;
            }

            return new Transcript(audio.SessionId, NextText, EngineId, [], DetectedLanguage: "en");
        }

        /// <summary>Where a slice begins in the array it was sliced from; a slice of nothing is at zero.</summary>
        private static int Offset(ReadOnlyMemory<float> slice)
        {
            if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(slice, out var segment))
            {
                throw new InvalidOperationException("the engine was handed samples that are not a slice of the take");
            }

            return segment.Offset;
        }
    }

    private sealed class FakeLogger : IAppLogger
    {
        private readonly object _lock = new();
        private readonly List<(AppLogEntry Entry, Guid? Dictation)> _lines = [];

        public AppLogEntry[] Entries
        {
            get
            {
                lock (_lock)
                {
                    return _lines.Select(line => line.Entry).ToArray();
                }
            }
        }

        public IEnumerable<AppEventCode> Codes => Entries.Select(entry => entry.Event);

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
