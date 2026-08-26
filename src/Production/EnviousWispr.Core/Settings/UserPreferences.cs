namespace EnviousWispr.Core.Settings;

public enum FinalAsrEngine
{
    Automatic,
    Parakeet,
    Whisper,
}

public enum DictationRecordingMode
{
    PushToTalk,
    Toggle,
}

public enum PolishProvider
{
    None,
    EgOne,
    Ollama,
    OpenAI,
    Anthropic,
    Gemini,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum OverlayPillPosition
{
    Top,
    Bottom,
}

public enum RecordingPillDesign
{
    Classic,
    ReadingWell,
    LevelRail,
}

public enum RecordingSoundPairing
{
    DustMote,
    VelvetHush,
    MutedConfirm,
    WhisperTick,
    RoundPebble,
    PaperTap,
    SoftHush,
    LowNod,
    CloudPop,
    VelvetTap,
    SatinShift,
    AirGlint,
}

public enum WhisperLanguagePreference
{
    Automatic,
    English,
    French,
    German,
    Spanish,
}

public static class WhisperLanguageCodes
{
    public static string For(WhisperLanguagePreference preference) => preference switch
    {
        WhisperLanguagePreference.Automatic => "auto",
        WhisperLanguagePreference.English => "en",
        WhisperLanguagePreference.French => "fr",
        WhisperLanguagePreference.German => "de",
        WhisperLanguagePreference.Spanish => "es",
        _ => throw new ArgumentOutOfRangeException(nameof(preference)),
    };

    public static bool TryNormalize(string? value, out string code)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        code = normalized switch
        {
            "auto" => "auto",
            "en" or "en-us" or "en-gb" => "en",
            "fr" or "fr-fr" => "fr",
            "de" or "de-de" => "de",
            "es" or "es-es" => "es",
            _ => string.Empty,
        };
        return code.Length > 0;
    }
}

public sealed record DictationPreferences(
    FinalAsrEngine FinalEngine,
    string PushToTalkGesture,
    bool WordCorrectionEnabled,
    bool FillerRemovalEnabled,
    bool EmojiFormatterEnabled,
    bool SpokenPunctuationEnabled,
    WhisperLanguagePreference WhisperLanguage = WhisperLanguagePreference.Automatic,
    DictationRecordingMode RecordingMode = DictationRecordingMode.PushToTalk,
    string CancelGesture = "Escape",
    bool EscapeRecoveryEnabled = false,
    string QuickAddGesture = "Ctrl+Alt+W")
{
    public static DictationPreferences Default { get; } = new(
        FinalAsrEngine.Automatic,
        "F8",
        WordCorrectionEnabled: true,
        FillerRemovalEnabled: true,
        EmojiFormatterEnabled: true,
        SpokenPunctuationEnabled: false,
        WhisperLanguage: WhisperLanguagePreference.Automatic,
        RecordingMode: DictationRecordingMode.PushToTalk,
        CancelGesture: "Escape",
        EscapeRecoveryEnabled: false,
        QuickAddGesture: "Ctrl+Alt+W");
}

public sealed record PolishPreferences(
    PolishProvider Provider,
    string? ModelId,
    string? OllamaEndpoint = null)
{
    public static PolishPreferences Default { get; } = new(
        PolishProvider.None,
        ModelId: null,
        OllamaEndpoint: null);
}

public sealed record HistoryPreferences(bool IsEnabled, int RetentionDays)
{
    public static HistoryPreferences Default { get; } = new(IsEnabled: true, RetentionDays: 30);
}

public sealed record ObservabilityPreferences(
    bool LocalDiagnosticsEnabled,
    int DiagnosticRetentionDays,
    bool ShareAnonymousTelemetry)
{
    public static ObservabilityPreferences Default { get; } = new(
        LocalDiagnosticsEnabled: true,
        DiagnosticRetentionDays: 14,
        ShareAnonymousTelemetry: false);
}

public sealed record UserPreferences(
    DictationPreferences Dictation,
    PolishPreferences Polish,
    HistoryPreferences History,
    AppTheme Theme,
    bool LivePreviewEnabled = false,
    OverlayPillPosition OverlayPosition = OverlayPillPosition.Top,
    RecordingPillDesign PillDesignWithoutWords = RecordingPillDesign.Classic,
    RecordingPillDesign PillDesignWithWords = RecordingPillDesign.ReadingWell,
    bool PlayRecordingSounds = false,
    RecordingSoundPairing RecordingSoundPairing = RecordingSoundPairing.WhisperTick)
{
    public static UserPreferences Default { get; } = new(
        DictationPreferences.Default,
        PolishPreferences.Default,
        HistoryPreferences.Default,
        AppTheme.System,
        LivePreviewEnabled: false,
        OverlayPosition: OverlayPillPosition.Top,
        PillDesignWithoutWords: RecordingPillDesign.Classic,
        PillDesignWithWords: RecordingPillDesign.ReadingWell,
        PlayRecordingSounds: false,
        RecordingSoundPairing: RecordingSoundPairing.WhisperTick);
}
