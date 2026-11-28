using System.Text.RegularExpressions;
using EnviousWispr.Core.Settings;

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
            ["pt"] = new(["um"], StringComparer.OrdinalIgnoreCase),
            ["sv"] = new(["er"], StringComparer.OrdinalIgnoreCase),
            ["sl"] = new(["um"], StringComparer.OrdinalIgnoreCase),
            ["hr"] = new(["um"], StringComparer.OrdinalIgnoreCase),
        };

    private static readonly HashSet<string> AllLanguageProtectedTokens =
        ProtectedByLanguage.Values.Aggregate(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            static (union, tokens) =>
            {
                union.UnionWith(tokens);
                return union;
            });

    public static string Remove(string text, string? detectedLanguage) =>
        Remove(text, detectedLanguage, englishVetoed: false);

    public static string Remove(string text, string? detectedLanguage, bool englishVetoed)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var protectedWords = englishVetoed
            ? AllLanguageProtectedTokens
            : NormalizeLanguage(detectedLanguage) is { } baseCode &&
              ProtectedByLanguage.TryGetValue(baseCode, out var tokens)
                ? tokens
                : null;

        var result = FillerRegex().Replace(text, match =>
        {
            var word = match.Groups["filler"].Value;
            return protectedWords is not null && protectedWords.Contains(word)
                ? match.Value
                : string.Empty;
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

    private static string? NormalizeLanguage(string? language)
    {
        var baseCode = SpokenPunctuationRules.BaseCode(language);
        if (baseCode is null)
        {
            return null;
        }

        return baseCode is "nb" or "nn" ? "no" : baseCode;
    }

    // Two alternatives (#2728): mm/ah are refused after digits (bare, space, or hyphen) so "10 mm",
    // "10mm", "5-mm", and "200 ah" stay intact while "give me 3 um copies" still strips "um".
    [GeneratedRegex(
        @"(?:^|\s*)(?<filler>\b(?:um|umm|uh|uhh|hmm|mhm|mmm|er)\b|(?<![0-9])(?<![0-9] )(?<![0-9]-)\b(?:mm|ah)\b)[-.,!?\u2026:;\u2014]*(?=\s|$)",
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
