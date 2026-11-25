using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Customer-facing summary details for the selected final transcription engine.</summary>
public sealed record EngineSummaryPresentation(
    string Name,
    string BadgeText,
    string Description,
    string IconGlyph,
    bool ShowWhisperLanguage);

/// <summary>Runtime installation and download state for one final transcription engine model pack.</summary>
public sealed record SpeechEngineModelState(
    bool IsInstalled,
    bool CanRemove,
    bool CanDownload,
    SpeechModelDelivery DeliveryStage = SpeechModelDelivery.Unknown,
    double? DownloadPercent = null,
    string? StatusDetail = null,
    string? BundleSizeText = null)
{
    public static SpeechEngineModelState Unknown(string bundleSizeText) => new(
        IsInstalled: false,
        CanRemove: false,
        CanDownload: false,
        DeliveryStage: SpeechModelDelivery.Unknown,
        BundleSizeText: bundleSizeText);
}

/// <summary>Side-by-side presentation model for a final transcription engine card (Parakeet or Whisper).</summary>
public sealed record SpeechEngineCardPresentation(
    FinalAsrEngine Engine,
    string Name,
    string BadgeText,
    string Tagline,
    string ModelName,
    string LanguagesText,
    string BundleSizeText,
    string HardwareNote,
    string Description,
    bool IsSelected,
    bool IsInstalled,
    string StatusText,
    string? StatusDetail,
    bool ShowDownloadButton,
    string DownloadButtonText,
    bool DownloadButtonEnabled,
    bool ShowProgress,
    double DownloadPercent,
    bool ShowCancelButton,
    bool ShowRemoveButton,
    bool RemoveButtonEnabled);

/// <summary>Confirmation copy shown before uninstalling a currently active speech engine model.</summary>
public sealed record SpeechEngineRemovalConfirmation(
    string Title,
    string Message,
    string ConfirmButtonText,
    string CancelButtonText);

/// <summary>Visibility and customer-facing copy for the Dictation output language card.</summary>
public sealed record DictationOutputLanguagePresentation(
    bool IsVisible,
    string StatusText);

/// <summary>Pure presentation helpers for the transcription engine, EG-1 sizing, and output language cards.</summary>
public static class EnginePolishPresentation
{
    public const string ParakeetBundleSizeLabel = "640 MB";
    public const string WhisperBundleSizeLabel = "548 MB";
    public const string RemovalBlockedBusyNotice =
        "Finish the current dictation or file transcription before removing a speech model.";

    public const string EgOneFullModelId = "eg-1";
    public const string EgOneMiniModelId = "eg-1-mini";
    public const string EgOneMicroModelId = "eg-1-micro";

    public static EngineSummaryPresentation DescribeEngine(FinalAsrEngine engine) => engine switch
    {
        FinalAsrEngine.Whisper => new EngineSummaryPresentation(
            Name: "Whisper",
            BadgeText: "99+ languages",
            Description: "Multilingual speech recognition for 99+ languages (548 MB). Best with an NVIDIA graphics card; slower on processor-only PCs.",
            IconGlyph: "\uE774",
            ShowWhisperLanguage: true),
        _ => new EngineSummaryPresentation(
            Name: "Parakeet",
            BadgeText: "Recommended",
            Description: "Fast local speech recognition for 25 languages (640 MB) that runs smoothly on most PCs.",
            IconGlyph: "\uE945",
            ShowWhisperLanguage: false),
    };

    public static string DefaultBundleSizeLabel(FinalAsrEngine engine) => engine switch
    {
        FinalAsrEngine.Whisper => WhisperBundleSizeLabel,
        _ => ParakeetBundleSizeLabel,
    };

