using System.Buffers;
using System.Globalization;
using System.Text;

namespace EnviousWispr.PostProcessing;

internal static class TextSimilarity
{
    public static double LevenshteinSimilarity(string left, string right)
    {
        var a = ToRuneArray(left);
        var b = ToRuneArray(right);
        return LevenshteinSimilarity(a.AsSpan(), b.AsSpan());
    }

    internal static double LevenshteinSimilarity(
        ReadOnlySpan<Rune> a,
        ReadOnlySpan<Rune> b,
        int maxAllowedDistance = int.MaxValue)
    {
        if (a.Length == 0)
        {
            return b.Length == 0 ? 1 : 0;
        }

        var maxLength = Math.Max(a.Length, b.Length);
        if (Math.Abs(a.Length - b.Length) > maxAllowedDistance)
        {
            return 0.0;
        }

        if (b.Length == 0)
        {
            return 0.0;
        }

        var rowLength = b.Length + 1;
        int[]? rented = rowLength > 128 ? ArrayPool<int>.Shared.Rent(rowLength * 2) : null;
        try
        {
            Span<int> buffer = rented is null
                ? stackalloc int[256]
                : rented.AsSpan(0, rowLength * 2);
            var previous = buffer[..rowLength];
            var current = buffer.Slice(rowLength, rowLength);

            for (var j = 0; j < rowLength; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var rowMin = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1]
                        ? previous[j - 1]
                        : 1 + Math.Min(previous[j - 1], Math.Min(previous[j], current[j - 1]));
                    current[j] = cost;
                    if (cost < rowMin)
                    {
                        rowMin = cost;
                    }
                }

                if (rowMin > maxAllowedDistance)
                {
                    return 0.0;
                }

                var temp = previous;
                previous = current;
                current = temp;
            }

            var distance = previous[b.Length];
            return distance > maxAllowedDistance
                ? 0.0
                : 1 - ((double)distance / maxLength);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    internal static Rune[] ToRuneArray(string value) =>
        value.EnumerateRunes().ToArray();

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

    public static double DiceSimilarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1.0;
        }

        var a = left.EnumerateRunes().ToArray();
        var b = right.EnumerateRunes().ToArray();
        if (a.Length < 2 || b.Length < 2)
        {
            return 0.0;
        }

        var bigramsA = new Dictionary<(int, int), int>(a.Length - 1);
        for (var i = 0; i < a.Length - 1; i++)
        {
            var pair = (a[i].Value, a[i + 1].Value);
            bigramsA[pair] = bigramsA.TryGetValue(pair, out var count) ? count + 1 : 1;
        }

        var intersection = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var pair = (b[i].Value, b[i + 1].Value);
            if (bigramsA.TryGetValue(pair, out var remaining) && remaining > 0)
            {
                intersection++;
                bigramsA[pair] = remaining - 1;
            }
        }

        return (2.0 * intersection) / ((a.Length - 1) + (b.Length - 1));
    }

    /// <summary>Composite 0.40 Levenshtein + 0.40 bigram Dice + 0.20 Soundex score matching macOS WordCorrector.score.</summary>
    public static double CompositeWordScore(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0.0;
        }

        var lev = LevenshteinSimilarity(left, right);
        var dice = DiceSimilarity(left, right);
        var soundexMatch = string.Equals(Soundex(left), Soundex(right), StringComparison.Ordinal) ? 1.0 : 0.0;
        return (0.40 * lev) + (0.40 * dice) + (0.20 * soundexMatch);
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
