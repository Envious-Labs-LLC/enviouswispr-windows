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
    /// final processing and the recording watchdog clear it on their own paths.
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

    /// <summary>Capture is complete: stop the background work and turn the audio into delivered text.</summary>
    Task FinalizeAsync(DictationSessionId sessionId, CapturedAudio audio, bool recoveryOnly);

    /// <summary>The recording ended with nothing to process: stop the background work.</summary>
    Task StopBackgroundWorkAsync();

    void ShowTransitionStatus(SessionTransitionResult result);

    /// <summary>The transition threw for a reason that is not a timeout.</summary>
    void RecordSessionFailure();

    /// <summary>Stops everything, aborts and resets the session, logs the recovery, shows the status.</summary>
    Task RecoverFailedSessionAsync(AppError failure, SessionFailureKind kind);

    /// <summary>Records whether a dictation is in flight, at every place one can end.</summary>
    Task RecordDictationEdgeAsync();
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

    public DictationSessionExecutor(PushToTalkSessionController controller, IDictationSessionEffects effects)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(effects);
        _controller = controller;
        _effects = effects;
    }

    public async Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(command);
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
            await _effects.RecordDictationEdgeAsync().ConfigureAwait(false);
        }
    }
}
