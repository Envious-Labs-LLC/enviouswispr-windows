using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnviousWispr.Core.Settings;

/// <summary>One canonical term and all of its recognized spoken mishearings inside a vocabulary pack.</summary>
public sealed record VocabularyPackTerm(
    string Canonical,
    IReadOnlyList<string> Aliases,
    WordCategory Category = WordCategory.General);

/// <summary>
/// Sparse per-word override applied over a shipped vocabulary pack term.
/// Null properties inherit the shipped pack's default value.
/// </summary>
public sealed record VocabularyPackWordOverride
{
    [JsonConstructor]
    public VocabularyPackWordOverride(bool? isEnabled = null, IReadOnlyList<string>? aliases = null)
    {
        IsEnabled = isEnabled == false ? false : null;
        Aliases = aliases is null ? null : CustomWordEntry.NormalizeAliases(aliases);
    }

    /// <summary>False when the user disabled this pack word; null means enabled (shipped default).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsEnabled { get; init; }

    /// <summary>Replacement spoken-alias list when customized; null means use shipped aliases.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>True when neither field overrides the shipped pack entry.</summary>
    [JsonIgnore]
    public bool IsEmpty => IsEnabled is null && Aliases is null;

    public bool Equals(VocabularyPackWordOverride? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        if (IsEnabled != other.IsEnabled)
        {
            return false;
        }

        if (Aliases is null)
        {
            return other.Aliases is null;
        }

        return other.Aliases is not null &&
               Aliases.SequenceEqual(other.Aliases, StringComparer.Ordinal);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsEnabled);
        if (Aliases is not null)
        {
            hash.Add(1);
            foreach (var alias in Aliases)
            {
                hash.Add(alias, StringComparer.Ordinal);
            }
        }

        return hash.ToHashCode();
    }
}

