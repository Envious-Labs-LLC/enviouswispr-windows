using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Customer-facing summary details for the selected final transcription engine.</summary>
public sealed record EngineSummaryPresentation(
    string Name,
    string BadgeText,
    string Description,
    string IconGlyph,
    bool ShowWhisperLanguage,
    bool IsRecommended = false);

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
    bool RemoveButtonEnabled,
    bool IsRecommended = false);

/// <summary>Confirmation copy shown before uninstalling a currently active speech engine model.</summary>
public sealed record SpeechEngineRemovalConfirmation(
    string Title,
    string Message,
    string ConfirmButtonText,
    string CancelButtonText);

/// <summary>Static quality, speed, and footprint metadata for one EG-1 tier.</summary>
public sealed record EgOneTierFacts(
    string ModelId,
    string Title,
    string Tagline,
    string Description,
    string QualityText,
    string SpeedText,
    string MemoryText,
    string DefaultDiskSizeText);

/// <summary>Runtime installation and download state for one EG-1 model size.</summary>
public sealed record EgOneModelState(
    string ModelId,
    bool IsInstalled,
    bool CanRemove,
    bool HasBundledManifest,
    SpeechModelDelivery DeliveryStage = SpeechModelDelivery.Missing,
    double? DownloadPercent = null,
    string? StatusDetail = null,
    string? BundleSizeText = null)
{
    public static EgOneModelState Unknown(string modelId) => new(
        ModelId: EgOneModelCatalog.NormalizeOrDefault(modelId),
        IsInstalled: false,
        CanRemove: false,
        HasBundledManifest: false,
        DeliveryStage: SpeechModelDelivery.Missing);
}

/// <summary>Presentation model for one selectable EG-1 size card with inline download/remove actions.</summary>
public sealed record EgOneCardPresentation(
    string ModelId,
    string Title,
    string Tagline,
    string Description,
    string QualityText,
    string SpeedText,
    string MemoryText,
    string DiskSizeText,
    string SpecsSummary,
    bool IsRecommended,
    bool IsSelected,
    bool IsInstalled,
    string StatusText,
    string StatusDetail,
    bool ShowDownloadButton,
    string DownloadButtonText,
    bool DownloadButtonEnabled,
    bool ShowProgress,
    double DownloadPercent,
    bool ShowCancelButton,
    bool ShowRemoveButton,
    bool RemoveButtonEnabled,
    string AutomationName,
    bool CanSelect = true,
    bool ShowChooseProviderButton = false,
    EgOneMemoryPresentation? Memory = null);

/// <summary>Summary badge and status text for the active AI Polish provider header card.</summary>
public sealed record ActivePolishProviderBadgePresentation(
    string BadgeText,
    string StatusText,
    string ChipStyleKey,
    string ChipTextStyleKey);

/// <summary>Confirmation copy shown before removing an installed EG-1 model size.</summary>
public sealed record EgOneRemovalConfirmation(
    string Title,
    string Message,
    string ConfirmButtonText,
    string CancelButtonText);

/// <summary>A user-facing choice for how soon an idle local EG-1 model releases memory.</summary>
public sealed record PolishUnloadPolicyChoice(PolishUnloadPolicy Policy, string DisplayName);

/// <summary>Visibility and customer-facing copy for the Dictation output language card.</summary>
public sealed record DictationOutputLanguagePresentation(
    bool IsVisible,
    string StatusText);

/// <summary>Pure presentation helpers for the transcription engine, EG-1 sizing, and output language cards.</summary>
public static class EnginePolishPresentation
{
    public const string UnloadPolishModelTitle = "Unload polish model after";
    public const string UnloadPolishModelHelp =
        "Frees the polish model's memory after the time you choose. Your next dictation wakes it while you speak; the first one after sleeping may take a moment longer.";
    public const string UnloadPolishModelImmediatelyWarning =
        "The polish model is freed after about 10 seconds without a request. Expect a short wait if you finish speaking before it wakes.";

