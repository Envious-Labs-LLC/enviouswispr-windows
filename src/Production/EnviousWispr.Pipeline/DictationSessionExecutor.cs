using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;

namespace EnviousWispr.Pipeline;

/// <summary>Why a session had to be recovered rather than finished.</summary>
public enum SessionFailureKind
{
    /// <summary>Final processing exceeded its deadline; the words are kept, nothing is delivered.</summary>
    TimedOut,

    /// <summary>A transition threw; the session is reset safely.</summary>
    Failed,

    /// <summary>Windows locked or suspended with nothing recording, or mid-finalisation; the session is reset safely.</summary>
    Interrupted,

    /// <summary>Windows locked or suspended and the recovery itself exceeded its deadline; reset safely.</summary>
    InterruptionTimedOut,

    /// <summary>Windows locked or suspended and the recovery threw; reset safely.</summary>
    InterruptionFailed,
}

/// <summary>
/// Everything the shell does on behalf of a push-to-talk transition that this executor decides on.
/// Each member is one concrete effect - a notice, a status, background work, a log line - and none of
/// them decides anything. The decisions live in <see cref="DictationSessionExecutor"/>.
/// </summary>
public interface IDictationSessionEffects
{
    /// <summary>The user's Escape Recovery setting, read when a recording starts.</summary>
    bool EscapeRecoveryEnabled { get; }

    /// <summary>The machine is under memory or disk pressure at admission: the log line.</summary>
    void RecordResourcePressure(AppError? failure);

    void ShowRecoveredTextWaiting();

    void ShowMemoryCritical();

    void ShowDiskLow();

    /// <summary>Writes the transition's content-free event.</summary>
    void RecordTransition(SessionTransitionResult result);

    /// <summary>What the background work is told as a recording starts: read now, handed over as values.</summary>
    RecordingBackgroundSettings RecordingSettings();

    /// <summary>Windows locked or is suspending with a recording open: the audio is being kept. Shown after the background work has stopped, before transcription.</summary>
    void ShowInterruptionPreserving(SystemLifecycleTransition transition);

    void ShowTransitionStatus(SessionTransitionResult result);

    /// <summary>The transition threw for a reason that is not a timeout.</summary>
    void RecordSessionFailure();

    /// <summary>The interruption's recovery threw.</summary>
    void RecordInterruptionFailure();

    /// <summary>The session was recovered after a failure: the log line.</summary>
    void RecordSessionRecovered(AppError failure);

    /// <summary>The session was recovered after a failure: the status, worded for the kind of failure.</summary>
    void ShowSessionRecovered(SessionFailureKind kind);

    /// <summary>Records whether a dictation is in flight, at every place one can end.</summary>
    Task RecordDictationEdgeAsync();

    /// <summary>Windows interrupted while the previous command was still running, and the recovery has waited too long.</summary>
    void ShowInterruptionPending();

    /// <summary>
    /// Shutdown: the session-specific disposal - the timers, streaming and the preview stopped, the
    /// capture let go of, the session controller and the delivery route disposed. After the last
    /// command when the shutdown's waits were enough; beside a command that outlived them when not.
    /// </summary>
    Task TearDownSessionAsync();

    /// <summary>The recording ran to its limit and was aborted: the log line.</summary>
    void RecordRecordingTimedOut(AppError failure);

    /// <summary>The recording ran to its limit and was aborted: the status.</summary>
    void ShowRecordingTimedOut();
}

