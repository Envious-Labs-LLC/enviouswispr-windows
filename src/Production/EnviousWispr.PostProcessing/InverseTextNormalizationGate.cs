using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

/// <summary>Vetted non-English language rule-set identity (#1677).</summary>
public sealed record LanguageRuleSet
{
    public string BaseCode { get; }

    public LanguageRuleSet(string language)
    {
        BaseCode = SpokenPunctuationRules.BaseCode(language)
            ?? throw new ArgumentException("Language code cannot be empty.", nameof(language));
    }

    internal static LanguageRuleSet? TryCreate(string? language) =>
        SpokenPunctuationRules.BaseCode(language) is { } code && code != "en"
            ? new LanguageRuleSet(code)
            : null;
}

/// <summary>Immutable registry of vetted non-English language rule sets (#1677).</summary>
public sealed class LanguageRuleRegistry
{
    private readonly Dictionary<string, LanguageRuleSet> _sets;

    public static LanguageRuleRegistry Production { get; } = BuildProduction();

    private LanguageRuleRegistry(Dictionary<string, LanguageRuleSet> sets)
    {
        _sets = sets;
    }

    public LanguageRuleRegistry(IEnumerable<LanguageRuleSet> ruleSets)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);
        _sets = new Dictionary<string, LanguageRuleSet>(StringComparer.Ordinal);
        foreach (var set in ruleSets)
        {
            if (!_sets.TryAdd(set.BaseCode, set))
            {
                throw new ArgumentException($"Duplicate rule set base code '{set.BaseCode}'.", nameof(ruleSets));
            }
        }
    }

    public int Count => _sets.Count;

    public LanguageRuleSet? RuleSet(string? language)
    {
        var code = SpokenPunctuationRules.BaseCode(language);
        if (code is null || code == "en")
        {
            return null;
        }

        return _sets.GetValueOrDefault(code);
    }

    private static LanguageRuleRegistry BuildProduction()
    {
        var sets = new Dictionary<string, LanguageRuleSet>(StringComparer.Ordinal);
        foreach (var code in new[] { "de", "fr", "es", "it", "pt", "nl", "pl", "sv", "uk" })
        {
            if (LanguageRuleSet.TryCreate(code) is { } set)
            {
                sets[set.BaseCode] = set;
            }
        }

        return new LanguageRuleRegistry(sets);
    }
}

public enum ItnRouteKind
{
    English,
    Language,
    Neutral,
}

public sealed record ItnRoute(
    ItnRouteKind Kind,
    string? LanguageCode = null,
    string? SkipReason = null)
{
    public static ItnRoute English { get; } = new(ItnRouteKind.English);

    public static ItnRoute Language(string baseCode) => new(ItnRouteKind.Language, LanguageCode: baseCode);

    public static ItnRoute Neutral(string skipReason) => new(ItnRouteKind.Neutral, SkipReason: skipReason);
}

public readonly record struct ItnExecutionResult(
    string Text,
    bool SpokenFinalFullStop);

/// <summary>
/// Port of macOS <c>InverseTextNormalizationGate</c> (#1677 / #2614): routes takes cleanly across
/// <c>English</c>, <c>Language(code)</c>, and <c>Neutral(reason)</c>.
/// </summary>
public static class InverseTextNormalizationGate
{
    public static string? SkipReason(
        string? language,
        bool englishVetoed,
        bool backendSupportsLid)
    {
        if (englishVetoed)
        {
            return "language_vetoed";
        }

        var trimmed = language?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var isEnglish = trimmed.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("en_", StringComparison.OrdinalIgnoreCase);
            return isEnglish ? null : "non_english";
        }

        return backendSupportsLid ? "lid_backend_nil" : null;
    }

    public static ItnRoute ResolveRoute(
        string? language,
        bool englishVetoed,
        bool backendSupportsLid,
        LanguageRuleRegistry? registry = null)
    {
        var activeRegistry = registry ?? LanguageRuleRegistry.Production;
        var skip = SkipReason(language, englishVetoed, backendSupportsLid);
        if (skip is null)
        {
            return ItnRoute.English;
        }

        if (skip == "non_english" && activeRegistry.RuleSet(language) is { } set)
        {
            return ItnRoute.Language(set.BaseCode);
        }

        return ItnRoute.Neutral(skip);
    }

    public static ItnExecutionResult Execute(
        string text,
        ItnRoute route,
        bool spokenPunctuation,
        bool spokenSymbols = true,
        LanguageRuleRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(route);
        var activeRegistry = registry ?? LanguageRuleRegistry.Production;

        switch (route.Kind)
        {
            case ItnRouteKind.English:
            {
                var normalized = InverseTextNormalizer.Normalize(
                    text,
                    spokenPunctuation,
                    spokenSymbols,
                    out var spokenFinalFullStop);
                return new ItnExecutionResult(normalized, spokenFinalFullStop);
            }

            case ItnRouteKind.Language:
            {
                var rules = activeRegistry.RuleSet(route.LanguageCode);
                if (rules is null || !string.Equals(rules.BaseCode, route.LanguageCode, StringComparison.Ordinal))
                {
                    return new ItnExecutionResult(InverseTextNormalizer.NormalizeLanguageNeutral(text), false);
                }

                var normalized = InverseTextNormalizer.Normalize(text, rules);
                return new ItnExecutionResult(normalized, false);
            }

            case ItnRouteKind.Neutral:
            default:
                return new ItnExecutionResult(InverseTextNormalizer.NormalizeLanguageNeutral(text), false);
        }
    }
}
