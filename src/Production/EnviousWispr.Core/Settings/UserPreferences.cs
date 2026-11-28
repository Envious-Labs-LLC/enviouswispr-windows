using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Core.Settings;

[JsonConverter(typeof(FinalAsrEngineJsonConverter))]
public enum FinalAsrEngine
{
    Parakeet = 0,
    Whisper = 1,
}

/// <summary>Parses and serializes final ASR engine choices, normalizing legacy "Automatic" to Parakeet.</summary>
public static class FinalAsrEngineChoices
{
    public const string ParakeetPersistedValue = "Parakeet";
    public const string WhisperPersistedValue = "Whisper";

    public static string ToPersistedValue(FinalAsrEngine engine) => engine switch
    {
        FinalAsrEngine.Whisper => WhisperPersistedValue,
        _ => ParakeetPersistedValue,
    };

    public static bool TryParse(string? value, out FinalAsrEngine engine)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            engine = default;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "parakeet":
            case "automatic":
                engine = FinalAsrEngine.Parakeet;
                return true;
            case "whisper":
                engine = FinalAsrEngine.Whisper;
                return true;
            default:
                engine = default;
                return false;
        }
    }
}

public sealed class FinalAsrEngineJsonConverter : JsonConverter<FinalAsrEngine>
{
    public override FinalAsrEngine Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (FinalAsrEngineChoices.TryParse(value, out var parsed))
            {
                return parsed;
            }

            throw new JsonException($"Invalid FinalAsrEngine value '{value}'.");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            // Legacy schema numbered Automatic = 0, Parakeet = 1, Whisper = 2.
            return number switch
            {
                0 or 1 => FinalAsrEngine.Parakeet,
                2 => FinalAsrEngine.Whisper,
                _ => (FinalAsrEngine)number,
            };
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when reading FinalAsrEngine.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        FinalAsrEngine value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(FinalAsrEngineChoices.ToPersistedValue(value));
    }
}

/// <summary>Which local engine produces the display-only live preview while speaking (#127).</summary>
[JsonConverter(typeof(LivePreviewEngineChoiceJsonConverter))]
public enum LivePreviewEngineChoice
{
    Parakeet = 0,
    Whisper = 1,
}

public static class LivePreviewEngineChoices
{
    public const string ParakeetPersistedValue = "Parakeet";
    public const string WhisperPersistedValue = "Whisper";

    public static string ToPersistedValue(LivePreviewEngineChoice choice) => choice switch
    {
        LivePreviewEngineChoice.Whisper => WhisperPersistedValue,
        _ => ParakeetPersistedValue,
    };

    public static LivePreviewEngineChoice ParseOrDefault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LivePreviewEngineChoice.Parakeet;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "whisper" or "universal" or "multilingual" => LivePreviewEngineChoice.Whisper,
            _ => LivePreviewEngineChoice.Parakeet,
        };
    }
}

public sealed class LivePreviewEngineChoiceJsonConverter : JsonConverter<LivePreviewEngineChoice>
{
    public override LivePreviewEngineChoice Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => LivePreviewEngineChoices.ParseOrDefault(reader.GetString()),
            JsonTokenType.Number when reader.TryGetInt32(out var number) && number == (int)LivePreviewEngineChoice.Whisper =>
                LivePreviewEngineChoice.Whisper,
            _ => LivePreviewEngineChoice.Parakeet,
        };

    public override void Write(
        Utf8JsonWriter writer,
        LivePreviewEngineChoice value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(LivePreviewEngineChoices.ToPersistedValue(value));
    }
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

/// <summary>How long the local EG-1 polish model sits idle before releasing memory (ref: macOS #3853).</summary>
public enum PolishUnloadPolicy
{
    Never = 0,
    Immediately = 1,
    TwoMinutes = 2,
    FiveMinutes = 3,
    TenMinutes = 4,
    FifteenMinutes = 5,
    SixtyMinutes = 6,
}

/// <summary>How long the microphone capture engine stays warm after dictation ends (ref: macOS WarmEnginePolicy).</summary>
public enum MicrophoneWarmPolicy
{
    Off = 0,
    Seconds10 = 1,
    Seconds30 = 2,
    Seconds60 = 3,
    Always = 4,
}

/// <summary>Which window receives dictated text when focus moves between pressing the hotkey and releasing it.</summary>
public enum PasteTargetMode
{
    WhereDictationStarted = 0,
    WhereCursorIsAtRelease = 1,
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

public static class WhisperLanguageCodes
{
    public static string For(string? language) => LanguageCatalog.NormalizeOrDefault(language);

