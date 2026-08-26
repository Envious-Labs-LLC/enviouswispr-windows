namespace EnviousWispr.Core.Settings;

public enum FinalAsrEngine
{
    Automatic,
    Parakeet,
    Whisper,
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

public sealed record DictationPreferences(
    FinalAsrEngine FinalEngine,
    string PushToTalkGesture,
    bool WordCorrectionEnabled,
    bool FillerRemovalEnabled,
    bool EmojiFormatterEnabled,
    bool SpokenPunctuationEnabled)
{
    public static DictationPreferences Default { get; } = new(
        FinalAsrEngine.Automatic,
        "F8",
        WordCorrectionEnabled: true,
        FillerRemovalEnabled: true,
        EmojiFormatterEnabled: true,
        SpokenPunctuationEnabled: false);
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

public sealed record UserPreferences(
    DictationPreferences Dictation,
    PolishPreferences Polish,
    HistoryPreferences History,
    AppTheme Theme)
{
    public static UserPreferences Default { get; } = new(
        DictationPreferences.Default,
        PolishPreferences.Default,
        HistoryPreferences.Default,
        AppTheme.System);
}
