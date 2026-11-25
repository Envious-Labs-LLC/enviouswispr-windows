using EnviousWispr.Core.Settings;

namespace EnviousWispr.ASR;

/// <summary>Unicode script families enforced by Parakeet 25-language decoder pinning.</summary>
public enum ParakeetScript
{
    Latin,
    Cyrillic,
    Greek,
}

/// <summary>
/// Filters SentencePiece vocabulary tokens to a single writing script (Latin, Cyrillic, or Greek)
/// matching macOS <c>TokenLanguageFilter</c> for Parakeet TDT v3's 25 supported European languages.
/// </summary>
public static class TokenLanguageFilter
{
    /// <summary>Returns the target Parakeet writing script for a supported language code, or null for automatic/unsupported languages.</summary>
    public static ParakeetScript? ScriptForLanguage(string? languageCode)
    {
        if (!WhisperLanguageCodes.TryNormalize(languageCode, out var normalized) ||
            normalized == LanguageCatalog.AutomaticCode ||
            !LanguageCatalog.ParakeetSupportedCodes.Contains(normalized))
        {
            return null;
        }

        return normalized switch
        {
            "ru" or "uk" or "bg" => ParakeetScript.Cyrillic,
            "el" => ParakeetScript.Greek,
            _ => ParakeetScript.Latin,
        };
    }

    /// <summary>Checks whether a vocabulary token is compatible with the requested writing script.</summary>
    public static bool Matches(string token, ParakeetScript script)
    {
        ArgumentNullException.ThrowIfNull(token);

        var cleaned = token.Replace("\u2581", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return true;
        }

        foreach (var rune in cleaned.EnumerateRunes())
        {
            var scalar = (uint)rune.Value;
            var matchesScalar = script switch
            {
                ParakeetScript.Latin => IsLatinAllowed(scalar),
                ParakeetScript.Cyrillic => IsCyrillicAllowed(scalar),
                ParakeetScript.Greek => IsGreekAllowed(scalar),
                _ => false,
            };

            if (!matchesScalar)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLatinAllowed(uint scalar) =>
        scalar is (>= 0x0020 and <= 0x007F)
            or (>= 0x00A0 and <= 0x00FF)
            or (>= 0x0100 and <= 0x017F)
            or (>= 0x0180 and <= 0x024F)
            or (>= 0x0300 and <= 0x036F)
            or (>= 0x1E00 and <= 0x1EFF)
            || IsCommonCurrency(scalar);

    private static bool IsCyrillicAllowed(uint scalar) =>
        scalar is >= 0x0400 and <= 0x04FF || IsCommonNonLetterAscii(scalar) || IsCommonCurrency(scalar);

    private static bool IsGreekAllowed(uint scalar) =>
        scalar is (>= 0x0370 and <= 0x03FF)
            or (>= 0x1F00 and <= 0x1FFF)
            or (>= 0x0300 and <= 0x036F) || IsCommonNonLetterAscii(scalar) || IsCommonCurrency(scalar);

    private static bool IsCommonCurrency(uint scalar) =>
        scalar is 0x00A3 or 0x00A5 or 0x20AC;

    private static bool IsCommonNonLetterAscii(uint scalar) =>
        scalar is >= 0x0020 and <= 0x007F &&
        scalar is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));
}
