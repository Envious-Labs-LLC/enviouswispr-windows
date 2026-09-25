using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

namespace EnviousWispr.App.Composition;

/// <summary>
/// The shell's half of a push-to-talk transition: every concrete effect the executor in Pipeline
/// decides on. Rendering goes through the window's sinks, logging through the app log, the run-state
/// edge through its store. Nothing here sequences: the executor owns the order around a recording,
/// the deadline, admission, recovery and the finalisation call.
/// </summary>
internal sealed class SessionEffects(SessionCompositionParts parts) : IDictationSessionEffects
{
    private readonly IAppLogger _logger = parts.Logger;

    private readonly ISessionView _view = parts.Shell.View;

    public bool EscapeRecoveryEnabled => parts.Shell.Dictation().EscapeRecoveryEnabled;

    public void RecordResourcePressure(AppError? failure) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.ResourcePressureDetected,
            AppFailureCategory.ResourcePressure,
            ErrorCode: failure?.Code));

    public void ShowRecoveredTextWaiting()
    {
        // The notice and the status first, the window after: the order the window's queue always ran
        // them in, since showing the window was itself a further dispatch.
        _view.ShowNotice(
            "Recovered text is waiting",
            "Copy or delete the unfinished dictation on Home before starting another recording.");
        _view.ShowStatus(DictationStatus.Quiet("Review recovered text before recording again"));
        _view.ShowMainWindow();
    }

    public void ShowMemoryCritical()
    {
        _view.ShowNotice(
            "Windows memory is critically low",
            "Close another memory-heavy app, then try dictation again. No recording was started.",
            isError: true);
        _view.ShowStatus(DictationStatus.Distress(
            "Recording paused because Windows memory is critically low"));
    }

    public void ShowDiskLow() =>
        _view.ShowNotice(
            "Disk space is critically low",
            "Dictation can continue, but EnviousWispr may be unable to save an encrypted crash-recovery copy.");

    /// <remarks>
    /// THE HOOK'S RECORDING FLAG IS NOT SET HERE. It was, from the transitions that passed this method - and the
    /// watchdog's timeout and the lifecycle recovery abort and reset a recording without passing it, so the hook
    /// went on believing a recording ran (#86). <see cref="SessionComposition"/> ties the flag to the controller's
    /// own session changes instead, which every ending passes through.
    /// </remarks>
    public void RecordTransition(SessionTransitionResult result)
    {

        var eventCode = result.Kind switch
        {
            SessionTransitionKind.Started => AppEventCode.DictationRecordingStarted,
            SessionTransitionKind.FinalizeReady => AppEventCode.DictationCaptureFinalized,
            SessionTransitionKind.Cancelled => AppEventCode.DictationCancelled,
            SessionTransitionKind.Failed => AppEventCode.DictationSessionFailed,
            _ => (AppEventCode?)null,
        };
        if (result.Kind == SessionTransitionKind.Started &&
            parts.Capture is ICaptureStartTimings timings &&
            timings.LastDeviceOpenMilliseconds is { } openMs)
        {
            // THE NUMBER THAT DECIDES A FEATURE. Warming the capture engine removes the OPEN half
            // and nothing else, so if open is cheap the whole idea is worth nothing and the privacy
            // question behind it never needs asking. Logged rather than reasoned about, because the
            // one thing nobody has done is look.
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.CaptureDeviceOpened,
                ElapsedMilliseconds: openMs));
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.CaptureStreamStarted,
                ElapsedMilliseconds: timings.LastStreamStartMilliseconds ?? -1));
        }

        if (eventCode is not null)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                eventCode.Value,
                AppFailureCategories.For(result.Error),
                ErrorCode: result.Error?.Code));
        }
    }

    public RecordingBackgroundSettings RecordingSettings() =>
        new(RecordingLimits.WatchdogDuration(), parts.Shell.Dictation);

    public void ShowInterruptionPreserving(SystemLifecycleTransition transition) =>
        _view.ShowStatus(DictationStatus.Quiet(
            transition == SystemLifecycleTransition.Suspending
                ? "Windows is suspending. Captured audio is being preserved"
                : "Windows locked. Captured audio is being preserved"));

    public void ShowTransitionStatus(SessionTransitionResult result) =>
        _view.ShowStatus(SessionStatus(result));

    public void RecordSessionFailure() =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationSessionFailed,
            AppFailureCategory.Unknown));

    public void RecordSessionRecovered(AppError failure) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationSessionRecovered,
            AppFailureCategory.Recovery,
            ErrorCode: failure.Code));

    public void ShowSessionRecovered(SessionFailureKind kind) =>
        _view.ShowStatus(kind switch
        {
            SessionFailureKind.TimedOut => DictationStatus.Quiet("The dictation timed out and was recovered safely"),
            SessionFailureKind.Interrupted or SessionFailureKind.InterruptionFailed =>
                DictationStatus.Quiet("Windows interrupted the session; it was reset safely"),
            SessionFailureKind.InterruptionTimedOut =>
                DictationStatus.Quiet("Windows interrupted the session; recovery timed out safely"),
            _ => DictationStatus.Error("Session failed and was reset safely"),
        });

    /// <summary>Records whether a dictation is in flight, at every place one can end.</summary>
    /// <remarks>
    /// ONE OWNER OF SESSION TRANSITIONS, AND ITS EVERY COMMAND WRITES THIS. A key, the recording
    /// watchdog's timeout and Windows locking or suspending are all commands on one queue, and the
    /// executor records the edge in the finally of each. It used to be three flows, and writing the
    /// edge in only the first left the flag stuck true after either of the others, so a later ordinary
    /// restart told somebody their dictation was lost when it was not. A warning that fires when
    /// nothing happened is how the banner this replaces lost its meaning.
    ///
    /// READ OFF THE CONTROLLER RATHER THAN INFERRED. Each command reaches here by several routes and
    /// the controller is the only thing that knows the answer on all of them. Read through the
    /// shell's own reference to it, not the one this composition was handed: the executor's teardown
    /// disposes the controller and then tells the shell to let go of its reference (ReleaseSession),
    /// and a controller disposed still holds the session it was disposed under. The teardown runs
    /// only once the session is quiescent, so no command reads it beside the disposal; reading the
    /// reference the shell holds gives null once the session is gone, which a disposed controller
    /// would not.
    ///
    /// IT CANNOT THROW, BECAUSE ITS CALLER IS A FINALLY INSIDE THE COMMAND THAT HOLDS THE SESSION. An
    /// exception escaping here would fault the command, which the coordinator survives, but the
    /// submitter would be told of a storage fault instead of what became of the dictation - so a
    /// failed write is logged and swallowed.
    /// </remarks>
    public async Task RecordDictationEdgeAsync()
    {
        if (parts.Shell.RunId() is not { } runId)
        {
            return;
        }

        try
        {
            if (!await parts.RunState.SetDictationActiveAsync(
                    runId,
                    parts.Shell.AttachedSession() is not null,
                    DateTimeOffset.UtcNow).ConfigureAwait(false))
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.ApplicationRunStateEdgeFailed,
                    AppFailureCategory.StorageUnavailable));
            }
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.ApplicationRunStateEdgeFailed,
                AppFailureCategory.StorageUnavailable));
        }
    }

    public void RecordInterruptionFailure() =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationSessionFailed,
            AppFailureCategory.SystemLifecycle));

    public void ShowInterruptionPending() =>
        _view.ShowStatus(DictationStatus.Distress(
            "Windows interrupted the active dictation; recovery is still pending"));

    public void DetachCaptureObservers() => parts.Shell.DetachCaptureObservers();

    public void ReleaseSession() => parts.Shell.ReleaseSession();

    public void DisposeDeliveryRoute() => parts.Shell.DisposeDeliveryRoute();

    public void RecordTeardownFailure() =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.UnhandledFailure,
            AppFailureCategory.Recovery));

    public void RecordRecordingTimedOut(AppError failure) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationSessionRecovered,
            AppFailureCategory.Recovery,
            ErrorCode: failure.Code));

    public void ShowRecordingTimedOut() =>
        _view.ShowStatus(DictationStatus.Warning("Recording timed out and was cancelled safely"));

    public void RecordPreviewStillRunning() =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.LivePreviewFailed,
            AppFailureCategory.RuntimeWorker,
            ErrorCode: AppErrorCode.RuntimeResourceBusy));

    private static DictationStatus SessionStatus(SessionTransitionResult result) => result.Kind switch
    {
        SessionTransitionKind.Started =>
            DictationStatus.Recording("Recording. Release to finish, Escape to cancel"),
        SessionTransitionKind.FinalizeReady when result.Error is not null =>
            DictationStatus.Quiet("Capture preserved after a microphone interruption"),
        SessionTransitionKind.FinalizeReady =>
            DictationStatus.Quiet("Capture complete. Transcribing locally"),
        SessionTransitionKind.Cancelled => DictationStatus.Quiet("Cancelled. Nothing will be delivered"),
        SessionTransitionKind.Failed => DictationStatus.Error("Session failed safely"),
        _ => DictationStatus.Quiet("Idle"),
    };
}

/// <summary>How long a recording may run before the watchdog ends it.</summary>
public static class RecordingLimits
{
    public static readonly TimeSpan MaximumRecordingDuration = TimeSpan.FromMinutes(5);

    /// <summary>The production limit, or a short one a journey asked for through the environment.</summary>
    public static TimeSpan WatchdogDuration()
    {
        var requested = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_RECORDING_TIMEOUT_MILLISECONDS");
        return int.TryParse(requested, out var milliseconds) &&
            milliseconds is >= 500 and <= 30_000
                ? TimeSpan.FromMilliseconds(milliseconds)
                : MaximumRecordingDuration;
    }
}