    public static IReadOnlyList<PolishUnloadPolicyChoice> PolishUnloadPolicyChoices { get; } =
    [
        new(PolishUnloadPolicy.Never, "Never"),
        new(PolishUnloadPolicy.Immediately, "Immediately"),
        new(PolishUnloadPolicy.TwoMinutes, "After 2 minutes"),
        new(PolishUnloadPolicy.FiveMinutes, "After 5 minutes"),
        new(PolishUnloadPolicy.TenMinutes, "After 10 minutes"),
        new(PolishUnloadPolicy.FifteenMinutes, "After 15 minutes"),
        new(PolishUnloadPolicy.SixtyMinutes, "After 1 hour"),
    ];
    public const string ParakeetBundleSizeLabel = "640 MB";
    public const string WhisperBundleSizeLabel = "548 MB";
    public const string RemovalBlockedBusyNotice =
        "Finish the current dictation or file transcription before removing a speech model.";
    public const string SpeechDownloadBlockedBusyNotice =
        "Finish the current dictation or file transcription, then download.";
    public const string EgOneRemovalBlockedBusyNotice =
        "Finish the current dictation or file transcription before removing an EG-1 model.";
    public const string EgOneDownloadBlockedBusyNotice =
        "Finish the current dictation or file transcription, then download.";

    public const string EgOneFullModelId = EgOneModelCatalog.FullId;
    public const string EgOneMiniModelId = EgOneModelCatalog.MiniId;
    public const string EgOneMicroModelId = EgOneModelCatalog.MicroId;

    public static EngineSummaryPresentation DescribeEngine(FinalAsrEngine engine) => engine switch
    {
        FinalAsrEngine.Whisper => new EngineSummaryPresentation(
            Name: "Whisper",
            BadgeText: "99+ languages",
            Description: "Multilingual speech recognition for 99+ languages (548 MB). Best with an NVIDIA graphics card; slower on processor-only PCs.",
            IconGlyph: "\uE774",
            ShowWhisperLanguage: true,
            IsRecommended: false),
        _ => new EngineSummaryPresentation(
            Name: "Parakeet",
            BadgeText: "Recommended",
            Description: "Fast local speech recognition for 25 languages (640 MB) that runs smoothly on most PCs.",
            IconGlyph: "\uE945",
            ShowWhisperLanguage: true,
            IsRecommended: true),
    };

    public static EngineSummaryPresentation DescribeEngine(
        FinalAsrEngine engine,
        FinalAsrEngine? recommendedEngine,
        string? recommendedBadgeText = null)
    {
        var baseSummary = DescribeEngine(engine);
        if (recommendedEngine is not { } recommended)
        {
            return baseSummary;
        }

        var isRecommended = engine == recommended;
        var badgeText = isRecommended
            ? (string.IsNullOrWhiteSpace(recommendedBadgeText) ? "Recommended" : recommendedBadgeText.Trim())
            : (engine == FinalAsrEngine.Whisper ? "99+ languages" : "25 languages");
        return baseSummary with
        {
            BadgeText = badgeText,
            IsRecommended = isRecommended,
        };
    }

    public static string DefaultBundleSizeLabel(FinalAsrEngine engine) => engine switch
    {
        FinalAsrEngine.Whisper => WhisperBundleSizeLabel,
        _ => ParakeetBundleSizeLabel,
    };

    public static SpeechEngineCardPresentation DescribeEngineCard(
        FinalAsrEngine engine,
        FinalAsrEngine selectedEngine,
        SpeechEngineModelState state,
        bool otherDownloadInFlight) =>
        DescribeEngineCard(
            engine,
            selectedEngine,
            state,
            otherDownloadInFlight,
            recommendedEngine: null,
            recommendedBadgeText: null);

    public static SpeechEngineCardPresentation DescribeEngineCard(
        FinalAsrEngine engine,
        FinalAsrEngine selectedEngine,
        SpeechEngineModelState state,
        bool otherDownloadInFlight,
        FinalAsrEngine? recommendedEngine,
        string? recommendedBadgeText = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var summary = DescribeEngine(engine, recommendedEngine, recommendedBadgeText);
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
            RemoveButtonEnabled: !otherDownloadInFlight,
            IsRecommended: summary.IsRecommended);
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

