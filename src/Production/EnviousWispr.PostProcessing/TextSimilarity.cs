using System.Globalization;
using System.Text;

namespace EnviousWispr.PostProcessing;

internal static class TextSimilarity
{
    public static double LevenshteinSimilarity(string left, string right)
    {
        var a = left.EnumerateRunes().ToArray();
        var b = right.EnumerateRunes().ToArray();
        if (a.Length == 0)
        {
            return b.Length == 0 ? 1 : 0;
        }

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = a[i - 1] == b[j - 1]
                    ? previous[j - 1]
                    : 1 + Math.Min(previous[j - 1], Math.Min(previous[j], current[j - 1]));
            }

            (previous, current) = (current, previous);
        }

        return 1 - ((double)previous[b.Length] / Math.Max(a.Length, b.Length));
    }

    public static string Soundex(string value)
    {
        var letters = value.Normalize(NormalizationForm.FormD)
            .Where(character => char.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetter)
            .Select(char.ToLowerInvariant)
            .ToArray();
        if (letters.Length == 0)
        {
            return "0000";
        }

        var result = new StringBuilder(4).Append(char.ToUpperInvariant(letters[0]));
        var previous = SoundexDigit(letters[0]);
        foreach (var character in letters.AsSpan(1))
        {
            var digit = SoundexDigit(character);
            if (digit != '0' && digit != previous)
            {
                result.Append(digit);
                if (result.Length == 4)
                {
                    break;
                }
            }

            previous = digit;
        }

        while (result.Length < 4)
        {
            result.Append('0');
        }

        return result.ToString();
    }

    private static char SoundexDigit(char value) => value switch
    {
        'b' or 'f' or 'p' or 'v' => '1',
        'c' or 'g' or 'j' or 'k' or 'q' or 's' or 'x' or 'z' => '2',
        'd' or 't' => '3',
        'l' => '4',
        'm' or 'n' => '5',
        'r' => '6',
        _ => '0',
    };
}
