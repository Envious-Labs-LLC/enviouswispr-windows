using EnviousWispr.Core.Input;

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

/// <summary>Which spelling English dictation is delivered in. Ref: macOS #3124.</summary>
/// <remarks>
/// A PREFERENCE OF ITS OWN, NOT A LANGUAGE. The engines only ever receive "en" - Parakeet has no language input on
/// Windows and whisper.cpp's language token carries no region - so British is applied to the words after
/// recognition, wherever the take is English: Parakeet's always, and Whisper's when it is locked to or detects
/// English. macOS folds the same choice into its language picker as "English (UK)"; Windows has no language picker
/// for its default engine, so here it is a setting beside the other text features.
/// </remarks>
public enum EnglishSpelling
{
    American,
    British,
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
    string QuickAddGesture = "Ctrl+Alt+W",
    bool AutoStopEnabled = false,
    double AutoStopSilenceSeconds = 2.0,
    EnglishSpelling EnglishSpelling = EnglishSpelling.American,
    string PasteLastGesture = DictationPreferences.DefaultPasteLastGesture,
    string CopyLastGesture = "")
{
    /// <summary>Paste Last Dictation's default: Wispr Flow's Windows binding for the same action. Ref: #206.</summary>
    /// <remarks>
    /// Measured before it was chosen (issue #206): consuming the Z of Alt+Shift+Z let Windows' Alt+Shift layout toggle
    /// fire once in eight runs, and never with the masking key the hook sends; Ctrl+Alt+V is Paste Special in Office
    /// and AltGr+V is "@" in Hungarian. Copy Last has no default: the tray carries it until someone binds one.
    /// </remarks>
    public const string DefaultPasteLastGesture = "Alt+Shift+Z";

    /// <summary>
    /// The last-dictation shortcuts with any that would clash with a required key left unset.
    /// </summary>
    /// <remarks>
    /// FOR A FILE WRITTEN BEFORE THESE SHORTCUTS EXISTED. It gains the Alt+Shift+Z default on load, and somebody who
    /// had already given Alt+Shift+Z to recording, cancel or Add-a-word would then hold a file the validator refuses
    /// - which resets every setting they have. Their own binding wins; Paste Last starts unset. Ref: #206.
    /// </remarks>
    public DictationPreferences WithoutClashingLastDictationShortcuts()
    {
        var required = new[] { PushToTalkGesture, CancelGesture, QuickAddGesture }
            .Select(value => HotkeyGestureParser.Parse(value).Gesture)
            .ToArray();
        string Settled(string value) =>
            HotkeyGestureParser.ParseOptional(value).Gesture is { } gesture && required.Contains(gesture)
                ? string.Empty
                : value;

        var paste = Settled(PasteLastGesture);
        var copy = Settled(CopyLastGesture);
        if (!string.IsNullOrEmpty(copy) &&
            HotkeyGestureParser.ParseOptional(copy).Gesture == HotkeyGestureParser.ParseOptional(paste).Gesture)
        {
            copy = string.Empty;
        }

        return this with { PasteLastGesture = paste, CopyLastGesture = copy };
    }

    public static DictationPreferences Default { get; } = new(
        FinalAsrEngine.Automatic,
        "F8",
        WordCorrectionEnabled: true,
        FillerRemovalEnabled: true,
        EmojiFormatterEnabled: true,
        SpokenPunctuationEnabled: false,
        WhisperLanguage: WhisperLanguagePreference.Automatic,
        RecordingMode: DictationRecordingMode.PushToTalk,
        // OFF by default, and that is resolved from the founder's priority order rather than
        // chosen: priority one is that dictation works every time it physically can, and a switch
        // that can end a recording early must not be on for anyone who has not asked for it.
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
    RecordingSoundPairing RecordingSoundPairing = RecordingSoundPairing.WhisperTick,
    bool CopyInsteadOfPaste = false)
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
