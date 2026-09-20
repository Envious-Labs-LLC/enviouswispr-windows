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
    /// <summary>Recovered text from an earlier run is still waiting on Home.</summary>
    bool HasPendingRecovery { get; }

    /// <summary>The user's Escape Recovery setting, read when a recording starts.</summary>
    bool EscapeRecoveryEnabled { get; }

    /// <summary>
    /// Whether the recording under way was started with Escape Recovery on. Held by the shell because
    /// final processing and the shell's own transition-event handling clear it on their paths.
    /// </summary>
    bool EscapeRecoveryForSession { get; set; }

    /// <summary>Probes the machine and applies the admission policy, logging pressure as it goes.</summary>
    DictationAdmissionResult EvaluateAdmission();

    void ShowRecoveredTextWaiting();

    void ShowMemoryCritical();

    void ShowDiskLow();

    Task StopRecordingWatchdogAsync();

    /// <summary>Writes the transition's content-free event.</summary>
    void RecordTransition(SessionTransitionResult result);

    /// <summary>The recording is open: watchdog, live preview, auto-stop, streaming, in that order.</summary>
    Task OnRecordingStartedAsync(DictationSessionId sessionId);

    /// <summary>
    /// Capture is complete: stop the background work and turn the audio into delivered text. The
    /// processing deadline this arms stays armed until <see cref="ReleaseProcessingDeadline"/>.
    /// When <paramref name="preserving"/> names the Windows transition that ended the recording, the
    /// "captured audio is being preserved" status is shown after the background work has stopped and
    /// before transcription - where the shell's lifecycle callback used to show it.
    /// </summary>
    Task FinalizeAsync(
        DictationSessionId sessionId,
        CapturedAudio audio,
        bool recoveryOnly,
        SystemLifecycleTransition? preserving = null);

    /// <summary>
    /// Releases the processing deadline armed by <see cref="FinalizeAsync"/>, if this command armed one.
    /// Called last, after any recovery, so that lock/suspend recovery and shutdown can still cancel a
    /// finalisation that is being recovered - the order the shell always had.
    /// </summary>
    void ReleaseProcessingDeadline();

    /// <summary>The recording ended with nothing to process: stop the background work.</summary>
    Task StopBackgroundWorkAsync();

    void ShowTransitionStatus(SessionTransitionResult result);

    /// <summary>The transition threw for a reason that is not a timeout.</summary>
    void RecordSessionFailure();

    /// <summary>The interruption's recovery threw.</summary>
    void RecordInterruptionFailure();

    /// <summary>Stops everything, aborts and resets the session, logs the recovery, shows the status.</summary>
    Task RecoverFailedSessionAsync(AppError failure, SessionFailureKind kind);

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
/// accordingly. The shell used to hold this body; the coordinator runs it, one command at a time.
/// </summary>
/// <remarks>
/// EVERY DECISION THE OLD HANDLER MADE IS HERE, IN THE SAME ORDER, AND EVERY EFFECT IT HAD IS BEHIND
/// THE PORT. That is the whole of step 3 on #148: the shell keeps rendering, logging, the timers and
/// final processing; this class keeps the branching that used to be tangled up with them. A press
/// first asks whether recovered text is still waiting and whether the machine can afford a recording;
/// a release or a cancel first stops the watchdog; Escape with recovery on releases rather than
/// cancels, so the words are kept without being delivered.
///
/// FAILURE AND RECOVERY ARE THE LINES SOMEBODY READS FIRST WHEN A DICTATION WENT WRONG, so the session
/// id is carried into the catches in a variable rather than inherited from a scope that has already
/// been disposed, and it is seeded from the controller because a release or a cancel can throw
/// BEFORE it returns a transition, and the recording those lines are about already exists.
/// </remarks>
public sealed class DictationSessionExecutor : ISessionCommandExecutor
{
    private readonly PushToTalkSessionController _controller;
    private readonly IDictationSessionEffects _effects;
    private bool _tornDown;

