using System.Text.RegularExpressions;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

public sealed record WordCorrectionResult(string Text, int ReplacementCount);

public static partial class CustomWordCorrector
{
    public const double SimilarityThreshold = 0.82;

    /// <summary>How close a heard word must be when the user asked for a generous match.</summary>
    public const double LooseSimilarityThreshold = 0.72;

    /// <summary>How close a heard word must be when the user asked for a mean one.</summary>
    public const double StrictSimilarityThreshold = 0.92;

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

        // A SURFACE TWO WORDS DISAGREE ABOUT IS LEFT ALONE, rather than decided by which row came
        // first. Aliases sharing a replacement, or one word's replacement being another's spoken
        // form, both reach this - and taking the first meant the correction a person saw depended on
        // the order of a list they had sorted for their own reasons. Where the rows AGREE on the
        // replacement and differ only on how forgiving to be, the strictest wins: two rows arguing
        // about how much to change of what somebody said is settled by changing less.
        var candidates = customWords
            .Where(IsUsable)
            .SelectMany(entry => Surfaces(entry).Select(surface => new Candidate(
                CollapseSpaces(surface),
                entry.Replacement,
                entry.Strictness)))
            .Where(candidate => !ContainsReservedTrigger(candidate.Surface))
            .GroupBy(candidate => candidate.Surface, StringComparer.OrdinalIgnoreCase)
            .Where(group => group
                .Select(candidate => candidate.Replacement)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1)
            .Select(group => group.First() with
            {
                Strictness = group
                    .OrderByDescending(candidate => Caution(candidate.Strictness))
                    .First()
                    .Strictness,
            })
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

            // EACH CANDIDATE IS JUDGED AGAINST ITS OWN BAR BEFORE ANY OF THEM ARE RANKED. Taking the
            // highest score first and then asking whether it cleared its bar let a STRICT word that
            // was never going to be corrected stand in front of a LOOSE word that would have been -
            // one word's setting silently deciding another word's outcome.
            //
            // THE USER'S CHOICE MOVES THE BAR; THE OTHER TWO TERMS STILL APPLY TO IT. Length and
            // vocabulary size are properties of the word and the list, not of how forgiving somebody
            // wanted to be, so asking for a strict match on a long word should not also throw away
            // the allowance long words have always had.
            var eligible = fuzzyCandidates
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
                    Threshold = BaseThreshold(candidate.Strictness)
                        - LengthAwareAdjustment(candidate.Surface.Length)
                        + LargeVocabularyPenalty(fuzzyCandidates.Length),
                })
                .Where(item => item.Score >= item.Threshold)
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (eligible.Length == 0)
            {
                return token;
            }

            // TWO SURFACES THAT WRITE THE SAME WORD ARE NOT RIVALS. The ambiguity check exists to
            // catch a token that could become two DIFFERENT words, so a spelling and its own alias
            // scoring closely must not read as a disagreement.
            var best = eligible[0];
            var rival = eligible
                .Skip(1)
                .FirstOrDefault(item => !string.Equals(
                    item.Candidate.Replacement,
                    best.Candidate.Replacement,
                    StringComparison.OrdinalIgnoreCase));
            if (rival is not null && best.Score - rival.Score < AmbiguityMargin)
            {
                return token;
            }

            replacementCount++;
            return best.Candidate.Replacement;
        });

        return new WordCorrectionResult(result, replacementCount);
    }

    /// <summary>How little of what somebody said this setting is willing to change.</summary>
    /// <remarks>
    /// The enum's own numbers are ordered for storage, where zero has to mean the ordinary rule so
    /// that a file written before this existed reads correctly. That is not the order two settings
    /// are compared in, so the comparison gets its own answer rather than borrowing one.
    /// </remarks>
    private static int Caution(MatchStrictness strictness) => strictness switch
    {
        MatchStrictness.Loose => 0,
        MatchStrictness.Strict => 2,
        _ => 1,
    };

    /// <summary>The bar a heard word must clear for a custom word at the given strictness.</summary>
    public static double BaseThreshold(MatchStrictness strictness) => strictness switch
    {
        MatchStrictness.Loose => LooseSimilarityThreshold,
        MatchStrictness.Strict => StrictSimilarityThreshold,
        _ => SimilarityThreshold,
    };

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

    private sealed record Candidate(string Surface, string Replacement, MatchStrictness Strictness);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\u2019-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