    public static string NormalizeEgOneModelId(string? modelId) =>
        EgOneModelCatalog.NormalizeOrDefault(modelId);

    public static string? NormalizeOptionalEgOneModelId(string? modelId) =>
        EgOneModelCatalog.TryNormalize(modelId, out var normalized) ? normalized : null;

    public static bool IsKnownEgOneModelId(string? modelId) =>
        EgOneModelCatalog.IsKnown(modelId);

    public static EgOneTierFacts DescribeEgOneTier(string? modelId) =>
        NormalizeEgOneModelId(modelId) switch
        {
            EgOneMiniModelId => new EgOneTierFacts(
                ModelId: EgOneMiniModelId,
                Title: "EG-1 Mini",
                Tagline: "Fast all-rounder",
                Description: "High accuracy at roughly half the size of EG-1. Ideal for everyday laptops and fast GPU-accelerated polish.",
                QualityText: "High accuracy",
                SpeedText: "Fast",
                MemoryText: "~1.6 GB memory",
                DefaultDiskSizeText: "~1.4 GB disk"),
            EgOneMicroModelId => new EgOneTierFacts(
                ModelId: EgOneMicroModelId,
                Title: "EG-1 Micro",
                Tagline: "Smallest and fastest",
                Description: "Ultra-lightweight model built for fast local polish on CPU-only laptops and PCs with tighter memory.",
                QualityText: "Fast lightweight polish",
                SpeedText: "Fastest",
                MemoryText: "~1.0 GB memory",
                DefaultDiskSizeText: "~520 MB disk"),
            _ => new EgOneTierFacts(
                ModelId: EgOneFullModelId,
                Title: "EG-1",
                Tagline: "Best quality",
                Description: "The most accurate offline model. Best on PCs with 24 GB+ RAM and GPU acceleration for long dictation and multilingual cleanup.",
                QualityText: "Best accuracy",
                SpeedText: "Standard speed",
                MemoryText: "~3.4 GB memory",
                DefaultDiskSizeText: "~3.0 GB disk"),
        };

    public static string DescribeEgOneUnavailableNotice(string? modelId)
    {
        var tier = DescribeEgOneTier(modelId);
        return $"{tier.Title} is not available to download in this version yet. Choose another AI Polish provider, such as OpenAI, Claude, Gemini, or Ollama.";
    }

