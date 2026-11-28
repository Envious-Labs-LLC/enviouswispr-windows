using System.Globalization;
using System.Text;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.PostProcessing;

/// <summary>Language grammar rules for seam continuation casing.</summary>
public sealed record SeamCasingPolicy(
    bool NounVeto,
    IReadOnlySet<string> PoliteForms);

public enum SeamCasingVerdictKind
{
    MayLower,
    Keep,
    Unavailable,
}

public readonly record struct SeamCasingVerdict(
    SeamCasingVerdictKind Kind,
    CaseRepairDecision Decision)
{
    public static SeamCasingVerdict MayLower => new(SeamCasingVerdictKind.MayLower, CaseRepairDecision.LowercasedFirst);

    public static SeamCasingVerdict Keep(CaseRepairDecision decision) => new(SeamCasingVerdictKind.Keep, decision);

    public static SeamCasingVerdict Unavailable(CaseRepairDecision decision) => new(SeamCasingVerdictKind.Unavailable, decision);
}

public interface ISeamCasingOracle
{
    bool SupportsLanguage(string? languageCode);

    SeamCasingPolicy? PolicyFor(string? languageCode);

    SeamCasingVerdict Evaluate(string word, string leftContext, string payload, string? languageCode);
}

/// <summary>Deterministic 12-language continuation casing policy, guards, and locale helpers.</summary>
public static class SeamCasingPolicies
{
    public static readonly char[] EdgeTrimChars = ['.', '!', '?', ',', ';', ':'];

    private static readonly IReadOnlySet<string> EmptyPoliteForms =
        new HashSet<string>(StringComparer.Ordinal);

    private static readonly SeamCasingPolicy PlainPolicy = new(NounVeto: false, EmptyPoliteForms);

