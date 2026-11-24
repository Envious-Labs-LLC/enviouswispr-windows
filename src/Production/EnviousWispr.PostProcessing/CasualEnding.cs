using System.Text.RegularExpressions;

namespace EnviousWispr.PostProcessing;

/// <summary>
/// Casual mode: leave off the full stop at the very end of a dictation. Ref: macOS #3737.
/// </summary>
public static partial class CasualEnding
{
    private static readonly HashSet<char> FullStops = ['.', '\u3002', '\u0964'];

    private static readonly HashSet<char> LeadingOpeners = ['(', '[', '"', '\'', '\u201C', '\u2018'];

    [GeneratedRegex(@"^(?:\p{L}{1,2}\.){2,}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DottedAbbreviationRegex();

    /// <summary>
    /// Removes the final full stop if it meets the Casual Mode contract; otherwise returns the input unchanged.
    /// </summary>
    /// <param name="text">The text to inspect.</param>
    /// <param name="spokenFinalFullStop">True if ITN recognized an explicit spoken full stop at the end.</param>
    /// <param name="languageCode">Language code of the take (e.g. "en", "de-DE", "en-GB").</param>
    public static string RemoveFinalFullStop(string text, bool spokenFinalFullStop, string? languageCode)
    {
        if (spokenFinalFullStop || string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Find last non-whitespace character
        var lastIndex = -1;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                lastIndex = i;
                break;
            }
        }

        if (lastIndex < 0)
        {
            return text;
        }

        var mark = text[lastIndex];
        if (!FullStops.Contains(mark))
        {
            return text;
        }

        // Keep if '.' is preceded by '.' (dot run / ellipsis ...)
        if (lastIndex > 0 && text[lastIndex - 1] == '.')
        {
            return text;
        }

        if (mark == '.')
        {
            // Keep if '.' is preceded by a digit and base language != "en"
            if (lastIndex > 0 && char.IsDigit(text[lastIndex - 1]))
            {
                var baseLang = GetBaseLanguage(languageCode);
                if (!string.Equals(baseLang, "en", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
            }

            // Keep if final whitespace-delimited token matches ^(?:\p{L}{1,2}\.){2,}$
            var finalWord = ExtractFinalWord(text, lastIndex);
            if (DottedAbbreviationRegex().IsMatch(finalWord))
            {
                return text;
            }
        }

        var before = text[..lastIndex];
        var after = text[(lastIndex + 1)..];
        var remaining = string.Concat(before, after);

        // Keep if removing it would leave only whitespace
        if (string.IsNullOrWhiteSpace(remaining))
        {
            return text;
        }

        return remaining;
    }

    private static string? GetBaseLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        var trimmed = languageCode.Trim();
        var separatorIndex = trimmed.IndexOfAny(['-', '_']);
        return (separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed).ToLowerInvariant();
    }

    private static string ExtractFinalWord(string text, int lastIndex)
    {
        var startIndex = 0;
        for (var i = lastIndex; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                startIndex = i + 1;
                break;
            }
        }

        var word = text.Substring(startIndex, lastIndex + 1 - startIndex);
        var openerEnd = 0;
        while (openerEnd < word.Length && LeadingOpeners.Contains(word[openerEnd]))
        {
            openerEnd++;
        }

        return openerEnd > 0 ? word[openerEnd..] : word;
    }
}
