using System.Text.RegularExpressions;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

public sealed record WordCorrectionResult(string Text, int ReplacementCount);

public static partial class CustomWordCorrector
{
    public const double SimilarityThreshold = 0.82;
    public const double AmbiguityMargin = 0.05;

    private static readonly HashSet<string> ReservedTriggerWords =
        new(["emoji", "emoticon"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FuzzyStopWords = new(
        [
            "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from",
            "he", "her", "his", "i", "in", "is", "it", "me", "my", "of", "on", "or",
            "our", "she", "so", "that", "the", "their", "them", "they", "this", "to",
            "up", "was", "we", "were", "with", "you", "your",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static WordCorrectionResult Correct(
        string text,
        IReadOnlyList<CustomWordEntry> customWords)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(customWords);
        if (text.Length == 0 || customWords.Count == 0)
        {
            return new WordCorrectionResult(text, 0);
        }

        var candidates = customWords
            .Where(IsUsable)
            .SelectMany(entry => Surfaces(entry).Select(surface => new Candidate(
                CollapseSpaces(surface),
                entry.Replacement)))
            .Where(candidate => !ContainsReservedTrigger(candidate.Surface))
            .DistinctBy(
                candidate => candidate.Surface,
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => WordCount(candidate.Surface))
            .ThenByDescending(candidate => candidate.Surface.Length)
            .ToArray();
        if (candidates.Length == 0)
        {
            return new WordCorrectionResult(text, 0);
        }

        var result = text;
        var replacementCount = 0;
        foreach (var candidate in candidates)
        {
            var pattern = SurfacePattern(candidate.Surface);
            result = Regex.Replace(
                result,
                pattern,
                match =>
                {
                    if (match.Value.Equals(candidate.Replacement, StringComparison.Ordinal))
                    {
                        return match.Value;
                    }

                    replacementCount++;
                    return candidate.Replacement;
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }

        var fuzzyCandidates = candidates
            .Where(candidate => WordCount(candidate.Surface) == 1 && candidate.Surface.Length >= 5)
            .ToArray();
        if (fuzzyCandidates.Length == 0)
        {
            return new WordCorrectionResult(result, replacementCount);
        }

        result = WordTokenRegex().Replace(result, match =>
        {
            var token = match.Value;
            if (ReservedTriggerWords.Contains(token) || FuzzyStopWords.Contains(token) || token.Length < 5)
            {
                return token;
            }

            var scored = fuzzyCandidates
                .Where(candidate => !string.Equals(
                    candidate.Replacement,
                    token,
                    StringComparison.Ordinal))
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = TextSimilarity.LevenshteinSimilarity(
                        token.ToLowerInvariant(),
                        candidate.Surface.ToLowerInvariant()),
                })
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (scored.Length == 0)
            {
                return token;
            }

            var best = scored[0];
            var secondBest = scored
                .Skip(1)
                .FirstOrDefault(item => !string.Equals(
                    item.Candidate.Replacement,
                    best.Candidate.Replacement,
                    StringComparison.OrdinalIgnoreCase));
            var threshold = SimilarityThreshold - LengthAwareAdjustment(best.Candidate.Surface.Length)
                + LargeVocabularyPenalty(fuzzyCandidates.Length);
            if (best.Score < threshold ||
                secondBest is not null && best.Score - secondBest.Score < AmbiguityMargin)
            {
                return token;
            }

            replacementCount++;
            return best.Candidate.Replacement;
        });

        return new WordCorrectionResult(result, replacementCount);
    }

    public static double LargeVocabularyPenalty(int poolSize) =>
        Math.Min(0.06, Math.Max(0, ((poolSize - 100) / 500) * 0.02));

    public static double LengthAwareAdjustment(int candidateLength) =>
        Math.Min(0.04, Math.Max(0, candidateLength - 8) * 0.005);

    private static bool IsUsable(CustomWordEntry entry) =>
        entry is not null &&
        !string.IsNullOrWhiteSpace(entry.SpokenForm) &&
        !string.IsNullOrWhiteSpace(entry.Replacement);

    private static IEnumerable<string> Surfaces(CustomWordEntry entry)
    {
        yield return entry.SpokenForm;
        if (!string.Equals(entry.SpokenForm, entry.Replacement, StringComparison.OrdinalIgnoreCase))
        {
            yield return entry.Replacement;
        }
    }

    private static bool ContainsReservedTrigger(string surface) =>
        surface.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(ReservedTriggerWords.Contains);

    private static int WordCount(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string CollapseSpaces(string value) =>
        WhitespaceRegex().Replace(value.Trim(), " ");

    private static string SurfacePattern(string surface) =>
        $@"(?<![\p{{L}}\p{{N}}]){string.Join(@"[\s,.!?\u2014\u2013-]+", surface.Split(' ').Select(Regex.Escape))}(?![\p{{L}}\p{{N}}])";

    private sealed record Candidate(string Surface, string Replacement);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\u2019-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