    private static readonly Dictionary<string, SeamCasingPolicy> Policies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = PlainPolicy,
            ["fr"] = PlainPolicy,
            ["it"] = PlainPolicy,
            ["es"] = PlainPolicy,
            ["pt"] = PlainPolicy,
            ["nl"] = PlainPolicy,
            ["da"] = PlainPolicy,
            ["fi"] = PlainPolicy,
            ["ru"] = PlainPolicy,
            ["tr"] = PlainPolicy,
            ["de"] = new(
                NounVeto: true,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "sie", "ihr", "ihnen", "ihre", "ihrer", "ihres", "ihrem", "ihren",
                }),
            ["sv"] = new(
                NounVeto: false,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "ni", "er", "ert", "era",
                }),
        };

    private static readonly HashSet<string> AlwaysCapitalizedEn =
        new(StringComparer.Ordinal)
        {
            "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
            "January", "February", "March", "April", "May", "June", "July", "August",
            "September", "October", "November", "December",
        };

    private static readonly HashSet<string> PronounIEn =
        new(StringComparer.Ordinal)
        {
            "I", "I'm", "I've", "I'll", "I'd",
        };

    private static readonly Dictionary<string, CultureInfo> CultureByLanguage =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tr"] = CultureInfo.GetCultureInfo("tr-TR"),
            ["de"] = CultureInfo.GetCultureInfo("de-DE"),
            ["nl"] = CultureInfo.GetCultureInfo("nl-NL"),
            ["fr"] = CultureInfo.GetCultureInfo("fr-FR"),
            ["it"] = CultureInfo.GetCultureInfo("it-IT"),
            ["es"] = CultureInfo.GetCultureInfo("es-ES"),
            ["pt"] = CultureInfo.GetCultureInfo("pt-BR"),
            ["da"] = CultureInfo.GetCultureInfo("da-DK"),
            ["sv"] = CultureInfo.GetCultureInfo("sv-SE"),
            ["fi"] = CultureInfo.GetCultureInfo("fi-FI"),
            ["ru"] = CultureInfo.GetCultureInfo("ru-RU"),
            ["en"] = CultureInfo.GetCultureInfo("en-US"),
        };

    public static IReadOnlyCollection<string> SupportedLanguages { get; } =
        ["en", "de", "fr", "it", "es", "pt", "nl", "da", "sv", "fi", "ru", "tr"];

    public static string? NormalizeBaseCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        var trimmed = languageCode.Trim();
        var separator = trimmed.IndexOfAny(['-', '_']);
        var baseCode = (separator < 0 ? trimmed : trimmed[..separator]).ToLowerInvariant();
        return baseCode.Length == 0 ? null : baseCode;
    }

    public static SeamCasingPolicy? PolicyFor(string? languageCode)
    {
        var baseCode = NormalizeBaseCode(languageCode);
        return baseCode is not null && Policies.TryGetValue(baseCode, out var policy)
            ? policy
            : null;
    }

    public static CultureInfo CasingCulture(string? languageCode)
    {
        var baseCode = NormalizeBaseCode(languageCode);
        return baseCode is not null && CultureByLanguage.TryGetValue(baseCode, out var culture)
            ? culture
            : CultureInfo.GetCultureInfo("en-US");
    }

    public static bool IsDutchIJDigraph(string bareWord, string? languageCode)
    {
        var baseCode = NormalizeBaseCode(languageCode);
        if (!string.Equals(baseCode, "nl", StringComparison.Ordinal) || bareWord.Length < 2)
        {
            return false;
        }

        if (bareWord[0] != 'I' || bareWord[1] != 'J')
        {
            return false;
        }

        for (var i = 2; i < bareWord.Length; i++)
        {
            if (char.IsUpper(bareWord[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static string ToLowerLocale(string word, string? languageCode)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        var baseCode = NormalizeBaseCode(languageCode);
        var culture = CasingCulture(baseCode);
        if (string.Equals(baseCode, "nl", StringComparison.Ordinal) &&
            word.Length >= 2 &&
            word[0] == 'I' &&
            word[1] == 'J')
        {
            return "ij" + word[2..].ToLower(culture);
        }

        return word.ToLower(culture);
    }

    public static string ExtractBareFirstWord(string strippedText)
    {
        if (string.IsNullOrEmpty(strippedText))
        {
            return string.Empty;
        }

        var span = strippedText.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]))
        {
            end++;
        }

        return span[..end].Trim(EdgeTrimChars).ToString();
    }

    public static CaseRepairDecision? CheckDeterministicGuard(
        string strippedText,
        string bareWord,
        string? languageCode,
        IReadOnlySet<string>? protectedWords)
    {
        var policy = PolicyFor(languageCode);
        if (policy is null)
        {
            return CaseRepairDecision.SkippedLanguageNotSupported;
        }

        if (string.IsNullOrEmpty(strippedText))
        {
            return CaseRepairDecision.AlreadyLower;
        }

        var firstRune = Rune.GetRuneAt(strippedText, 0);
        if (!Rune.IsUpper(firstRune))
        {
            return CaseRepairDecision.AlreadyLower;
        }

        if (protectedWords is not null &&
            StartsWithProtectedSpelling(strippedText, bareWord, protectedWords))
        {
            return CaseRepairDecision.SkippedProtectedWord;
        }

        if (string.IsNullOrEmpty(bareWord))
        {
            return CaseRepairDecision.AlreadyLower;
        }

        var hasInternalUpper = false;
        var firstSkipped = false;
        foreach (var rune in bareWord.EnumerateRunes())
        {
            if (!firstSkipped)
            {
                firstSkipped = true;
                continue;
            }

            if (Rune.IsUpper(rune))
            {
                hasInternalUpper = true;
                break;
            }
        }

        var baseCode = NormalizeBaseCode(languageCode);
        if (hasInternalUpper && !IsDutchIJDigraph(bareWord, baseCode))
        {
            return CaseRepairDecision.SkippedMixedCaseOrAcronym;
        }

        foreach (var rune in bareWord.EnumerateRunes())
        {
            if (Rune.IsDigit(rune))
            {
                return CaseRepairDecision.SkippedContainsDigit;
            }
        }

        if (string.Equals(baseCode, "en", StringComparison.Ordinal))
        {
            var normalizedPronoun = bareWord.Replace('\u2019', '\'');
            if (PronounIEn.Contains(normalizedPronoun))
            {
                return CaseRepairDecision.SkippedPronounI;
            }

            if (AlwaysCapitalizedEn.Contains(bareWord))
            {
                return CaseRepairDecision.SkippedAlwaysCapitalized;
            }
        }

        if (policy.PoliteForms.Count > 0 && policy.PoliteForms.Contains(bareWord.ToLowerInvariant()))
        {
            return CaseRepairDecision.SkippedPoliteForm;
        }

        return null;
    }

    public static bool StartsWithProtectedSpelling(
        string strippedText,
        string bareWord,
        IReadOnlySet<string> protectedWords)
    {
        if (protectedWords.Count == 0 || string.IsNullOrEmpty(strippedText))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(bareWord) && protectedWords.Contains(bareWord))
        {
            return true;
        }

        foreach (var spelling in protectedWords)
        {
            if (string.IsNullOrEmpty(spelling) || strippedText.Length < spelling.Length)
            {
                continue;
            }

            if (!strippedText.StartsWith(spelling, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (strippedText.Length == spelling.Length)
            {
                return true;
            }

            var next = Rune.GetRuneAt(strippedText, spelling.Length);
            if (Rune.IsWhiteSpace(next) || !Rune.IsLetterOrDigit(next))
            {
                return true;
            }
        }

        return false;
    }

    public static string LoweringLeadingUnit(string text, string? languageCode)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var index = 0;
        while (index < text.Length)
        {
            var rune = Rune.GetRuneAt(text, index);
            if (!Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                break;
            }

            index += rune.Utf16SequenceLength;
        }

        if (index >= text.Length)
        {
            return text;
        }

        var baseCode = NormalizeBaseCode(languageCode);
        var leading = text[..index];
        var stripped = text.AsSpan(index);

        if (string.Equals(baseCode, "nl", StringComparison.Ordinal) &&
            stripped.Length >= 2 &&
            stripped[0] == 'I' &&
            stripped[1] == 'J')
        {
            return string.Concat(leading, "ij", stripped[2..]);
        }

        var firstRune = Rune.GetRuneAt(text, index);
        var unitLength = firstRune.Utf16SequenceLength;
        var culture = CasingCulture(baseCode);
        var lowered = text.Substring(index, unitLength).ToLower(culture);
        return string.Concat(leading, lowered, text.AsSpan(index + unitLength));
    }
}

