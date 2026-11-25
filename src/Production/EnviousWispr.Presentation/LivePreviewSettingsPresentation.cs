using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

public enum LivePreviewReadinessKind
{
    Off,
    Ready,
    StandingInWithParakeet,
    DownloadingWithParakeetStandIn,
    Downloading,
    NeedsDownload,
    DownloadFailed,
    ParakeetModelMissing,
}

public enum LivePreviewStatusTone
{
    Off,
    Ready,
    Warning,
    Error,
}

public sealed record LivePreviewEngineCardPresentation(
    LivePreviewEngineChoice Engine,
    string Title,
    string Description,
    string StatusText,
    bool IsSelected,
    double? ProgressPercent = null,
    bool CanDownload = false,
    string DownloadButtonText = "Download (190 MB)",
    bool CanCancel = false,
    bool CanRemove = false,
    string BadgeText = "",
    string Tagline = "",
    string ModelName = "",
    string LanguagesText = "",
    string DownloadSizeText = "",
    string HardwareNote = "",
    string StatusHeadline = "");

/// <summary>Confirmation dialog copy shown before removing the downloaded Whisper preview model.</summary>
public sealed record LivePreviewWhisperRemovalConfirmation(
    string Title,
    string Message,
    string ConfirmButtonText,
    string CancelButtonText);

public sealed record LivePreviewSectionPresentation(
    LivePreviewReadinessKind ReadinessKind,
    string StatusBadgeText,
    LivePreviewStatusTone StatusTone,
    string StatusDetailText,
    string PrivacyReassuranceText,
    LivePreviewEngineCardPresentation ParakeetCard,
    LivePreviewEngineCardPresentation WhisperCard);

/// <summary>
/// Headless presentation mapping for the Dictation Settings Live Preview section (macOS #2154, #2123, #4325; Windows #127).
/// </summary>
public static class LivePreviewSettingsPresentation
{
    public const string ParakeetCardTitle = "Parakeet (Fast - Default)";
    public const string ParakeetCardDescription =
        "Fast everyday live preview in 25 languages. Reuses the default speech model (no extra download) and runs smoothly even without a graphics card.";

    public const string WhisperCardTitle = "Whisper (Full Multilingual)";
    public const string WhisperCardDescription =
        "Covers 99 languages with automatic language detection. Needs one optional 190 MB download; best with an NVIDIA graphics card (slower on processor-only PCs).";

    public const string PrivacyReassuranceCopy =
        "Live Preview is display-only, stays on this PC, is discarded when the recording ends, and never changes a character of what gets pasted.";

