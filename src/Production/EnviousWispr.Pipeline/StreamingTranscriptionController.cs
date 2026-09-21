using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

/// <summary>
/// The shell's half of streaming transcription: the final engine it built, the capture it can sample,
/// and whether Live Preview - which stands streaming down - is on.
/// </summary>
public interface IStreamingTranscriptionEffects
{
    /// <summary>The user's Live Preview switch, read when a recording starts; streaming yields to it.</summary>
    bool LivePreviewEnabled { get; }

    /// <summary>The final speech engine in force, or null when none is loaded.</summary>
    ITranscriptionEngine? Engine { get; }

    /// <summary>The take so far, or null when the capture in force cannot be sampled.</summary>
    IAudioSnapshotSource? Audio { get; }
}

/// <summary>
/// Transcribes finished parts of a recording while the user is still speaking, and at the release
/// transcribes only what it did not already cover, joining the two. Runs without a window.
/// </summary>
/// <remarks>
/// ITS WORST CASE IS TODAY'S BEHAVIOUR, and that is the design rather than a safety net bolted on. The
/// full audio is kept regardless; the streamed text is only USED if every commit succeeded. Any
/// failure - a dead worker, a cancelled request, an exception - clears the usable flag and the release
/// transcribes the whole take exactly as it always did. Dictation working is the first rule this
/// product has, and a speed feature must not be able to break it.
///
/// SO THERE IS NO SETTING. A change that cannot make things worse does not need one, and every switch
/// added is a thing a user has to understand before they benefit.
///
/// IT DOES NOT RUN WITH LIVE PREVIEW ON. Both transcribe during the recording and both use the same
/// worker, so together they would queue behind each other and make the release SLOWER than doing
/// nothing. Live Preview is the user's explicit choice and is display-only; this is invisible and
/// makes the real text faster. Turning off the thing they chose would be wrong, so the invisible one
/// stands down.
///
/// ONE OWNER FOR THE BUFFER AND THE JOIN. The committed text, the sample the commits reach, and the
/// usable flag are written by the loop and read by the release; they live together so the release
/// reads the loop's last word and not a shell field somebody else could reset. The loop has been
/// stopped before the release reads - the finalisation stops it first - so the reads are of settled
/// state, and the join checks its conditions again anyway, because this is the last place to refuse.
/// </remarks>
public sealed class StreamingTranscriptionController
{
    /// <summary>How often the loop looks for a stretch it can commit.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a pause has to be before the words in front of it are finished.</summary>
    private static readonly TimeSpan SegmentPause = TimeSpan.FromMilliseconds(400);

    private readonly IStreamingTranscriptionEffects _effects;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly StreamingTranscriptAccumulator _streamed = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private int _streamedThroughSample;
    private bool _usable;