/// <summary>Effective display state of one word inside a vocabulary pack for the pack editor.</summary>
public sealed record VocabularyPackWordState(
    string PackId,
    string Canonical,
    IReadOnlyList<string> ShippedAliases,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> DisplayAliases,
    bool IsEnabled,
    bool IsEdited)
{
    public bool Equals(VocabularyPackWordState? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return string.Equals(PackId, other.PackId, StringComparison.Ordinal)
            && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal)
            && IsEnabled == other.IsEnabled
            && IsEdited == other.IsEdited
            && ShippedAliases.SequenceEqual(other.ShippedAliases, StringComparer.Ordinal)
            && Aliases.SequenceEqual(other.Aliases, StringComparer.Ordinal)
            && DisplayAliases.SequenceEqual(other.DisplayAliases, StringComparer.Ordinal);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PackId, StringComparer.Ordinal);
        hash.Add(Canonical, StringComparer.Ordinal);
        hash.Add(IsEnabled);
        hash.Add(IsEdited);
        foreach (var alias in Aliases)
        {
            hash.Add(alias, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => IsEnabled ? Canonical : $"{Canonical} (off)";
}

/// <summary>A curated, toggleable domain vocabulary pack loaded from shipped dictionary definitions.</summary>
public sealed record VocabularyPack
{
    private IReadOnlyList<VocabularyPackTerm>? _terms;
    private IReadOnlyList<CustomWordEntry>? _entries;
    private IReadOnlyList<string>? _exampleTerms;
    private Dictionary<string, VocabularyPackTerm>? _termsByOverrideKey;
    private Dictionary<string, CustomWordEntry>? _entriesByOverrideKey;

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

    public VocabularyPackTerm? FindTerm(string? canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return null;
        }

        var map = _termsByOverrideKey ??= BuildTermsByOverrideKey(Terms);
        return map.GetValueOrDefault(VocabularyPacks.OverrideKey(canonical));
    }

    internal CustomWordEntry? FindShippedEntry(string overrideKey)
    {
        var map = _entriesByOverrideKey ??= BuildEntriesByOverrideKey(Entries);
        return map.GetValueOrDefault(overrideKey);
    }

    private static Dictionary<string, VocabularyPackTerm> BuildTermsByOverrideKey(IReadOnlyList<VocabularyPackTerm> terms)
    {
        var map = new Dictionary<string, VocabularyPackTerm>(terms.Count, StringComparer.Ordinal);
        foreach (var term in terms)
        {
            map.TryAdd(VocabularyPacks.OverrideKey(term.Canonical), term);
        }

        return map;
    }

    private static Dictionary<string, CustomWordEntry> BuildEntriesByOverrideKey(IReadOnlyList<CustomWordEntry> entries)
    {
        var map = new Dictionary<string, CustomWordEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map.TryAdd(VocabularyPacks.OverrideKey(entry.Replacement), entry);
        }

        return map;
    }

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

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> EmptyOverrides { get; } =
        new ReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>(
            new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>(StringComparer.OrdinalIgnoreCase));

    public static string OverrideKey(string canonical) =>
        (canonical ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// True when the candidate alias list contains the exact same strings (case-sensitive) as the shipped list,
    /// regardless of order, so removing and re-adding a shipped alias cleanly restores the unedited state.
    /// </summary>
    public static bool AliasesEqualShipped(IReadOnlyList<string> aliases, IReadOnlyList<string> shippedAliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(shippedAliases);
        if (aliases.Count != shippedAliases.Count)
        {
            return false;
        }

        if (aliases.SequenceEqual(shippedAliases, StringComparer.Ordinal))
        {
            return true;
        }

        var shippedSet = new HashSet<string>(shippedAliases, StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            if (!shippedSet.Contains(alias))
            {
                return false;
            }
        }

        return true;
    }

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

    /// <summary>True when at least one enabled pack still has an active (non-disabled) term after applying overrides.</summary>
    public static bool HasAnyActiveTerms(
        IReadOnlyList<string>? enabledPackIds,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? overrides)
    {
        if (enabledPackIds is not { Count: > 0 })
        {
            return false;
        }

        foreach (var id in enabledPackIds)
        {
            if (FindById(id) is not { } pack)
            {
                continue;
            }

            if (overrides is null || !TryGetPackOverrides(overrides, pack.Id, out var packOverrides) || packOverrides.Count == 0)
            {
                if (pack.Terms.Count > 0)
                {
                    return true;
                }

                continue;
            }

            foreach (var term in pack.Terms)
            {
                if (!TryGetWordOverride(packOverrides, OverrideKey(term.Canonical), out var wordOverride) ||
                    wordOverride.IsEnabled != false)
                {
                    return true;
                }
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
    public static IReadOnlyList<CustomWordEntry> ResolveEntries(IReadOnlyList<string>? enabledPackIds) =>
        ResolveEntries(enabledPackIds, overrides: null);

    /// <summary>
    /// Resolves active vocabulary pack entries for the enabled pack IDs with sparse per-word overrides applied:
    /// disabled words are omitted, overridden aliases replace shipped aliases, and untouched words pass through unchanged.
    /// </summary>
    public static IReadOnlyList<CustomWordEntry> ResolveEntries(
        IReadOnlyList<string>? enabledPackIds,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? overrides)
    {
        if (enabledPackIds is not { Count: > 0 })
        {
            return [];
        }

        var entries = new List<CustomWordEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in enabledPackIds)
        {
            if (FindById(id) is not { } pack || !seen.Add(pack.Id))
            {
                continue;
            }

            if (overrides is null || !TryGetPackOverrides(overrides, pack.Id, out var packOverrides) || packOverrides.Count == 0)
            {
                entries.AddRange(pack.Entries);
                continue;
            }

            foreach (var term in pack.Terms)
            {
                var key = OverrideKey(term.Canonical);
                if (!TryGetWordOverride(packOverrides, key, out var wordOverride))
                {
                    if (pack.FindShippedEntry(key) is { } shipped)
                    {
                        entries.Add(shipped);
                    }

                    continue;
                }

                if (wordOverride.IsEnabled == false)
                {
                    continue;
                }

                if (wordOverride.Aliases is { } customAliases)
                {
                    entries.Add(new CustomWordEntry(
                        customAliases,
                        term.Canonical,
                        MatchStrictness.Default,
                        term.Category,
                        id: $"pack:{pack.Id}:{term.Canonical}",
                        origin: CustomWordOrigin.Pack));
                }
                else if (pack.FindShippedEntry(key) is { } shipped)
                {
                    entries.Add(shipped);
                }
            }
        }

        return entries.ToArray();
    }

    /// <summary>Returns all words in <paramref name="pack"/> with their effective override state, sorted alphabetically.</summary>
    public static IReadOnlyList<VocabularyPackWordState> GetPackWordStates(
        VocabularyPack pack,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? overrides)
    {
        ArgumentNullException.ThrowIfNull(pack);
        IReadOnlyDictionary<string, VocabularyPackWordOverride>? packOverrides = null;
        if (overrides is not null)
        {
            TryGetPackOverrides(overrides, pack.Id, out packOverrides);
        }

        var states = new VocabularyPackWordState[pack.Terms.Count];
        for (var i = 0; i < pack.Terms.Count; i++)
        {
            var term = pack.Terms[i];
            var key = OverrideKey(term.Canonical);
            VocabularyPackWordOverride? wordOverride = null;
            if (packOverrides is not null)
            {
                TryGetWordOverride(packOverrides, key, out wordOverride);
            }

            var isEnabled = wordOverride?.IsEnabled ?? true;
            var effectiveAliases = wordOverride?.Aliases ?? term.Aliases;
            var displayAliases = effectiveAliases.Count <= 1
                ? effectiveAliases
                : effectiveAliases
                    .OrderBy(alias => alias, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(alias => alias, StringComparer.Ordinal)
                    .ToArray();
            var isEdited = !isEnabled || !AliasesEqualShipped(effectiveAliases, term.Aliases);
            states[i] = new VocabularyPackWordState(
                pack.Id,
                term.Canonical,
                term.Aliases,
                effectiveAliases,
                displayAliases,
                isEnabled,
                isEdited);
        }

        Array.Sort(states, static (left, right) =>
        {
            var cmp = StringComparer.CurrentCultureIgnoreCase.Compare(left.Canonical, right.Canonical);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(left.Canonical, right.Canonical);
        });
        return states;
    }

    /// <summary>Counts how many words in <paramref name="pack"/> currently differ from shipped defaults.</summary>
    public static int CountEditedWords(
        VocabularyPack pack,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? overrides)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (overrides is null || !TryGetPackOverrides(overrides, pack.Id, out var packOverrides) || packOverrides.Count == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var term in pack.Terms)
        {
            var key = OverrideKey(term.Canonical);
            if (!TryGetWordOverride(packOverrides, key, out var wordOverride))
            {
                continue;
            }

            var isEnabled = wordOverride.IsEnabled ?? true;
            var effectiveAliases = wordOverride.Aliases ?? term.Aliases;
            if (!isEnabled || !AliasesEqualShipped(effectiveAliases, term.Aliases))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Formats the pack card detail summary with accurate singular/plural wording.</summary>
    public static string FormatSummary(VocabularyPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var countLabel = pack.FixCount == 1 ? "1 fix" : $"{pack.FixCount} fixes";
        return pack.ExampleTerms.Count == 0
            ? countLabel
            : $"{countLabel} \u00B7 e.g. {string.Join(", ", pack.ExampleTerms)}";
    }

    /// <summary>Creates a normalized sparse copy of pack overrides, pruning empty entries and empty packs.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> PruneEmptyOverrides(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return EmptyOverrides;
        }

        var packs = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawPackId, wordMap) in overrides)
        {
            if (string.IsNullOrWhiteSpace(rawPackId) || wordMap is null || wordMap.Count == 0)
            {
                continue;
            }

            var canonicalPackId = FindById(rawPackId)?.Id ?? rawPackId.Trim().ToLowerInvariant();
            var words = new Dictionary<string, VocabularyPackWordOverride>(StringComparer.Ordinal);
            foreach (var (rawWordKey, wordOverride) in wordMap)
            {
                if (string.IsNullOrWhiteSpace(rawWordKey) || wordOverride is null || wordOverride.IsEmpty)
                {
                    continue;
                }

                words[OverrideKey(rawWordKey)] = wordOverride;
            }

            if (words.Count > 0)
            {
                packs[canonicalPackId] = new ReadOnlyDictionary<string, VocabularyPackWordOverride>(words);
            }
        }

        return packs.Count == 0
            ? EmptyOverrides
            : new ReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>(packs);
    }

    /// <summary>Preserves raw structure for validation while returning an immutable snapshot.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> SnapshotOverrides(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return EmptyOverrides;
        }

        var packs = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>(overrides.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (packId, wordMap) in overrides)
        {
            if (wordMap is null)
            {
                packs[packId] = null!;
                continue;
            }

            var words = new Dictionary<string, VocabularyPackWordOverride>(wordMap.Count, StringComparer.Ordinal);
            foreach (var (wordKey, wordOverride) in wordMap)
            {
                words[wordKey] = wordOverride;
            }

            packs[packId] = new ReadOnlyDictionary<string, VocabularyPackWordOverride>(words);
        }

        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>(packs);
    }

    public static bool OverridesEqual(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> left,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (packId, leftPack) in left)
        {
            if (!TryGetPackOverrides(right, packId, out var rightPack))
            {
                return false;
            }

            if (leftPack is null || rightPack is null)
            {
                if (!ReferenceEquals(leftPack, rightPack))
                {
                    return false;
                }

                continue;
            }

            if (leftPack.Count != rightPack.Count)
            {
                return false;
            }

            foreach (var (wordKey, leftOverride) in leftPack)
            {
                if (!TryGetWordOverride(rightPack, wordKey, out var rightOverride) ||
                    !Equals(leftOverride, rightOverride))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static void AddOverridesToHash(
        ref HashCode hash,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> overrides)
    {
        foreach (var (packId, wordMap) in overrides.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(packId, StringComparer.OrdinalIgnoreCase);
            if (wordMap is null)
            {
                continue;
            }

            foreach (var (wordKey, wordOverride) in wordMap.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                hash.Add(wordKey, StringComparer.OrdinalIgnoreCase);
                hash.Add(wordOverride);
            }
        }
    }

    private static bool TryGetPackOverrides(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> overrides,
        string packId,
        out IReadOnlyDictionary<string, VocabularyPackWordOverride> packOverrides)
    {
        if (overrides.TryGetValue(packId, out packOverrides!))
        {
            return true;
        }

        foreach (var (key, value) in overrides)
        {
            if (string.Equals(key, packId, StringComparison.OrdinalIgnoreCase))
            {
                packOverrides = value;
                return true;
            }
        }

        packOverrides = null!;
        return false;
    }

    private static bool TryGetWordOverride(
        IReadOnlyDictionary<string, VocabularyPackWordOverride> packOverrides,
        string overrideKey,
        out VocabularyPackWordOverride wordOverride)
    {
        if (packOverrides.TryGetValue(overrideKey, out wordOverride!))
        {
            return true;
        }

        foreach (var (key, value) in packOverrides)
        {
            if (string.Equals(key, overrideKey, StringComparison.OrdinalIgnoreCase))
            {
                wordOverride = value;
                return true;
            }
        }

        wordOverride = null!;
        return false;
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