    /// <summary>The language a take is recognised in: a valid launch override pins it; otherwise the setting as it stands.</summary>
    /// <remarks>The override (ENVIOUSWISPR_ASR_LANGUAGE) is for tooling and journeys; an invalid one is ignored, as before.</remarks>
    public static string Current(string? setting, string? environmentOverride) =>
        TryNormalize(environmentOverride, out var overrideCode)
            ? overrideCode
            : LanguageCatalog.NormalizeOrDefault(setting);

    public static bool TryNormalize(string? value, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase))
        {
            code = LanguageCatalog.AutomaticCode;
            return true;
        }

        if (LanguageCatalog.TryGetEntry(trimmed, out var entry))
        {
            code = entry.LockCode;
            return true;
        }

        return false;
    }
}

public sealed record DictationPreferences(
    FinalAsrEngine FinalEngine,
    string PushToTalkGesture,
    bool WordCorrectionEnabled,
    bool FillerRemovalEnabled,
    bool EmojiFormatterEnabled,
    bool SpokenPunctuationEnabled,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(WhisperLanguageJsonConverter))]
    string WhisperLanguage = LanguageCatalog.AutomaticCode,
    DictationRecordingMode RecordingMode = DictationRecordingMode.PushToTalk,
    string CancelGesture = "Escape",
    bool EscapeRecoveryEnabled = false,
    string QuickAddGesture = "Ctrl+Alt+W",
    bool AutoStopEnabled = false,
    double AutoStopSilenceSeconds = 2.0,
    EnglishSpelling EnglishSpelling = EnglishSpelling.American,
    string PasteLastGesture = DictationPreferences.DefaultPasteLastGesture,
    string CopyLastGesture = "",
    bool CasualModeEnabled = false,
    bool LearnFromEditsEnabled = true,
    bool FixLearnedWordsEnabled = true,
    MicrophoneWarmPolicy WarmMicrophonePolicy = MicrophoneWarmPolicy.Seconds30,
    bool SilentMicWarningsEnabled = true)
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

    /// <remarks>
    /// CTRL+WIN IS THE RECORDING KEY A FRESH INSTALL STARTS ON: founder decision 2026-09-05 (#66), matching what people
    /// switching from other dictation apps already have in their fingers, and it unlocks the four gestures (hold, double
    /// tap, tap, triple tap) that an ordinary key cannot. Only a fresh install gets it: a saved profile keeps the key it
    /// has, so nobody's F8 changes under them. Verified through the real hook before it became the default (#226, #227).
    /// </remarks>
    public static DictationPreferences Default { get; } = new(
        FinalAsrEngine.Parakeet,
        "Ctrl+Win",
        WordCorrectionEnabled: true,
        FillerRemovalEnabled: true,
        EmojiFormatterEnabled: true,
        SpokenPunctuationEnabled: false,
        WhisperLanguage: LanguageCatalog.AutomaticCode,
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
    string? OllamaEndpoint = null,
    string? EgOneModelId = null,
    PolishUnloadPolicy UnloadPolicy = PolishUnloadPolicy.Never)
{
    public static PolishPreferences Default { get; } = new(
        PolishProvider.None,
        ModelId: null,
        OllamaEndpoint: null,
        EgOneModelId: null,
        UnloadPolicy: PolishUnloadPolicy.Never);
}

public sealed record HistoryPreferences(bool IsEnabled, int RetentionDays)
{
    public static HistoryPreferences Default { get; } = new(IsEnabled: true, RetentionDays: 30);
}

public sealed record ObservabilityPreferences(
    bool LocalDiagnosticsEnabled,
    int DiagnosticRetentionDays,
    bool ShareAnonymousTelemetry,
    bool SendCrashReports = true)
{
    public static ObservabilityPreferences Default { get; } = new(
        LocalDiagnosticsEnabled: true,
        DiagnosticRetentionDays: 14,
        ShareAnonymousTelemetry: true,
        SendCrashReports: true);

    public bool ShareUsageMetrics => ShareAnonymousTelemetry;
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
    bool PlayRecordingSounds = true,
    RecordingSoundPairing RecordingSoundPairing = RecordingSoundPairing.WhisperTick,
    bool CopyInsteadOfPaste = false,
    [property: JsonConverter(typeof(LivePreviewEngineChoiceJsonConverter))]
    LivePreviewEngineChoice LivePreviewEngine = LivePreviewEngineChoice.Parakeet,
    PasteTargetMode PasteTargetMode = PasteTargetMode.WhereDictationStarted,
    bool SmartInsertionEnabled = true)
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
        PlayRecordingSounds: true,
        RecordingSoundPairing: RecordingSoundPairing.WhisperTick,
        CopyInsteadOfPaste: false,
        LivePreviewEngine: LivePreviewEngineChoice.Parakeet,
        PasteTargetMode: PasteTargetMode.WhereDictationStarted,
        SmartInsertionEnabled: true);
}
