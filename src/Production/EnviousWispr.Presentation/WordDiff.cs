using System.Globalization;
using System.Text;

namespace EnviousWispr.Presentation;

public enum WordDiffKind
{
    Equal,
    Delete,
    Insert,
}

public readonly record struct WordDiffEntry(WordDiffKind Kind, string Text);

public readonly record struct WordDiffToken(string Surface, string Trailing, string Key)
{
    public string FullText => Surface + Trailing;
}

public static class WordDiff
{
    private const int Unreachable = -1;

    /// <summary>
    /// Splits text into whitespace-terminated tokens, preserving trailing whitespace on each token.
    /// Any leading whitespace before the first token is ignored so diffs align cleanly on words.
    /// </summary>
    public static IReadOnlyList<WordDiffToken> Tokenize(string? text, CultureInfo? culture = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<WordDiffToken>();
        }

        var tokens = new List<WordDiffToken>();
        var i = 0;
        var len = text.Length;

        while (i < len && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        while (i < len)
        {
            var surfaceStart = i;
            while (i < len && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var surfaceEnd = i;
            while (i < len && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var surface = text[surfaceStart..surfaceEnd];
            var trailing = i > surfaceEnd ? text[surfaceEnd..i] : string.Empty;
            var key = KeyFor(surface, culture);
            tokens.Add(new WordDiffToken(surface, trailing, key));
        }

        return tokens;
    }

    /// <summary>
    /// Computes the comparison key for a token by trimming outer sentence punctuation and case-folding,
    /// while preserving diacritics and meaningful symbols/markers such as $, +, #, %, and @.
    /// Falls back to the original surface if trimming leaves an empty string.
    /// </summary>
    public static string KeyFor(string surface, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(surface))
        {
            return string.Empty;
        }

        var start = 0;
        var end = surface.Length - 1;
        while (start <= end && IsOuterSentencePunctuation(surface[start]))
        {
            start++;
        }

        while (end >= start && IsOuterSentencePunctuation(surface[end]))
        {
            end--;
        }

        var core = start <= end ? surface[start..(end + 1)] : surface;
        var targetCulture = culture ?? CultureInfo.InvariantCulture;
        var folded = core.ToLower(targetCulture);
        if (folded.Contains('\u0130'))
        {
            folded = folded.Replace("\u0130", "i", StringComparison.Ordinal);
        }

        if (folded.Contains("i\u0307", StringComparison.Ordinal))
        {
            folded = folded.Replace("i\u0307", "i", StringComparison.Ordinal);
        }

        return folded.Normalize(NormalizationForm.FormC);
    }

    private static bool IsOuterSentencePunctuation(char c) =>
        char.IsPunctuation(c) && c is not ('#' or '%' or '@' or '&');

    /// <summary>
    /// Computes minimal word-level edit entries turning <paramref name="original"/> into <paramref name="edited"/>.
    /// </summary>
    public static IReadOnlyList<WordDiffEntry> Compute(string? original, string? edited, CultureInfo? culture = null)
    {
        var origTokens = Tokenize(original, culture);
        var editTokens = Tokenize(edited, culture);
        return Compute(origTokens, editTokens);
    }

    /// <summary>
    /// Computes minimal word-level edit entries over pre-tokenized original and edited sequences.
    /// Equal spans emit the edited surface/trailing text so punctuation and casing polish applies cleanly.
    /// </summary>
    public static IReadOnlyList<WordDiffEntry> Compute(
        IReadOnlyList<WordDiffToken> originalTokens,
        IReadOnlyList<WordDiffToken> editedTokens)
    {
        ArgumentNullException.ThrowIfNull(originalTokens);
        ArgumentNullException.ThrowIfNull(editedTokens);

        if (originalTokens.Count == 0 && editedTokens.Count == 0)
        {
            return Array.Empty<WordDiffEntry>();
        }

        var entries = new List<WordDiffEntry>();
        DiffRange(originalTokens, 0, originalTokens.Count, editedTokens, 0, editedTokens.Count, entries);
        return entries;
    }

    private static void DiffRange(
        IReadOnlyList<WordDiffToken> a,
        int aStart,
        int aEnd,
        IReadOnlyList<WordDiffToken> b,
        int bStart,
        int bEnd,
        List<WordDiffEntry> output)
    {
        var prefixLen = 0;
        while (aStart + prefixLen < aEnd
            && bStart + prefixLen < bEnd
            && string.Equals(a[aStart + prefixLen].Key, b[bStart + prefixLen].Key, StringComparison.Ordinal))
        {
            prefixLen++;
        }

        for (var i = 0; i < prefixLen; i++)
        {
            AppendEqual(output, b[bStart + i].FullText);
        }

        aStart += prefixLen;
        bStart += prefixLen;

        var suffixLen = 0;
        while (aEnd - suffixLen > aStart
            && bEnd - suffixLen > bStart
            && string.Equals(a[aEnd - 1 - suffixLen].Key, b[bEnd - 1 - suffixLen].Key, StringComparison.Ordinal))
        {
            suffixLen++;
        }

        var coreAEnd = aEnd - suffixLen;
        var coreBEnd = bEnd - suffixLen;

        var n = coreAEnd - aStart;
        var m = coreBEnd - bStart;

        if (n == 0)
        {
            for (var j = bStart; j < coreBEnd; j++)
            {
                AppendEntry(output, WordDiffKind.Insert, b[j].FullText);
            }
        }
        else if (m == 0)
        {
            for (var i = aStart; i < coreAEnd; i++)
            {
                AppendEntry(output, WordDiffKind.Delete, a[i].FullText);
            }
        }
        else
        {
            var snake = FindMiddleSnake(a, aStart, coreAEnd, b, bStart, coreBEnd);

            DiffRange(a, aStart, aStart + snake.StartX, b, bStart, bStart + snake.StartY, output);

            for (var k = snake.StartY; k < snake.EndY; k++)
            {
                AppendEqual(output, b[bStart + k].FullText);
            }

            DiffRange(a, aStart + snake.EndX, coreAEnd, b, bStart + snake.EndY, coreBEnd, output);
        }

        for (var i = 0; i < suffixLen; i++)
        {
            AppendEqual(output, b[coreBEnd + i].FullText);
        }
    }

    private static void AppendEqual(List<WordDiffEntry> output, string text) =>
        AppendEntry(output, WordDiffKind.Equal, text);

    private static void AppendEntry(List<WordDiffEntry> output, WordDiffKind kind, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (output.Count > 0 && output[^1].Kind == kind)
        {
            output[^1] = new WordDiffEntry(kind, output[^1].Text + text);
            return;
        }

        output.Add(new WordDiffEntry(kind, text));
    }

    private readonly record struct MiddleSnake(int StartX, int StartY, int EndX, int EndY);

    private static MiddleSnake FindMiddleSnake(
        IReadOnlyList<WordDiffToken> a,
        int aStart,
        int aEnd,
        IReadOnlyList<WordDiffToken> b,
        int bStart,
        int bEnd)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        var delta = n - m;
        var isOdd = (delta & 1) != 0;
        var maxD = (n + m + 1) / 2;
        var offset = maxD + 1;
        var arraySize = 2 * offset + 1;

        var vf = new int[arraySize];
        var vb = new int[arraySize];
        Array.Fill(vf, Unreachable);
        Array.Fill(vb, Unreachable);

        // d == 0 pass: advance initial snakes from (0, 0) forward and (n, m) backward.
        var fwd0 = 0;
        while (fwd0 < n
            && fwd0 < m
            && string.Equals(a[aStart + fwd0].Key, b[bStart + fwd0].Key, StringComparison.Ordinal))
        {
            fwd0++;
        }

        vf[offset] = fwd0;

        var back0 = 0;
        while (back0 < n
            && back0 < m
            && string.Equals(a[aEnd - 1 - back0].Key, b[bEnd - 1 - back0].Key, StringComparison.Ordinal))
        {
            back0++;
        }

        vb[offset] = back0;

        if (!isOdd && delta == 0 && fwd0 >= n - back0)
        {
            return new MiddleSnake(0, 0, fwd0, fwd0);
        }

        for (var d = 1; d <= maxD; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var xStart = ChooseForwardX(vf, offset, d, k, n, m);
                if (xStart == Unreachable)
                {
                    vf[k + offset] = Unreachable;
                    continue;
                }

                var yStart = xStart - k;
                var x = xStart;
                var y = yStart;
                while (x < n
                    && y < m
                    && string.Equals(a[aStart + x].Key, b[bStart + y].Key, StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                vf[k + offset] = x;

                if (isOdd)
                {
                    var c = delta - k;
                    if (c >= -(d - 1) && c <= d - 1)
                    {
                        var backU = vb[c + offset];
                        if (backU != Unreachable && x >= n - backU)
                        {
                            return new MiddleSnake(xStart, yStart, x, y);
                        }
                    }
                }
            }

            for (var c = -d; c <= d; c += 2)
            {
                var uStart = ChooseForwardX(vb, offset, d, c, n, m);
                if (uStart == Unreachable)
                {
                    vb[c + offset] = Unreachable;
                    continue;
                }

                var vStart = uStart - c;
                var u = uStart;
                var v = vStart;
                while (u < n
                    && v < m
                    && string.Equals(a[aEnd - 1 - u].Key, b[bEnd - 1 - v].Key, StringComparison.Ordinal))
                {
                    u++;
                    v++;
                }

                vb[c + offset] = u;

                if (!isOdd)
                {
                    var k = delta - c;
                    if (k >= -d && k <= d)
                    {
                        var fwdX = vf[k + offset];
                        if (fwdX != Unreachable && fwdX >= n - u)
                        {
                            return new MiddleSnake(n - u, m - v, n - uStart, m - vStart);
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException("Myers diff failed to locate a middle snake within bounded edit distance.");
    }

    private static int ChooseForwardX(int[] v, int offset, int d, int k, int n, int m)
    {
        var downCandidate = Unreachable;
        if (k != d)
        {
            var prev = v[k + 1 + offset];
            if (prev != Unreachable)
            {
                var yDown = prev - k;
                if (prev <= n && yDown >= 0 && yDown <= m)
                {
                    downCandidate = prev;
                }
            }
        }

        var rightCandidate = Unreachable;
        if (k != -d)
        {
            var prev = v[k - 1 + offset];
            if (prev != Unreachable)
            {
                var nextX = prev + 1;
                var yRight = nextX - k;
                if (nextX <= n && yRight >= 0 && yRight <= m)
                {
                    rightCandidate = nextX;
                }
            }
        }

        if (downCandidate == Unreachable)
        {
            return rightCandidate;
        }

        if (rightCandidate == Unreachable)
        {
            return downCandidate;
        }

        return rightCandidate > downCandidate ? rightCandidate : downCandidate;
    }
}
