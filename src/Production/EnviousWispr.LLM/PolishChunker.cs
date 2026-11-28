using System.Globalization;
using System.Text;

namespace EnviousWispr.LLM;

internal readonly record struct PolishTextPiece(string Text, string SeparatorAfter);

internal static class PolishChunker
{
    public const int MaximumCloudWordsPerChunk = 500;
    public const int MaximumLocalWordsPerChunk = 200;
    public const int MaximumUtf8BytesPerChunk = 7_424;

    private static readonly char[] SentenceEndings = ['.', '!', '?', '\u3002', '\uFF01', '\uFF1F', '\u0964'];

    public static int MaximumBytesForWords(int maxWords)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWords, 0);
        return Math.Max(4, MaximumUtf8BytesPerChunk * Math.Min(maxWords, MaximumCloudWordsPerChunk) / MaximumCloudWordsPerChunk);
    }

    public static IReadOnlyList<PolishTextPiece> Split(string text, int maxChunkCharacters) =>
        Split(text, maxChunkCharacters, maxWords: null, maxUtf8Bytes: null);

    public static IReadOnlyList<PolishTextPiece> Split(
        string text,
        int maxChunkCharacters,
        int? maxWords,
        int? maxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxChunkCharacters, 0);

        var resolvedMaxWords = maxWords ?? int.MaxValue;
        var resolvedMaxBytes = maxUtf8Bytes ?? int.MaxValue;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolvedMaxWords, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolvedMaxBytes, 0);

        return SplitCore(text, resolvedMaxWords, resolvedMaxBytes, maxChunkCharacters);
    }

    public static IReadOnlyList<PolishTextPiece> SplitByBudget(
        string text,
        int maxWords = MaximumCloudWordsPerChunk,
        int? maxUtf8Bytes = null,
        int? maxCharacters = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWords, 0);

        var resolvedMaxBytes = maxUtf8Bytes ?? MaximumBytesForWords(maxWords);
        var resolvedMaxChars = maxCharacters ?? int.MaxValue;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolvedMaxBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolvedMaxChars, 0);

        return SplitCore(text, maxWords, resolvedMaxBytes, resolvedMaxChars);
    }

    public static int CountWords(ReadOnlySpan<char> span)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in span)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    private static List<PolishTextPiece> SplitCore(
        string text,
        int maxWords,
        int maxUtf8Bytes,
        int maxCharacters)
    {
        if (text.Length == 0 || FitsBounds(text.AsSpan(), maxWords, maxUtf8Bytes, maxCharacters))
        {
            return [new PolishTextPiece(text, string.Empty)];
        }

        var sentences = FindSentenceRanges(text);
        var segments = new List<(int Start, int End)>(sentences.Count);
        var hasPending = false;
        var pendingStart = 0;
        var pendingEnd = 0;
        var pendingWords = 0;
        var pendingBytes = 0;

        void FlushPending()
        {
            if (!hasPending)
            {
                return;
            }

            var (trimmedStart, trimmedEnd) = TrimRange(text, pendingStart, pendingEnd);
            if (trimmedEnd > trimmedStart)
            {
                segments.Add((trimmedStart, trimmedEnd));
            }

            hasPending = false;
            pendingWords = 0;
            pendingBytes = 0;
        }

        foreach (var (rawStart, rawEnd) in sentences)
        {
            var (start, end) = TrimRange(text, rawStart, rawEnd);
            if (end <= start)
            {
                continue;
            }

            var span = text.AsSpan(start, end - start);
            var words = CountWords(span);
            if (words == 0)
            {
                continue;
            }

            var bytes = Encoding.UTF8.GetByteCount(span);
            var chars = span.Length;

            if (words > maxWords || bytes > maxUtf8Bytes || chars > maxCharacters)
            {
                FlushPending();
                AppendOverlongSentence(text, start, end, maxWords, maxUtf8Bytes, maxCharacters, segments);
                continue;
            }

            if (hasPending)
            {
                var combinedSpan = text.AsSpan(pendingStart, end - pendingStart);
                var gapBytes = Encoding.UTF8.GetByteCount(text.AsSpan(pendingEnd, start - pendingEnd));
                if (pendingWords + words > maxWords ||
                    pendingBytes + gapBytes + bytes > maxUtf8Bytes ||
                    combinedSpan.Length > maxCharacters)
                {
                    FlushPending();
                }
            }

            if (!hasPending)
            {
                hasPending = true;
                pendingStart = start;
                pendingEnd = end;
                pendingWords = words;
                pendingBytes = bytes;
            }
            else
            {
                var gapBytes = Encoding.UTF8.GetByteCount(text.AsSpan(pendingEnd, start - pendingEnd));
                pendingEnd = end;
                pendingWords += words;
                pendingBytes += gapBytes + bytes;
            }
        }

        FlushPending();

        if (segments.Count == 0)
        {
            return [new PolishTextPiece(text, string.Empty)];
        }

        return MaterializePieces(text, segments);
    }

    private static List<PolishTextPiece> MaterializePieces(
        string text,
        List<(int Start, int End)> segments)
    {
        var pieces = new List<PolishTextPiece>(segments.Count + 1);
        if (segments[0].Start > 0)
        {
            // Keep leading whitespace verbatim if the source started with whitespace.
            pieces.Add(new PolishTextPiece(string.Empty, text[..segments[0].Start]));
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            var nextStart = i + 1 < segments.Count ? segments[i + 1].Start : text.Length;
            var separator = nextStart > end ? text[end..nextStart] : string.Empty;
            pieces.Add(new PolishTextPiece(text[start..end], separator));
        }

        return pieces;
    }

    private static List<(int Start, int End)> FindSentenceRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\n')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    var split = i;
                    while (split < text.Length && text[split] == '\n')
                    {
                        split++;
                    }

                    ranges.Add((start, split));
                    start = split;
                    i = split - 1;
                    continue;
                }

                ranges.Add((start, i + 1));
                start = i + 1;
                continue;
            }

            if (Array.IndexOf(SentenceEndings, ch) >= 0)
            {
                var isSegmentedEnding = ch is '\u3002' or '\uFF01' or '\uFF1F' or '\u0964';
                if (isSegmentedEnding || i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                {
                    var end = i + 1;
                    while (end < text.Length && char.IsWhiteSpace(text[end]) && text[end] != '\n')
                    {
                        end++;
                    }

                    ranges.Add((start, end));
                    start = end;
                    i = end - 1;
                }
            }
        }

        if (start < text.Length)
        {
            ranges.Add((start, text.Length));
        }

        return ranges;
    }

    private static void AppendOverlongSentence(
        string text,
        int sentenceStart,
        int sentenceEnd,
        int maxWords,
        int maxUtf8Bytes,
        int maxCharacters,
        List<(int Start, int End)> segments)
    {
        var words = new List<(int Start, int End)>();
        var index = sentenceStart;
        while (index < sentenceEnd)
        {
            while (index < sentenceEnd && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= sentenceEnd)
            {
                break;
            }

            var wordStart = index;
            while (index < sentenceEnd && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            words.Add((wordStart, index));
        }

        var cursor = 0;
        while (cursor < words.Count)
        {
            var end = cursor;
            while (end < words.Count && (end - cursor) < maxWords)
            {
                var candidateSpan = text.AsSpan(words[cursor].Start, words[end].End - words[cursor].Start);
                var candidateBytes = Encoding.UTF8.GetByteCount(candidateSpan);
                if (end > cursor && (candidateBytes > maxUtf8Bytes || candidateSpan.Length > maxCharacters))
                {
                    break;
                }

                end++;
            }

            var pieceStart = words[cursor].Start;
            var pieceEnd = words[end - 1].End;
            var pieceSpan = text.AsSpan(pieceStart, pieceEnd - pieceStart);
            if (Encoding.UTF8.GetByteCount(pieceSpan) > maxUtf8Bytes || pieceSpan.Length > maxCharacters)
            {
                AppendSplitAtCharacterBoundaries(text, pieceStart, pieceEnd, maxUtf8Bytes, maxCharacters, segments);
            }
            else
            {
                segments.Add((pieceStart, pieceEnd));
            }

            cursor = end;
        }
    }

    private static void AppendSplitAtCharacterBoundaries(
        string text,
        int start,
        int end,
        int maxUtf8Bytes,
        int maxCharacters,
        List<(int Start, int End)> segments)
    {
        var segmentStart = start;
        var currentBytes = 0;
        var currentChars = 0;
        var pos = start;

        while (pos < end)
        {
            var elementLength = StringInfo.GetNextTextElementLength(text.AsSpan(pos, end - pos));
            if (elementLength <= 0)
            {
                elementLength = 1;
            }

            var elementBytes = Encoding.UTF8.GetByteCount(text.AsSpan(pos, elementLength));
            if (pos > segmentStart &&
                (currentBytes + elementBytes > maxUtf8Bytes || currentChars + elementLength > maxCharacters))
            {
                segments.Add((segmentStart, pos));
                segmentStart = pos;
                currentBytes = 0;
                currentChars = 0;
            }

            currentBytes += elementBytes;
            currentChars += elementLength;
            pos += elementLength;
        }

        if (pos > segmentStart)
        {
            segments.Add((segmentStart, pos));
        }
    }

    private static bool FitsBounds(ReadOnlySpan<char> span, int maxWords, int maxUtf8Bytes, int maxCharacters) =>
        span.Length <= maxCharacters &&
        CountWords(span) <= maxWords &&
        Encoding.UTF8.GetByteCount(span) <= maxUtf8Bytes;

    private static (int Start, int End) TrimRange(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end);
    }
}
