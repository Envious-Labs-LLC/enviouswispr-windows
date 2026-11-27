using System.Buffers;
using System.Numerics;
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

    private const int IndexCacheCapacity = 8;
    private static readonly object IndexCacheLock = new();
    private static readonly List<CachedIndexEntry> IndexCache = new(IndexCacheCapacity);

    public static WordCorrectionResult Correct(
        string text,
        IReadOnlyList<CustomWordEntry> customWords) =>
        Correct(text, customWords, packWords: null, excludeLearnedAliases: false);

    public static WordCorrectionResult Correct(
        string text,
        IReadOnlyList<CustomWordEntry> customWords,
        IReadOnlyList<CustomWordEntry>? packWords) =>
        Correct(text, customWords, packWords, excludeLearnedAliases: false);

    public static WordCorrectionResult Correct(
        string text,
        IReadOnlyList<CustomWordEntry> customWords,
        IReadOnlyList<CustomWordEntry>? packWords,
        bool excludeLearnedAliases,
        bool reserveExcludedLearnedAliases = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(customWords);
        if (text.Length == 0 || (customWords.Count == 0 && (packWords is null || packWords.Count == 0)))
        {
            return new WordCorrectionResult(text, 0);
        }

        var index = GetOrBuildIndex(customWords, packWords, excludeLearnedAliases, reserveExcludedLearnedAliases);
        if (index.IsEmpty)
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
        Match[]? tokens = null;
        List<Proposal> proposals;
        if (index.HasPhrases)
        {
            tokens = WordTokenRegex().Matches(text).ToArray();
            proposals = PhraseProposals(tokens, index, claimed);
        }
        else
        {
            proposals = new List<Proposal>();
        }

        // SINGLE WORDS AFTERWARDS, both exact and near, over whatever the phrases left. A rule that
        // accounts for more of the sentence explains it better, so it settles its words first.
        if (index.HasSingleWords)
        {
            ExactProposals(text, index, claimed, proposals);
        }

        if (index.HasFuzzyCandidates)
        {
            tokens ??= WordTokenRegex().Matches(text).ToArray();
            FuzzyProposals(tokens, index, claimed, proposals);
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

    private static CompiledWordIndex GetOrBuildIndex(
        IReadOnlyList<CustomWordEntry> customWords,
        IReadOnlyList<CustomWordEntry>? packWords,
        bool excludeLearnedAliases,
        bool reserveExcludedLearnedAliases)
    {
        if (customWords is CustomWordEntry[] customArray && (packWords is null || packWords is CustomWordEntry[]))
        {
            var packArray = packWords as CustomWordEntry[];
            lock (IndexCacheLock)
            {
                for (var i = IndexCache.Count - 1; i >= 0; i--)
                {
                    var cached = IndexCache[i];
                    if (cached.Matches(customArray, packArray, excludeLearnedAliases, reserveExcludedLearnedAliases))
                    {
                        if (i != IndexCache.Count - 1)
                        {
                            IndexCache.RemoveAt(i);
                            IndexCache.Add(cached);
                        }

                        return cached.Index;
                    }
                }
            }

            var built = CompiledWordIndex.Build(
                customArray,
                packArray,
                excludeLearnedAliases,
                reserveExcludedLearnedAliases);
            lock (IndexCacheLock)
            {
                for (var i = 0; i < IndexCache.Count; i++)
                {
                    if (IndexCache[i].Matches(customArray, packArray, excludeLearnedAliases, reserveExcludedLearnedAliases))
                    {
                        return IndexCache[i].Index;
                    }
                }

                if (IndexCache.Count >= IndexCacheCapacity)
                {
                    IndexCache.RemoveAt(0);
                }

                IndexCache.Add(new CachedIndexEntry(
                    customArray,
                    (CustomWordEntry[])customArray.Clone(),
                    packArray,
                    packArray is { Length: > 0 } ? (CustomWordEntry[])packArray.Clone() : null,
                    excludeLearnedAliases,
                    reserveExcludedLearnedAliases,
                    built));
            }

            return built;
        }

        return CompiledWordIndex.Build(customWords, packWords, excludeLearnedAliases, reserveExcludedLearnedAliases);
    }

    /// <summary>Finds the single words written exactly as the list spells them.</summary>
    private static void ExactProposals(
        string text,
        CompiledWordIndex index,
        List<Span> claimed,
        List<Proposal> accepted)
    {
        var hits = new List<ExactHit>();

        // 1. Single O(text.Length) pass over maximal letter/number runs against exact alnum map.
        if (index.ExactAlnumSingleWords.Count > 0)
        {
            var pos = 0;
            while (pos < text.Length)
            {
                if (!IsWordChar(text[pos]))
                {
                    pos++;
                    continue;
                }

                var start = pos;
                pos++;
                while (pos < text.Length && IsWordChar(text[pos]))
                {
                    pos++;
                }

                var length = pos - start;
                var slice = text.Substring(start, length);
                if (index.ExactAlnumSingleWords.TryGetValue(slice, out var candidate))
                {
                    hits.Add(new ExactHit(start, length, candidate));
                }
            }
        }

        // 2. Single O(text.Length) pass over Aho-Corasick trie for surfaces containing punctuation/hyphens.
        index.ExactComplexMatcher?.FindMatches(text, hits);

        if (hits.Count == 0)
        {
            return;
        }

        if (hits.Count > 1)
        {
            hits.Sort(static (a, b) =>
            {
                var lenCmp = b.Candidate.Surface.Length.CompareTo(a.Candidate.Surface.Length);
                if (lenCmp != 0)
                {
                    return lenCmp;
                }

                var surfaceCmp = string.CompareOrdinal(a.Candidate.Surface, b.Candidate.Surface);
                return surfaceCmp != 0 ? surfaceCmp : a.Start.CompareTo(b.Start);
            });
        }

        foreach (var hit in hits)
        {
            var value = text.AsSpan(hit.Start, hit.Length);
            if (MemoryExtensions.Equals(value, hit.Candidate.Replacement.AsSpan(), StringComparison.Ordinal) ||
                IsClaimed(claimed, hit.Start, hit.Length))
            {
                continue;
            }

            claimed.Add(new Span(hit.Start, hit.Length));
            accepted.Add(new Proposal(hit.Start, hit.Length, hit.Candidate.Replacement));
        }
    }

    /// <summary>Finds runs of words close enough to a custom phrase to be that phrase.</summary>
    private static List<Proposal> PhraseProposals(
        Match[] tokens,
        CompiledWordIndex index,
        List<Span> claimed)
    {
        var longest = index.LongestPhraseWordCount;
        var proposals = new List<Proposal>();
        for (var wordIndex = 0; wordIndex < tokens.Length; wordIndex++)
        {
            // EXACT FIRST ACROSS EVERY RUN LENGTH, AND ONLY THEN NEAR MATCHES. A phrase written the
            // way the list spells it settles this position outright; asking "is this near anything"
            // before "is this exactly something" would let a near miss of a longer phrase beat an
            // exact shorter one starting in the same place.
            var settled = false;
            for (var run = Math.Min(longest, tokens.Length - wordIndex); run >= 2 && !settled; run--)
            {
                if (!Reachable(index, claimed, tokens, wordIndex, run, out var rivals, out var exactMap, out var here))
                {
                    continue;
                }

                if (exactMap is null || !exactMap.TryGetValue(here.Phrase, out var written))
                {
                    continue;
                }

                if (!here.Phrase.Equals(written.Replacement, StringComparison.Ordinal))
                {
                    proposals.Add(new Proposal(here.Start, here.Length, written.Replacement));
                }

                claimed.Add(new Span(here.Start, here.Length));
                wordIndex += run - 1;
                settled = true;
            }

            if (settled)
            {
                continue;
            }

            for (var run = Math.Min(longest, tokens.Length - wordIndex); run >= 2; run--)
            {
                if (!Reachable(index, claimed, tokens, wordIndex, run, out var rivals, out _, out var found))
                {
                    continue;
                }

                var (start, length, phrase, words) = found;

                // SAYING IT RIGHT IS A FACT ABOUT WHAT WAS SAID, so it is settled before any
                // competition between rules and against EVERY rule rather than against whichever
                // scored highest - a run that already reads as one rule's answer is correct however
                // well some other rule happens to score against it.
                var alreadyRight = false;
                foreach (var candidate in rivals)
                {
                    if (phrase.Equals(candidate.Replacement, StringComparison.Ordinal))
                    {
                        alreadyRight = true;
                        break;
                    }
                }

                if (alreadyRight)
                {
                    claimed.Add(new Span(start, length));
                    wordIndex += run - 1;
                    break;
                }

                var everyday = false;
                for (var w = 0; w < words.Length; w++)
                {
                    if (PhraseStopWords.Contains(words[w]))
                    {
                        everyday = true;
                        break;
                    }
                }

                var phraseLower = phrase.ToLowerInvariant();
                var phraseRunes = TextSimilarity.ToRuneArray(phraseLower);
                var phraseMask = ComputeRuneMask(phraseRunes);

                CompiledCandidate? best = null;
                var bestScore = double.NegativeInfinity;
                CompiledCandidate? secondBestDifferent = null;
                var secondScore = double.NegativeInfinity;

                foreach (var candidate in rivals)
                {
                    var threshold = PhraseThreshold(candidate.Strictness, everyday);
                    var maxLength = Math.Max(phraseRunes.Length, candidate.SurfaceRunes.Length);
                    var maxAllowedDistance = MaxDistanceForThreshold(threshold, maxLength);
                    if (Math.Abs(phraseRunes.Length - candidate.SurfaceRunes.Length) > maxAllowedDistance ||
                        MaskLowerBoundDistance(phraseMask, candidate.CharMask) > maxAllowedDistance)
                    {
                        continue;
                    }

                    var score = TextSimilarity.LevenshteinSimilarity(
                        phraseRunes.AsSpan(),
                        candidate.SurfaceRunes.AsSpan(),
                        maxAllowedDistance);
                    if (score < threshold)
                    {
                        continue;
                    }

                    UpdateTopTwo(
                        candidate,
                        score,
                        ref best,
                        ref bestScore,
                        ref secondBestDifferent,
                        ref secondScore);
                }

                if (best is null)
                {
                    continue;
                }

                if (secondBestDifferent is not null && bestScore - secondScore < AmbiguityMargin)
                {
                    continue;
                }

                claimed.Add(new Span(start, length));
                proposals.Add(new Proposal(start, length, best.Replacement));
                wordIndex += run - 1;
                break;
            }
        }

        return proposals;
    }

    private static bool Reachable(
        CompiledWordIndex index,
        List<Span> claimed,
        Match[] tokens,
        int wordIndex,
        int run,
        out CompiledCandidate[] rivals,
        out Dictionary<string, Candidate>? exactMap,
        out Run found)
    {
        rivals = [];
        exactMap = null;
        found = default;
        if (!index.PhrasesByLength.TryGetValue(run, out var bucket))
        {
            return false;
        }

        var slice = tokens.AsSpan(wordIndex, run);
        var start = slice[0].Index;
        var length = slice[run - 1].Index + slice[run - 1].Length - start;
        if (IsClaimed(claimed, start, length))
        {
            return false;
        }

        var words = new string[run];
        for (var word = 0; word < run; word++)
        {
            var tokenValue = slice[word].Value;
            if (ReservedTriggerWords.Contains(tokenValue))
            {
                return false;
            }

            words[word] = tokenValue;
        }

        rivals = bucket.Candidates;
        exactMap = bucket.ExactBySurface;
        found = new Run(start, length, string.Join(' ', words), words);
        return true;
    }

    private readonly record struct Run(int Start, int Length, string Phrase, string[] Words);

    /// <summary>Finds single words close enough to a custom word to be that word.</summary>
    private static void FuzzyProposals(
        Match[] tokens,
        CompiledWordIndex index,
        List<Span> claimed,
        List<Proposal> proposals)
    {
        var poolPenalty = LargeVocabularyPenalty(index.FuzzyPoolSize);
        var lowestPossibleThreshold = LooseSimilarityThreshold - 0.04 + poolPenalty;

        foreach (var match in tokens)
        {
            var token = match.Value;
            if (token.Length < 5 ||
                ReservedTriggerWords.Contains(token) ||
                FuzzyStopWords.Contains(token) ||
                IsClaimed(claimed, match.Index, match.Length))
            {
                continue;
            }

            var tokenLower = token.ToLowerInvariant();
            var tokenRunes = TextSimilarity.ToRuneArray(tokenLower);
            var tokenLen = tokenRunes.Length;
            if (tokenLen < 5)
            {
                continue;
            }

            var tokenMask = ComputeRuneMask(tokenRunes);
            var maxDeltaForToken = Math.Max(0, (int)Math.Floor((1.0 - lowestPossibleThreshold) * Math.Max(tokenLen, index.MaxFuzzyRuneLength) + 1e-9));
            var minCandidateLen = Math.Max(index.MinFuzzyRuneLength, tokenLen - maxDeltaForToken);
            var maxCandidateLen = Math.Min(index.MaxFuzzyRuneLength, tokenLen + maxDeltaForToken);

            CompiledCandidate? best = null;
            var bestScore = double.NegativeInfinity;
            CompiledCandidate? secondBestDifferent = null;
            var secondScore = double.NegativeInfinity;

            for (var candidateLen = minCandidateLen; candidateLen <= maxCandidateLen; candidateLen++)
            {
                if (!index.FuzzyByRuneLength.TryGetValue(candidateLen, out var bucket))
                {
                    continue;
                }

                var maxLen = Math.Max(tokenLen, candidateLen);
                foreach (var candidate in bucket)
                {
                    if (string.Equals(candidate.Replacement, token, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var threshold = candidate.BaseThreshold - candidate.LengthAdjustment + poolPenalty;
                    var maxAllowedDistance = MaxDistanceForThreshold(threshold, maxLen);
                    if (Math.Abs(tokenLen - candidateLen) > maxAllowedDistance ||
                        MaskLowerBoundDistance(tokenMask, candidate.CharMask) > maxAllowedDistance)
                    {
                        continue;
                    }

                    var score = TextSimilarity.LevenshteinSimilarity(
                        tokenRunes.AsSpan(),
                        candidate.SurfaceRunes.AsSpan(),
                        maxAllowedDistance);
                    if (score < threshold)
                    {
                        continue;
                    }

                    UpdateTopTwo(
                        candidate,
                        score,
                        ref best,
                        ref bestScore,
                        ref secondBestDifferent,
                        ref secondScore);
                }
            }

            if (best is null)
            {
                continue;
            }

            // TWO SURFACES THAT WRITE THE SAME WORD ARE NOT RIVALS.
            if (secondBestDifferent is not null && bestScore - secondScore < AmbiguityMargin)
            {
                continue;
            }

            claimed.Add(new Span(match.Index, match.Length));
            proposals.Add(new Proposal(match.Index, match.Length, best.Replacement));
        }
    }

    private static void UpdateTopTwo(
        CompiledCandidate candidate,
        double score,
        ref CompiledCandidate? best,
        ref double bestScore,
        ref CompiledCandidate? secondBestDifferent,
        ref double secondScore)
    {
        if (score > bestScore)
        {
            if (best is not null &&
                !string.Equals(best.Replacement, candidate.Replacement, StringComparison.OrdinalIgnoreCase))
            {
                secondBestDifferent = best;
                secondScore = bestScore;
            }

            best = candidate;
            bestScore = score;
        }
        else if (best is not null &&
                 !string.Equals(candidate.Replacement, best.Replacement, StringComparison.OrdinalIgnoreCase) &&
                 score > secondScore)
        {
            secondBestDifferent = candidate;
            secondScore = score;
        }
    }

    private static int MaxDistanceForThreshold(double threshold, int maxLength)
    {
        if (threshold >= 1.0)
        {
            return 0;
        }

        if (threshold <= 0.0)
        {
            return maxLength;
        }

        return Math.Max(0, (int)Math.Floor(((1.0 - threshold) * maxLength) + 1e-9));
    }

    private static ulong ComputeRuneMask(ReadOnlySpan<Rune> runes)
    {
        ulong mask = 0;
        foreach (var rune in runes)
        {
            var val = rune.Value;
            int bit;
            if (val is >= 'a' and <= 'z')
            {
                bit = val - 'a';
            }
            else if (val is >= '0' and <= '9')
            {
                bit = 26 + (val - '0');
            }
            else
            {
                bit = 36 + ((int)((uint)val * 2654435761u) % 28);
                if (bit < 36)
                {
                    bit = 36;
                }
            }

            mask |= 1UL << bit;
        }

        return mask;
    }

    private static int MaskLowerBoundDistance(ulong leftMask, ulong rightMask)
    {
        var onlyLeft = BitOperations.PopCount(leftMask & ~rightMask);
        var onlyRight = BitOperations.PopCount(rightMask & ~leftMask);
        return Math.Max(onlyLeft, onlyRight);
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

    private static HashSet<string>? CollectReservedLearnedSurfaces(IReadOnlyList<CustomWordEntry> entries)
    {
        HashSet<string>? reserved = null;
        foreach (var entry in entries)
        {
            if (entry is null || !IsUsable(entry) || !entry.IsAutoLearned)
            {
                continue;
            }

            foreach (var alias in entry.EffectiveLearnedAliases())
            {
                var surface = CollapseSpaces(alias);
                if (!string.IsNullOrEmpty(surface) && !ContainsReservedTrigger(surface))
                {
                    reserved ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    reserved.Add(surface);
                }
            }
        }

        return reserved;
    }

    private static void RegisterEntries(
        IReadOnlyList<CustomWordEntry> entries,
        Dictionary<string, Candidate> owners,
        bool overwriteExistingAliases,
        bool excludeLearnedAliases = false,
        HashSet<string>? skipSurfaces = null)
    {
        foreach (var entry in entries)
        {
            if (entry is null || !IsUsable(entry) || (excludeLearnedAliases && entry.IsPureAutoLearned))
            {
                continue;
            }

            var effectiveLearned = excludeLearnedAliases ? entry.EffectiveLearnedAliases() : [];
            HashSet<string>? learnedAliases = effectiveLearned.Count > 0
                ? effectiveLearned.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (var alias in entry.Aliases)
            {
                if (learnedAliases is not null && learnedAliases.Contains(alias))
                {
                    continue;
                }

                var surface = CollapseSpaces(alias);
                if (skipSurfaces is not null && skipSurfaces.Contains(surface))
                {
                    continue;
                }

                if (!ContainsReservedTrigger(surface) &&
                    (overwriteExistingAliases || !owners.ContainsKey(surface)))
                {
                    owners[surface] = new Candidate(surface, entry.Replacement, entry.Strictness);
                }
            }
        }

        foreach (var entry in entries)
        {
            if (entry is null || !IsUsable(entry) || (excludeLearnedAliases && entry.IsPureAutoLearned))
            {
                continue;
            }

            var surface = CollapseSpaces(entry.Replacement);
            if (skipSurfaces is not null && skipSurfaces.Contains(surface))
            {
                continue;
            }

            if ((WordCount(surface) == 1 || entry.Aliases.Count == 0) &&
                !ContainsReservedTrigger(surface) &&
                !owners.ContainsKey(surface))
            {
                owners[surface] = new Candidate(surface, entry.Replacement, entry.Strictness);
            }
        }
    }

    private static bool IsUsable(CustomWordEntry entry) =>
        entry is not null &&
        !string.IsNullOrWhiteSpace(entry.SpokenForm) &&
        !string.IsNullOrWhiteSpace(entry.Replacement);

    private static bool IsWordChar(char c) =>
        char.IsLetter(c) || char.IsNumber(c);

    private static bool IsAllWordChars(string surface)
    {
        if (surface.Length == 0)
        {
            return false;
        }

        foreach (var c in surface)
        {
            if (!IsWordChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsReservedTrigger(string surface) =>
        surface.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(ReservedTriggerWords.Contains);

    private static int WordCount(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    internal static string CollapseSpaces(string value) =>
        WhitespaceRegex().Replace(value.Trim(), " ");

    internal static string SurfacePattern(string surface) =>
        $@"(?<![\p{{L}}\p{{N}}]){string.Join(@"[\s,.!?\u2014\u2013-]+", surface.Split(' ').Select(Regex.Escape))}(?![\p{{L}}\p{{N}}])";

    private static bool IsClaimed(List<Span> claimed, int start, int length) =>
        claimed.Any(span => start < span.Start + span.Length && span.Start < start + length);

    private sealed class CompiledWordIndex
    {
        public Dictionary<string, Candidate> ExactAlnumSingleWords { get; }

        public AhoCorasickMatcher? ExactComplexMatcher { get; }

        public Dictionary<int, PhraseBucket> PhrasesByLength { get; }

        public int LongestPhraseWordCount { get; }

        public Dictionary<int, CompiledCandidate[]> FuzzyByRuneLength { get; }

        public int FuzzyPoolSize { get; }

        public int MinFuzzyRuneLength { get; }

        public int MaxFuzzyRuneLength { get; }

        public bool HasPhrases => PhrasesByLength.Count > 0;

        public bool HasSingleWords => ExactAlnumSingleWords.Count > 0 || ExactComplexMatcher is not null;

        public bool HasFuzzyCandidates => FuzzyPoolSize > 0;

        public bool IsEmpty => !HasPhrases && !HasSingleWords;

        private CompiledWordIndex(
            Dictionary<string, Candidate> exactAlnumSingleWords,
            AhoCorasickMatcher? exactComplexMatcher,
            Dictionary<int, PhraseBucket> phrasesByLength,
            int longestPhraseWordCount,
            Dictionary<int, CompiledCandidate[]> fuzzyByRuneLength,
            int fuzzyPoolSize,
            int minFuzzyRuneLength,
            int maxFuzzyRuneLength)
        {
            ExactAlnumSingleWords = exactAlnumSingleWords;
            ExactComplexMatcher = exactComplexMatcher;
            PhrasesByLength = phrasesByLength;
            LongestPhraseWordCount = longestPhraseWordCount;
            FuzzyByRuneLength = fuzzyByRuneLength;
            FuzzyPoolSize = fuzzyPoolSize;
            MinFuzzyRuneLength = minFuzzyRuneLength;
            MaxFuzzyRuneLength = maxFuzzyRuneLength;
        }

        public static CompiledWordIndex Build(
            IReadOnlyList<CustomWordEntry> customWords,
            IReadOnlyList<CustomWordEntry>? packWords,
            bool excludeLearnedAliases,
            bool reserveExcludedLearnedAliases)
        {
            var owners = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            RegisterEntries(customWords, owners, overwriteExistingAliases: true, excludeLearnedAliases: excludeLearnedAliases);

            if (packWords is { Count: > 0 })
            {
                var reservedLearned = excludeLearnedAliases && reserveExcludedLearnedAliases
                    ? CollectReservedLearnedSurfaces(customWords)
                    : null;
                // Isolated toggleable pack layer: pack entries never overwrite user-defined entries
                // or active auto-learned aliases still awaiting contextual verification.
                RegisterEntries(packWords, owners, overwriteExistingAliases: false, excludeLearnedAliases: false, skipSurfaces: reservedLearned);
            }

            if (owners.Count == 0)
            {
                return new CompiledWordIndex(
                    new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase),
                    exactComplexMatcher: null,
                    new Dictionary<int, PhraseBucket>(),
                    0,
                    new Dictionary<int, CompiledCandidate[]>(),
                    0,
                    int.MaxValue,
                    0);
            }

            var orderedCandidates = owners.Values
                .OrderByDescending(candidate => WordCount(candidate.Surface))
                .ThenByDescending(candidate => candidate.Surface.Length)
                .ToArray();

            var exactAlnum = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            var exactComplex = new List<Candidate>();
            var phrasesGrouped = new Dictionary<int, List<CompiledCandidate>>();
            var exactPhrasesGrouped = new Dictionary<int, Dictionary<string, Candidate>>();
            var fuzzyGrouped = new Dictionary<int, List<CompiledCandidate>>();
            var longestPhrase = 0;
            var fuzzyPoolSize = 0;
            var minFuzzyLen = int.MaxValue;
            var maxFuzzyLen = 0;

            foreach (var candidate in orderedCandidates)
            {
                var wordCount = WordCount(candidate.Surface);
                if (wordCount == 1)
                {
                    if (IsAllWordChars(candidate.Surface))
                    {
                        exactAlnum[candidate.Surface] = candidate;
                    }
                    else
                    {
                        exactComplex.Add(candidate);
                    }

                    if (candidate.Surface.Length >= 5)
                    {
                        var surfaceLower = candidate.Surface.ToLowerInvariant();
                        var runes = TextSimilarity.ToRuneArray(surfaceLower);
                        var compiled = new CompiledCandidate(
                            candidate.Surface,
                            candidate.Replacement,
                            candidate.Strictness,
                            runes,
                            ComputeRuneMask(runes),
                            BaseThreshold(candidate.Strictness),
                            LengthAwareAdjustment(candidate.Surface.Length));
                        if (!fuzzyGrouped.TryGetValue(runes.Length, out var bucket))
                        {
                            bucket = new List<CompiledCandidate>();
                            fuzzyGrouped[runes.Length] = bucket;
                        }

                        bucket.Add(compiled);
                        fuzzyPoolSize++;
                        if (runes.Length < minFuzzyLen)
                        {
                            minFuzzyLen = runes.Length;
                        }

                        if (runes.Length > maxFuzzyLen)
                        {
                            maxFuzzyLen = runes.Length;
                        }
                    }
                }
                else if (wordCount > 1)
                {
                    if (wordCount > longestPhrase)
                    {
                        longestPhrase = wordCount;
                    }

                    var surfaceLower = candidate.Surface.ToLowerInvariant();
                    var runes = TextSimilarity.ToRuneArray(surfaceLower);
                    var compiled = new CompiledCandidate(
                        candidate.Surface,
                        candidate.Replacement,
                        candidate.Strictness,
                        runes,
                        ComputeRuneMask(runes),
                        BaseThreshold(candidate.Strictness),
                        LengthAwareAdjustment(candidate.Surface.Length));

                    if (!phrasesGrouped.TryGetValue(wordCount, out var phraseList))
                    {
                        phraseList = new List<CompiledCandidate>();
                        phrasesGrouped[wordCount] = phraseList;
                    }

                    phraseList.Add(compiled);

                    if (!exactPhrasesGrouped.TryGetValue(wordCount, out var exactMap))
                    {
                        exactMap = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
                        exactPhrasesGrouped[wordCount] = exactMap;
                    }

                    exactMap[candidate.Surface] = candidate;
                }
            }

            var phraseBuckets = new Dictionary<int, PhraseBucket>(phrasesGrouped.Count);
            foreach (var (wordCount, list) in phrasesGrouped)
            {
                phraseBuckets[wordCount] = new PhraseBucket(list.ToArray(), exactPhrasesGrouped[wordCount]);
            }

            var fuzzyBuckets = new Dictionary<int, CompiledCandidate[]>(fuzzyGrouped.Count);
            foreach (var (len, list) in fuzzyGrouped)
            {
                fuzzyBuckets[len] = list.ToArray();
            }

            return new CompiledWordIndex(
                exactAlnum,
                exactComplex.Count > 0 ? AhoCorasickMatcher.Build(exactComplex) : null,
                phraseBuckets,
                longestPhrase,
                fuzzyBuckets,
                fuzzyPoolSize,
                minFuzzyLen,
                maxFuzzyLen);
        }
    }

    private sealed class AhoCorasickMatcher
    {
        private sealed class Node
        {
            public Dictionary<char, int>? Children;
            public int Failure;
            public List<Candidate>? Outputs;
        }

        private readonly Node[] _nodes;

        private AhoCorasickMatcher(Node[] nodes)
        {
            _nodes = nodes;
        }

        public static AhoCorasickMatcher Build(List<Candidate> patterns)
        {
            var nodes = new List<Node> { new() };
            foreach (var pattern in patterns)
            {
                var state = 0;
                foreach (var c in pattern.Surface)
                {
                    var lower = char.ToLowerInvariant(c);
                    var children = nodes[state].Children ??= new Dictionary<char, int>();
                    if (!children.TryGetValue(lower, out var next))
                    {
                        next = nodes.Count;
                        nodes.Add(new Node());
                        children[lower] = next;
                    }

                    state = next;
                }

                (nodes[state].Outputs ??= new List<Candidate>(1)).Add(pattern);
            }

            var queue = new Queue<int>();
            if (nodes[0].Children is { } rootChildren)
            {
                foreach (var next in rootChildren.Values)
                {
                    nodes[next].Failure = 0;
                    queue.Enqueue(next);
                }
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var children = nodes[current].Children;
                if (children is null)
                {
                    continue;
                }

                foreach (var (ch, child) in children)
                {
                    queue.Enqueue(child);
                    var failure = nodes[current].Failure;
                    while (failure != 0 && (nodes[failure].Children is null || !nodes[failure].Children!.ContainsKey(ch)))
                    {
                        failure = nodes[failure].Failure;
                    }

                    if (nodes[failure].Children is not null && nodes[failure].Children!.TryGetValue(ch, out var fallback))
                    {
                        failure = fallback;
                    }

                    nodes[child].Failure = failure;
                    if (nodes[failure].Outputs is { Count: > 0 } inherited)
                    {
                        (nodes[child].Outputs ??= new List<Candidate>(inherited.Count)).AddRange(inherited);
                    }
                }
            }

            return new AhoCorasickMatcher(nodes.ToArray());
        }

        public void FindMatches(string text, List<ExactHit> hits)
        {
            Dictionary<Candidate, int>? lastEndByCandidate = null;
            var state = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = char.ToLowerInvariant(text[i]);
                while (state != 0 && (_nodes[state].Children is null || !_nodes[state].Children!.ContainsKey(ch)))
                {
                    state = _nodes[state].Failure;
                }

                if (_nodes[state].Children is not null && _nodes[state].Children!.TryGetValue(ch, out var next))
                {
                    state = next;
                }

                var outputs = _nodes[state].Outputs;
                if (outputs is null)
                {
                    continue;
                }

                var end = i + 1;
                if (end < text.Length && IsWordChar(text[end]))
                {
                    continue;
                }

                foreach (var candidate in outputs)
                {
                    var len = candidate.Surface.Length;
                    var start = end - len;
                    if (start < 0 || (start > 0 && IsWordChar(text[start - 1])))
                    {
                        continue;
                    }

                    lastEndByCandidate ??= new Dictionary<Candidate, int>(ReferenceEqualityComparer.Instance);
                    if (lastEndByCandidate.TryGetValue(candidate, out var lastEnd) && start < lastEnd)
                    {
                        continue;
                    }

                    lastEndByCandidate[candidate] = end;
                    hits.Add(new ExactHit(start, len, candidate));
                }
            }
        }
    }

    private sealed record PhraseBucket(
        CompiledCandidate[] Candidates,
        Dictionary<string, Candidate> ExactBySurface);

    private sealed record CompiledCandidate(
        string Surface,
        string Replacement,
        MatchStrictness Strictness,
        Rune[] SurfaceRunes,
        ulong CharMask,
        double BaseThreshold,
        double LengthAdjustment);

    private readonly record struct ExactHit(int Start, int Length, Candidate Candidate);

    private sealed record CachedIndexEntry(
        CustomWordEntry[] CustomWordsRef,
        CustomWordEntry[] CustomWordsSnapshot,
        CustomWordEntry[]? PackWordsRef,
        CustomWordEntry[]? PackWordsSnapshot,
        bool ExcludeLearnedAliases,
        bool ReserveExcludedLearnedAliases,
        CompiledWordIndex Index)
    {
        public bool Matches(
            CustomWordEntry[] customWords,
            CustomWordEntry[]? packWords,
            bool excludeLearnedAliases,
            bool reserveExcludedLearnedAliases)
        {
            if (ExcludeLearnedAliases != excludeLearnedAliases ||
                ReserveExcludedLearnedAliases != reserveExcludedLearnedAliases ||
                !ReferenceEquals(CustomWordsRef, customWords) ||
                CustomWordsSnapshot.Length != customWords.Length)
            {
                return false;
            }

            if ((PackWordsSnapshot?.Length ?? 0) != (packWords?.Length ?? 0))
            {
                return false;
            }

            for (var i = 0; i < customWords.Length; i++)
            {
                if (!ReferenceEquals(CustomWordsSnapshot[i], customWords[i]))
                {
                    return false;
                }
            }

            if (packWords is { Length: > 0 } && !ReferenceEquals(PackWordsRef, packWords))
            {
                if (PackWordsSnapshot is null || PackWordsSnapshot.Length != packWords.Length)
                {
                    return false;
                }

                for (var i = 0; i < packWords.Length; i++)
                {
                    if (!ReferenceEquals(PackWordsSnapshot[i], packWords[i]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

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