    public StreamingTranscriptionController(IStreamingTranscriptionEffects effects, IAppLogger logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Whether a streaming loop has been started and not yet stopped.</summary>
    public bool IsRunning => _loop is not null;

    /// <summary>The loop, so a test can wait for one that ends on its own rather than poll for it.</summary>
    internal Task? Loop => _loop;

    /// <summary>Forgets the last take and, unless streaming stands down, starts committing this one.</summary>
    public void Start(DictationSessionId sessionId)
    {
        // REFUSED WHILE THE LAST LOOP IS STILL OWNED. A loop a bounded stop left inside the engine
        // still holds the accumulator and would append its late segment to the next recording's;
        // that recording runs without a head start, and the next stop joins what is left.
        if (_loop is not null)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.StreamingAbandoned,
                AppFailureCategory.RuntimeWorker,
                ErrorCode: AppErrorCode.RuntimeResourceBusy));
            _usable = false;
            return;
        }

        _streamed.Clear();
        _streamedThroughSample = 0;
        _usable = false;

        if (_effects.LivePreviewEnabled ||
            _effects.Engine is not { } engine ||
            _effects.Audio is not { } snapshots)
        {
            return;
        }

        _usable = true;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _loop = RunAsync(snapshots, engine, sessionId, cancellation.Token);
    }

    private async Task RunAsync(
        IAudioSnapshotSource snapshots,
        ITranscriptionEngine engine,
        DictationSessionId sessionId,
        CancellationToken cancellationToken)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var dictation = DictationScope.Begin(sessionId.Value);
        SpeechSegmenter? segmenter = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, _clock, cancellationToken).ConfigureAwait(false);

                // The WHOLE recording so far, not a window: a commit is a range measured from the
                // start, and a rolling window would make those indices mean something different on
                // every poll.
                var snapshot = snapshots.GetSnapshot(TimeSpan.MaxValue);
                if (snapshot is null || snapshot.SessionId != sessionId)
                {
                    continue;
                }

                // THE SEGMENTER LEARNS THE RATE FROM THE AUDIO IT SEGMENTS, at the first snapshot,
                // rather than assuming the capture's target rate from a project this one does not
                // reference. The rate does not change within a take.
                segmenter ??= new SpeechSegmenter(snapshot.SampleRate, SegmentPause);
                var commit = StreamingCommitPlanner.NextCommit(
                    snapshot.Samples.Span,
                    snapshot.SampleRate,
                    _streamedThroughSample,
                    segmenter);
                if (commit is not { } range)
                {
                    continue;
                }

                var slice = snapshot.Samples.Slice(
                    range.StartSample,
                    range.EndSample - range.StartSample);
                var transcript = await engine.TranscribeAsync(
                    new CapturedAudio(sessionId, slice, snapshot.SampleRate, snapshot.Channels),
                    cancellationToken).ConfigureAwait(false);

                _streamed.Append(transcript.Text);
                _streamedThroughSample = range.EndSample;
                _logger.Write(new AppLogEntry(
                    _clock.GetUtcNow(),
                    AppEventCode.StreamingSegmentCommitted));
            }
        }
        catch (OperationCanceledException)
        {
            // The recording ended, which is the ordinary case. What has been committed so far
            // stays usable - the ranges already transcribed are still correct.
        }
        catch (Exception exception) when (
            exception is not (StackOverflowException or OutOfMemoryException))
        {
            // ANY failure gives up on the head start entirely rather than delivering a partial
            // transcript. Half a dictation is worse than a slow one.
            _usable = false;
            // THE CAUGHT EXCEPTION IS READ RATHER THAN DISCARDED. This handler used to assert
            // AsrUnavailable whatever had happened, so a busy runtime, a dead worker and a missing
            // model pack wrote one identical line and no log could tell them apart. The engine
            // already carries the code it failed with, so the category is now observed instead of
            // chosen when the handler was written.
            var error = (exception as TranscriptionEngineException)?.Error;
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.StreamingAbandoned,
                // UNKNOWN WHEN THE EXCEPTION IS UNTYPED, because the catch takes almost anything -
                // a disposed runtime, a memory-mapped read, an infrastructure fault - and calling
                // all of those AsrUnavailable is the same false assertion this change removed for
                // the typed case.
                error is null ? AppFailureCategory.Unknown : AppFailureCategories.For(error),
                ErrorCode: error?.Code));
        }
    }

    /// <summary>Ends the loop and waits for it, however long that takes; what it committed stays usable.</summary>
    public Task<StopOutcome> StopAsync() => StopAsync(deadline: null);

    /// <summary>Ends the loop and waits up to the deadline; a loop still running past it stays owned.</summary>
    public async Task<StopOutcome> StopAsync(TimeSpan? deadline)
    {
        var cancellation = _cancellation;
        var loop = _loop;
        if (cancellation is null)
        {
            return StopOutcome.Completed;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (loop is not null &&
            await BoundedJoin.JoinAsync(loop, deadline, _clock).ConfigureAwait(false) == StopOutcome.StillRunning)
        {
            // STILL RUNNING, STILL OWNED: the loop is inside the engine with a token source it still
            // reads; both stay in their fields for the next stop to join.
            return StopOutcome.StillRunning;
        }

        _cancellation = null;
        _loop = null;
        cancellation.Dispose();
        return StopOutcome.Completed;
    }

    /// <summary>
    /// Transcribes only what streaming did not already cover, and joins the two.
    /// </summary>
    /// <remarks>
    /// THIS IS WHERE STREAMING PAYS. Everything committed while the user was speaking is already
    /// text, so the release only has to recognise the tail - which is why a long dictation stops
    /// costing a long wait.
    ///
    /// IT FALLS BACK TO THE WHOLE RECORDING ON ANY DOUBT, and the conditions are checked here
    /// rather than trusted from the loop. No head start, a failure flag, or a tail that would be
    /// longer than the audio all mean transcribe everything, exactly as before streaming existed.
    /// Half a dictation is worse than a slow one, and this is the last place to refuse.
    ///
    /// THE TAIL'S ENGINE ID AND LANGUAGE ARE THE ONES REPORTED, because they came from the same
    /// engine on the same audio and the committed pieces cannot disagree about them. The token
    /// timings are the tail's alone and are already only used for diagnostics.
    /// </remarks>
    public async Task<Transcript> TranscribeUsingAnyHeadStartAsync(
        ITranscriptionEngine engine,
        CapturedAudio audio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(audio);
        // The runner that calls this has opened the dictation's scope already; opened again here
        // because the line this writes belongs to the audio's dictation whoever the caller is.
        using var dictation = DictationScope.Begin(audio.SessionId.Value);
        var headStart = _streamed.ToString();
        var usable = _usable &&
            _streamedThroughSample > 0 &&
            _streamedThroughSample < audio.Samples.Length &&
            !string.IsNullOrWhiteSpace(headStart);

        if (!usable)
        {
            return await engine.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
        }

        var tailAudio = audio with
        {
            Samples = audio.Samples[_streamedThroughSample..],
        };
        var tail = await engine.TranscribeAsync(tailAudio, cancellationToken).ConfigureAwait(false);

        var joined = new StreamingTranscriptAccumulator();
        joined.Append(headStart);
        joined.Append(tail.Text);

        _logger.Write(new AppLogEntry(
            _clock.GetUtcNow(),
            AppEventCode.StreamingHeadStartUsed));

        return tail with { Text = joined.ToString() };
    }
}