    public static ActivePolishProviderBadgePresentation DescribeActivePolishProvider(
        PolishProvider provider,
        EgOneModelState? selectedEgOneState,
        bool egOneServerExists,
        bool isOllamaHosted)
    {
        switch (provider)
        {
            case PolishProvider.EgOne:
                if (selectedEgOneState?.DeliveryStage == SpeechModelDelivery.Downloading)
                {
                    return new ActivePolishProviderBadgePresentation(
                        BadgeText: "Downloading...",
                        StatusText: "Preparing EG-1 on this PC.",
                        ChipStyleKey: "BrandAccentChipStyle",
                        ChipTextStyleKey: "BrandAccentChipTextStyle");
                }

                if (selectedEgOneState?.DeliveryStage == SpeechModelDelivery.Activating)
                {
                    return new ActivePolishProviderBadgePresentation(
                        BadgeText: "Activating...",
                        StatusText: "Activating EG-1 on this PC.",
                        ChipStyleKey: "BrandAccentChipStyle",
                        ChipTextStyleKey: "BrandAccentChipTextStyle");
                }

                if (selectedEgOneState?.IsInstalled == true && egOneServerExists)
                {
                    return new ActivePolishProviderBadgePresentation(
                        BadgeText: "On this PC",
                        StatusText: "Our model for cleaning up dictation on this PC.",
                        ChipStyleKey: "BrandSuccessChipStyle",
                        ChipTextStyleKey: "BrandSuccessChipTextStyle");
                }

                if (selectedEgOneState?.IsInstalled == true)
                {
                    return new ActivePolishProviderBadgePresentation(
                        BadgeText: "Needs setup",
                        StatusText: "EG-1 model files are present, but the local EG-1 runtime was not found.",
                        ChipStyleKey: "BrandNeutralChipStyle",
                        ChipTextStyleKey: "BrandNeutralChipTextStyle");
                }

                return new ActivePolishProviderBadgePresentation(
                    BadgeText: "Not installed",
                    StatusText: selectedEgOneState?.HasBundledManifest == true
                        ? "Download an EG-1 model size below to run offline dictation polish on this PC."
                        : "EG-1 is not installed on this PC. Choose another AI Polish provider below.",
                    ChipStyleKey: "BrandNeutralChipStyle",
                    ChipTextStyleKey: "BrandNeutralChipTextStyle");

            case PolishProvider.Ollama:
                return new ActivePolishProviderBadgePresentation(
                    BadgeText: isOllamaHosted ? "Runs on Ollama's servers" : "On this PC",
                    StatusText: "Local models on this PC or hosted models on Ollama's servers.",
                    ChipStyleKey: "BrandAccentChipStyle",
                    ChipTextStyleKey: "BrandAccentChipTextStyle");

            case PolishProvider.OpenAI:
                return new ActivePolishProviderBadgePresentation(
                    BadgeText: "Cloud",
                    StatusText: "OpenAI GPT models (requires your API key).",
                    ChipStyleKey: "BrandAccentChipStyle",
                    ChipTextStyleKey: "BrandAccentChipTextStyle");

            case PolishProvider.Anthropic:
                return new ActivePolishProviderBadgePresentation(
                    BadgeText: "Cloud",
                    StatusText: "Anthropic Claude models (requires your API key).",
                    ChipStyleKey: "BrandAccentChipStyle",
                    ChipTextStyleKey: "BrandAccentChipTextStyle");

            case PolishProvider.Gemini:
                return new ActivePolishProviderBadgePresentation(
                    BadgeText: "Cloud",
                    StatusText: "Google Gemini models (requires your API key).",
                    ChipStyleKey: "BrandAccentChipStyle",
                    ChipTextStyleKey: "BrandAccentChipTextStyle");

            default:
                return new ActivePolishProviderBadgePresentation(
                    BadgeText: "Off",
                    StatusText: "AI Polish is disabled.",
                    ChipStyleKey: "BrandNeutralChipStyle",
                    ChipTextStyleKey: "BrandNeutralChipTextStyle");
        }
    }