    public static SpeechEngineCardPresentation DescribeEngineCard(
        FinalAsrEngine engine,
        FinalAsrEngine selectedEngine,
        SpeechEngineModelState state,
        bool otherDownloadInFlight)
    {
        ArgumentNullException.ThrowIfNull(state);
        var summary = DescribeEngine(engine);
        var bundleSize = string.IsNullOrWhiteSpace(state.BundleSizeText)
            ? DefaultBundleSizeLabel(engine)
            : state.BundleSizeText;
        var isWhisper = engine == FinalAsrEngine.Whisper;
        var isDownloading = state.DeliveryStage == SpeechModelDelivery.Downloading;
        var isActivating = state.DeliveryStage == SpeechModelDelivery.Activating;
        var showDownload = !state.IsInstalled &&
            state.CanDownload &&
            state.DeliveryStage is not (SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating or SpeechModelDelivery.CannotDownload);
        var downloadLabel = state.DeliveryStage is SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled
            ? $"Retry ({bundleSize})"
            : $"Download ({bundleSize})";
        var statusText = state.DeliveryStage switch
        {
            SpeechModelDelivery.Downloading => "Downloading...",
            SpeechModelDelivery.Activating => "Starting...",
            SpeechModelDelivery.Failed => "Download failed",
            SpeechModelDelivery.Cancelled => "Download cancelled",
            SpeechModelDelivery.CannotDownload => "Unavailable",
            _ => state.IsInstalled ? "Installed" : "Not installed",
        };
        var detail = state.DeliveryStage switch
        {
            SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating or SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled or SpeechModelDelivery.CannotDownload
                => state.StatusDetail,
            _ => state.IsInstalled ? "Verified on this PC for offline dictation." : $"Ready to download ({bundleSize}).",
        };

        return new SpeechEngineCardPresentation(
            Engine: engine,
            Name: summary.Name,
            BadgeText: summary.BadgeText,
            Tagline: isWhisper ? "Broad multilingual" : "Fast local accuracy",
            ModelName: isWhisper ? WhisperModelIds.Final : ParakeetModelIds.Final,
            LanguagesText: isWhisper ? "99+ languages" : "25 languages",
            BundleSizeText: bundleSize,
            HardwareNote: isWhisper ? "Best with NVIDIA GPU" : "Runs fast on CPU",
            Description: summary.Description,
            IsSelected: engine == selectedEngine,
            IsInstalled: state.IsInstalled,
            StatusText: statusText,
            StatusDetail: detail,
            ShowDownloadButton: showDownload,
            DownloadButtonText: downloadLabel,
            DownloadButtonEnabled: !otherDownloadInFlight,
            ShowProgress: isDownloading && state.DownloadPercent is not null,
            DownloadPercent: state.DownloadPercent ?? 0,
            ShowCancelButton: isDownloading,
            ShowRemoveButton: state.IsInstalled && state.CanRemove && !isDownloading && !isActivating,
            RemoveButtonEnabled: !otherDownloadInFlight);
    }

    public static SpeechEngineRemovalConfirmation DescribeActiveEngineRemovalDialog(
        FinalAsrEngine engine,
        bool livePreviewUsesParakeet,
        bool isSelectedFinalEngine = true)
    {
        if (engine == FinalAsrEngine.Whisper)
        {
            return new SpeechEngineRemovalConfirmation(
                Title: "Remove Whisper model?",
                Message: "Whisper is currently selected for dictation. Removing it pauses final Whisper transcription until you download it again or switch to Parakeet.",
                ConfirmButtonText: "Remove model",
                CancelButtonText: "Keep model");
        }

        if (!isSelectedFinalEngine)
        {
            return new SpeechEngineRemovalConfirmation(
                Title: "Remove Parakeet model?",
                Message: "Parakeet is currently powering Live Preview. Removing it keeps Whisper active for dictation, while Parakeet Live Preview will be unavailable until you download Parakeet again.",
                ConfirmButtonText: "Remove model",
                CancelButtonText: "Keep model");
        }

        var previewSentence = livePreviewUsesParakeet
            ? " Parakeet Live Preview will also become unavailable until Parakeet is downloaded again."
            : " Parakeet Live Preview will remain unavailable while Parakeet is removed.";
        return new SpeechEngineRemovalConfirmation(
            Title: "Remove Parakeet model?",
            Message: "Parakeet is currently selected for dictation. Removing it pauses final Parakeet transcription until you download it again or switch to Whisper." +
                previewSentence,
            ConfirmButtonText: "Remove model",
            CancelButtonText: "Keep model");
    }

    public static string NormalizeEgOneModelId(string? modelId)
    {
        var trimmed = modelId?.Trim();
        if (string.Equals(trimmed, EgOneMiniModelId, StringComparison.OrdinalIgnoreCase))
        {
            return EgOneMiniModelId;
        }

        if (string.Equals(trimmed, EgOneMicroModelId, StringComparison.OrdinalIgnoreCase))
        {
            return EgOneMicroModelId;
        }

        return EgOneFullModelId;
    }

    public static bool IsKnownEgOneModelId(string? modelId)
    {
        var trimmed = modelId?.Trim();
        return string.Equals(trimmed, EgOneFullModelId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, EgOneMiniModelId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, EgOneMicroModelId, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatEgOneInstallStatus(bool serverExecutableExists, bool modelFileExists) =>
        serverExecutableExists && modelFileExists
            ? "Ready on this PC"
            : "Not installed yet (local model files required)";

    public static DictationOutputLanguagePresentation DescribeDictationOutputLanguage(PolishProvider provider) =>
        provider switch
        {
            PolishProvider.None => new DictationOutputLanguagePresentation(
                IsVisible: false,
                StatusText: string.Empty),
            PolishProvider.Anthropic => new DictationOutputLanguagePresentation(
                IsVisible: true,
                StatusText: "Claude is selected. Translation output options will appear here in a future update."),
            _ => new DictationOutputLanguagePresentation(
                IsVisible: true,
                StatusText: "Switch AI Polish to Claude (Anthropic) to unlock dictation translation when available."),
        };
}
