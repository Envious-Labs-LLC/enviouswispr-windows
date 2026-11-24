using System.Text.RegularExpressions;

namespace EnviousWispr.Services.Diagnostics;

/// <summary>
/// Dual-stage privacy sanitizer shared by PostHog and Sentry telemetry on Windows,
/// mirroring macOS <c>SentryEventSanitizer</c> while extending user-path scrubbing to Windows profile paths.
/// </summary>
public static partial class ObservabilitySanitizer
{
    public const string RedactedPlaceholder = "[REDACTED]";

    public static IReadOnlySet<string> ContentFreeKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "trace_id",
        "span_id",
        "parent_span_id",
        "kernel_version",
        "device_app_hash",
        "revision",
    };

    private static readonly string[] ApiKeyPrefixes =
    [
        "sk-",
        "phc_",
        "sntrys_",
        "key_",
    ];

    /// <summary>
    /// Scrubs the username segment from Windows (<c>C:\Users\&lt;name&gt;\...</c>) and
    /// macOS (<c>/Users/&lt;name&gt;/...</c>) paths while leaving the remaining path structure intact.
    /// Pure, idempotent, and never throws.
    /// </summary>
    public static string RedactUserPath(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var scrubbedWindows = WindowsUserPathRegex().Replace(input, "${prefix}[REDACTED]");
        return PosixUserPathRegex().Replace(scrubbedWindows, "/Users/[REDACTED]");
    }

    /// <summary>
    /// Redacts a string if either the raw input or its username-scrubbed form matches any PII/secret pattern:
    /// - Strings &gt; 100 chars (unless <c>http://</c> or <c>https://</c>)
    /// - API key prefixes (<c>sk-</c>, <c>phc_</c>, <c>sntrys_</c>, <c>key_</c> length &gt;= 20)
    /// - &gt;= 32 contiguous hex characters
    /// - Email addresses
    /// Otherwise returns the username-scrubbed string.
    /// </summary>
    public static string RedactString(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var scrubbed = RedactUserPath(raw);
        if (MatchesDenylist(raw) || MatchesDenylist(scrubbed))
        {
            return RedactedPlaceholder;
        }

        return scrubbed;
    }

    public static string? RedactNullableString(string? raw) =>
        raw is null ? null : RedactString(raw);

    public static Dictionary<string, string> RedactStringDictionary(
        IReadOnlyDictionary<string, string>? input)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input is null)
        {
            return output;
        }

        foreach (var (key, value) in input)
        {
            output[key] = ContentFreeKeys.Contains(key)
                ? value
                : RedactString(value);
        }

        return output;
    }

    public static Dictionary<string, object?> RedactDictionary(
        IReadOnlyDictionary<string, object?>? input)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (input is null)
        {
            return output;
        }

        foreach (var (key, value) in input)
        {
            if (ContentFreeKeys.Contains(key) && value is string contentFreeValue)
            {
                output[key] = contentFreeValue;
            }
            else
            {
                output[key] = RedactValue(value);
            }
        }

        return output;
    }

    public static object? RedactValue(object? value) => value switch
    {
        null => null,
        string str => RedactString(str),
        IReadOnlyDictionary<string, object?> readOnlyDict => RedactDictionary(readOnlyDict),
        IDictionary<string, object?> dict => RedactDictionary(
            dict.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
        IReadOnlyDictionary<string, string> strDict => RedactStringDictionary(strDict),
        IDictionary<string, string> mutableStrDict => RedactStringDictionary(
            mutableStrDict.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
        IEnumerable<string> stringSeq => stringSeq.Select(RedactString).ToArray(),
        IEnumerable<object?> objSeq => objSeq.Select(RedactValue).ToArray(),
        _ => value,
    };

    /// <summary>
    /// Validates that a PostHog anonymous ID is a canonical hyphenated UUID (lowercase or uppercase),
    /// rejecting empty strings, compact 32-hex UUIDs, or caller-supplied identifiers.
    /// </summary>
    public static string? CanonicalAnonymousPostHogId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Guid.TryParseExact(raw, "D", out var parsed))
        {
            return null;
        }

        var lower = parsed.ToString("D");
        var upper = lower.ToUpperInvariant();
        return string.Equals(raw, lower, StringComparison.Ordinal) ||
               string.Equals(raw, upper, StringComparison.Ordinal)
            ? raw
            : null;
    }

    private static bool MatchesDenylist(string input)
    {
        if (input.Length > 100 &&
            !input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (input.Length >= 20)
        {
            foreach (var prefix in ApiKeyPrefixes)
            {
                if (input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (EmbeddedApiKeyRegex().IsMatch(input))
        {
            return true;
        }

        if (ContiguousHexRegex().IsMatch(input))
        {
            return true;
        }

        if (EmailRegex().IsMatch(input))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?<prefix>[A-Za-z]:(?:\\\\|[\\/])+Users(?:\\\\|[\\/])+)[^\\/\r\n""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex(@"/Users/[^/\r\n""']+", RegexOptions.CultureInvariant)]
    private static partial Regex PosixUserPathRegex();

    [GeneratedRegex(@"(?i)\b(?:sk-[A-Za-z0-9_\-]{17,}|phc_[A-Za-z0-9_\-]{16,}|sntrys_[A-Za-z0-9_\-]{13,}|key_[A-Za-z0-9_\-]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedApiKeyRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{32,}", RegexOptions.CultureInvariant)]
    private static partial Regex ContiguousHexRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
