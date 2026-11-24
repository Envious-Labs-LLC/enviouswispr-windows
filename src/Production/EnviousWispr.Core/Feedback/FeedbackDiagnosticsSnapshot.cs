using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EnviousWispr.Core.Feedback;

/// <summary>
/// Privacy-safe, content-free diagnostics snapshot attached to feedback when the user opts in.
/// Contains strictly operational metadata — never dictated text, audio, API keys, clipboard text,
/// file paths, custom words, or PII.
/// </summary>
public sealed partial record FeedbackDiagnosticsSnapshot
{
    public const int MaxRecentEventCodes = 20;
    private const int MaxFieldLength = 80;

    [JsonConstructor]
    public FeedbackDiagnosticsSnapshot(
        string appVersion,
        string osVersion,
        string finalAsrEngine,
        string polishProvider,
        bool livePreviewEnabled,
        string livePreviewEngine,
        string recordingMode,
        bool autoStopEnabled,
        string hardwareClass,
        IReadOnlyList<string>? recentEventCodes = null)
    {
        AppVersion = SanitizeMetadataField(appVersion, "unknown");
        OsVersion = SanitizeMetadataField(osVersion, "unknown");
        FinalAsrEngine = SanitizeMetadataField(finalAsrEngine, "unknown");
        PolishProvider = SanitizeMetadataField(polishProvider, "unknown");
        LivePreviewEnabled = livePreviewEnabled;
        LivePreviewEngine = SanitizeMetadataField(livePreviewEngine, "unknown");
        RecordingMode = SanitizeMetadataField(recordingMode, "unknown");
        AutoStopEnabled = autoStopEnabled;
        HardwareClass = SanitizeMetadataField(hardwareClass, "Unknown");
        RecentEventCodes = NormalizeEventCodes(recentEventCodes);
    }

    public string AppVersion { get; }

    public string OsVersion { get; }

    public string FinalAsrEngine { get; }

    public string PolishProvider { get; }

    public bool LivePreviewEnabled { get; }

    public string LivePreviewEngine { get; }

    public string RecordingMode { get; }

    public bool AutoStopEnabled { get; }

    public string HardwareClass { get; }

    public IReadOnlyList<string> RecentEventCodes { get; }

    /// <summary>
    /// Formats the snapshot as human-readable key-value lines for the dialog preview.
    /// </summary>
    public string FormatPreviewText()
    {
        var sb = new StringBuilder();
        sb.Append("App Version: ").AppendLine(AppVersion);
        sb.Append("OS Version: ").AppendLine(OsVersion);
        sb.Append("Final ASR Engine: ").AppendLine(FinalAsrEngine);
        sb.Append("Polish Provider: ").AppendLine(PolishProvider);
        sb.Append("Live Preview: ")
            .Append(LivePreviewEnabled ? "On" : "Off")
            .Append(" (")
            .Append(LivePreviewEngine)
            .AppendLine(")");
        sb.Append("Recording Mode: ").AppendLine(RecordingMode);
        sb.Append("Auto-Stop: ").AppendLine(AutoStopEnabled ? "On" : "Off");
        sb.Append("Hardware Class: ").AppendLine(HardwareClass);
        sb.Append("Recent Event Codes: ")
            .Append(RecentEventCodes.Count == 0 ? "None" : string.Join(", ", RecentEventCodes));
        return sb.ToString();
    }

    private static string SanitizeMetadataField(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (ContainsDisallowedContent(trimmed))
        {
            return "[REDACTED]";
        }

        return trimmed.Length <= MaxFieldLength
            ? trimmed
            : trimmed[..MaxFieldLength];
    }

    private static string[] NormalizeEventCodes(IReadOnlyList<string>? codes)
    {
        if (codes is null || codes.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(Math.Min(codes.Count, MaxRecentEventCodes));
        foreach (var raw in codes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (!EventCodeIdentifierRegex().IsMatch(trimmed))
            {
                continue;
            }

            normalized.Add(trimmed);
            if (normalized.Count >= MaxRecentEventCodes)
            {
                break;
            }
        }

        return [.. normalized];
    }

    private static bool ContainsDisallowedContent(string value) =>
        WindowsPathRegex().IsMatch(value) ||
        PosixPathRegex().IsMatch(value) ||
        EmailRegex().IsMatch(value) ||
        ApiKeyRegex().IsMatch(value) ||
        value.IndexOfAny(['\r', '\n']) >= 0;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventCodeIdentifierRegex();

    [GeneratedRegex(@"[A-Za-z]:(?:\\\\|[\\/])", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?:/Users/|/h[o]me/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PosixPathRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)\b(?:sk-[A-Za-z0-9_\-]{17,}|phc_[A-Za-z0-9_\-]{16,}|sntrys_[A-Za-z0-9_\-]{13,}|key_[A-Za-z0-9_\-]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyRegex();
}
