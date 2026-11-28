using System.Globalization;
using System.Text;

namespace EnviousWispr.LLM;

/// <summary>
/// Deterministic repair for sentence-initial discourse markers dropped by local polish models (ref: macOS #963).
/// </summary>
public static class LeadingMarkerRepair
{
    private static readonly Dictionary<string, bool> Markers =
        new(StringComparer.Ordinal)
        {
            ["actually"] = true,
            ["basically"] = true,
            ["honestly"] = true,
            ["well"] = true,
            ["overall"] = true,
            ["literally"] = false,
        };

    private static readonly HashSet<string> KeepCapitalized = new(StringComparer.Ordinal)
    {
        "i", "im", "ill", "ive", "id",
    };

    private static readonly HashSet<string> CorrectionCues = new(StringComparer.Ordinal)
    {
        "no", "wait", "sorry",
    };

    /// <summary>
    /// Restores a deleted leading discourse marker when <paramref name="input"/> begins with one
    /// and <paramref name="output"/> dropped it without collapsing an immediate self-correction.
    /// </summary>
    public static string Repair(string input, string output, string? expectedLanguage)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var language = NormalizeBaseLanguage(expectedLanguage);
        if (language is not null && !string.Equals(language, "en", StringComparison.Ordinal))
        {
            return output;
        }

        var trimmedOutput = output.Trim();
        if (trimmedOutput.Length == 0)
        {
            return output;
        }

        var inputTokens = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (inputTokens.Length < 2)
        {
            return output;
        }

        var firstInputNormalized = NormalizeToken(inputTokens[0]);
        if (!Markers.TryGetValue(firstInputNormalized, out var needsComma))
        {
            return output;
        }

        var outputTokens = trimmedOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (outputTokens.Length == 0)
        {
            return output;
        }

        var firstOutputToken = outputTokens[0];
        var firstOutputNormalized = NormalizeToken(firstOutputToken);
        if (string.Equals(firstOutputNormalized, firstInputNormalized, StringComparison.Ordinal))
        {
            return output;
        }

        if (CountNormalizedToken(outputTokens, firstInputNormalized) >= CountNormalizedToken(inputTokens, firstInputNormalized))
        {
            return output;
        }

        var secondInputNormalized = NormalizeToken(inputTokens[1]);
        if (CorrectionCues.Contains(secondInputNormalized) &&
            !string.Equals(firstOutputNormalized, secondInputNormalized, StringComparison.Ordinal))
        {
            return output;
        }

        var capitalizedMarker = string.Concat(
            char.ToUpperInvariant(firstInputNormalized[0]).ToString(),
            firstInputNormalized.AsSpan(1));
        var separator = needsComma ? ", " : " ";

        var body = trimmedOutput;
        if (!KeepCapitalized.Contains(firstOutputNormalized) &&
            string.Equals(firstOutputNormalized, secondInputNormalized, StringComparison.Ordinal) &&
            RestHasNoUppercase(firstOutputToken) &&
            StartsWithUppercaseRune(body, out var firstRuneLength, out var lowerFirst))
        {
            body = lowerFirst + body[firstRuneLength..];
        }

        return string.Concat(capitalizedMarker, separator, body);
    }

    private static int CountNormalizedToken(string[] tokens, string targetNormalized)
    {
        var count = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(NormalizeToken(tokens[i]), targetNormalized, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static string? NormalizeBaseLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var trimmed = language.Trim().ToLowerInvariant();
        if (string.Equals(trimmed, "auto", StringComparison.Ordinal))
        {
            return null;
        }

        var separator = trimmed.IndexOfAny(['-', '_']);
        return separator > 0 ? trimmed[..separator] : trimmed;
    }

    private static string NormalizeToken(string token)
    {
        var stripped = token
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("\u2019", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        var start = 0;
        while (start < stripped.Length && char.IsPunctuation(stripped[start]))
        {
            start++;
        }

        var end = stripped.Length;
        while (end > start && char.IsPunctuation(stripped[end - 1]))
        {
            end--;
        }

        return stripped[start..end];
    }

    private static bool RestHasNoUppercase(string token)
    {
        var enumerator = token.EnumerateRunes();
        if (!enumerator.MoveNext())
        {
            return true;
        }

        while (enumerator.MoveNext())
        {
            if (Rune.GetUnicodeCategory(enumerator.Current) is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter)
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithUppercaseRune(string value, out int utf16Length, out string lower)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            utf16Length = rune.Utf16SequenceLength;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter)
            {
                lower = Rune.ToLowerInvariant(rune).ToString();
                return true;
            }

            lower = string.Empty;
            return false;
        }

        utf16Length = 0;
        lower = string.Empty;
        return false;
    }
}
