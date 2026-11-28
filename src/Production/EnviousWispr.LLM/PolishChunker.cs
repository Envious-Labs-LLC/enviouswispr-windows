namespace EnviousWispr.LLM;

internal readonly record struct PolishTextPiece(string Text, string SeparatorAfter);

internal static class PolishChunker
{
    public static IReadOnlyList<PolishTextPiece> Split(string text, int maxChunkCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxChunkCharacters, 0);

        if (text.Length <= maxChunkCharacters)
        {
            return [new PolishTextPiece(text, string.Empty)];
        }

        var chunks = new List<PolishTextPiece>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var remaining = text.Length - cursor;
            if (remaining <= maxChunkCharacters)
            {
                chunks.Add(new PolishTextPiece(text[cursor..], string.Empty));
                break;
            }

            var limit = cursor + maxChunkCharacters;
            var (splitEnd, nextCursor) = FindBestSplitBounds(text, cursor, limit);
            var separator = nextCursor > splitEnd ? text[splitEnd..nextCursor] : string.Empty;
            chunks.Add(new PolishTextPiece(text[cursor..splitEnd], separator));
            cursor = nextCursor;
        }

        return chunks;
    }

    private static (int SplitEnd, int NextCursor) FindBestSplitBounds(string text, int start, int limit)
    {
        var windowLength = limit - start;
        var minPreferred = start + (windowLength / 2);

        // 1. Prefer paragraph breaks (\n\n) so document structure stays intact.
        var paragraphBreak = text.LastIndexOf("\n\n", limit - 1, windowLength, StringComparison.Ordinal);
        if (paragraphBreak >= minPreferred)
        {
            return (paragraphBreak, paragraphBreak + 2);
        }

        var singleNewline = text.LastIndexOf('\n', limit - 1, windowLength);
        if (singleNewline >= minPreferred)
        {
            return (singleNewline, singleNewline + 1);
        }

        // 2. Prefer sentence boundaries (. ! ? followed by whitespace).
        for (var i = limit - 2; i >= minPreferred; i--)
        {
            if (text[i] is '.' or '!' or '?' && char.IsWhiteSpace(text[i + 1]))
            {
                var next = i + 1;
                while (next < limit && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                return (i + 1, next);
            }
        }

        // 3. Fall back to whitespace while preserving the exact whitespace run in SeparatorAfter.
        for (var i = limit - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                var next = i + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                var boundaryStart = i;
                while (boundaryStart > start && char.IsWhiteSpace(text[boundaryStart - 1]))
                {
                    boundaryStart--;
                }

                return (boundaryStart, next);
            }
        }

        // 4. Unbroken string without whitespace: hard split at limit.
        return (limit, limit);
    }
}