    public static EgOneCardPresentation DescribeEgOneCard(
        string modelId,
        string selectedModelId,
        string? recommendedModelId,
        EgOneModelState state,
        bool otherDownloadInFlight,
        bool serverExecutableExists,
        ulong? totalPhysicalMemoryBytes = null,
        ulong? systemUsedMemoryBytes = null,
        EgOneServerActivity serverActivity = EgOneServerActivity.Inactive,
        bool? isLightestOverride = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var normalizedId = NormalizeEgOneModelId(modelId);
        var facts = DescribeEgOneTier(normalizedId);
        var diskSizeText = string.IsNullOrWhiteSpace(state.BundleSizeText)
            ? facts.DefaultDiskSizeText
            : state.BundleSizeText;
        var isSelected = string.Equals(normalizedId, NormalizeEgOneModelId(selectedModelId), StringComparison.Ordinal);
        var isRecommended = string.Equals(normalizedId, recommendedModelId, StringComparison.Ordinal);
        var isDownloading = state.DeliveryStage == SpeechModelDelivery.Downloading;
        var isActivating = state.DeliveryStage == SpeechModelDelivery.Activating;
        var isDownloadAvailable = state.HasBundledManifest && state.DeliveryStage != SpeechModelDelivery.CannotDownload;
        var isUnavailable = !state.IsInstalled && !isDownloadAvailable && !isDownloading && !isActivating;

        var statusText = state.DeliveryStage switch
        {
            SpeechModelDelivery.Downloading => "Downloading...",
            SpeechModelDelivery.Activating => "Activating...",
            SpeechModelDelivery.Failed => "Download failed",
            SpeechModelDelivery.Cancelled => "Download cancelled",
            _ when state.IsInstalled => isSelected && serverExecutableExists ? "Installed · Ready" : "Installed",
            _ when isUnavailable => "Not available yet",
            _ => "Not installed",
        };

        var statusDetail = state.DeliveryStage switch
        {
            SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating or SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled
                when !string.IsNullOrWhiteSpace(state.StatusDetail) => state.StatusDetail!,
            _ when isUnavailable => DescribeEgOneUnavailableNotice(normalizedId),
            _ when state.IsInstalled => serverExecutableExists
                ? "Verified on this PC for offline dictation polish."
                : "Local model files found on this PC. EG-1 runtime executable was not found.",
            _ => $"Not downloaded yet ({diskSizeText}).",
        };

        var showDownloadButton = !state.IsInstalled &&
            !isDownloading &&
            !isActivating &&
            isDownloadAvailable;

        var downloadButtonText = state.DeliveryStage is SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled
            ? "Retry download"
            : state.HasBundledManifest && !string.IsNullOrWhiteSpace(state.BundleSizeText)
                ? $"Download ({state.BundleSizeText})"
                : "Download";

        var canSelect = state.IsInstalled || isDownloadAvailable || isDownloading || isActivating || isSelected;
        var showChooseProviderButton = isSelected && isUnavailable;

        var specsSummary = $"{facts.QualityText} · {facts.SpeedText} · {facts.MemoryText} · {diskSizeText}";
        var isLightest = isLightestOverride ?? string.Equals(normalizedId, EgOneMicroModelId, StringComparison.Ordinal);
        var memory = EgOneMemoryBarPresentation.Describe(
            normalizedId,
            facts.Title,
            totalPhysicalMemoryBytes,
            systemUsedMemoryBytes,
            serverActivity,
            isLightest);
        var automationName = isRecommended
            ? $"{facts.Title}. Best for this PC. {facts.Tagline}. {facts.Description} {memory.AccessibilityValue}"
            : $"{facts.Title}. {facts.Tagline}. {facts.Description} {memory.AccessibilityValue}";

        return new EgOneCardPresentation(
            ModelId: normalizedId,
            Title: facts.Title,
            Tagline: facts.Tagline,
            Description: facts.Description,
            QualityText: facts.QualityText,
            SpeedText: facts.SpeedText,
            MemoryText: facts.MemoryText,
            DiskSizeText: diskSizeText,
            SpecsSummary: specsSummary,
            IsRecommended: isRecommended,
            IsSelected: isSelected,
            IsInstalled: state.IsInstalled,
            StatusText: statusText,
            StatusDetail: statusDetail,
            ShowDownloadButton: showDownloadButton,
            DownloadButtonText: downloadButtonText,
            DownloadButtonEnabled: !otherDownloadInFlight,
            ShowProgress: isDownloading && state.DownloadPercent is not null,
            DownloadPercent: state.DownloadPercent ?? 0,
            ShowCancelButton: isDownloading,
            ShowRemoveButton: state.IsInstalled && state.CanRemove && !isDownloading && !isActivating,
            RemoveButtonEnabled: !otherDownloadInFlight,
            AutomationName: automationName,
            CanSelect: canSelect,
            ShowChooseProviderButton: showChooseProviderButton,
            Memory: memory);
    }

    public static EgOneRemovalConfirmation DescribeEgOneRemovalDialog(
        string? modelId,
        bool isCurrentlySelected,
        bool egOneActiveProvider)
    {
        var tier = DescribeEgOneTier(modelId);
        var message = isCurrentlySelected && egOneActiveProvider
            ? $"{tier.Title} is currently selected for AI Polish. Removing it deletes the local model files from this PC and stops offline EG-1 polish until you download a size again or switch providers."
            : $"{tier.Title} is currently selected as your EG-1 size. Removing it deletes its local model files from this PC until you download it again.";

        return new EgOneRemovalConfirmation(
            Title: $"Remove {tier.Title}?",
            Message: message,
            ConfirmButtonText: "Remove model",
            CancelButtonText: "Keep model");
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
