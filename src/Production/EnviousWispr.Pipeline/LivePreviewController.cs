using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Pipeline;

/// <summary>
/// The shell's half of live preview: the engine it built, the capture it can sample, the setting the
/// user chose, and the surface the words go on. Nothing here decides; the controller does.
/// </summary>
public interface ILivePreviewEffects
{
    /// <summary>The user's Live Preview switch, read at the moment a recording starts.</summary>
    bool Enabled { get; }

    /// <summary>The preview engine the shell built, or null when it could not.</summary>
    ILivePreviewEngine? Engine { get; }

    /// <summary>
    /// Why <see cref="Engine"/> is null, when it is. Held by the shell rather than logged where it
    /// was discovered, and reported only at the moment somebody actually turns Live Preview on.
    /// </summary>
    AppErrorCode? EngineUnavailableReason { get; }

    /// <summary>The take so far, or null when the capture in force cannot be sampled.</summary>
    IAudioSnapshotSource? Audio { get; }

    /// <summary>The dictation being recorded right now, or null; the stop path joins its lines to it.</summary>
    DictationSessionId? RecordingSessionId { get; }

    /// <summary>Words for the screen, tagged with the dictation they belong to and with whether their screen is still open.</summary>
    /// <remarks>
    /// THE FRAME CARRIES ITS OWN VALIDITY. Rendering happens on another thread, later; a frame that
    /// was current when it was handed over may be stale by the time it is drawn. The renderer asks
    /// the frame at the draw, not this controller at the hand-over.
    /// </remarks>
    void ShowPreview(LivePreviewFrame frame);

    /// <summary>The preview surface goes blank.</summary>
    void ClearPreview();
}

/// <summary>
/// Owns a live preview from the first snapshot to the last: starting the engine, the loop that keeps
/// words on screen at the cadence, and the stop that every path - release, cancel, watchdog, lock,
/// quit - reaches. Runs without a window.
/// </summary>
/// <remarks>
/// START AND STOP ARE SERIALISED ON ONE GATE, because stopping is reached from more places than
/// starting and two of them can coincide: a watchdog firing while the release it was guarding is
/// already stopping the loop. Under the gate a second start finds the first loop and returns; a
/// second stop finds nothing and returns. Neither can observe the other half-way.
///
/// THE ENGINE STARTS IN THE BACKGROUND, AND THE GATE IS NOT HELD WHILE IT DOES. Starting the preview
/// worker can take seconds and is allowed fifteen; the recording-start transition used to wait for
/// it, so a key released during that wait was queued behind it and the microphone stayed open until
/// the worker answered. Now the start returns once the work is owned, the transition completes, and a
/// release reaches the capture at once. The stop cancels a startup still in flight through the same
/// token the loop uses, waits for it to leave, and stops the engine, which releases whatever the
/// startup had acquired; the final transcription still waits for that, so it never shares the
/// machine with a preview worker that is half-way up.
///
/// THE LOOP IS DISPLAY-ONLY. It can never change the final transcript, and a failed pass ends the loop
/// with a line in the log rather than a word on screen. Release or cancellation stops it through the
/// token, which is its normal exit and not a failure.
///
/// AN UPDATE CARRIES ITS DICTATION. The engine tags each update with the session the snapshot came
/// from; one tagged with any other dictation is not this preview's to show, and is dropped.
/// </remarks>
public sealed class LivePreviewController : IAsyncDisposable
{
    /// <summary>The most audio one pass is given; older samples have already been previewed.</summary>
    private static readonly TimeSpan MaximumWindow = TimeSpan.FromSeconds(20);

    /// <summary>Half a second of 16 kHz audio: fewer samples than this are not worth a pass.</summary>
    private const int MinimumSamplesPerPass = 8_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILivePreviewEffects _effects;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cancellation;
    private Task? _work;
    private Task<RuntimeWorkerResult>? _engineStop;
    private bool _engineRefusedStop;
    private long _sequence;
    private int _closure;
    private int _stopRequests;
    private bool _started;
    private bool _disposed;