/// <summary>
/// Decides what one push-to-talk signal means for the session and drives the state machine
/// accordingly; the coordinator runs it, one command at a time.
/// </summary>
/// <remarks>
/// THE DECISIONS ARE HERE AND EVERY EFFECT IS BEHIND THE PORT. The shell renders, logs and translates
/// native events; this class owns the branching. A press first asks whether recovered text is still
/// waiting and whether the machine can afford a recording; a release or a cancel first stops the
/// watchdog; Escape with recovery on releases rather than cancels, so the words are kept without
/// being delivered.
///
/// FAILURE AND RECOVERY ARE THE LINES SOMEBODY READS FIRST WHEN A DICTATION WENT WRONG, so the session
/// id is carried into the catches in a variable rather than inherited from a scope that has already
/// been disposed, and it is seeded from the controller because a release or a cancel can throw
/// BEFORE it returns a transition, and the recording those lines are about already exists.
/// </remarks>
public sealed class DictationSessionExecutor : ISessionCommandExecutor
{
    /// <summary>The longest a finalisation may take - transcription, text processing and delivery - before it is cancelled and recovered.</summary>
    public static readonly TimeSpan MaximumFinalProcessingDuration = TimeSpan.FromMinutes(3);

    private readonly PushToTalkSessionController _controller;
    private readonly ISessionBackgroundWork _background;
    private readonly ISessionFinalization _finalization;
    private readonly ISessionRecoveryState _persistence;
    private readonly ISystemResourceProbe _resources;
    private readonly IDictationSessionEffects _effects;
    private readonly TimeSpan _processingDeadline;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _processing;
    private bool _escapeRecoveryForSession;
    private bool _closed;

    public DictationSessionExecutor(
        PushToTalkSessionController controller,
        ISessionBackgroundWork background,
        ISessionFinalization finalization,
        ISessionRecoveryState persistence,
        ISystemResourceProbe resources,
        IDictationSessionEffects effects,
        TimeSpan? processingDeadline = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(finalization);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(effects);
        _controller = controller;
        _background = background;
        _finalization = finalization;
        _persistence = persistence;
        _resources = resources;
        _effects = effects;
        _processingDeadline = processingDeadline ?? MaximumFinalProcessingDuration;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Whether the recording under way was started with Escape Recovery on.</summary>
    /// <remarks>
    /// THE EXECUTOR'S OWN STATE. It is set when a recording starts, decides whether Escape releases
    /// or cancels, and is cleared on a cancel or a failure and as a finalisation begins.
    /// </remarks>
    public bool EscapeRecoveryForSession => _escapeRecoveryForSession;

    public bool IsProcessing => Volatile.Read(ref _processing) is not null;

    public void CancelProcessing()
    {
        Cancel(Volatile.Read(ref _processing));
    }

    /// <summary>The finalisation's own token source, as the opaque generation an interruption captures before it is admitted.</summary>
    public object? ProcessingGeneration => Volatile.Read(ref _processing);

    public void CancelProcessing(object generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        // ONLY THE GENERATION NAMED. The finalisation in flight when the interruption was captured may
        // have ended on its own by now, and the consumer may already be inside the interruption's own
        // finalisation with a new source: that one is the take's preservation, not what the
        // interruption was queued behind, and it is left to run.
        var processing = Volatile.Read(ref _processing);
        if (ReferenceEquals(processing, generation))
        {
            Cancel(processing);
        }
    }

    private static void Cancel(CancellationTokenSource? processing)
    {
        try
        {
            processing?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The finalisation ended between the read and the cancel, which is the outcome asked for.
        }
    }

    /// <summary>The interruption waited five seconds behind another command: recovery is pending, and it is said so now.</summary>
    public Task ExpireAsync(SessionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var dictation = _controller.CurrentSession is { } interrupted
            ? DictationScope.Begin(interrupted.Id.Value)
            : NoScope.Instance;
        _effects.ShowInterruptionPending();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Admission has closed for good. A finalisation that has not yet issued its delivery keeps the
    /// words for recovery rather than delivering into an app that is leaving; one already issued is
    /// left to settle. Nothing else changes: a command in flight finishes on its own terms.
    /// </summary>
    public void Close()
    {
        Volatile.Write(ref _closed, true);
        _finalization.CloseDelivery();
    }

    /// <summary>
    /// The session's teardown, run by the coordinator's shutdown only once nothing is using the session:
    /// the watchdog and the background work stopped under the deadline, each saying whether it finished,
    /// then - only behind owners that all finished - the shell's own disposal of the controller and the
    /// delivery route through the port, joined under what is left of the same deadline.
    /// </summary>
    /// <remarks>
    /// ONE DEADLINE, HANDED ON AS WHAT IS LEFT OF IT. The watchdog, the three background owners and
    /// the shell's disposal are stopped one after another, and each is given the remainder - not the
    /// whole again - so the teardown as a whole ends inside the deadline it was given. A shell disposal
    /// still running past it is reported, not waited for; the shell reads the report before it
    /// disposes anything else the session uses.
    /// </remarks>
    public async Task<SessionTeardownReport> TearDownAsync(TimeSpan deadline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deadline, TimeSpan.Zero);
        var budget = new StopBudget(deadline, _clock);
        var watchdog = await _background.StopWatchdogAsync(budget.Left).ConfigureAwait(false);
        var background = await _background.StopAsync(budget.Left).ConfigureAwait(false);
        if (watchdog != StopOutcome.Completed || !background.Completed)
        {
            // AN OWNER STILL RUNNING STILL USES THE CAPTURE AND THE CONTROLLER: the shell's disposal of
            // them is not run beside it. The report says which owner, and that the shell did not run.
            return new SessionTeardownReport(watchdog, background, Shell: null);
        }

        var shell = await BoundedJoin.JoinAsync(_effects.TearDownSessionAsync(), budget.Left, _clock).ConfigureAwait(false);
        return new SessionTeardownReport(watchdog, background, shell);
    }

    public Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Kind switch
        {
            SessionCommandKind.Interruption => InterruptAsync(command),
            SessionCommandKind.Timeout => TimeOutAsync(command),
            _ => ExecutePushToTalkAsync(command, stoppingToken),
        };
    }

