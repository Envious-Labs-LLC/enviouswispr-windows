using System.Diagnostics;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Extracts observed-misspelling candidates owned by auto-learned entries and builds bounded A/B
/// context windows for the runtime learned-word checker. Mirrors macOS <c>LearnedWordCandidates</c>.
/// </summary>
public static class LearnedWordCandidates
{
    public const int DefaultMaxSpots = 16;
    public const int MaxContextUtf16 = 400;
    public static readonly TimeSpan DefaultSearchBudget = TimeSpan.FromMilliseconds(200);

    private readonly record struct RawCandidate(
        int RangeStart,
        int RangeLength,
        string Canonical);

    public static IReadOnlyList<LearnedWord> ExtractLearnedWords(IReadOnlyList<CustomWordEntry> customWords)
    {
        ArgumentNullException.ThrowIfNull(customWords);
        if (customWords.Count == 0)
        {
            return [];
        }

        var result = new List<LearnedWord>();
        foreach (var entry in customWords)
        {
            if (entry is null || !entry.HasCheckerAliases || string.IsNullOrWhiteSpace(entry.Replacement))
            {
                continue;
            }

            var misspellings = entry.EffectiveLearnedAliases();
            var cleaned = misspellings
                .Select(CustomWordCorrector.CollapseSpaces)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (cleaned.Length > 0)
            {
                result.Add(new LearnedWord(entry.Replacement.Trim(), cleaned));
            }
        }

        return result;
    }

    public static IReadOnlyList<LearnedWordCheckQuestion> Search(
        string text,
        IReadOnlyList<LearnedWord> learnedWords,
        int maxSpots = DefaultMaxSpots,
        TimeSpan? searchBudget = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(learnedWords);

        if (text.Length == 0 || learnedWords.Count == 0 || maxSpots <= 0)
        {
            return [];
        }

        var budget = searchBudget ?? DefaultSearchBudget;
        if (budget <= TimeSpan.Zero)
        {
            return [];
        }

        var budgetTimer = Stopwatch.StartNew();
        var raw = new List<RawCandidate>();
        var seen = new HashSet<(int Start, int Length, string Word)>();

        foreach (var word in learnedWords)
        {
            if (budgetTimer.Elapsed >= budget)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(word.Canonical))
            {
                continue;
            }

            foreach (var misspelling in word.ObservedMisspellings)
            {
                if (budgetTimer.Elapsed >= budget)
                {
                    break;
                }

                var surface = CustomWordCorrector.CollapseSpaces(misspelling);
                if (string.IsNullOrEmpty(surface) || !ContainsAllSurfaceTokens(text, surface))
                {
                    continue;
                }

                var remaining = budget - budgetTimer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var pattern = CustomWordCorrector.SurfacePattern(surface);
                try
                {
                    foreach (Match match in Regex.Matches(
                        text,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        remaining))
                    {
                        if (match.Length == 0 ||
                            string.Equals(match.Value, word.Canonical, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var key = (match.Index, match.Length, word.Canonical);
                        if (seen.Add(key))
                        {
                            raw.Add(new RawCandidate(match.Index, match.Length, word.Canonical));
                        }
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
            }
        }

        if (raw.Count == 0)
        {
            return [];
        }

        raw.Sort(static (a, b) =>
        {
            var cmp = a.RangeStart.CompareTo(b.RangeStart);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = b.RangeLength.CompareTo(a.RangeLength);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Canonical, b.Canonical);
        });

        var count = Math.Min(raw.Count, maxSpots);
        var questions = new List<LearnedWordCheckQuestion>(count);
        for (var i = 0; i < count; i++)
        {
            var candidate = raw[i];
            var (contextStart, contextLength) = BuildContextWindow(
                text,
                candidate.RangeStart,
                candidate.RangeLength,
                MaxContextUtf16);
            questions.Add(new LearnedWordCheckQuestion(
                id: i + 1,
                sentence: text,
                rangeStart: candidate.RangeStart,
                rangeLength: candidate.RangeLength,
                contextStart: contextStart,
                contextLength: contextLength,
                word: candidate.Canonical));
        }

        return questions;
    }

    internal static (int ContextStart, int ContextLength) BuildContextWindow(
        string text,
        int rangeStart,
        int rangeLength,
        int maxContextUtf16 = MaxContextUtf16)
    {
        if (text.Length <= maxContextUtf16)
        {
            return (0, text.Length);
        }

        var rangeEnd = rangeStart + rangeLength;
        var spanLen = Math.Min(rangeLength, maxContextUtf16);
        var start = Math.Max(0, rangeStart - (maxContextUtf16 - spanLen) / 2);
        start = Math.Min(start, Math.Max(0, text.Length - maxContextUtf16));
        if (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        var alignedStart = start;
        while (alignedStart < rangeStart && alignedStart < text.Length && !char.IsWhiteSpace(text[alignedStart]))
        {
            if (alignedStart == 0 || char.IsWhiteSpace(text[alignedStart - 1]))
            {
                break;
            }

            alignedStart++;
        }

        while (alignedStart < rangeStart && alignedStart < text.Length && char.IsWhiteSpace(text[alignedStart]))
        {
            alignedStart++;
        }

        if (alignedStart <= rangeStart && text.Length - alignedStart >= spanLen)
        {
            start = alignedStart;
        }

        var end = Math.Min(text.Length, start + maxContextUtf16);
        if (end < rangeEnd)
        {
            start = Math.Max(0, rangeEnd - maxContextUtf16);
            if (start > 0 && char.IsLowSurrogate(text[start]))
            {
                start++;
            }

            end = Math.Min(text.Length, Math.Max(rangeEnd, start + maxContextUtf16));
        }

        if (end < text.Length && end > rangeEnd && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return (start, end - start);
    }

    private static bool ContainsAllSurfaceTokens(string text, string surface)
    {
        var span = surface.AsSpan();
        while (!span.IsEmpty)
        {
            var space = span.IndexOf(' ');
            var token = space < 0 ? span : span[..space];
            if (!token.IsEmpty &&
                text.AsSpan().IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (space < 0)
            {
                break;
            }

            span = span[(space + 1)..];
        }

        return true;
    }
}
