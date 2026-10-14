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
    private readonly UatRuntimePhaseJournal? _phaseJournal;
    private readonly StreamingTranscriptAccumulator _streamed = new();
    private readonly object _ownership = new();
    private StreamingWork? _work;

    private sealed class StreamingWork
    {
        public CancellationTokenSource Scheduling { get; } = new();
        public CancellationTokenSource Request { get; } = new();
        public Task Loop { get; set; } = Task.CompletedTask;
        public bool AdmissionClosed { get; set; }
        public bool CleanupClosed { get; set; }
        public Task? Stop { get; set; }
        public Task? RequestCancellation { get; set; }
    }
    private int _streamedThroughSample;
    private bool _usable;

    public StreamingTranscriptionController(IStreamingTranscriptionEffects effects, IAppLogger logger, TimeProvider clock,
        UatRuntimePhaseJournal? phaseJournal = null)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _logger = logger;
        _clock = clock;
        _phaseJournal = phaseJournal ?? UatRuntimePhaseJournal.FromEnvironment();
    }

    /// <summary>Whether a streaming loop has been started and not yet stopped.</summary>
    public bool IsRunning { get { lock (_ownership) return _work is not null; } }

    /// <summary>The loop, so a test can wait for one that ends on its own rather than poll for it.</summary>
    internal Task? Loop { get { lock (_ownership) return _work?.Loop; } }

    /// <summary>Forgets the last take and, unless streaming stands down, starts committing this one.</summary>
    public void Start(DictationSessionId sessionId)
    {
        lock (_ownership)
        {
            // An unfinished stop owns the buffer even when its loop has ended.
            if (_work is not null)
            {
                _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.StreamingAbandoned,
                    AppFailureCategory.RuntimeWorker, ErrorCode: AppErrorCode.RuntimeResourceBusy));
                return;
            }

            _streamed.Clear();
            _streamedThroughSample = 0;
            _usable = false;
            if (_effects.LivePreviewEnabled || _effects.Engine is not { } engine ||
                _effects.Audio is not { } snapshots)
            {
                return;
            }

            _usable = true;
            var work = new StreamingWork();
            _work = work;
            work.Loop = RunAsync(snapshots, engine, sessionId, work);
        }
    }

    private async Task RunAsync(
        IAudioSnapshotSource snapshots,
        ITranscriptionEngine engine,
        DictationSessionId sessionId,
        StreamingWork work)
    {
        var cancellationToken = work.Scheduling.Token;
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
                Task<Transcript> request;
                lock (_ownership)
                {
                    // Selection alone is not admission: stop and issue share this boundary.
                    if (work.AdmissionClosed) break;
                    request = engine.TranscribeAsync(
                        new CapturedAudio(sessionId, slice, snapshot.SampleRate, snapshot.Channels),
                        work.Request.Token);
                }
                var transcript = await request.ConfigureAwait(false);
                work.Request.Token.ThrowIfCancellationRequested();

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

    /// <summary>Cancels active speech work and joins the same owned stop.</summary>
    public Task<StopOutcome> StopAsync() => StopAsync(deadline: null);

    /// <summary>Cancels active speech work; unfinished callbacks and work remain owned.</summary>
    public Task<StopOutcome> StopAsync(TimeSpan? deadline) => StopOwnedAsync(deadline, ordinary: false, CancellationToken.None);

    /// <summary>
    /// Stops admission, allowing issued speech work one second of patience before cancellation.
    /// Patience is policy, not a hardware guarantee. The supplied budget bounds observation of the
    /// whole owned stop; cancellation of that observation never means the speech work has ended.
    /// </summary>
    public Task<StopOutcome> CompleteOrdinaryAsync(TimeSpan cleanupBudget, CancellationToken processingToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupBudget, TimeSpan.Zero);
        return StopOwnedAsync(cleanupBudget, ordinary: true, processingToken);
    }

    private async Task<StopOutcome> StopOwnedAsync(TimeSpan? deadline, bool ordinary, CancellationToken processingToken)
    {
        if (deadline is { } limit) ArgumentOutOfRangeException.ThrowIfLessThan(limit, TimeSpan.Zero);
        _phaseJournal?.Record(UatRuntimePhase.StopEntered, DictationScope.Current);
        StreamingWork work;
        Task stop;
        lock (_ownership)
        {
            if (_work is not { } current) return StopOutcome.Completed;
            work = current;
            if (!ordinary) RequestCancellation(work);
            if (work.Stop is null)
            {
                work.AdmissionClosed = true;
                var schedulingCancellation = work.Scheduling.CancelAsync();
                work.Stop = FinishStopAsync(work, schedulingCancellation, ordinary, processingToken);
            }
            stop = work.Stop;
        }

        try
        {
            return await BoundedJoin.JoinAsync(stop, deadline, _clock).ConfigureAwait(false);
        }
        finally
        {
            lock (_ownership)
            {
                if (stop.IsCompleted && ReferenceEquals(_work, work))
                {
                    // FinishStop includes callbacks and registrations, not just the loop.
                    _work = null;
                    work.Scheduling.Dispose();
                    work.Request.Dispose();
                }
            }
        }
    }

    private void RequestCancellation(StreamingWork work)
    {
        lock (_ownership)
        {
            if (work.CleanupClosed || work.RequestCancellation is not null) return;
            work.AdmissionClosed = true;
            work.RequestCancellation = work.Request.CancelAsync();
            _phaseJournal?.Record(UatRuntimePhase.CancellationRequested, DictationScope.Current);
        }
    }

    private async Task FinishStopAsync(StreamingWork work, Task schedulingCancellation,
        bool ordinary, CancellationToken processingToken)
    {
        if (ordinary)
        {
            await using var registration = processingToken.Register(() => RequestCancellation(work));
            try
            {
                await work.Loop.WaitAsync(TimeSpan.FromSeconds(1), _clock, processingToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                RequestCancellation(work);
            }
            catch (OperationCanceledException)
            {
                // The wait can lose a race with loop completion. Either way, still join all cleanup.
                if (!work.Loop.IsCompleted) RequestCancellation(work);
            }
        }

        // Cancellation callbacks may still be using request resources after the loop ends.
        // No second stop owner is created when an abort escalates an ordinary drain.
        await work.Loop.ConfigureAwait(false);
        Task requestCancellation;
        lock (_ownership)
        {
            work.CleanupClosed = true;
            requestCancellation = work.RequestCancellation ?? Task.CompletedTask;
        }
        await Task.WhenAll(schedulingCancellation, requestCancellation).ConfigureAwait(false);
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
        lock (_ownership)
        {
            if (_work is not null)
            {
                throw new TranscriptionEngineException(new AppError(AppErrorCode.RuntimeResourceBusy,
                    AppErrorStage.RuntimeWorker, CanRetry: true));
            }
        }
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
