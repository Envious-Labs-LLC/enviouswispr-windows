namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Shape guard for repeat-learning a pair the judge once refused (#3975): only name-, brand-,
/// or alphanumeric-shaped terms may bypass the judge on a later repeat.
/// </summary>
public static class RepeatShapeGuard
{
    public static bool Passes(string corrected, int startToken, string pasted)
    {
        var atSentenceStart = IsSentenceStart(startToken, pasted);
        var tokens = EditAlignment.SplitWords(corrected);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Any(char.IsLetter) && token.Any(char.IsDigit))
            {
                return true;
            }

            var span = i == 0 && atSentenceStart && token.Length > 0
                ? token.AsSpan(1)
                : token.AsSpan();
            foreach (var ch in span)
            {
                if (char.IsUpper(ch))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsSentenceStart(int tokenIndex, string text)
    {
        if (tokenIndex < 0 || string.IsNullOrEmpty(text))
        {
            return false;
        }

        var index = 0;
        ReadOnlySpan<char> previous = default;
        var hasPrevious = false;
        var sawNewline = false;
        var i = 0;

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (text[i] is '\r' or '\n')
                {
                    sawNewline = true;
                }

                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (index == tokenIndex)
            {
                if (!hasPrevious)
                {
                    return true;
                }

                if (sawNewline)
                {
                    return true;
                }

                for (var k = previous.Length - 1; k >= 0; k--)
                {
                    var ch = previous[k];
                    if (ch is '"' or '\'' or '\u201D' or '\u2019' or '\u00BB' or ')' or ']' or '}')
                    {
                        continue;
                    }

                    return ch is '.' or '!' or '?' or '\u2026';
                }

                return false;
            }

            previous = text.AsSpan(start, i - start);
            hasPrevious = true;
            sawNewline = false;
            index++;
        }

        return false;
    }
}
