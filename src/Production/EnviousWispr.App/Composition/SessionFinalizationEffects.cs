using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Pipeline;

namespace EnviousWispr.App.Composition;

/// <summary>The shell's half of a finalisation: rendering, logging, and the leaf reads it supplies.</summary>
/// <remarks>
/// THE ENGINE, THE DELIVERY ROUTE AND THE OPTIONS ARE READ AT THE CALL, not captured when the session
/// was built: an engine loads after the app starts, a word taught mid-dictation reaches this polish,
/// a setting saved while the engine was working reaches this dictation. The runner decides when to
/// ask; this only answers.
/// </remarks>
internal sealed class SessionFinalizationEffects(SessionCompositionParts parts) : ISessionFinalizationEffects
{
    private readonly IAppLogger _logger = parts.Logger;

    private readonly ISessionView _view = parts.Shell.View;

    public ITranscriptionEngine? Engine => parts.Shell.Engine();

    public ITextDelivery? Delivery => parts.Shell.Delivery();

    public FinalizationOptions CurrentOptions() => parts.Shell.Options();

    public void ArchiveAudio(CapturedAudio audio) => parts.Shell.ArchiveAudio(audio);

    public void RecordTranscriptionUnavailable() =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationTranscriptionFailed,
            AppFailureCategory.AsrUnavailable));

    public void ShowTranscriptionUnavailable() =>
        _view.ShowStatus(DictationStatus.Advisory(
                "Audio captured, but local transcription is unavailable", StatusActions.OpenTranscription)
            .AboutTheTranscriptionEngine());

    public void ShowTranscribing() =>
        _view.ShowStatus(DictationStatus.Processing("Transcribing locally..."));

    public void RecordTranscriptionStarted() =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationTranscriptionStarted));

    public void RecordTranscriptionFinished(Transcript transcript, long elapsedMilliseconds) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            transcript.UsedFallback
                ? AppEventCode.DictationTranscriptionDegraded
                : AppEventCode.DictationTranscriptionCompleted,
            transcript.UsedFallback
                ? AppFailureCategories.For(transcript.DegradedError)
                : AppFailureCategory.None,
            elapsedMilliseconds));

    public void RecordTranscriptionFailed(AppError? failure, long elapsedMilliseconds) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationTranscriptionFailed,
            AppFailureCategories.For(failure),
            elapsedMilliseconds));

    public void ShowTranscriptionFailed() =>
        _view.ShowStatus(DictationStatus.Error("Local transcription failed safely"));

    public void ShowDelivering() =>
        _view.ShowStatus(DictationStatus.Processing("Delivering to the app you started in..."));

    public void RecordDeliveryStarted() =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.TextDeliveryStarted));

    public void RecordDelivery(DeliveryResult delivery, long elapsedMilliseconds)
    {
        var eventCode = delivery switch
        {
            { Delivered: true } => AppEventCode.TextDeliveryCompleted,
            { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.None } =>
                AppEventCode.TextDeliveryClipboardFallback,
            { ClipboardFallback: true } => AppEventCode.TextDeliveryRefused,
            _ => AppEventCode.TextDeliveryFailed,
        };
        var errorCode = delivery.RefusalReason switch
        {
            TextDeliveryRefusalReason.None => (AppErrorCode?)null,
            TextDeliveryRefusalReason.TargetUnavailable or
                TextDeliveryRefusalReason.TargetChanged => AppErrorCode.DeliveryTargetChanged,
            TextDeliveryRefusalReason.ProtectedField => AppErrorCode.DeliveryProtectedField,
            TextDeliveryRefusalReason.ElevatedTarget => AppErrorCode.DeliveryElevatedTarget,
            TextDeliveryRefusalReason.ClipboardUnavailable => AppErrorCode.DeliveryClipboardUnavailable,
            TextDeliveryRefusalReason.InputStateUnsafe or
                TextDeliveryRefusalReason.InputBlocked => AppErrorCode.DeliveryInputBlocked,
            TextDeliveryRefusalReason.UnsupportedTarget or
                TextDeliveryRefusalReason.UnsafeMultilineTarget => AppErrorCode.DeliveryUnsupportedTarget,
            // EACH WAY THE WORDS DID NOT LAND KEEPS ITS NAME IN THE LOG (plan-2 step 13): an
            // accessibility failure Windows reported, a direct write that could not be verified, the
            // caller's cancellation, a disposal under the delivery, a defect. They used to share
            // "unsupported target", which is a policy refusal and none of them.
            TextDeliveryRefusalReason.AccessibilityUnavailable => AppErrorCode.DeliveryAccessibilityUnavailable,
            TextDeliveryRefusalReason.DirectWriteUnverified => AppErrorCode.DeliveryUnverified,
            TextDeliveryRefusalReason.Cancelled => AppErrorCode.DeliveryCancelled,
            TextDeliveryRefusalReason.DeliveryDisposed => AppErrorCode.DeliveryDisposed,
            TextDeliveryRefusalReason.DeliveryFaulted => AppErrorCode.DeliveryFaulted,
            _ => AppErrorCode.DeliveryFaulted,
        };
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            eventCode,
            delivery.Delivered ? AppFailureCategory.None : AppFailureCategory.TextDelivery,
            elapsedMilliseconds,
            ErrorCode: errorCode));
    }

    public void ReportDelivery(DeliveryResult delivery, string? language) =>
        _view.ReportDelivery(DeliveryStatusReport.For(delivery), language);

    public void ShowEscapeRecoveryFinished() =>
        _view.ShowNotice(
            "Escape Recovery finished",
            "The dictation is ready to copy on Home and stays in History for 24 hours unless you Keep it.");

    public void ShowHeldStatus(FinalizationReport report)
    {
        var processed = report.Finalized?.Processed;
        var polishResult = report.Finalized?.Polish;
        var transcript = report.Transcript;
        var recoveryOnly = report.Outcome == FinalizationOutcome.EscapeRecovery;
        var cloudProvider = parts.Shell.CloudPolishProviderName();
        DictationStatus status = processed is null || string.IsNullOrWhiteSpace(processed.Output.Text)
                ? DictationStatus.Quiet("No speech detected")
                : recoveryOnly
                    ? DictationStatus.Quiet("Escape Recovery finished. Text is ready to copy")
                : processed.IsDegraded
                ? DictationStatus.Success("Transcribed and cleaned locally with a safe fallback")
                : polishResult is { UsedFallback: true }
                    ? DictationStatus.Success(PolishFallbackStatus(polishResult))
                : polishResult is { UsedFallback: false }
                    ? cloudProvider is null
                        ? DictationStatus.Success("Transcribed and polished locally")
                        : DictationStatus.Success(
                            $"Transcribed and polished directly with {cloudProvider}")
                : transcript is { UsedFallback: true }
                    ? DictationStatus.Success("Transcribed and cleaned locally with CPU fallback")
                    : DictationStatus.Success("Transcribed and cleaned locally");
        _view.ShowStatus(status);
    }

    public void RecordDictationCompleted(long waitMilliseconds) =>
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationCompleted,
            AppFailureCategory.None,
            waitMilliseconds));

    private static string PolishFallbackStatus(PolishResult result) => result.Error?.Code switch
    {
        AppErrorCode.PolishEndpointInvalid =>
            "Cleaned locally; Ollama endpoint must point to this PC",
        AppErrorCode.PolishRemoteModelDisallowed =>
            "Cleaned locally; hosted Ollama models are disabled",
        AppErrorCode.PolishModelUnavailable =>
            "Cleaned locally; the selected Ollama model is not installed",
        AppErrorCode.PolishTimedOut =>
            "Cleaned locally; Ollama timed out",
        AppErrorCode.PolishProviderUnavailable =>
            "Cleaned locally; Ollama is offline",
        AppErrorCode.PolishOutputTruncated =>
            "Cleaned locally; Ollama returned incomplete text",
        _ => "Cleaned locally; AI polish failed safely",
    };
}

/// <summary>The buttons an advisory can carry.</summary>
/// <remarks>
/// ONE INSTANCE RATHER THAN ONE PER ROW. Several sentences send the user to the same page, and
/// several copies of the same two words is how one of them ends up saying something else.
/// </remarks>
public static class StatusActions
{
    /// <summary>The button an advisory about the speech engine carries.</summary>
    public static readonly PillAction OpenTranscription =
        new("Open settings", PillActionKind.OpenTranscriptionSettings, "Open transcription settings");
}