    public LivePreviewController(ILivePreviewEffects effects, IAppLogger logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _effects = effects;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Whether a preview has been started - its engine may still be starting - and not yet stopped.</summary>
    public bool IsRunning => _work is not null || _engineStop is not null || _engineRefusedStop;

    /// <summary>The owned work, startup and loop, so a test can wait for one that ends on its own rather than poll.</summary>
    internal Task? Work => _work;

    public async Task StartAsync(DictationSessionId sessionId)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var scope = DictationScope.Begin(sessionId.Value);
        // THE START'S PLACE IN LINE IS TAKEN BEFORE ANYTHING ELSE, before the shell is read and before
        // the gate: a stop that lands anywhere after this - while the shell is being read, while the
        // gate is waited for, while the work is being published - is counted, and the start sees the
        // count change once it has published and takes its own work down.
        var requestsBefore = Volatile.Read(ref _stopRequests);
        if (!_effects.Enabled)
        {
            return;
        }

        var engine = _effects.Engine;
        var audio = _effects.Audio;
        // THE USER HAS ASKED FOR THIS BY THE TIME WE GET HERE, so a refusal is news and the two
        // reasons are different facts. A missing preview model is a thing they can fix by installing
        // one; a capture source that cannot be sampled is not. Reporting them as one silent return
        // is what let somebody switch Live Preview on, watch the toggle stay on, see nothing happen,
        // and find no trace of why.
        if (engine is null)
        {
            var reason = _effects.EngineUnavailableReason ?? AppErrorCode.RuntimeProviderUnavailable;
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.LivePreviewFailed,
                reason == AppErrorCode.ModelPackUnavailable
                    ? AppFailureCategory.AsrUnavailable
                    : AppFailureCategory.RuntimeProvider,
                ErrorCode: reason));
            return;
        }

        if (audio is null)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.LivePreviewFailed,
                AppFailureCategory.AudioUnavailable,
                ErrorCode: AppErrorCode.AudioDeviceUnavailable));
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // REFUSED WHILE THE LAST LOOP IS STILL OWNED, or its engine's stop is still in flight: a
            // second loop beside the first would share the engine and the screen with it. The next
            // stop joins what is left; a preview starts again once that has completed.
            if (_work is not null || _engineStop is not null || _engineRefusedStop)
            {
                return;
            }

            // SUPERSEDED BEFORE IT STARTS. A stop that landed since this start took its place is a
            // stop of this start too: nothing is published and no loop runs a pass - not even a
            // synchronous one - for a screen that was closed while the start was on its way.
            if (Volatile.Read(ref _stopRequests) != requestsBefore)
            {
                return;
            }

            _sequence = 0;
            _started = false;
            var cancellation = new CancellationTokenSource();
            var closure = Volatile.Read(ref _closure);
            Volatile.Write(ref _cancellation, cancellation);
            // THE SOURCE IS PUBLISHED BEFORE THE LOOP EXISTS, and the count is read again after: a stop
            // that lands from here on cancels this very source, and one that landed between the check
            // above and the publication - which found nothing to cancel - is seen here, before the
            // loop is created with a token already cancelled and a screen already closed.
            if (Volatile.Read(ref _stopRequests) != requestsBefore)
            {
                Interlocked.Increment(ref _closure);
                cancellation.Cancel();
            }

            _work = RunAsync(sessionId, engine, audio, closure, cancellation.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(
        DictationSessionId sessionId,
        ILivePreviewEngine engine,
        IAudioSnapshotSource audio,
        int closure,
        CancellationToken cancellationToken)
    {
        // The work inherits the start's scope as a child flow and would stay joined without this;
        // opened anyway, because the rule is one line per flow and not a property of who called whom.
        using var dictation = DictationScope.Begin(sessionId.Value);
        try
        {
            var startup = await engine.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!startup.Succeeded)
            {
                _logger.Write(new AppLogEntry(
                    _clock.GetUtcNow(),
                    AppEventCode.LivePreviewFailed,
                    AppFailureCategories.For(startup.Error)));
                return;
            }

            // WRITTEN BEFORE THE FIRST PASS, so a log whose first update precedes its start cannot
            // read as two previews. The stop reads the flag to decide whether there is a start to
            // close: a startup that was cancelled or refused never started, and says nothing more.
            _started = true;
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.LivePreviewStarted));
            while (true)
            {
                // ASKED BEFORE EVERY SNAPSHOT, not only inside the waits. An engine that finished
                // starting after the stop had already cancelled would otherwise run one pass on a
                // dictation that is over and put its words on a screen the stop is about to clear.
                cancellationToken.ThrowIfCancellationRequested();

                // THE WAIT MOVED TO THE FAR SIDE OF THE WORK, WHICH IS THE WHOLE FIX. It used to sit
                // here, so the period was the interval PLUS the cost of a pass rather than the larger
                // of the two, and nothing could reach the screen before both had elapsed however fast
                // the engine became. On the measured 7.9-second take that bought exactly one update
                // and the second was not slow but impossible. Ref: #99 and `LivePreviewCadence`.
                var snapshot = audio.GetSnapshot(MaximumWindow);
                if (snapshot is null || snapshot.Samples.Length < MinimumSamplesPerPass)
                {
                    // NOT THE CADENCE, BECAUSE THIS IS NOT AN UPDATE. There is not yet enough audio to
                    // transcribe, and waiting the full interval to re-ask is what made a person watch
                    // "Listening..." for four seconds. Half the threshold this guard enforces, so the
                    // first pass cannot start more than a quarter second late.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var started = _clock.GetTimestamp();
                var update = await engine.PreviewAsync(
                    snapshot,
                    Interlocked.Increment(ref _sequence),
                    cancellationToken).ConfigureAwait(false);
                var passCost = _clock.GetElapsedTime(started);
                if (!update.Succeeded)
                {
                    _logger.Write(new AppLogEntry(
                        _clock.GetUtcNow(),
                        AppEventCode.LivePreviewFailed,
                        AppFailureCategories.For(update.Error),
                        (long)passCost.TotalMilliseconds));
                    return;
                }

                _logger.Write(new AppLogEntry(
                    _clock.GetUtcNow(),
                    AppEventCode.LivePreviewUpdated,
                    ElapsedMilliseconds: (long)passCost.TotalMilliseconds,
                    // WHICH LANGUAGE THIS PASS WAS TOLD, so a change is seen to reach the preview too (#241).
                    RecognitionLanguage: DiagnosticRecognitionLanguages.From(update.RecognitionLanguage)));
                // THE CLOSURE IS CHECKED AGAIN AT THE DISPATCH, not only at the start. A stop that
                // ran out of patience left this loop inside the engine; when the engine answers at
                // last, the screen this was for has been closed, and its words must not reach it.
                if (update.SessionId == sessionId.Value && Volatile.Read(ref _closure) == closure)
                {
                    _effects.ShowPreview(new LivePreviewFrame(
                        sessionId,
                        update.Text,
                        () => Volatile.Read(ref _closure) == closure));
                }

                // A FLOOR, NOT AN ADDITION. A pass slower than the interval waits nothing and the next
                // one starts immediately; a fast one still cannot flood the screen. The engine is a
                // limb and must not spend the machine the final transcript is waiting on.
                await Task.Delay(LivePreviewCadence.DelayAfter(passCost), _clock, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Release or cancellation intentionally stops preview without affecting final ASR. A
            // startup cancelled before the engine answered leaves the same way, having started
            // nothing - and says so, because a dictation released inside the worker's startup would
            // otherwise read exactly like one where Live Preview never tried.
            if (!_started)
            {
                _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.LivePreviewStartupCancelled));
            }
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.LivePreviewFailed,
                AppFailureCategory.RuntimeWorker));
        }
    }

    /// <summary>Stops the preview and waits for its loop and its engine to finish, however long that takes; says whether the engine agreed.</summary>
    public Task<StopOutcome> StopAsync() => StopAsync(deadline: null);

    /// <summary>
    /// Stops the preview and waits up to the deadline - for the gate, the loop and the engine's own stop
    /// together. What did not finish stays owned: a loop still inside the engine, or an engine stop
    /// still in flight or refused, is reported <see cref="StopOutcome.StillRunning"/> and joined again
    /// by the next stop; the engine is never stopped under a loop still using it.
    /// </summary>
    public async Task<StopOutcome> StopAsync(TimeSpan? deadline)
    {
        // STOPPING IS REACHED FROM MORE PLACES THAN STARTING, and one of them is quitting the app
        // from the tray mid-recording - a shutdown path that inherits nothing, where the line saying
        // the preview stopped was the last thing written about that dictation and was joined to
        // nothing. Read off the shell rather than taken as a parameter, because the callers that
        // lose the join are exactly the ones with no id to pass.
        using var dictation = _effects.RecordingSessionId is { } recording
            ? DictationScope.Begin(recording.Value)
            : NoScope.Instance;
        var budget = new StopBudget(deadline, _clock);
        // THE STOP IS PUBLISHED BEFORE THE GATE IS WAITED FOR. The screen closes and the loop in
        // flight is asked to stop now, so a stop that runs out of budget waiting for the gate - held
        // by another stop inside a held engine - has still closed the screen and cancelled the loop;
        // and a start that publishes its work in the same instant sees the request and cancels its
        // own (see StartAsync). A frame the engine hands back late, or one already queued for the
        // window, finds the closure changed at its render and draws nothing.
        // THE REQUEST FIRST, THE CLOSURE SECOND. A start reads the request count before it does
        // anything and again after it publishes; a stop that bumped the closure first could be caught
        // between the two writes by a start that then captured the new closure with no request on
        // record, and its frames would pass every check. With the request written first, a start
        // that captures the new closure has already seen the request.
        Interlocked.Increment(ref _stopRequests);
        Interlocked.Increment(ref _closure);
        try
        {
            Volatile.Read(ref _cancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // THE SOURCE WAS RETIRED BETWEEN THE READ AND THE CANCEL by a stop that owned the gate and
            // saw its loop finish; there is nothing left of it to cancel, and this stop carries on to
            // the gate to see for itself.
        }
        // THE GATE IS ON THE BUDGET TOO. A stop queued behind another that is itself waiting on a held
        // engine would otherwise wait without limit, and a deadline that does not cover the wait for
        // the gate is not a deadline.
        if (!await budget.TryEnterAsync(_gate).ConfigureAwait(false))
        {
            _effects.ClearPreview();
            return StopOutcome.StillRunning;
        }

        try
        {
            var cancellation = _cancellation;
            var work = _work;
            cancellation?.Cancel();
            if (work is not null &&
                await BoundedJoin.JoinAsync(work, budget.Remaining, _clock).ConfigureAwait(false) == StopOutcome.StillRunning)
            {
                // STILL RUNNING, STILL OWNED. The loop is inside the engine; the token source it
                // holds is not disposed, the engine it is using is not stopped under it, and the
                // fields keep both so the next stop joins the same work. Only the screen is cleared.
                _effects.ClearPreview();
                return StopOutcome.StillRunning;
            }

            if (work is not null)
            {
                _cancellation = null;
                _work = null;
                cancellation?.Dispose();
            }

            // STOPPED EVEN WHEN THE START WAS CANCELLED HALF-WAY. The engine's own start releases
            // what it acquired when it is cancelled or refused; its stop is still called so a worker
            // that came up between the cancel and the check is taken down, and so the engine's
            // answer to "are you stopped" is always its own. THE STOP IS OWNED AS A TASK: one that
            // did not finish inside the budget is kept and joined by the next stop rather than
            // issued again beside itself; one the engine refused - its worker still there - is
            // reported as such and asked for again next time.
            if (_effects.Engine is { } engine)
            {
                var engineStop = _engineStop ??= engine.StopAsync();
                StopOutcome joined;
                try
                {
                    joined = await BoundedJoin.JoinAsync(engineStop, budget.Remaining, _clock).ConfigureAwait(false);
                }
                catch
                {
                    // A STOP THAT THREW IS NOT JOINED AGAIN: the next stop asks the engine afresh, and
                    // the fault is the caller's to see, as it always was.
                    _engineStop = null;
                    throw;
                }

                if (joined == StopOutcome.StillRunning)
                {
                    _effects.ClearPreview();
                    return StopOutcome.StillRunning;
                }

                _engineStop = null;
                var stopped = await engineStop.ConfigureAwait(false);
                _engineRefusedStop = !stopped.Succeeded;
                if (_engineRefusedStop)
                {
                    _effects.ClearPreview();
                    return StopOutcome.StillRunning;
                }
            }

            _effects.ClearPreview();
            if (work is not null && _started)
            {
                _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.LivePreviewStopped));
            }

            _started = false;
            return StopOutcome.Completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the preview's worker by force after a stop the engine refused: the worker killed and its
    /// exit waited for up to the deadline, its resource let go of by the engine once the exit is seen.
    /// <see cref="StopOutcome.Completed"/> when nothing of the preview is owned any more;
    /// <see cref="StopOutcome.StillRunning"/> when the loop is still inside the engine (an abort is
    /// not run under it), the engine cannot be aborted, the exit was not seen inside the deadline, or
    /// nothing of the deadline was left once the gate was taken - a worker's exit takes time to see,
    /// and an abort given none is an honest non-completion, not a call the runtime refuses.
    /// </summary>
    /// <remarks>
    /// THE RELEASE'S LAST RESORT, NOT ITS FIRST. A stop asks the worker to go and waits; only a worker
    /// that answered no - still there, still holding the lease the final engine needs - is killed,
    /// and only by the session's owner, which knows the final transcription is about to begin. The
    /// gate is taken like a stop's, under the same deadline.
    /// </remarks>
    public async Task<StopOutcome> AbortAsync(TimeSpan deadline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deadline, TimeSpan.Zero);
        using var dictation = _effects.RecordingSessionId is { } recording
            ? DictationScope.Begin(recording.Value)
            : NoScope.Instance;
        var budget = new StopBudget(deadline, _clock);
        if (!await budget.TryEnterAsync(_gate).ConfigureAwait(false))
        {
            return StopOutcome.StillRunning;
        }

        try
        {
            if (_work is not null || _engineStop is not null)
            {
                return StopOutcome.StillRunning;
            }

            if (!_engineRefusedStop)
            {
                return StopOutcome.Completed;
            }

            // READ ONCE, JUDGED ONCE, HANDED ON AS READ. The remainder is recomputed at every read, so a
            // check on one read and a call on the next could pass a remainder that has since run out to
            // a runtime that refuses it.
            var left = budget.Left;
            if (_effects.Engine is not IAbortableLivePreviewEngine engine || left <= TimeSpan.Zero)
            {
                return StopOutcome.StillRunning;
            }

            var aborted = await engine.AbortAsync(left).ConfigureAwait(false);
            if (aborted.Outcome is not (RuntimeWorkerAbortOutcome.Exited or RuntimeWorkerAbortOutcome.NoWorker))
            {
                return StopOutcome.StillRunning;
            }

            _engineRefusedStop = false;
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.LivePreviewAborted));
            return StopOutcome.Completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops whatever is running and releases the gate. The engine is the shell's to dispose.</summary>
    /// <remarks>
    /// THE LAST CALL, BY CONTRACT RATHER THAN BY ENFORCEMENT. The shell disposes this after admission
    /// has closed and the session's own teardown has stopped the preview, which runs only once the
    /// session is quiescent; a shutdown whose budget ran out first disposes nothing, this included.
    /// So nothing is expected to be starting or stopping a preview at the same time; the gate is
    /// disposed on that understanding and a start or stop that arrives after it would find a
    /// disposed semaphore. A second dispose is a no-op
    /// once the first has succeeded; one whose stop threw is not remembered as done, so the next
    /// attempt stops and disposes rather than reporting a release that never happened.
    /// This is the contract the shell's own preview gate had; it is written down here because the
    /// gate now has a type of its own that somebody could reach for elsewhere.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // DISPOSED ONLY ONCE THE STOP IS COMPLETE. A stop the engine refused, or one still inside
        // it, leaves the preview owned and the gate in place; the next stop or disposal tries again,
        // and only a stop that saw everything finish lets the gate go.
        if (await StopAsync().ConfigureAwait(false) != StopOutcome.Completed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }
}
