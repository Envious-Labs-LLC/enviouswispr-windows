using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Customer-facing summary details for the selected final transcription engine.</summary>
public sealed record EngineSummaryPresentation(
    string Name,
    string BadgeText,
    string Description,
    string IconGlyph,
    bool ShowWhisperLanguage);

/// <summary>Visibility and customer-facing copy for the Dictation output language card.</summary>
public sealed record DictationOutputLanguagePresentation(
    bool IsVisible,
    string StatusText);

/// <summary>Pure presentation helpers for the transcription engine, EG-1 sizing, and output language cards.</summary>
public static class EnginePolishPresentation
{
    public const string EgOneFullModelId = "eg-1";
    public const string EgOneMiniModelId = "eg-1-mini";
    public const string EgOneMicroModelId = "eg-1-micro";

    public static EngineSummaryPresentation DescribeEngine(FinalAsrEngine engine) => engine switch
    {
        FinalAsrEngine.Parakeet => new EngineSummaryPresentation(
            Name: "Parakeet",
            BadgeText: "Fast · English",
            Description: "Fast local English dictation that runs smoothly on most PCs.",
            IconGlyph: "\uE945",
            ShowWhisperLanguage: false),
        FinalAsrEngine.Whisper => new EngineSummaryPresentation(
            Name: "Whisper",
            BadgeText: "Multilingual",
            Description: "Supports English, Spanish, French, and German. Best with an NVIDIA graphics card; slower on processor-only PCs.",
            IconGlyph: "\uE774",
            ShowWhisperLanguage: true),
        _ => new EngineSummaryPresentation(
            Name: "Automatic",
            BadgeText: "Recommended",
            Description: "Automatically chooses the best local speech engine for this PC.",
            IconGlyph: "\uE8C1",
            ShowWhisperLanguage: true),
    };

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
