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
        public required Lazy<Task> CancellationDispatch { get; set; }
        public bool OrdinaryStop { get; set; }
        public CancellationToken ProcessingToken { get; set; }
        public Func<TimeSpan>? CleanupRemaining { get; set; }
    }

    private DictationSessionId? _sessionId;
    private Transcript? _lastCommittedTranscript;
    private int _streamedThroughSample;
    private int _lastSnapshotSamples;
    private bool _hadEmptySegment;
    private bool _usable;

    public StreamingTranscriptionController(
        IStreamingTranscriptionEffects effects,
        IAppLogger logger,
        TimeProvider clock,
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
    public bool IsRunning
    {
        get
        {
            lock (_ownership)
            {
                return _work is not null;
            }
        }
    }

    /// <summary>The loop, so a test can wait for one that ends on its own rather than poll for it.</summary>
    internal Task? Loop
    {
        get
        {
            lock (_ownership)
            {
                return _work?.Loop;
            }
        }
    }

    /// <summary>Forgets the last take and, unless streaming stands down, starts committing this one.</summary>
    public void Start(DictationSessionId sessionId)
    {
        lock (_ownership)
        {
            // An unfinished stop owns the buffer even when its loop has ended.
            if (_work is not null)
            {
                _logger.Write(new AppLogEntry(
                    _clock.GetUtcNow(),
                    AppEventCode.StreamingAbandoned,
                    AppFailureCategory.RuntimeWorker,
                    ErrorCode: AppErrorCode.RuntimeResourceBusy));
                return;
            }

            _streamed.Clear();
            _lastCommittedTranscript = null;
            _streamedThroughSample = 0;
            _lastSnapshotSamples = 0;
            _hadEmptySegment = false;
            _sessionId = sessionId;
            _usable = false;
            if (_effects.LivePreviewEnabled ||
                _effects.Engine is not { } engine ||
                _effects.Audio is not { } snapshots)
            {
                return;
            }

            _usable = true;
            StreamingWork work = null!;
            work = new StreamingWork
            {
                // The processing callback only schedules this owned dispatch. It never waits on
                // the ownership lock or on native cancellation callbacks on the cancelling thread.
                CancellationDispatch = new Lazy<Task>(() => Task.Run(() => RequestCancellation(work))),
            };
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
                lock (_ownership)
                {
                    if (work.AdmissionClosed)
                    {
                        break;
                    }
                }

                int snapshotStartSample;
                int totalCapturedSamples;
                ReadOnlyMemory<float> snapshotSamples;
                int snapshotSampleRate;
                int snapshotChannels;
                if (snapshots is IAudioIncrementalSnapshotSource incremental)
                {
                    var incrementalSnapshot = incremental.GetSnapshotFrom(_streamedThroughSample);
                    if (incrementalSnapshot is null ||
                        incrementalSnapshot.SessionId != sessionId ||
                        incrementalSnapshot.Samples.IsEmpty)
                    {
                        continue;
                    }

                    snapshotStartSample = incrementalSnapshot.StartSample;
                    totalCapturedSamples = incrementalSnapshot.TotalCapturedSamples;
                    snapshotSamples = incrementalSnapshot.Samples;
                    snapshotSampleRate = incrementalSnapshot.SampleRate;
                    snapshotChannels = 1;
                }
                else
                {
                    // Legacy fallback for snapshot-only sources: request the whole buffer.
                    var snapshot = snapshots.GetSnapshot(TimeSpan.MaxValue);
                    if (snapshot is null || snapshot.SessionId != sessionId)
                    {
                        continue;
                    }

                    snapshotStartSample = 0;
                    totalCapturedSamples = snapshot.Samples.Length;
                    snapshotSamples = snapshot.Samples;
                    snapshotSampleRate = snapshot.SampleRate;
                    snapshotChannels = snapshot.Channels;
                }

                // THE SEGMENTER LEARNS THE RATE FROM THE AUDIO IT SEGMENTS, at the first snapshot,
                // rather than assuming the capture's target rate from a project this one does not
                // reference. The rate does not change within a take.
                segmenter ??= new SpeechSegmenter(snapshotSampleRate, SegmentPause);
                var localCommitted = Math.Max(0, _streamedThroughSample - snapshotStartSample);
                var localCommit = StreamingCommitPlanner.NextCommit(
                    snapshotSamples.Span,
                    snapshotSampleRate,
                    localCommitted,
                    segmenter);
                if (localCommit is not { } localRange)
                {
                    continue;
                }

                var range = new StreamingCommit(
                    snapshotStartSample + localRange.StartSample,
                    snapshotStartSample + localRange.EndSample);
                var slice = snapshotSamples.Slice(
                    localRange.StartSample,
                    localRange.LengthSamples);
                var coverage = _phaseJournal is not null
                    ? segmenter.MeasureCoverage(snapshotSamples.Span, localRange.StartSample, localRange.EndSample)
                    : default;
                var remainingInSnapshot = Math.Max(0, totalCapturedSamples - range.EndSample);
                var sampleRate = snapshotSampleRate > 0 ? (int?)snapshotSampleRate : null;
                var segmentStarted = _clock.GetTimestamp();
                using var correlation = UatRuntimePhaseJournal.BeginRequestCorrelation(out var requestId);
                Task<Transcript> request;
                lock (_ownership)
                {
                    // Selection alone is not admission: stop and issue share this boundary.
                    if (work.AdmissionClosed)
                    {
                        break;
                    }

                    _phaseJournal?.Record(
                        UatRuntimePhase.StreamingSegmentAdmitted,
                        sessionId.Value,
                        requestId: requestId,
                        startSample: range.StartSample,
                        endSample: range.EndSample,
                        coveredSamples: _streamedThroughSample,
                        remainingSamples: remainingInSnapshot,
                        speechSamples: coverage.SpeechSamples,
                        silenceSamples: coverage.SilenceSamples,
                        sampleRate: sampleRate);
                    request = engine.TranscribeAsync(
                        new CapturedAudio(sessionId, slice, snapshotSampleRate, snapshotChannels),
                        work.Request.Token);
                }

                Transcript transcript;
                try
                {
                    transcript = await request.ConfigureAwait(false);
                    work.Request.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    _phaseJournal?.Record(
                        UatRuntimePhase.StreamingSegmentCancelled,
                        sessionId.Value,
                        requestId: correlation.RequestId,
                        startSample: range.StartSample,
                        endSample: range.EndSample,
                        coveredSamples: _streamedThroughSample,
                        remainingSamples: remainingInSnapshot,
                        speechSamples: coverage.SpeechSamples,
                        silenceSamples: coverage.SilenceSamples,
                        sampleRate: sampleRate);
                    throw;
                }
                catch (Exception exception) when (
                    exception is not (StackOverflowException or OutOfMemoryException))
                {
                    _phaseJournal?.Record(
                        UatRuntimePhase.StreamingSegmentFailed,
                        sessionId.Value,
                        requestId: correlation.RequestId,
                        startSample: range.StartSample,
                        endSample: range.EndSample,
                        coveredSamples: _streamedThroughSample,
                        remainingSamples: remainingInSnapshot,
                        speechSamples: coverage.SpeechSamples,
                        silenceSamples: coverage.SilenceSamples,
                        sampleRate: sampleRate);
                    throw;
                }

                var segmentElapsedMs = (long)_clock.GetElapsedTime(segmentStarted).TotalMilliseconds;
                var isSegmentEmpty = string.IsNullOrWhiteSpace(transcript.Text);
                if (isSegmentEmpty)
                {
                    _hadEmptySegment = true;
                }
                else
                {
                    _streamed.Append(transcript.Text);
                    _lastCommittedTranscript = _lastCommittedTranscript is { UsedFallback: true } prior && !transcript.UsedFallback
                        ? transcript with { UsedFallback = true, DegradedError = prior.DegradedError }
                        : transcript;
                }

                _streamedThroughSample = range.EndSample;
                _lastSnapshotSamples = totalCapturedSamples;
                _phaseJournal?.Record(
                    UatRuntimePhase.StreamingSegmentReturned,
                    sessionId.Value,
                    requestId: correlation.RequestId,
                    startSample: range.StartSample,
                    endSample: range.EndSample,
                    coveredSamples: _streamedThroughSample,
                    remainingSamples: remainingInSnapshot,
                    speechSamples: coverage.SpeechSamples,
                    silenceSamples: coverage.SilenceSamples,
                    sampleRate: sampleRate);
                _logger.Write(new AppLogEntry(
                    _clock.GetUtcNow(),
                    isSegmentEmpty
                        ? AppEventCode.StreamingSegmentEmpty
                        : AppEventCode.StreamingSegmentCommitted,
                    ElapsedMilliseconds: segmentElapsedMs));
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

    /// <summary>
    /// Closes admission for new streaming polls or segment requests while leaving any already-issued
    /// segment request running until ordinary completion or stop joins it.
    /// </summary>
    public void CloseAdmission()
    {
        lock (_ownership)
        {
            if (_work is not { } work || work.CleanupClosed)
            {
                return;
            }

            work.AdmissionClosed = true;
        }
    }

    /// <summary>Cancels active speech work and joins the same owned stop.</summary>
    public Task<StopOutcome> StopAsync() => StopAsync(deadline: null);

    /// <summary>Cancels active speech work; unfinished callbacks and work remain owned.</summary>
    public Task<StopOutcome> StopAsync(TimeSpan? deadline, CancellationToken cancellationToken = default) =>
        StopOwnedAsync(deadline, ordinary: false, cleanupRemaining: null, CancellationToken.None, cancellationToken);

    /// <summary>
    /// Stops admission while issued speech work finishes under the original processing deadline.
    /// Only cancellation or post-processing cleanup starts the shared cleanup allowance. An expired
    /// observation never means that native work or its cancellation callbacks have ended.
    /// </summary>
    public Task<StopOutcome> CompleteOrdinaryAsync(Func<TimeSpan> cleanupRemaining, CancellationToken processingToken)
    {
        ArgumentNullException.ThrowIfNull(cleanupRemaining);
        return StopOwnedAsync(null, ordinary: true, cleanupRemaining, processingToken, CancellationToken.None);
    }

    private async Task<StopOutcome> StopOwnedAsync(
        TimeSpan? deadline,
        bool ordinary,
        Func<TimeSpan>? cleanupRemaining,
        CancellationToken processingToken,
        CancellationToken waitCancellation)
    {
        if (deadline is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, TimeSpan.Zero);
        }

        _phaseJournal?.Record(UatRuntimePhase.StopEntered, DictationScope.Current, coveredSamples: _streamedThroughSample);
        StreamingWork work;
        Task stop;
        lock (_ownership)
        {
            if (_work is not { } current)
            {
                _phaseJournal?.Record(UatRuntimePhase.StopCompleted, DictationScope.Current, coveredSamples: _streamedThroughSample);
                return StopOutcome.Completed;
            }

            work = current;
            if (!ordinary)
            {
                RequestCancellation(work);
            }

            if (work.Stop is null)
            {
                work.AdmissionClosed = true;
                work.OrdinaryStop = ordinary;
                work.ProcessingToken = processingToken;
                work.CleanupRemaining = cleanupRemaining;
                var schedulingCancellation = work.Scheduling.CancelAsync();
                work.Stop = FinishStopAsync(work, schedulingCancellation, ordinary, processingToken);
            }

            stop = work.Stop;
        }

        try
        {
            if (ordinary && work.OrdinaryStop)
            {
                try
                {
                    await stop.WaitAsync(work.ProcessingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (work.ProcessingToken.IsCancellationRequested)
                {
                    return await BoundedJoin.JoinAsync(stop, work.CleanupRemaining!(), _clock, CancellationToken.None).ConfigureAwait(false);
                }

                return StopOutcome.Completed;
            }

            // An abort that won the first-stop boundary cannot be revived by an ordinary caller.
            return await BoundedJoin
                .JoinAsync(stop, ordinary ? cleanupRemaining!() : deadline, _clock, waitCancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            if (stop.IsCompleted)
            {
                try
                {
                    // Completion may race a timed-out wait. Observe the actual ending before release.
                    await stop.ConfigureAwait(false);
                }
                finally
                {
                    var completedOwnedWork = false;
                    lock (_ownership)
                    {
                        if (ReferenceEquals(_work, work))
                        {
                            _work = null;
                            work.Scheduling.Dispose();
                            work.Request.Dispose();
                            completedOwnedWork = true;
                        }
                    }

                    if (completedOwnedWork)
                    {
                        _phaseJournal?.Record(UatRuntimePhase.StopCompleted, DictationScope.Current, coveredSamples: _streamedThroughSample);
                    }
                }
            }
        }
    }

    // Joins only the stop already owned; never escalates or creates another cleanup operation.
    internal async Task<StopOutcome> JoinOwnedStopAsync()
    {
        StreamingWork? work;
        Task? stop;
        lock (_ownership)
        {
            work = _work;
            if (work is null)
            {
                return StopOutcome.Completed;
            }

            stop = work.Stop;
            if (stop is null)
            {
                return StopOutcome.StillRunning;
            }
        }

        try
        {
            await stop.ConfigureAwait(false);
            return StopOutcome.Completed;
        }
        finally
        {
            if (stop.IsCompleted)
            {
                var completedOwnedWork = false;
                lock (_ownership)
                {
                    if (ReferenceEquals(_work, work))
                    {
                        _work = null;
                        work.Scheduling.Dispose();
                        work.Request.Dispose();
                        completedOwnedWork = true;
                    }
                }

                if (completedOwnedWork)
                {
                    _phaseJournal?.Record(UatRuntimePhase.StopCompleted, DictationScope.Current, coveredSamples: _streamedThroughSample);
                }
            }
        }
    }

    private void RequestCancellation(StreamingWork work)
    {
        lock (_ownership)
        {
            if (work.CleanupClosed || work.RequestCancellation is not null)
            {
                return;
            }

            work.AdmissionClosed = true;
            work.RequestCancellation = work.Request.CancelAsync();
            _phaseJournal?.Record(UatRuntimePhase.CancellationRequested, DictationScope.Current);
        }
    }

    private async Task FinishStopAsync(
        StreamingWork work,
        Task schedulingCancellation,
        bool ordinary,
        CancellationToken processingToken)
    {
        try
        {
            if (ordinary)
            {
                await using var registration = processingToken.Register(
                    static state => { _ = ((Lazy<Task>)state!).Value; },
                    work.CancellationDispatch);
                try
                {
                    await work.Loop.WaitAsync(processingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (processingToken.IsCancellationRequested)
                {
                    _ = work.CancellationDispatch.Value;
                }
            }
        }
        finally
        {
            try
            {
                await work.Loop.ConfigureAwait(false);
            }
            finally
            {
                // An escaping loop fault still joins its callbacks before stop completion.
                try
                {
                    // The processing registration is already disposed. No dispatch can appear
                    // after this join, and a queued dispatch must finish before CTS disposal.
                    if (work.CancellationDispatch.IsValueCreated)
                    {
                        await work.CancellationDispatch.Value.ConfigureAwait(false);
                    }
                }
                finally
                {
                    Task requestCancellation;
                    lock (_ownership)
                    {
                        work.CleanupClosed = true;
                        requestCancellation = work.RequestCancellation ?? Task.CompletedTask;
                    }

                    await Task.WhenAll(schedulingCancellation, requestCancellation).ConfigureAwait(false);
                }
            }
        }
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
    /// Only when the final tail or whole-recording request itself fails without caller cancellation
    /// does it preserve already-committed words as a degraded fallback rather than losing everything.
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
                throw new TranscriptionEngineException(new AppError(
                    AppErrorCode.RuntimeResourceBusy,
                    AppErrorStage.RuntimeWorker,
                    CanRetry: true));
            }
        }
        // The runner that calls this has opened the dictation's scope already; opened again here
        // because the line this writes belongs to the audio's dictation whoever the caller is.
        using var dictation = DictationScope.Begin(audio.SessionId.Value);
        var sameSession = _sessionId == audio.SessionId;
        var headStart = sameSession ? _streamed.ToString() : string.Empty;
        var segmenter = _phaseJournal is not null && audio.SampleRate > 0
            ? new SpeechSegmenter(audio.SampleRate, SegmentPause)
            : null;
        var usable = _usable &&
            !_hadEmptySegment &&
            sameSession &&
            _streamedThroughSample > 0 &&
            (_streamedThroughSample < audio.Samples.Length ||
             (_streamedThroughSample == audio.Samples.Length &&
              audio.Outcome == AudioCaptureOutcome.Completed)) &&
            audio.Samples.Length >= _lastSnapshotSamples &&
            !string.IsNullOrWhiteSpace(headStart);

        if (!usable)
        {
            try
            {
                return await TranscribeWholeAudioAsync(
                        engine,
                        audio,
                        segmenter,
                        sameSession ? _streamedThroughSample : 0,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception) when (
                CanPreserveCommittedHeadStart(audio, headStart, sameSession, exception, cancellationToken))
            {
                return PreserveCommittedHeadStart(audio.SessionId, headStart, engine.EngineId, exception);
            }
        }

        var remainingSamples = audio.Samples.Length - _streamedThroughSample;
        if (remainingSamples == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _phaseJournal?.Record(
                UatRuntimePhase.FinalTailSkipped,
                audio.SessionId.Value,
                startSample: _streamedThroughSample,
                endSample: audio.Samples.Length,
                coveredSamples: _streamedThroughSample,
                remainingSamples: 0,
                speechSamples: 0,
                silenceSamples: 0,
                sampleRate: audio.SampleRate > 0 ? audio.SampleRate : null);
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.StreamingHeadStartUsed,
                ElapsedMilliseconds: 0));
            return new Transcript(
                audio.SessionId,
                headStart,
                _lastCommittedTranscript?.EngineId ?? engine.EngineId,
                TokenTimings: _lastCommittedTranscript?.TokenTimings ?? [],
                UsedFallback: _lastCommittedTranscript?.UsedFallback ?? false,
                DegradedError: _lastCommittedTranscript?.DegradedError,
                DetectedLanguage: _lastCommittedTranscript?.DetectedLanguage,
                RecognitionLanguage: _lastCommittedTranscript?.RecognitionLanguage);
        }

        var tailCoverage = segmenter?.MeasureCoverage(audio.Samples.Span, _streamedThroughSample, audio.Samples.Length)
            ?? default;
        var tailAudio = audio with
        {
            Samples = audio.Samples[_streamedThroughSample..],
        };
        var tailStarted = _clock.GetTimestamp();
        Transcript tail;
        try
        {
            using var tailCorrelation = UatRuntimePhaseJournal.BeginRequestCorrelation(out var tailRequestId);
            _phaseJournal?.Record(
                UatRuntimePhase.FinalTailDispatched,
                audio.SessionId.Value,
                requestId: tailRequestId,
                startSample: _streamedThroughSample,
                endSample: audio.Samples.Length,
                coveredSamples: _streamedThroughSample,
                remainingSamples: remainingSamples,
                speechSamples: tailCoverage.SpeechSamples,
                silenceSamples: tailCoverage.SilenceSamples,
                sampleRate: audio.SampleRate > 0 ? audio.SampleRate : null);
            tail = await TranscribePossiblyLongAudioAsync(engine, tailAudio, cancellationToken)
                .ConfigureAwait(false);
            _phaseJournal?.Record(
                UatRuntimePhase.FinalTailReturned,
                audio.SessionId.Value,
                requestId: tailCorrelation.RequestId,
                startSample: _streamedThroughSample,
                endSample: audio.Samples.Length,
                coveredSamples: audio.Samples.Length,
                remainingSamples: 0,
                speechSamples: tailCoverage.SpeechSamples,
                silenceSamples: tailCoverage.SilenceSamples,
                sampleRate: audio.SampleRate > 0 ? audio.SampleRate : null);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (TranscriptionEngineException exception) when (
            exception.Error.Code == AppErrorCode.TranscriptionInputTooShort &&
            exception.Error.Stage == AppErrorStage.FinalAsr)
        {
            if (IsRunning)
            {
                // An unfinished stop still owns the loop; it may admit or finish engine work.
                // This refusal must not start another request while that ownership remains.
                throw;
            }

            // This precise error is emitted before native admission. Other errors do not promise
            // that the runtime is safe to reuse, so they never enter this recovery path.
            _usable = false;
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.StreamingAbandoned,
                AppFailureCategories.For(exception.Error),
                ErrorCode: exception.Error.Code));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await TranscribeWholeAudioAsync(
                        engine,
                        audio,
                        segmenter,
                        _streamedThroughSample,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception wholeException) when (
                CanPreserveCommittedHeadStart(audio, headStart, sameSession, wholeException, cancellationToken))
            {
                return PreserveCommittedHeadStart(audio.SessionId, headStart, engine.EngineId, wholeException);
            }
        }
        catch (Exception exception) when (
            CanPreserveCommittedHeadStart(audio, headStart, sameSession, exception, cancellationToken))
        {
            return PreserveCommittedHeadStart(audio.SessionId, headStart, engine.EngineId, exception);
        }

        var tailElapsedMs = (long)_clock.GetElapsedTime(tailStarted).TotalMilliseconds;
        var joined = new StreamingTranscriptAccumulator();
        joined.Append(headStart);
        joined.Append(tail.Text);

        _logger.Write(new AppLogEntry(
            _clock.GetUtcNow(),
            AppEventCode.StreamingHeadStartUsed,
            ElapsedMilliseconds: tailElapsedMs));

        var usedFallback = tail.UsedFallback || (_lastCommittedTranscript?.UsedFallback ?? false);
        var degradedError = tail.DegradedError ?? _lastCommittedTranscript?.DegradedError;
        return tail with
        {
            Text = joined.ToString(),
            UsedFallback = usedFallback,
            DegradedError = degradedError,
        };
    }

    private async Task<Transcript> TranscribeWholeAudioAsync(
        ITranscriptionEngine engine,
        CapturedAudio audio,
        SpeechSegmenter? segmenter,
        int coveredSamples,
        CancellationToken cancellationToken)
    {
        using var dictation = DictationScope.Begin(audio.SessionId.Value);
        var wholeCoverage = segmenter?.MeasureCoverage(audio.Samples.Span, 0, audio.Samples.Length)
            ?? default;
        using var wholeCorrelation = UatRuntimePhaseJournal.BeginRequestCorrelation(out var wholeRequestId);
        _phaseJournal?.Record(
            UatRuntimePhase.FinalWholeAudioDispatched,
            audio.SessionId.Value,
            requestId: wholeRequestId,
            startSample: 0,
            endSample: audio.Samples.Length,
            coveredSamples: Math.Clamp(coveredSamples, 0, audio.Samples.Length),
            remainingSamples: audio.Samples.Length,
            speechSamples: wholeCoverage.SpeechSamples,
            silenceSamples: wholeCoverage.SilenceSamples,
            sampleRate: audio.SampleRate > 0 ? audio.SampleRate : null);
        var transcript = await TranscribePossiblyLongAudioAsync(engine, audio, cancellationToken)
            .ConfigureAwait(false);
        _phaseJournal?.Record(
            UatRuntimePhase.FinalWholeAudioReturned,
            audio.SessionId.Value,
            requestId: wholeCorrelation.RequestId,
            startSample: 0,
            endSample: audio.Samples.Length,
            coveredSamples: audio.Samples.Length,
            remainingSamples: 0,
            speechSamples: wholeCoverage.SpeechSamples,
            silenceSamples: wholeCoverage.SilenceSamples,
            sampleRate: audio.SampleRate > 0 ? audio.SampleRate : null);
        return transcript;
    }

    private static async Task<Transcript> TranscribePossiblyLongAudioAsync(
        ITranscriptionEngine engine,
        CapturedAudio audio,
        CancellationToken cancellationToken)
    {
        using var dictation = DictationScope.Begin(audio.SessionId.Value);
        var sampleRate = audio.SampleRate > 0 ? audio.SampleRate : AudioQuietCut.DefaultSampleRate;
        var maxPieceSamples = checked(sampleRate * AudioQuietCut.MaximumPieceSeconds);
        if (audio.Samples.Length <= maxPieceSamples)
        {
            return await engine.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
        }

        var assembled = new StreamingTranscriptAccumulator();
        Transcript? firstTranscript = null;
        var usedFallback = false;
        AppError? degradedError = null;
        var offset = 0;

        while (offset < audio.Samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = audio.Samples.Span[offset..];
            var pieceLength = AudioQuietCut.NextPieceLength(remaining, atEnd: true, sampleRate);
            if (pieceLength <= 0)
            {
                break;
            }

            var piece = audio.Samples.Slice(offset, pieceLength);
            offset += pieceLength;
            if (AudioQuietCut.IsSilent(piece.Span))
            {
                continue;
            }

            Transcript pieceTranscript;
            try
            {
                pieceTranscript = await engine.TranscribeAsync(
                    audio with { Samples = piece },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TranscriptionEngineException exception) when (
                !cancellationToken.IsCancellationRequested &&
                firstTranscript is not null &&
                !assembled.IsEmpty)
            {
                // Preserve all earlier transcribed pieces rather than losing up to an hour of dictation.
                return new Transcript(
                    audio.SessionId,
                    assembled.ToString(),
                    firstTranscript.EngineId,
                    TokenTimings: firstTranscript.TokenTimings,
                    UsedFallback: true,
                    DegradedError: exception.Error,
                    DetectedLanguage: firstTranscript.DetectedLanguage,
                    RecognitionLanguage: firstTranscript.RecognitionLanguage);
            }

            firstTranscript ??= pieceTranscript;
            usedFallback |= pieceTranscript.UsedFallback;
            degradedError ??= pieceTranscript.DegradedError;
            if (!string.IsNullOrWhiteSpace(pieceTranscript.Text))
            {
                assembled.Append(pieceTranscript.Text);
            }
        }

        if (firstTranscript is null)
        {
            return new Transcript(audio.SessionId, string.Empty, engine.EngineId);
        }

        return new Transcript(
            audio.SessionId,
            assembled.ToString(),
            firstTranscript.EngineId,
            TokenTimings: firstTranscript.TokenTimings,
            UsedFallback: usedFallback,
            DegradedError: degradedError,
            DetectedLanguage: firstTranscript.DetectedLanguage,
            RecognitionLanguage: firstTranscript.RecognitionLanguage);
    }

    private bool CanPreserveCommittedHeadStart(
        CapturedAudio audio,
        string headStart,
        bool sameSession,
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is not (OperationCanceledException or StackOverflowException or OutOfMemoryException) &&
        !cancellationToken.IsCancellationRequested &&
        audio.Outcome != AudioCaptureOutcome.Cancelled &&
        !IsRunning &&
        sameSession &&
        _streamedThroughSample > 0 &&
        !string.IsNullOrWhiteSpace(headStart);

    private Transcript PreserveCommittedHeadStart(
        DictationSessionId sessionId,
        string headStart,
        string fallbackEngineId,
        Exception exception)
    {
        _usable = false;
        var error = (exception as TranscriptionEngineException)?.Error;
        _logger.Write(new AppLogEntry(
            _clock.GetUtcNow(),
            AppEventCode.StreamingHeadStartPreserved,
            error is null ? AppFailureCategory.Unknown : AppFailureCategories.For(error),
            ErrorCode: error?.Code));
        return new Transcript(
            sessionId,
            headStart,
            _lastCommittedTranscript?.EngineId ?? fallbackEngineId,
            TokenTimings: [],
            UsedFallback: true,
            DegradedError: error,
            DetectedLanguage: _lastCommittedTranscript?.DetectedLanguage,
            RecognitionLanguage: _lastCommittedTranscript?.RecognitionLanguage);
    }
}