    public DictationSessionExecutor(PushToTalkSessionController controller, IDictationSessionEffects effects)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(effects);
        _controller = controller;
        _effects = effects;
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
    /// The shell's session-specific disposal, through its port. Run by the coordinator's shutdown once
    /// the queue is closed and, when its two waits were enough, once the last command has finished;
    /// when they were not, beside the command still running, which then finds the session torn down.
    /// </summary>
    public Task ShutdownAsync()
    {
        // THE STATE IS WRITTEN BEFORE THE TEARDOWN STARTS, so a command that fails from here on fails
        // as one torn down beside, whichever disposed dependency it happened to reach first.
        Volatile.Write(ref _tornDown, true);
        return _effects.TearDownSessionAsync();
    }

    public async Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            return await (command.Kind switch
            {
                SessionCommandKind.Interruption => InterruptAsync(command),
                SessionCommandKind.Timeout => TimeOutAsync(command),
                _ => ExecutePushToTalkAsync(command, stoppingToken),
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (TornDown && exception is not (StackOverflowException or OutOfMemoryException))
        {
            // TORN DOWN BESIDE THIS COMMAND, AND ITS OWN RECOVERY FOUND THE SAME. A command's catches
            // recover into the session; when the session has been torn down under them, the recovery
            // itself fails on a disposed dependency, and that failure ends here rather than faulting
            // the submitter's task: written as a failure, under the dictation it was about where one
            // is still known, and answered as one.
            using var dictation = (command.TimedOutSession ?? _controller.CurrentSession?.Id) is { } known
                ? DictationScope.Begin(known.Value)
                : NoScope.Instance;
            RecordFailure(command.Kind);
            return new SessionCommandResult(SessionCommandDisposition.Failed);
        }
    }

    /// <summary>Whether the shutdown has torn the session down - under a command still running, when it outlived both waits.</summary>
    private bool TornDown => Volatile.Read(ref _tornDown);

    private void RecordFailure(SessionCommandKind kind)
    {
        if (kind == SessionCommandKind.Interruption)
        {
            _effects.RecordInterruptionFailure();
        }
        else
        {
            _effects.RecordSessionFailure();
        }
    }

    /// <summary>
    /// Windows is locking or suspending. A recording is released and finalised exactly as a key release
    /// would finalise it - transcribed, delivered where it can be, held for recovery where it cannot -
    /// and anything else in flight is reset; with nothing in flight, nothing is done. The body the
    /// shell's lifecycle callback used to run under the session gate, in its order.
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
                await _effects.StopRecordingWatchdogAsync().ConfigureAwait(false);
                var result = await _controller.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                _effects.RecordTransition(result);
                if (result.Kind == SessionTransitionKind.FinalizeReady &&
                    result.Session is not null &&
                    result.Audio is not null)
                {
                    await _effects.FinalizeAsync(result.Session.Id, result.Audio, recoveryOnly: false, preserving: transition)
                        .ConfigureAwait(false);
                    return new SessionCommandResult(SessionCommandDisposition.Applied, result.Session);
                }
            }

