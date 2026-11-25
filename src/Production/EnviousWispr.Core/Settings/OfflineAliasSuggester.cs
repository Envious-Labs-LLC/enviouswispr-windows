using System.Text;
using System.Text.RegularExpressions;

namespace EnviousWispr.Core.Settings;

/// <summary>
/// Generates deterministic offline mishearing suggestions for a canonical word or phrase when AI polish is unavailable
/// or before an online lookup completes.
/// </summary>
public static partial class OfflineAliasSuggester
{
    private static readonly (string Pattern, string Replacement)[] PhoneticSubstitutions =
    [
        ("wispr", "whisper"),
        ("ph", "f"),
        ("f", "ph"),
        ("tion", "shun"),
        ("sion", "zhun"),
        ("qu", "kw"),
        ("ck", "k"),
        ("x", "ks"),
        ("ae", "e"),
        ("oe", "e"),
    ];

    public static IReadOnlyList<string> Suggest(
        string? canonical,
        IEnumerable<string>? existingAliases = null,
        int maxCount = 6)
    {
        var trimmed = canonical?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || maxCount <= 0)
        {
            return [];
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingAliases is not null)
        {
            foreach (var alias in existingAliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    existing.Add(alias.Trim());
                }
            }
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = SpaceRunRegex().Replace(value.Trim(), " ");
            if (normalized.Length < 2 ||
                string.Equals(normalized, trimmed, StringComparison.Ordinal) ||
                existing.Contains(normalized) ||
                !seen.Add(normalized))
            {
                return;
            }

            candidates.Add(normalized);
        }

        // Split camel/PascalCase or punctuation boundaries (e.g. EnviousWispr -> envious wispr, OpenAI -> open ai)
        var splitCamel = SplitCamelCase(trimmed);
        if (!string.Equals(splitCamel, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            TryAdd(splitCamel.ToLowerInvariant());
        }

        // Lowercased form captures simple capitalization misses by ASR
        var lower = trimmed.ToLowerInvariant();
        TryAdd(lower);

        // Punctuation-stripped spaced form (e.g. Next.js -> next js, CI/CD -> ci cd)
        var unpunctuated = PunctuationSeparatorRegex().Replace(trimmed, " ");
        if (!string.Equals(unpunctuated, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            TryAdd(unpunctuated.ToLowerInvariant());
        }

        // Acronym letter spacing for compact uppercase words (e.g. FERPA -> f e r p a, SQL -> s q l)
        if (trimmed.Length is >= 2 and <= 6 && !trimmed.Contains(' ') && trimmed.All(char.IsLetter) && trimmed.All(char.IsUpper))
        {
            TryAdd(string.Join(' ', trimmed.ToLowerInvariant().ToCharArray()));
        }

        // Phonetic heuristics on both full lowercase and camel-split lowercase forms
        foreach (var seed in new[] { lower, splitCamel.ToLowerInvariant() }.Distinct(StringComparer.Ordinal))
        {
            foreach (var (from, to) in PhoneticSubstitutions)
            {
                if (seed.Contains(from, StringComparison.Ordinal))
                {
                    TryAdd(seed.Replace(from, to, StringComparison.Ordinal));
                }
            }

            var collapsedDoubles = CollapseDoubledLetters(seed);
            if (!string.Equals(collapsedDoubles, seed, StringComparison.Ordinal))
            {
                TryAdd(collapsedDoubles);
            }
        }

        // Hyphenated / spaced variants for multi-word terms
        if (splitCamel.Contains(' ') && !trimmed.Contains(' '))
        {
            TryAdd(splitCamel.ToLowerInvariant().Replace(' ', '-'));
        }
        else if (trimmed.Contains(' '))
        {
            TryAdd(trimmed.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant());
        }

        return candidates.Take(maxCount).ToArray();
    }

    private static string SplitCamelCase(string input)
    {
        var sb = new StringBuilder(input.Length + 8);
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (i > 0 && char.IsUpper(ch))
            {
                var prev = input[i - 1];
                var nextLower = i + 1 < input.Length && char.IsLower(input[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextLower))
                {
                    sb.Append(' ');
                }
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string CollapseDoubledLetters(string text)
    {
        var sb = new StringBuilder(text.Length);
        char? prev = null;
        foreach (var ch in text)
        {
            if (prev is { } last && ch == last && char.IsLetter(ch))
            {
                continue;
            }

            sb.Append(ch);
            prev = ch;
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRunRegex();

    [GeneratedRegex(@"[-_./:]+")]
    private static partial Regex PunctuationSeparatorRegex();
}