    /// <summary>
    /// Windows is locking or suspending. A recording is released and finalised exactly as a key release
    /// would finalise it - transcribed, delivered where it can be, held for recovery where it cannot -
    /// and anything else in flight is reset; with nothing in flight, nothing is done. Runs under the
    /// session gate as a command of its own.
    /// </summary>
    private async Task<SessionCommandResult> InterruptAsync(SessionCommand command)
    {
        // WINDOWS LOCKING OR SUSPENDING ARRIVES ON ITS OWN CALLBACK, so this flow inherits nothing
        // and had no dictation at all - the capture transition, the preview stop, the failure and
        // the recovery lines all landed joined to nothing, on the path where a user most wants to
        // know what happened to their words.
        using var dictation = _controller.CurrentSession is { } interrupted
            ? DictationScope.Begin(interrupted.Id.Value)
            : NoScope.Instance;
        var transition = command.Transition ?? SystemLifecycleTransition.SessionLocked;
        try
        {
            // NOTHING TO INTERRUPT, NOTHING DONE - as the shell's callback did. The edge is still
            // recorded in the finally, as it was.
            if (_controller.CurrentSession is null)
            {
                return new SessionCommandResult(SessionCommandDisposition.Ignored);
            }

            if (_controller.CurrentSession.State == DictationSessionState.Recording)
            {
                await _background.StopWatchdogAsync().ConfigureAwait(false);
                var result = await _controller.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                RecordTransition(result);
                if (result.Kind == SessionTransitionKind.FinalizeReady &&
                    result.Session is not null &&
                    result.Audio is not null)
                {
                    await FinalizeAsync(result.Session.Id, result.Audio, recoveryOnly: false, preserving: transition)
                        .ConfigureAwait(false);
                    return new SessionCommandResult(SessionCommandDisposition.Applied, result.Session);
                }
            }

            await RecoverInterruptedAsync(AppErrorCode.Cancelled, SessionFailureKind.Interrupted).ConfigureAwait(false);
            return new SessionCommandResult(SessionCommandDisposition.Applied);
        }
        catch (OperationCanceledException)
        {
            await RecoverInterruptedAsync(AppErrorCode.SessionTimedOut, SessionFailureKind.InterruptionTimedOut)
                .ConfigureAwait(false);
            return new SessionCommandResult(SessionCommandDisposition.Failed);
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            _effects.RecordInterruptionFailure();
            await RecoverInterruptedAsync(AppErrorCode.InvalidTransition, SessionFailureKind.InterruptionFailed)
                .ConfigureAwait(false);
            return new SessionCommandResult(SessionCommandDisposition.Failed);
        }
        finally
        {
            ReleaseProcessingDeadline();
            await _effects.RecordDictationEdgeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Records a transition, and lets go of the Escape setting when the transition ends the recording without a finalisation.</summary>
    /// <remarks>
    /// EVERY TRANSITION PASSES HERE, whichever command produced it. The shell cleared the flag inside
    /// the one method that logged a transition, so a release that failed under a lock cleared it as
    /// surely as a key's cancel did; keeping that one site is what keeps the next recording from
    /// inheriting a setting the previous one was started with.
    /// </remarks>
    private void RecordTransition(SessionTransitionResult result)
    {
        if (result.Kind is SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
        {
            _escapeRecoveryForSession = false;
        }

        _effects.RecordTransition(result);
    }

    private Task RecoverInterruptedAsync(AppErrorCode code, SessionFailureKind kind) =>
        RecoverFailedSessionAsync(new AppError(code, AppErrorStage.SystemLifecycle, CanRetry: true), kind);

    /// <summary>Stops everything, aborts and resets whatever session is left, says so, and shows it.</summary>
    /// <remarks>
    /// THE SHELL'S RECOVERY, LINE FOR LINE, WITHOUT THE SHELL: the four stops (the watchdog first),
    /// then abort and reset only when a session is still there, then the log line, then the pending
    /// recovery shown through the persistence owner, then the status - whose words are the shell's
    /// for the kind of failure this was.
    /// </remarks>
    private async Task RecoverFailedSessionAsync(AppError failure, SessionFailureKind kind)
    {
        await _background.StopWatchdogAsync().ConfigureAwait(false);
        await _background.StopAsync().ConfigureAwait(false);
        if (_controller.CurrentSession is not null)
        {
            await _controller.AbortAsync(failure, CancellationToken.None).ConfigureAwait(false);
            await _controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _effects.RecordSessionRecovered(failure);
        _persistence.ShowPendingRecovery();
        _effects.ShowSessionRecovered(kind);
    }

    /// <summary>
    /// The recording armed as the command's session has run for as long as it is allowed. If it is
    /// still the one recording, every loop is stopped and it is aborted and reset; if the recording
    /// has moved on, nothing. Runs under the session gate as a command of its own.
    /// </summary>
    private async Task<SessionCommandResult> TimeOutAsync(SessionCommand command)
    {
        var sessionId = command.TimedOutSession ?? throw new InvalidOperationException("A timeout names the recording it was armed for.");
        using var dictation = DictationScope.Begin(sessionId.Value);
        try
        {
            if (_controller.CurrentSession is
                {
                    Id: var currentId,
                    State: DictationSessionState.Recording,
                } && currentId == sessionId)
            {
                await _background.StopAsync().ConfigureAwait(false);
                var error = new AppError(AppErrorCode.SessionTimedOut, AppErrorStage.Session, CanRetry: true);
                await _controller.AbortAsync(error, CancellationToken.None).ConfigureAwait(false);
                await _controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                _effects.RecordRecordingTimedOut(error);
                _effects.ShowRecordingTimedOut();
                return new SessionCommandResult(SessionCommandDisposition.Applied);
            }

            return new SessionCommandResult(SessionCommandDisposition.Ignored);
        }
        finally
        {
            await _effects.RecordDictationEdgeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SessionCommandResult> ExecutePushToTalkAsync(SessionCommand command, CancellationToken stoppingToken)
    {
        // DELIBERATELY NOT FORWARDED. A transition in flight owns a microphone or a transcription and
        // finishes on its own terms; the shutdown waits its budget for that, or reports the command as
        // outstanding and tears nothing down beside it. A token reaching the controller mid-transition
        // would turn an orderly finalisation into a timed-out one. Every controller call below
        // therefore passes None on purpose.
        _ = stoppingToken;
        var none = CancellationToken.None;

        var signal = command.Signal;
        SessionTransitionResult? transition = null;
        var interrupted = _controller.CurrentSession?.Id.Value;
        try
        {
            // A TERMINAL POSTED FOR A RECORDING THAT HAS ENDED IS IGNORED HERE, at the run, not at the
            // queue: the loop that posted it was watching a take that is over, and the recording in
            // flight now is somebody else's.
            if (command.ForSession is { } forSession && _controller.CurrentSession?.Id != forSession)
            {
                return new SessionCommandResult(SessionCommandDisposition.Ignored);
            }

            if (signal == PushToTalkSignal.Pressed)
            {
                if (_persistence.HasPendingRecovery)
                {
                    _effects.ShowRecoveredTextWaiting();
                    return new SessionCommandResult(SessionCommandDisposition.Applied);
                }

                // THE MACHINE IS ASKED, AND THE POLICY IS APPLIED, HERE. Memory too low refuses the
                // recording; disk too low lets it run but stops the recovery copy being written, and
                // the persistence owner is told so before the recording starts.
                var admission = SystemResourceAdmissionPolicy.Evaluate(_resources.Probe());
                _persistence.CanPersistRecovery = admission.CanPersistRecovery;
                if (admission.Status != DictationAdmissionStatus.Ready)
                {
                    _effects.RecordResourcePressure(admission.Error);
                }

                if (!admission.CanStart)
                {
                    _effects.ShowMemoryCritical();
                    return new SessionCommandResult(SessionCommandDisposition.Applied);
                }

                if (!admission.CanPersistRecovery)
                {
                    _effects.ShowDiskLow();
                }
            }
            else
            {
                await _background.StopWatchdogAsync().ConfigureAwait(false);
            }

            var recoverCancelledRecording =
                signal == PushToTalkSignal.Cancelled && _escapeRecoveryForSession;
            var result = signal switch
            {
                // THE PRESS BRINGS ITS OWN TARGET. Captured at admission, inside the key callback; the
                // controller is told rather than asked, so the queue's hop cannot move the words.
                PushToTalkSignal.Pressed when command.StartContext is { } startContext =>
                    await _controller.PressAsync(startContext, none).ConfigureAwait(false),
                PushToTalkSignal.Pressed => await _controller.PressAsync(none).ConfigureAwait(false),
                PushToTalkSignal.Released => await _controller.ReleaseAsync(none).ConfigureAwait(false),
                PushToTalkSignal.Cancelled when recoverCancelledRecording =>
                    await _controller.ReleaseAsync(none).ConfigureAwait(false),
                PushToTalkSignal.Cancelled => await _controller.CancelAsync(none).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported push-to-talk signal."),
            };

            // FROM HERE THE DICTATION IS KNOWN, AND EVERYTHING BELOW BELONGS TO IT. Above this line
            // the session either does not exist yet (a press) or is being read off the controller,
            // and lines written there honestly have no dictation. `Begin` restores rather than
            // clears, so this nesting inside another scope is safe.
            transition = result;
            interrupted = result.Session?.Id.Value;
            using var dictation = interrupted is { } known
                ? DictationScope.Begin(known)
                : NoScope.Instance;
            RecordTransition(result);
            if (result.Kind == SessionTransitionKind.Started && result.Session is not null)
            {
                _escapeRecoveryForSession = _effects.EscapeRecoveryEnabled;
                // THE SETTINGS ARE READ ONCE, HERE, and the background work is told them: a recording
                // that started under one auto-stop preference finishes under it.
                await _background.StartAsync(result.Session.Id, _effects.RecordingSettings()).ConfigureAwait(false);
            }
            else if (result.Kind == SessionTransitionKind.FinalizeReady &&
                result.Session is not null &&
                result.Audio is not null)
            {
                await FinalizeAsync(result.Session.Id, result.Audio, recoverCancelledRecording, preserving: null)
                    .ConfigureAwait(false);
                return new SessionCommandResult(SessionCommandDisposition.Applied, result.Session);
            }
            else if (result.Kind is SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
            {
                await _background.StopAsync().ConfigureAwait(false);
                await _controller.ResetAsync(none).ConfigureAwait(false);
            }

            _effects.ShowTransitionStatus(result);
            return new SessionCommandResult(SessionCommandDisposition.Applied, result.Session);
        }
        catch (OperationCanceledException)
        {
            // THE CATCH RUNS AFTER THE TRY'S SCOPE HAS BEEN DISPOSED, so the id is carried in a
            // variable rather than inherited.
            using var failed = interrupted is { } timedOut
                ? DictationScope.Begin(timedOut)
                : NoScope.Instance;
            await RecoverFailedSessionAsync(
                    new AppError(AppErrorCode.SessionTimedOut, AppErrorStage.Session, CanRetry: true),
                    SessionFailureKind.TimedOut)
                .ConfigureAwait(false);
            return new SessionCommandResult(SessionCommandDisposition.Failed, transition?.Session);
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            using var failed = interrupted is { } broken
                ? DictationScope.Begin(broken)
                : NoScope.Instance;
            _effects.RecordSessionFailure();
            await RecoverFailedSessionAsync(
                    new AppError(AppErrorCode.InvalidTransition, AppErrorStage.Session, CanRetry: true),
                    SessionFailureKind.Failed)
                .ConfigureAwait(false);
            return new SessionCommandResult(SessionCommandDisposition.Failed, transition?.Session);
        }
        finally
        {
            ReleaseProcessingDeadline();
            await _effects.RecordDictationEdgeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Capture is complete: the background work is stopped and the audio becomes text, under one deadline.</summary>
    /// <remarks>
    /// THE DEADLINE IS ARMED BEFORE THE BACKGROUND WORK IS STOPPED and published before the first
    /// await, so it covers the preview's worker being waited for as well as the transcription and
    /// the delivery - the shell's order - and so a lock or an exit arriving during those stops finds
    /// something to cancel. THE CAPTURE HAS ALREADY STOPPED (the release before this), so the preview's
    /// worker is waited for after the microphone is closed, never before. The deadline stays armed
    /// until the command's finally, after any recovery, so a lock or an exit can still cancel a
    /// finalisation that is being recovered.
    /// </remarks>
    private async Task FinalizeAsync(
        DictationSessionId sessionId,
        CapturedAudio audio,
        bool recoveryOnly,
        SystemLifecycleTransition? preserving)
    {
        using var dictation = DictationScope.Begin(sessionId.Value);
        var processing = new CancellationTokenSource(_processingDeadline, _clock);
        Volatile.Write(ref _processing, processing);
        await _background.StopAsync().ConfigureAwait(false);
        if (preserving is { } transition)
        {
            _effects.ShowInterruptionPreserving(transition);
        }

        // THE ESCAPE SETTING IS SPENT AS THE FINALISATION BEGINS: whether this take was a recovery
        // has been decided (recoveryOnly), and the next recording reads the setting afresh.
        _escapeRecoveryForSession = false;
        // A FINALISATION THAT STARTS AFTER ADMISSION CLOSED KEEPS ITS WORDS: the app is leaving, and
        // a paste into whatever is in front is not the place for them. The recovery copy is.
        await _finalization.RunAsync(sessionId, audio, recoveryOnly || Volatile.Read(ref _closed), processing.Token).ConfigureAwait(false);
    }

    /// <summary>Releases the deadline this command armed, if it armed one. Called last, after any recovery.</summary>
    private void ReleaseProcessingDeadline()
    {
        var processing = Interlocked.Exchange(ref _processing, null);
        processing?.Dispose();
    }
}