    public static LivePreviewSectionPresentation Present(
        bool isEnabled,
        LivePreviewEngineChoice selectedEngine,
        bool parakeetInstalled,
        bool whisperPreviewInstalled,
        SpeechModelDelivery whisperDeliveryStage = SpeechModelDelivery.Unknown,
        double? whisperDownloadPercent = null,
        string? whisperDeliveryDetail = null,
        bool canRemoveWhisperModel = false)
    {
        var parakeetSelected = selectedEngine == LivePreviewEngineChoice.Parakeet;
        var whisperSelected = selectedEngine == LivePreviewEngineChoice.Whisper;
        var whisperDownloading = !whisperPreviewInstalled &&
            whisperDeliveryStage is SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating;

        var parakeetCardStatus = !parakeetInstalled
            ? "Requires the default Parakeet speech model on the Engine tab."
            : whisperSelected && !whisperPreviewInstalled && isEnabled
                ? "Standing in while the Whisper preview model is not downloaded yet."
                : "Included with the default speech model (0 MB extra download).";

        var parakeetStatusHeadline = !parakeetInstalled
            ? "Model missing"
            : whisperSelected && !whisperPreviewInstalled && isEnabled
                ? "Standing in"
                : "Included";

        var parakeetCard = new LivePreviewEngineCardPresentation(
            LivePreviewEngineChoice.Parakeet,
            ParakeetCardTitle,
            ParakeetCardDescription,
            parakeetCardStatus,
            IsSelected: parakeetSelected,
            BadgeText: "Recommended",
            Tagline: "Fast everyday live preview",
            ModelName: ParakeetModelIds.Final,
            LanguagesText: "25 languages",
            DownloadSizeText: "0 MB extra",
            HardwareNote: "Runs fast on CPU",
            StatusHeadline: parakeetStatusHeadline);

        var whisperCard = BuildWhisperCard(
            isEnabled,
            whisperSelected,
            parakeetInstalled,
            whisperPreviewInstalled,
            whisperDeliveryStage,
            whisperDownloadPercent,
            whisperDeliveryDetail,
            canRemoveWhisperModel);

        if (!isEnabled)
        {
            return new LivePreviewSectionPresentation(
                LivePreviewReadinessKind.Off,
                StatusBadgeText: "Off",
                LivePreviewStatusTone.Off,
                StatusDetailText: "Switch it on and this bar will show whether anything else is needed.",
                PrivacyReassuranceCopy,
                parakeetCard,
                whisperCard);
        }

        if (parakeetSelected)
        {
            return parakeetInstalled
                ? new LivePreviewSectionPresentation(
                    LivePreviewReadinessKind.Ready,
                    StatusBadgeText: "Ready",
                    LivePreviewStatusTone.Ready,
                    StatusDetailText: "Ready to show your words while you speak.",
                    PrivacyReassuranceCopy,
                    parakeetCard,
                    whisperCard)
                : new LivePreviewSectionPresentation(
                    LivePreviewReadinessKind.ParakeetModelMissing,
                    StatusBadgeText: "Needs download",
                    LivePreviewStatusTone.Warning,
                    StatusDetailText: "Download the default Parakeet speech model on the Engine tab to start Live Preview.",
                    PrivacyReassuranceCopy,
                    parakeetCard,
                    whisperCard);
        }

        if (whisperPreviewInstalled)
        {
            return new LivePreviewSectionPresentation(
                LivePreviewReadinessKind.Ready,
                StatusBadgeText: "Ready",
                LivePreviewStatusTone.Ready,
                StatusDetailText: "Ready to show your words while you speak.",
                PrivacyReassuranceCopy,
                parakeetCard,
                whisperCard);
        }

        if (whisperDownloading)
        {
            return parakeetInstalled
                ? new LivePreviewSectionPresentation(
                    LivePreviewReadinessKind.DownloadingWithParakeetStandIn,
                    StatusBadgeText: "Using Parakeet while Multilingual downloads",
                    LivePreviewStatusTone.Warning,
                    StatusDetailText: "Parakeet shows your live words right now and switches to Whisper when the 190 MB download finishes.",
                    PrivacyReassuranceCopy,
                    parakeetCard,
                    whisperCard)
                : new LivePreviewSectionPresentation(
                    LivePreviewReadinessKind.Downloading,
                    StatusBadgeText: "Downloading",
                    LivePreviewStatusTone.Warning,
                    StatusDetailText: "Live Preview starts when the 190 MB Whisper download finishes.",
                    PrivacyReassuranceCopy,
                    parakeetCard,
                    whisperCard);
        }

        if (whisperDeliveryStage == SpeechModelDelivery.Failed)
        {
            var failureReason = string.IsNullOrWhiteSpace(whisperDeliveryDetail)
                ? "The Whisper preview download did not finish."
                : whisperDeliveryDetail.Trim();
            var detail = parakeetInstalled
                ? $"{failureReason} Parakeet is standing in meanwhile."
                : failureReason;
            return new LivePreviewSectionPresentation(
                LivePreviewReadinessKind.DownloadFailed,
                StatusBadgeText: "Download did not finish",
                LivePreviewStatusTone.Error,
                StatusDetailText: detail,
                PrivacyReassuranceCopy,
                parakeetCard,
                whisperCard);
        }

        return parakeetInstalled
            ? new LivePreviewSectionPresentation(
                LivePreviewReadinessKind.StandingInWithParakeet,
                StatusBadgeText: "Using Parakeet",
                LivePreviewStatusTone.Warning,
                StatusDetailText: "Parakeet is standing in right now. Download the 190 MB Whisper preview model below for 99-language live preview.",
                PrivacyReassuranceCopy,
                parakeetCard,
                whisperCard)
            : new LivePreviewSectionPresentation(
                LivePreviewReadinessKind.NeedsDownload,
                StatusBadgeText: "Needs download",
                LivePreviewStatusTone.Warning,
                StatusDetailText: "Download the 190 MB Whisper preview model from the card below.",
                PrivacyReassuranceCopy,
                parakeetCard,
                whisperCard);
    }

