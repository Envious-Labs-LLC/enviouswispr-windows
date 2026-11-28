using System.Reflection;
using System.Text;
using System.Text.Json;

namespace EnviousWispr.Core.Settings;

/// <summary>One canonical term and all of its recognized spoken mishearings inside a vocabulary pack.</summary>
public sealed record VocabularyPackTerm(
    string Canonical,
    IReadOnlyList<string> Aliases,
    WordCategory Category = WordCategory.General);

/// <summary>A curated, toggleable domain vocabulary pack loaded from shipped dictionary definitions.</summary>
public sealed record VocabularyPack
{
    private IReadOnlyList<VocabularyPackTerm>? _terms;
    private IReadOnlyList<CustomWordEntry>? _entries;
    private IReadOnlyList<string>? _exampleTerms;

    public VocabularyPack(string id, string name, string description, string words)
    {
        Id = id;
        Name = name;
        Description = description;
        Words = words;
        IconGlyph = DefaultGlyphFor(id);
        DefaultCategory = WordCategory.General;
    }

    internal VocabularyPack(
        string id,
        string name,
        string description,
        string iconGlyph,
        WordCategory defaultCategory,
        IReadOnlyList<VocabularyPackTerm> terms)
    {
        Id = id;
        Name = name;
        Description = description;
        IconGlyph = iconGlyph;
        DefaultCategory = defaultCategory;
        _terms = terms;
        _entries = terms
            .Select(term => new CustomWordEntry(
                term.Aliases,
                term.Canonical,
                MatchStrictness.Default,
                term.Category,
                id: $"pack:{id}:{term.Canonical}",
                origin: CustomWordOrigin.Pack))
            .ToArray();
        _exampleTerms = terms
            .Select(term => term.Canonical)
            .OrderBy(canonical => canonical, StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        Words = FormatAsCsv(terms);
    }

    public string Id { get; init; }

    public string Name { get; init; }

    public string Description { get; init; }

    public string Words { get; init; }

    public string IconGlyph { get; init; }

    public WordCategory DefaultCategory { get; init; }

    public IReadOnlyList<VocabularyPackTerm> Terms => _terms ??= ParseTermsFromWords(Words, DefaultCategory);

    public IReadOnlyList<CustomWordEntry> Entries => _entries ??= Terms
        .Select(term => new CustomWordEntry(
            term.Aliases,
            term.Canonical,
            MatchStrictness.Default,
            term.Category,
            id: $"pack:{Id}:{term.Canonical}",
            origin: CustomWordOrigin.Pack))
        .ToArray();

    public int FixCount => Terms.Count;

    public IReadOnlyList<string> ExampleTerms => _exampleTerms ??= Terms
        .Select(term => term.Canonical)
        .OrderBy(canonical => canonical, StringComparer.Ordinal)
        .Take(3)
        .ToArray();

    private static string FormatAsCsv(IReadOnlyList<VocabularyPackTerm> terms)
    {
        var sb = new StringBuilder();
        foreach (var term in terms)
        {
            foreach (var alias in term.Aliases)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(EscapeCsv(alias)).Append(',').Append(EscapeCsv(term.Canonical));
            }
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string field)
    {
        if (!field.Contains(',') && !field.Contains('"') && !field.Contains('\n') && !field.Contains('\r'))
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static VocabularyPackTerm[] ParseTermsFromWords(string words, WordCategory category)
    {
        if (string.IsNullOrWhiteSpace(words))
        {
            return [];
        }

        var plan = CustomWordImport.Read(words, []);
        var grouped = new List<VocabularyPackTerm>();
        var byCanonical = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var addition in plan.Additions)
        {
            if (!byCanonical.TryGetValue(addition.Replacement, out var aliases))
            {
                aliases = [];
                byCanonical[addition.Replacement] = aliases;
            }

            foreach (var alias in addition.Aliases)
            {
                if (!aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                {
                    aliases.Add(alias);
                }
            }
        }

        foreach (var pair in byCanonical.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            grouped.Add(new VocabularyPackTerm(pair.Key, pair.Value.ToArray(), category));
        }

        return grouped.ToArray();
    }

    private static string DefaultGlyphFor(string? id) => id?.ToLowerInvariant() switch
    {
        "tech" => "\uE943",
        "medical" => "\uE95E",
        "legal" => "\uE8F1",
        "brands" => "\uE719",
        "names" => "\uE716",
        _ => "\uE8F1",
    };
}

/// <summary>Bundled toggleable vocabulary packs matching the shipped dictionary definitions.</summary>
public static class VocabularyPacks
{
    public static readonly VocabularyPack Tech = LoadPack(
        id: "tech",
        name: "Tech",
        description: "Programming, cloud, and developer tools.",
        iconGlyph: "\uE943",
        category: WordCategory.Domain);

    public static readonly VocabularyPack Medical = LoadPack(
        id: "medical",
        name: "Medical",
        description: "Medications, conditions, and clinical terms.",
        iconGlyph: "\uE95E",
        category: WordCategory.Domain);

    public static readonly VocabularyPack Legal = LoadPack(
        id: "legal",
        name: "Legal",
        description: "Litigation, contract, and court terminology.",
        iconGlyph: "\uE8F1",
        category: WordCategory.Domain);

    public static readonly VocabularyPack Brands = LoadPack(
        id: "brands",
        name: "Brands",
        description: "Company, product, and app names.",
        iconGlyph: "\uE719",
        category: WordCategory.Brand);

    public static readonly VocabularyPack Names = LoadPack(
        id: "names",
        name: "Names",
        description: "Common first names and surnames.",
        iconGlyph: "\uE716",
        category: WordCategory.Person);

    public static IReadOnlyList<VocabularyPack> All { get; } =
    [
        Tech,
        Medical,
        Legal,
        Brands,
        Names,
    ];

    private static readonly Dictionary<string, VocabularyPack> ById =
        All.ToDictionary(pack => pack.Id, StringComparer.OrdinalIgnoreCase);

    public static VocabularyPack? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : ById.GetValueOrDefault(id.Trim());

    public static bool HasAny(IReadOnlyList<string>? enabledPackIds)
    {
        if (enabledPackIds is not { Count: > 0 })
        {
            return false;
        }

        foreach (var id in enabledPackIds)
        {
            if (FindById(id) is not null)
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> NormalizePackIds(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in ids)
        {
            if (FindById(raw) is { } pack && seen.Add(pack.Id))
            {
                normalized.Add(pack.Id);
            }
        }

        return normalized.ToArray();
    }

    /// <summary>Resolves active vocabulary pack entries for the enabled pack IDs as an isolated layer.</summary>
    public static IReadOnlyList<CustomWordEntry> ResolveEntries(IReadOnlyList<string>? enabledPackIds)
    {
        if (enabledPackIds is not { Count: > 0 })
        {
            return [];
        }

        var entries = new List<CustomWordEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in enabledPackIds)
        {
            if (FindById(id) is { } pack && seen.Add(pack.Id))
            {
                entries.AddRange(pack.Entries);
            }
        }

        return entries.ToArray();
    }

    private static VocabularyPack LoadPack(
        string id,
        string name,
        string description,
        string iconGlyph,
        WordCategory category)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = $".Resources.Packs.{id}.json";
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Missing embedded vocabulary pack resource ending with '{suffix}'.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Failed to open embedded vocabulary pack '{resourceName}'.");
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
            ?? throw new InvalidOperationException($"Vocabulary pack '{resourceName}' parsed to null.");

        var terms = new List<VocabularyPackTerm>(dictionary.Count);
        foreach (var pair in dictionary.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var canonical = pair.Key?.Trim() ?? string.Empty;
            if (canonical.Length == 0)
            {
                continue;
            }

            var aliases = (pair.Value ?? [])
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (aliases.Length == 0)
            {
                continue;
            }

            terms.Add(new VocabularyPackTerm(canonical, aliases, category));
        }

        return new VocabularyPack(id, name, description, iconGlyph, category, terms.ToArray());
    }
}
