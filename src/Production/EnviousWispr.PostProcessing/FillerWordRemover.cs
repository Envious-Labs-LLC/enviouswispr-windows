using System.Text.RegularExpressions;

namespace EnviousWispr.PostProcessing;

public static partial class FillerWordRemover
{
    private static readonly Dictionary<string, HashSet<string>> ProtectedByLanguage =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = new(["er", "um"], StringComparer.OrdinalIgnoreCase),
            ["nl"] = new(["er"], StringComparer.OrdinalIgnoreCase),
            ["da"] = new(["er"], StringComparer.OrdinalIgnoreCase),
            ["no"] = new(["er"], StringComparer.OrdinalIgnoreCase),
        };

    public static string Remove(string text, string? detectedLanguage)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var language = NormalizeLanguage(detectedLanguage);
        ProtectedByLanguage.TryGetValue(language, out var protectedWords);
        var result = FillerRegex().Replace(text, match =>
        {
            var word = match.Groups["filler"].Value;
            return protectedWords?.Contains(word) == true ? match.Value : string.Empty;
        });
        result = RepeatedWordRegex().Replace(result, match => match.Groups["word"].Value);
        result = FragmentRestartRegex().Replace(result, match =>
        {
            var fragment = match.Groups["fragment"].Value;
            var completed = match.Groups["completed"].Value;
            return completed.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                ? completed
                : match.Value;
        });
        return RepeatedWhitespaceRegex().Replace(result, " ").Trim();
    }

    private static string NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim().ToLowerInvariant() ?? string.Empty;
        var separator = normalized.IndexOfAny(['-', '_']);
        if (separator >= 0)
        {
            normalized = normalized[..separator];
        }

        return normalized is "nb" or "nn" ? "no" : normalized;
    }

    [GeneratedRegex(
        @"(?:^|\s*)\b(?<filler>um|umm|uh|uhh|hmm|mm|mhm|mmm|ah|er)\b[-.,!?\u2026:;\u2014]*(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FillerRegex();

    [GeneratedRegex(
        @"\b(?<word>[\p{L}\p{N}]+)(?:\s+\k<word>)+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedWordRegex();

    [GeneratedRegex(
        @"\b(?<fragment>[\p{L}]{2,})-\s+(?<completed>[\p{L}]{3,})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex FragmentRestartRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedWhitespaceRegex();
}