            await RecoverInterruptedAsync(AppErrorCode.Cancelled, SessionFailureKind.Interrupted).ConfigureAwait(false);
            return new SessionCommandResult(SessionCommandDisposition.Applied);
        }
        catch (Exception exception) when (TornDown && exception is not (StackOverflowException or OutOfMemoryException))
        {
            // TORN DOWN BESIDE THIS COMMAND: the shutdown outlived both of its waits. Decided by the
            // state, not by the exception - any failure once the session is gone is this one. There
            // is nothing left to recover into, so nothing is aborted or reset; the failure is written
            // and the command ends. What the delivery route did before the teardown reached it stands.
            _effects.RecordInterruptionFailure();
            return new SessionCommandResult(SessionCommandDisposition.Failed);
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
            _effects.ReleaseProcessingDeadline();
            await _effects.RecordDictationEdgeAsync().ConfigureAwait(false);
        }
    }

    private Task RecoverInterruptedAsync(AppErrorCode code, SessionFailureKind kind) =>
        _effects.RecoverFailedSessionAsync(
            new AppError(code, AppErrorStage.SystemLifecycle, CanRetry: true),
            kind);

    /// <summary>
    /// The recording armed as the command's session has run for as long as it is allowed. If it is
    /// still the one recording, every loop is stopped and it is aborted and reset; if the recording
    /// has moved on, nothing. The body the watchdog used to run under the session gate.
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
                await _effects.StopBackgroundWorkAsync().ConfigureAwait(false);
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
        // finishes on its own terms; a shutdown token reaching the controller mid-transition would turn
        // an orderly finalisation into a timed-out one. Step 11 on #148 owns wiring shutdown properly.
        // Every controller call below therefore passes None on purpose.
        _ = stoppingToken;
        var none = CancellationToken.None;

        var signal = command.Signal;
        SessionTransitionResult? transition = null;
        var interrupted = _controller.CurrentSession?.Id.Value;
        try
        {
            if (signal == PushToTalkSignal.Pressed)
            {
                if (_effects.HasPendingRecovery)
                {
                    _effects.ShowRecoveredTextWaiting();
                    return new SessionCommandResult(SessionCommandDisposition.Applied);
                }

                var admission = _effects.EvaluateAdmission();
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
                await _effects.StopRecordingWatchdogAsync().ConfigureAwait(false);
            }

            var recoverCancelledRecording =
                signal == PushToTalkSignal.Cancelled && _effects.EscapeRecoveryForSession;
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
            _effects.RecordTransition(result);
            if (result.Kind == SessionTransitionKind.Started && result.Session is not null)
            {
                _effects.EscapeRecoveryForSession = _effects.EscapeRecoveryEnabled;
                await _effects.OnRecordingStartedAsync(result.Session.Id).ConfigureAwait(false);
            }
            else if (result.Kind == SessionTransitionKind.FinalizeReady &&
                result.Session is not null &&
                result.Audio is not null)
            {
                await _effects.FinalizeAsync(result.Session.Id, result.Audio, recoverCancelledRecording)
                    .ConfigureAwait(false);
                return new SessionCommandResult(SessionCommandDisposition.Applied, result.Session);
            }
            else if (result.Kind is SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
            {
                _effects.EscapeRecoveryForSession = false;
                await _effects.StopBackgroundWorkAsync().ConfigureAwait(false);
                await _controller.ResetAsync(none).ConfigureAwait(false);
            }

            _effects.ShowTransitionStatus(result);
            return new SessionCommandResult(SessionCommandDisposition.Applied, result.Session);
        }
        catch (Exception exception) when (TornDown && exception is not (StackOverflowException or OutOfMemoryException))
        {
            // TORN DOWN BESIDE THIS COMMAND: the shutdown outlived both of its waits and the session
            // controller went with the teardown. Decided by the state, not by the exception - any
            // failure once the session is gone is this one. There is nothing to abort or reset, so the
            // recovery that would try is not run; the failure is written under the dictation and the
            // command ends as failed. What the delivery route did before the teardown reached it stands.
            using var failed = interrupted is { } tornDown
                ? DictationScope.Begin(tornDown)
                : NoScope.Instance;
            _effects.RecordSessionFailure();
            return new SessionCommandResult(SessionCommandDisposition.Failed, transition?.Session);
        }
        catch (OperationCanceledException)
        {
            // THE CATCH RUNS AFTER THE TRY'S SCOPE HAS BEEN DISPOSED, so the id is carried in a
            // variable rather than inherited.
            using var failed = interrupted is { } timedOut
                ? DictationScope.Begin(timedOut)
                : NoScope.Instance;
            await _effects.RecoverFailedSessionAsync(
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
            await _effects.RecoverFailedSessionAsync(
                    new AppError(AppErrorCode.InvalidTransition, AppErrorStage.Session, CanRetry: true),
                    SessionFailureKind.Failed)
                .ConfigureAwait(false);
            return new SessionCommandResult(SessionCommandDisposition.Failed, transition?.Session);
        }
        finally
        {
            _effects.ReleaseProcessingDeadline();
            await _effects.RecordDictationEdgeAsync().ConfigureAwait(false);
        }
    }
}
