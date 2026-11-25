using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Presentation;

/// <summary>One capture device entry shown in the tray's Microphone submenu.</summary>
/// <param name="Id">The Windows audio device identifier, or <see langword="null"/> for System Default.</param>
/// <param name="DisplayName">The human-readable label shown in the submenu.</param>
public sealed record TrayMicrophoneItem(string? Id, string DisplayName);

/// <summary>Effective visual mode for the native tray popup menu.</summary>
public enum TrayMenuThemeMode
{
    Dark,
    Light,
    HighContrast,
}

/// <summary>Resolves the effective tray menu theme from app preference and Windows system state.</summary>
public static class TrayMenuThemeResolver
{
    /// <summary>
    /// Chooses Dark, Light, or HighContrast for the tray menu.
    /// High Contrast always wins; explicit Light/Dark override Windows; System follows Windows.
    /// </summary>
    public static TrayMenuThemeMode Resolve(
        AppTheme appTheme,
        bool highContrast,
        bool? systemUsesLightTheme,
        bool? appsUseLightTheme)
    {
        if (highContrast)
        {
            return TrayMenuThemeMode.HighContrast;
        }

        return appTheme switch
        {
            AppTheme.Light => TrayMenuThemeMode.Light,
            AppTheme.Dark => TrayMenuThemeMode.Dark,
            _ => (appsUseLightTheme ?? systemUsesLightTheme ?? false)
                ? TrayMenuThemeMode.Light
                : TrayMenuThemeMode.Dark,
        };
    }
}

/// <summary>Pure formatting helpers shared between the tray menu and its unit tests.</summary>
public static class TrayMenuFormatting
{
    /// <summary>Formats the disabled top status line (e.g. <c>Parakeet · AI Polish Off</c> or <c>Parakeet · EG-1</c>).</summary>
    public static string FormatStatusHeader(FinalAsrEngine engine, PolishProvider polish)
    {
        var engineLabel = engine switch
        {
            FinalAsrEngine.Whisper => "Whisper",
            _ => "Parakeet",
        };

        var polishLabel = polish switch
        {
            PolishProvider.EgOne => "EG-1",
            PolishProvider.Ollama => "Ollama",
            PolishProvider.OpenAI => "OpenAI",
            PolishProvider.Anthropic => "Anthropic",
            PolishProvider.Gemini => "Gemini",
            _ => "AI Polish Off",
        };

        return $"{engineLabel} · {polishLabel}";
    }

    /// <summary>Formats the disabled version line (e.g. <c>Version 1.0.0</c>).</summary>
    public static string FormatVersionLine(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? "Version"
            : $"Version {version.Trim()}";

    /// <summary>Returns the recording toggle label for the current tray state.</summary>
    public static string FormatRecordingItemText(TrayIconState state) =>
        state == TrayIconState.Recording ? "Stop Recording" : "Start Recording";

    /// <summary>Escapes Windows Forms mnemonic ampersands so labels render literally.</summary>
    public static string EscapeMnemonic(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("&", "&&", StringComparison.Ordinal);
    }
}