    public static double? RenderableProgress(double? percent)
    {
        if (percent is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        return Math.Clamp(value, 0.0, 100.0);
    }

    public static LivePreviewWhisperRemovalConfirmation DescribeWhisperRemovalDialog(
        bool whisperSelected,
        bool livePreviewEnabled,
        bool parakeetInstalled = true)
    {
        var message = (whisperSelected && livePreviewEnabled, parakeetInstalled) switch
        {
            (true, true) =>
                "Whisper is currently selected for Live Preview. Removing the 190 MB preview model deletes it from this PC and falls back to Parakeet until you download Whisper again.",
            (true, false) =>
                "Whisper is currently selected for Live Preview. Removing the 190 MB preview model deletes it from this PC, and Live Preview will stay unavailable until you download Whisper or Parakeet again.",
            (false, true) =>
                "This deletes the 190 MB Whisper preview model from this PC. Parakeet remains available for Live Preview, and you can re-download Whisper here anytime.",
            (false, false) =>
                "This deletes the 190 MB Whisper preview model from this PC. No local Live Preview model is installed right now, and you can re-download Whisper or Parakeet anytime.",
        };

        return new LivePreviewWhisperRemovalConfirmation(
            Title: "Remove Whisper preview model?",
            Message: message,
            ConfirmButtonText: "Remove Whisper model",
            CancelButtonText: "Keep model");
    }

    private static LivePreviewEngineCardPresentation BuildWhisperCard(
        bool isEnabled,
        bool isSelected,
        bool parakeetInstalled,
        bool whisperPreviewInstalled,
        SpeechModelDelivery deliveryStage,
        double? downloadPercent,
        string? deliveryDetail,
        bool canRemove)
    {
        const string badgeText = "99+ languages";
        const string tagline = "Multilingual live preview";
        const string languagesText = "99+ languages";
        const string downloadSizeText = "190 MB";
        const string hardwareNote = "Best with NVIDIA GPU";

        if (whisperPreviewInstalled)
        {
            return new LivePreviewEngineCardPresentation(
                LivePreviewEngineChoice.Whisper,
                WhisperCardTitle,
                WhisperCardDescription,
                StatusText: "190 MB preview model installed and verified.",
                IsSelected: isSelected,
                CanRemove: canRemove && deliveryStage is not (SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating),
                BadgeText: badgeText,
                Tagline: tagline,
                ModelName: WhisperModelIds.Preview,
                LanguagesText: languagesText,
                DownloadSizeText: downloadSizeText,
                HardwareNote: hardwareNote,
                StatusHeadline: isSelected ? "Installed · Active" : "Installed");
        }

        if (deliveryStage is SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating)
        {
            var progressText = !string.IsNullOrWhiteSpace(deliveryDetail)
                ? deliveryDetail.Trim()
                : isSelected && isEnabled && parakeetInstalled
                    ? "Downloading 190 MB preview model... Parakeet is standing in meanwhile."
                    : "Downloading 190 MB preview model...";
            return new LivePreviewEngineCardPresentation(
                LivePreviewEngineChoice.Whisper,
                WhisperCardTitle,
                WhisperCardDescription,
                StatusText: progressText,
                IsSelected: isSelected,
                ProgressPercent: RenderableProgress(downloadPercent),
                CanCancel: true,
                BadgeText: badgeText,
                Tagline: tagline,
                ModelName: WhisperModelIds.Preview,
                LanguagesText: languagesText,
                DownloadSizeText: downloadSizeText,
                HardwareNote: hardwareNote,
                StatusHeadline: deliveryStage == SpeechModelDelivery.Activating ? "Activating..." : "Downloading...");
        }

        if (deliveryStage == SpeechModelDelivery.Failed)
        {
            var failureText = !string.IsNullOrWhiteSpace(deliveryDetail)
                ? deliveryDetail.Trim()
                : "The download did not finish. Check your connection and try again.";
            return new LivePreviewEngineCardPresentation(
                LivePreviewEngineChoice.Whisper,
                WhisperCardTitle,
                WhisperCardDescription,
                StatusText: failureText,
                IsSelected: isSelected,
                CanDownload: true,
                DownloadButtonText: "Try again",
                BadgeText: badgeText,
                Tagline: tagline,
                ModelName: WhisperModelIds.Preview,
                LanguagesText: languagesText,
                DownloadSizeText: downloadSizeText,
                HardwareNote: hardwareNote,
                StatusHeadline: "Download failed");
        }

        if (deliveryStage == SpeechModelDelivery.Cancelled)
        {
            return new LivePreviewEngineCardPresentation(
                LivePreviewEngineChoice.Whisper,
                WhisperCardTitle,
                WhisperCardDescription,
                StatusText: "Download paused. It resumes where it stopped when you download again.",
                IsSelected: isSelected,
                CanDownload: true,
                DownloadButtonText: "Resume download",
                BadgeText: badgeText,
                Tagline: tagline,
                ModelName: WhisperModelIds.Preview,
                LanguagesText: languagesText,
                DownloadSizeText: downloadSizeText,
                HardwareNote: hardwareNote,
                StatusHeadline: "Download paused");
        }

        var notDownloadedStatus = isSelected && isEnabled && parakeetInstalled
            ? "Not downloaded yet (190 MB). Parakeet is standing in until you download it."
            : "Not downloaded yet (190 MB optional download).";
        return new LivePreviewEngineCardPresentation(
            LivePreviewEngineChoice.Whisper,
            WhisperCardTitle,
            WhisperCardDescription,
            StatusText: notDownloadedStatus,
            IsSelected: isSelected,
            CanDownload: deliveryStage != SpeechModelDelivery.CannotDownload,
            DownloadButtonText: "Download (190 MB)",
            BadgeText: badgeText,
            Tagline: tagline,
            ModelName: WhisperModelIds.Preview,
            LanguagesText: languagesText,
            DownloadSizeText: downloadSizeText,
            HardwareNote: hardwareNote,
            StatusHeadline: "Not installed");
    }
}