/// <summary>Delegate-backed <see cref="ISeamCasingOracle"/> value separating dictionary/context queries for clean testing.</summary>
public sealed class SeamCasingOracle : ISeamCasingOracle
{
    public enum DictionaryVerdict
    {
        Ordinary,
        NotOrdinary,
        Warming,
        Unavailable,
        TimedOut,
    }

    private readonly Func<string, string, DictionaryVerdict> _dictionaryLookup;
    private readonly Func<string, string, string, string, bool> _shouldKeepByContext;
    private readonly CaseRepairDecision? _unavailableReason;

    public SeamCasingOracle(
        Func<string, string, DictionaryVerdict> dictionaryLookup,
        Func<string, string, string, string, bool> shouldKeepByContext,
        CaseRepairDecision? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(dictionaryLookup);
        ArgumentNullException.ThrowIfNull(shouldKeepByContext);
        _dictionaryLookup = dictionaryLookup;
        _shouldKeepByContext = shouldKeepByContext;
        _unavailableReason = unavailableReason;
    }

    public static SeamCasingOracle Unavailable(CaseRepairDecision reason) =>
        new(
            static (_, _) => DictionaryVerdict.Unavailable,
            static (_, _, _, _) => true,
            reason);

    public bool SupportsLanguage(string? languageCode) =>
        SeamCasingPolicies.PolicyFor(languageCode) is not null;

    public SeamCasingPolicy? PolicyFor(string? languageCode) =>
        SeamCasingPolicies.PolicyFor(languageCode);

    public SeamCasingVerdict Evaluate(string word, string leftContext, string payload, string? languageCode)
    {
        if (_unavailableReason is { } reason)
        {
            return SeamCasingVerdict.Unavailable(reason);
        }

        var baseCode = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseCode is null || SeamCasingPolicies.PolicyFor(baseCode) is null)
        {
            return SeamCasingVerdict.Keep(CaseRepairDecision.SkippedLanguageNotSupported);
        }

        var lower = SeamCasingPolicies.ToLowerLocale(word, baseCode);
        var verdict = _dictionaryLookup(lower, baseCode);
        switch (verdict)
        {
            case DictionaryVerdict.NotOrdinary:
                return SeamCasingVerdict.Keep(CaseRepairDecision.SkippedNotOrdinaryWord);
            case DictionaryVerdict.Warming:
                return SeamCasingVerdict.Unavailable(CaseRepairDecision.SkippedOracleWarming);
            case DictionaryVerdict.TimedOut:
                return SeamCasingVerdict.Unavailable(CaseRepairDecision.SkippedOracleTimedOut);
            case DictionaryVerdict.Unavailable:
                return SeamCasingVerdict.Unavailable(CaseRepairDecision.SkippedOracleUnavailable);
            case DictionaryVerdict.Ordinary:
                break;
            default:
                return SeamCasingVerdict.Unavailable(CaseRepairDecision.SkippedOracleUnavailable);
        }

        if (_shouldKeepByContext(baseCode, leftContext, payload, lower))
        {
            return SeamCasingVerdict.Keep(CaseRepairDecision.SkippedOracleKeep);
        }

        return SeamCasingVerdict.MayLower;
    }
}
