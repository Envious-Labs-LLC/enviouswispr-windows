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

    /// <summary>How close a heard PHRASE must be to a custom phrase before it is corrected.</summary>
    /// <remarks>
    /// A HIGHER BAR THAN A SINGLE WORD, and macOS uses the same number. More words means more ways to
    /// be accidentally similar - two unrelated three-word phrases share their shape far more often
    /// than two unrelated words share their spelling - so the same bar would correct sentences nobody
    /// was aiming at.
    /// </remarks>
    public const double MultiWordSimilarityThreshold = 0.85;

    /// <summary>Added to a phrase's bar when the phrase contains an everyday word.</summary>
    /// <remarks>
    /// "SOUTH BAY" AND "SOUTH DAY" DIFFER BY ONE LETTER AND BY EVERYTHING ELSE. A phrase carrying a
    /// word people say constantly is far likelier to appear by accident, so it has to be a closer
    /// match before anything is changed. A choice the user made themselves REPLACES this rather than
    /// adding to it: somebody who asked for a generous match on a phrase asked for it knowing what
    /// the phrase contains.
    /// </remarks>
    public const double StopWordPenalty = 0.05;

    public const double AmbiguityMargin = 0.05;

    private static readonly HashSet<string> ReservedTriggerWords =
        new(["emoji", "emoticon"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The everyday words that make a PHRASE need a closer match.</summary>
    /// <remarks>
    /// A SHORTER LIST THAN THE SINGLE-WORD ONE, AND macOS'S OWN. Reusing the 44-word set meant
    /// "drove from bostin" against "drive from boston" was refused here and accepted there, purely
    /// because "from" is in the larger list. These fourteen are the words the reference platform
    /// treats as common enough to raise the bar - copied rather than reasoned about, because a set
    /// that disagrees with macOS by one word disagrees with it on real sentences.
    /// </remarks>
    private static readonly HashSet<string> PhraseStopWords = new(
        [
            "the", "and", "or", "is", "to", "for", "in",
            "a", "at", "on", "of", "we", "you", "it",
        ],
        StringComparer.OrdinalIgnoreCase);

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

        // WHO OWNS A SURFACE IS A RULE macOS ALREADY HAS, and this used to answer it differently.
        // Two rows claiming the same spoken form were BOTH dropped here, on the reasoning that
        // picking one silently is picking by position. macOS resolves it instead: among ordinary
        // rows the LAST one written wins, and a row's own replacement yields to any row that claims
        // it as a spoken form. Dropping both is safer and is not what the reference platform does,
        // so a person moving between the two would see different text from the same list.
        var usable = customWords.Where(IsUsable).ToArray();
        var owners = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in usable)
        {
            var surface = CollapseSpaces(entry.SpokenForm);
            if (!ContainsReservedTrigger(surface))
            {
                owners[surface] = new Candidate(surface, entry.Replacement, entry.Strictness);
            }
        }

        // A ONE-WORD REPLACEMENT IS MATCHABLE TOO, so saying the written form aloud still finds the
        // row - but it never takes a surface some row claims as its spoken form.
        //
        // A MULTI-WORD ONE IS NOT, AND macOS IS EXPLICIT ABOUT IT: its canonical self-entry is
        // space-free only. Adding phrases here let a written form nobody was matching against
        // reserve words and block a rule that was: with "zed" writing "red blue", the phrase
        // "red blue" claimed the front of "red blue sun" and stopped "blue sun" correcting anything.
        foreach (var entry in usable)
        {
            var surface = CollapseSpaces(entry.Replacement);
            if (WordCount(surface) == 1 &&
                !ContainsReservedTrigger(surface) &&
                !owners.ContainsKey(surface))
            {
                owners[surface] = new Candidate(surface, entry.Replacement, entry.Strictness);
            }
        }

        var candidates = owners.Values
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
        //
        // PHRASES ARE SETTLED POSITION BY POSITION, LEFT TO RIGHT, which is how macOS reads a
        // sentence. Resolving every exact phrase in the whole sentence before any near match let a
        // phrase starting LATER beat one starting earlier: with "north bae sun" and rules for
        // "north bay" and "bae sun", the exact "bae sun" won and macOS writes "North Bay". Leftmost
        // is a precedence rule there, not a coincidence, so two overlapping phrases that start in
        // different places are not a tie and are not treated as one.
        var claimed = new List<Span>();
        var proposals = PhraseProposals(text, candidates, claimed);

        // SINGLE WORDS AFTERWARDS, both exact and near, over whatever the phrases left. A rule that
        // accounts for more of the sentence explains it better, so it settles its words first.
        var singleWords = candidates
            .Where(candidate => WordCount(candidate.Surface) == 1)
            .ToArray();
        if (singleWords.Length > 0)
        {
            proposals.AddRange(ExactProposals(text, singleWords, claimed));
        }

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

    /// <summary>Finds the single words written exactly as the list spells them.</summary>
    /// <remarks>
    /// NO ARBITRATION LEFT TO DO. Every surface has exactly one owner now, decided the way macOS
    /// decides it, so two rules can no longer compete for the same spelling - and two DIFFERENT
    /// single words cannot overlap in a sentence, because each match is bounded by the word either
    /// side of it. What remains is to take each match that the phrases did not already spoken for.
    ///
    /// A MATCH THAT WOULD CHANGE NOTHING IS STILL SKIPPED, so a word already written the way the
    /// list spells it is not counted as a correction.
    /// </remarks>
    private static List<Proposal> ExactProposals(
        string text,
        IReadOnlyList<Candidate> candidates,
        List<Span> claimed)
    {
        var accepted = new List<Proposal>();
        foreach (var candidate in candidates
            .OrderByDescending(candidate => candidate.Surface.Length)
            .ThenBy(candidate => candidate.Surface, StringComparer.Ordinal))
        {
            foreach (Match match in Regex.Matches(
                text,
                SurfacePattern(candidate.Surface),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250)))
            {
                if (match.Value.Equals(candidate.Replacement, StringComparison.Ordinal) ||
                    IsClaimed(claimed, match.Index, match.Length))
                {
                    continue;
                }

                claimed.Add(new Span(match.Index, match.Length));
                accepted.Add(new Proposal(match.Index, match.Length, candidate.Replacement));
            }
        }

        return accepted;
    }

    /// <summary>Finds runs of words close enough to a custom phrase to be that phrase.</summary>
    /// <remarks>
    /// WITHOUT THIS THE PICKER LIED ON EVERY PHRASE. Somebody could set Loose on "South Bay" and
    /// nothing would happen to "South Bae", because close-enough matching only ever looked at single
    /// words of five letters or more - so a control was offered on entries it could not reach.
    ///
    /// LONGEST RUN FIRST, AND ONLY AGAINST PHRASES OF THE SAME LENGTH. A three-word phrase is
    /// compared with three-word rules and not with two-word ones, so a rule cannot win by being
    /// shorter than what was said. Once a run is taken the search moves past it, because two
    /// overlapping corrections of the same sentence cannot both be what somebody meant.
    ///
    /// A RUN THAT IS ALREADY WRITTEN CORRECTLY IS TAKEN OFF THE TABLE RATHER THAN LEFT OPEN. Saying
    /// the phrase exactly right is a fact about what was said; it is not a competition that a
    /// near-miss rule can then win by overlapping part of it.
    ///
    /// NOT PORTED: the domain-suffix handling macOS carries for phrases ending in ".com". That is
    /// its own behaviour with its own tests on that side, and guessing at it here would be worse
    /// than its absence.
    /// </remarks>
    private static List<Proposal> PhraseProposals(
        string text,
        IReadOnlyList<Candidate> phraseCandidates,
        List<Span> claimed)
    {
        var byLength = phraseCandidates
            .Where(candidate => WordCount(candidate.Surface) > 1)
            .GroupBy(candidate => WordCount(candidate.Surface))
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (byLength.Count == 0)
        {
            return [];
        }

        var longest = byLength.Keys.Max();
        var tokens = WordTokenRegex().Matches(text).ToArray();
        var proposals = new List<Proposal>();
        for (var index = 0; index < tokens.Length; index++)
        {
            // EXACT FIRST ACROSS EVERY RUN LENGTH, AND ONLY THEN NEAR MATCHES. A phrase written the
            // way the list spells it settles this position outright; asking "is this near anything"
            // before "is this exactly something" would let a near miss of a longer phrase beat an
            // exact shorter one starting in the same place.
            var settled = false;
            for (var run = Math.Min(longest, tokens.Length - index); run >= 2 && !settled; run--)
            {
                if (!Reachable(byLength, claimed, tokens, index, run, out var rivals, out var here))
                {
                    continue;
                }

                var written = rivals.FirstOrDefault(candidate =>
                    here.Phrase.Equals(candidate.Surface, StringComparison.OrdinalIgnoreCase));
                if (written is null)
                {
                    continue;
                }

                if (!here.Phrase.Equals(written.Replacement, StringComparison.Ordinal))
                {
                    proposals.Add(new Proposal(here.Start, here.Length, written.Replacement));
                }

                claimed.Add(new Span(here.Start, here.Length));
                index += run - 1;
                settled = true;
            }

            if (settled)
            {
                continue;
            }

            for (var run = Math.Min(longest, tokens.Length - index); run >= 2; run--)
            {
                if (!Reachable(byLength, claimed, tokens, index, run, out var rivals, out var found))
                {
                    continue;
                }

                var (start, length, phrase, words) = found;

                // SAYING IT RIGHT IS A FACT ABOUT WHAT WAS SAID, so it is settled before any
                // competition between rules and against EVERY rule rather than against whichever
                // scored highest - a run that already reads as one rule's answer is correct however
                // well some other rule happens to score against it.
                if (rivals.Any(candidate =>
                    phrase.Equals(candidate.Replacement, StringComparison.Ordinal)))
                {
                    claimed.Add(new Span(start, length));
                    index += run - 1;
                    break;
                }

                // EACH PHRASE IS JUDGED AGAINST ITS OWN BAR BEFORE ANY OF THEM ARE RANKED, which is
                // the same defect this had for single words: ranking first let a STRICT phrase that
                // was never going to be corrected stand in front of a LOOSE one that would have
                // been. "alpha betx" is 0.90 from a strict "alpha beta", which fails its 0.92, and
                // 0.80 from a loose "alpha zeta", which clears its 0.72 - and the sentence was left
                // alone because the strict phrase scored higher.
                var everyday = words.Any(PhraseStopWords.Contains);
                var eligible = rivals
                    .Select(candidate => new
                    {
                        Candidate = candidate,
                        Score = TextSimilarity.LevenshteinSimilarity(
                            phrase.ToLowerInvariant(),
                            candidate.Surface.ToLowerInvariant()),
                    })
                    .Where(item => item.Score >= PhraseThreshold(item.Candidate.Strictness, everyday))
                    .OrderByDescending(item => item.Score)
                    .ToArray();
                if (eligible.Length == 0)
                {
                    continue;
                }

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

                claimed.Add(new Span(start, length));
                proposals.Add(new Proposal(start, length, best.Candidate.Replacement));
                index += run - 1;
                break;
            }
        }

        return proposals;
    }

    /// <summary>One run of words in the sentence, and the rules that could speak for it.</summary>
    /// <remarks>
    /// SHARED BY BOTH HALVES OF THE PHRASE PASS so that "which words is this" is answered once. The
    /// exact half and the near half must be looking at exactly the same run, or the second would be
    /// deciding about a different piece of the sentence than the first refused.
    /// </remarks>
    private static bool Reachable(
        Dictionary<int, Candidate[]> byLength,
        List<Span> claimed,
        Match[] tokens,
        int index,
        int run,
        out Candidate[] rivals,
        out Run found)
    {
        rivals = [];
        found = default;
        if (!byLength.TryGetValue(run, out var candidates))
        {
            return false;
        }

        var slice = tokens.AsSpan(index, run);
        var start = slice[0].Index;
        var length = slice[run - 1].Index + slice[run - 1].Length - start;
        if (IsClaimed(claimed, start, length))
        {
            return false;
        }

        var words = new string[run];
        for (var word = 0; word < run; word++)
        {
            words[word] = slice[word].Value;
        }

        if (words.Any(ReservedTriggerWords.Contains))
        {
            return false;
        }

        rivals = candidates;
        found = new Run(start, length, string.Join(' ', words), words);
        return true;
    }

    private readonly record struct Run(int Start, int Length, string Phrase, string[] Words);

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

    /// <summary>The bar a heard phrase must clear for a custom phrase at the given strictness.</summary>
    public static double PhraseThreshold(MatchStrictness strictness, bool hasEverydayWord) =>
        strictness switch
        {
            MatchStrictness.Loose => LooseSimilarityThreshold,
            MatchStrictness.Strict => StrictSimilarityThreshold,
            _ => MultiWordSimilarityThreshold + (hasEverydayWord ? StopWordPenalty : 0),
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

    private static bool ContainsReservedTrigger(string surface) =>
        surface.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(ReservedTriggerWords.Contains);

    private static int WordCount(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string CollapseSpaces(string value) =>
        WhitespaceRegex().Replace(value.Trim(), " ");

    private static string SurfacePattern(string surface) =>
        $@"(?<![\p{{L}}\p{{N}}]){string.Join(@"[\s,.!?\u2014\u2013-]+", surface.Split(' ').Select(Regex.Escape))}(?![\p{{L}}\p{{N}}])";

    private static bool IsClaimed(List<Span> claimed, int start, int length) =>
        claimed.Any(span => start < span.Start + span.Length && span.Start < start + length);

    private sealed record Candidate(string Surface, string Replacement, MatchStrictness Strictness);

    /// <summary>Part of the original sentence that is spoken for, whether or not it is changed.</summary>
    private readonly record struct Span(int Start, int Length);

    /// <summary>A change to make to the original sentence.</summary>
    private readonly record struct Proposal(int Start, int Length, string Replacement);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\u2019-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
