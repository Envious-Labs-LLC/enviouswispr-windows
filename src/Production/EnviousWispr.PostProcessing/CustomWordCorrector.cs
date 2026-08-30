using System.Text;
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

        // EVERYTHING IS DECIDED AGAINST WHAT THE PERSON ACTUALLY SAID, AND WRITTEN BACK ONCE.
        // Rewriting the text as we go made three separate things depend on order: two phrases that
        // overlap gave whichever row came first, a word written in by one row could be found and
        // rewritten again by the next, and the fuzzy pass read a sentence that already contained
        // words nobody had said - counting one span of the original twice.
        var claimed = new List<Span>();
        var proposals = ExactProposals(text, candidates, claimed);

        var fuzzyCandidates = candidates
            .Where(candidate => WordCount(candidate.Surface) == 1 && candidate.Surface.Length >= 5)
            .ToArray();
        if (fuzzyCandidates.Length > 0)
        {
            proposals.AddRange(FuzzyProposals(text, fuzzyCandidates, claimed));
        }

        if (proposals.Count == 0)
        {
            return new WordCorrectionResult(text, 0);
        }

        // RIGHT TO LEFT, so every proposal's position is still the position it was found at.
        var rewritten = new StringBuilder(text);
        foreach (var proposal in proposals.OrderByDescending(proposal => proposal.Start))
        {
            rewritten.Remove(proposal.Start, proposal.Length).Insert(proposal.Start, proposal.Replacement);
        }

        return new WordCorrectionResult(rewritten.ToString(), proposals.Count);
    }

    /// <summary>Finds the phrases and words written exactly as the list spells them.</summary>
    /// <remarks>
    /// ARBITRATED RANK BY RANK, and dropping the whole disputed region was not enough on its own.
    /// With "red blue" and "blue sun" disputing the middle word, removing both let a plain "blue"
    /// rule underneath them rewrite the very words the dispute was about - so the words that were
    /// supposed to be left alone were not. A disputed match therefore RESERVES what it covers
    /// against everything ranked below it, without being applied itself.
    ///
    /// A MATCH THAT WOULD CHANGE NOTHING NEVER ENTERS THE ARGUMENT. A row spelling a word the way it
    /// is already written has nothing at stake, and letting it dispute a row that does have
    /// something at stake meant a real correction lost to a rule that wanted the text left exactly
    /// as it was.
    /// </remarks>
    private static List<Proposal> ExactProposals(
        string text,
        IReadOnlyList<Candidate> candidates,
        List<Span> claimed)
    {
        var found = new List<SurfaceMatch>();
        foreach (var candidate in candidates)
        {
            foreach (Match match in Regex.Matches(
                text,
                SurfacePattern(candidate.Surface),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250)))
            {
                if (!match.Value.Equals(candidate.Replacement, StringComparison.Ordinal))
                {
                    found.Add(new SurfaceMatch(match.Index, match.Length, candidate));
                }
            }
        }

        var accepted = new List<Proposal>();
        foreach (var rank in found
            .GroupBy(Standing)
            .OrderByDescending(rank => rank.Key.Words)
            .ThenByDescending(rank => rank.Key.Length))
        {
            var here = rank.ToArray();

            // TWO PHRASES OF THE SAME STANDING THAT WANT THE SAME WORDS ARE BOTH LEFT ALONE. Longer
            // phrases still beat shorter ones, which is the rule this always had; a tie is the one
            // case the list itself has no answer for, and picking one silently would be picking by
            // position.
            var disputed = here
                .Where(one => here.Any(other =>
                    !ReferenceEquals(one, other) &&
                    Overlaps(one, other) &&
                    !string.Equals(
                        one.Candidate.Replacement,
                        other.Candidate.Replacement,
                        StringComparison.Ordinal)))
                .ToHashSet();

            foreach (var match in disputed.Where(match => !IsClaimed(claimed, match)))
            {
                claimed.Add(new Span(match.Start, match.Length));
            }

            foreach (var match in here
                .Where(match => !disputed.Contains(match))
                .OrderBy(match => match.Start))
            {
                if (IsClaimed(claimed, match))
                {
                    continue;
                }

                claimed.Add(new Span(match.Start, match.Length));
                accepted.Add(new Proposal(match.Start, match.Length, match.Candidate.Replacement));
            }
        }

        return accepted;
    }

    /// <summary>Finds single words close enough to a custom word to be that word.</summary>
    /// <remarks>
    /// READ FROM THE ORIGINAL SENTENCE, NOT FROM THE CORRECTED ONE. Running this over the rewritten
    /// text meant it examined words nobody had said - a replacement written in by the exact pass
    /// could itself be corrected again, and one span of what the person actually said was then
    /// counted as two corrections.
    /// </remarks>
    private static List<Proposal> FuzzyProposals(
        string text,
        IReadOnlyList<Candidate> fuzzyCandidates,
        List<Span> claimed)
    {
        var proposals = new List<Proposal>();
        foreach (Match match in WordTokenRegex().Matches(text))
        {
            var token = match.Value;
            if (ReservedTriggerWords.Contains(token) ||
                FuzzyStopWords.Contains(token) ||
                token.Length < 5 ||
                IsClaimed(claimed, match.Index, match.Length))
            {
                continue;
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
                        + LargeVocabularyPenalty(fuzzyCandidates.Count),
                })
                .Where(item => item.Score >= item.Threshold)
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (eligible.Length == 0)
            {
                continue;
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
                continue;
            }

            claimed.Add(new Span(match.Index, match.Length));
            proposals.Add(new Proposal(match.Index, match.Length, best.Candidate.Replacement));
        }

        return proposals;
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

    private static bool Overlaps(SurfaceMatch left, SurfaceMatch right) =>
        left.Start < right.Start + right.Length && right.Start < left.Start + left.Length;

    private static bool IsClaimed(List<Span> claimed, SurfaceMatch match) =>
        IsClaimed(claimed, match.Start, match.Length);

    private static bool IsClaimed(List<Span> claimed, int start, int length) =>
        claimed.Any(span => start < span.Start + span.Length && span.Start < start + length);

    /// <summary>How strong a claim a match has on the words it covers.</summary>
    private static (int Words, int Length) Standing(SurfaceMatch match) =>
        (WordCount(match.Candidate.Surface), match.Candidate.Surface.Length);

    private sealed record Candidate(string Surface, string Replacement, MatchStrictness Strictness);

    private sealed record SurfaceMatch(int Start, int Length, Candidate Candidate);

    /// <summary>Part of the original sentence that is spoken for, whether or not it is changed.</summary>
    private readonly record struct Span(int Start, int Length);

    /// <summary>A change to make to the original sentence.</summary>
    private readonly record struct Proposal(int Start, int Length, string Replacement);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\u2019-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
