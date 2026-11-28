using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Learning;

namespace EnviousWispr.Core.Settings;

[JsonConverter(typeof(CustomWordEntryJsonConverter))]
public sealed record CustomWordEntry
{
    public CustomWordEntry(
        string spokenForm,
        string replacement,
        MatchStrictness strictness = MatchStrictness.Default)
        : this(
            aliases: string.IsNullOrWhiteSpace(spokenForm) ? [] : [spokenForm],
            replacement: replacement,
            strictness: strictness,
            category: WordCategory.General,
            id: null)
    {
    }

    public CustomWordEntry(
        IReadOnlyList<string> aliases,
        string replacement,
        MatchStrictness strictness = MatchStrictness.Default,
        WordCategory category = WordCategory.General,
        string? id = null,
        IReadOnlyList<string>? learnedAliases = null,
        DateTimeOffset? learnedAt = null)
    {
        Aliases = NormalizeAliases(aliases);
        Replacement = replacement?.Trim() ?? string.Empty;
        Strictness = strictness;
        Category = category;
        LearnedAliases = NormalizeLearnedAliases(learnedAliases, Aliases);
        LearnedAt = learnedAt;
        Id = !string.IsNullOrWhiteSpace(id)
            ? id.Trim()
            : CreateDeterministicId(Aliases, Replacement, Strictness, Category);
    }

    public const int MaximumAliasesPerEntry = 64;

    public string Id { get; init; }

    public IReadOnlyList<string> Aliases { get; init; }

    public string Replacement { get; init; }

    public MatchStrictness Strictness { get; init; }

    public WordCategory Category { get; init; }

    /// <summary>Subset of <see cref="Aliases"/> learned automatically from post-paste edits.</summary>
    public IReadOnlyList<string> LearnedAliases { get; init; }

    /// <summary>Timestamp when the app automatically learned this word from an edit; null when user-authored.</summary>
    public DateTimeOffset? LearnedAt { get; init; }

    /// <summary>Whether any part of this word was taught automatically by post-paste edit observation.</summary>
    [JsonIgnore]
    public bool IsAutoLearned => LearnedAt.HasValue || LearnedAliases.Count > 0;

    /// <summary>Whether every spoken alias on this entry is auto-learned (no user-authored alias exists).</summary>
    [JsonIgnore]
    public bool IsPureAutoLearned =>
        IsAutoLearned &&
        (LearnedAliases.Count == 0
            ? LearnedAt.HasValue
            : Aliases.All(alias => LearnedAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Returns only the auto-learned aliases for this entry, preserving any user-added aliases on mixed entries.</summary>
    public IReadOnlyList<string> EffectiveLearnedAliases() =>
        LearnedAliases.Count > 0
            ? LearnedAliases
            : LearnedAt.HasValue
                ? Aliases
                : [];

    /// <summary>Primary spoken form for legacy 1-to-1 callers; falls back to <see cref="Replacement"/> when no aliases exist.</summary>
    public string SpokenForm => Aliases.Count > 0 ? Aliases[0] : Replacement;

    public bool Equals(CustomWordEntry? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return string.Equals(Id, other.Id, StringComparison.Ordinal) &&
               string.Equals(Replacement, other.Replacement, StringComparison.Ordinal) &&
               Strictness == other.Strictness &&
               Category == other.Category &&
               Aliases.SequenceEqual(other.Aliases, StringComparer.Ordinal) &&
               LearnedAliases.SequenceEqual(other.LearnedAliases, StringComparer.Ordinal) &&
               Nullable.Equals(LearnedAt, other.LearnedAt);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(Replacement, StringComparer.Ordinal);
        hash.Add(Strictness);
        hash.Add(Category);
        foreach (var alias in Aliases)
        {
            hash.Add(alias, StringComparer.Ordinal);
        }

        foreach (var alias in LearnedAliases)
        {
            hash.Add(alias, StringComparer.Ordinal);
        }

        hash.Add(LearnedAt);
        return hash.ToHashCode();
    }

    public static IReadOnlyList<string> NormalizeLearnedAliases(
        IEnumerable<string>? learnedAliases,
        IReadOnlyList<string> aliases)
    {
        if (learnedAliases is null)
        {
            return [];
        }

        var allowed = aliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in learnedAliases)
        {
            var trimmed = raw?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && allowed.Contains(trimmed) && seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized.ToArray();
    }

    public static IReadOnlyList<string> NormalizeAliases(IEnumerable<string>? aliases)
    {
        if (aliases is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in aliases)
        {
            var trimmed = raw?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized.ToArray();
    }

    /// <summary>
    /// Merges legacy 1-to-1 entries that share canonical <see cref="Replacement"/> (exact case),
    /// <see cref="Strictness"/>, and <see cref="Category"/> into multi-alias entries without losing any spoken forms.
    /// </summary>
    public static IReadOnlyList<CustomWordEntry> NormalizeLegacyEntries(IReadOnlyList<CustomWordEntry>? entries)
    {
        if (entries is null || entries.Count <= 1)
        {
            return entries ?? [];
        }

        var merged = new List<CustomWordEntry>(entries.Count);
        var activeIndices = new Dictionary<(string Replacement, MatchStrictness Strictness, WordCategory Category), int>();
        var seenByGroup = new Dictionary<(string Replacement, MatchStrictness Strictness, WordCategory Category), HashSet<string>>();
        var changed = false;

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                changed = true;
                continue;
            }

            var key = (entry.Replacement, entry.Strictness, entry.Category);
            if (!seenByGroup.TryGetValue(key, out var groupSeen))
            {
                groupSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenByGroup[key] = groupSeen;
                foreach (var alias in entry.Aliases)
                {
                    groupSeen.Add(alias);
                }

                if (entry.Aliases.Count <= MaximumAliasesPerEntry)
                {
                    activeIndices[key] = merged.Count;
                    merged.Add(entry);
                    continue;
                }

                changed = true;
                foreach (var chunk in entry.Aliases.Chunk(MaximumAliasesPerEntry))
                {
                    activeIndices[key] = merged.Count;
                    merged.Add(new CustomWordEntry(
                        chunk,
                        entry.Replacement,
                        entry.Strictness,
                        entry.Category,
                        id: null));
                }

                continue;
            }

            var newAliases = new List<string>(entry.Aliases.Count);
            foreach (var alias in entry.Aliases)
            {
                if (groupSeen.Add(alias))
                {
                    newAliases.Add(alias);
                }
            }

            if (newAliases.Count == 0)
            {
                changed = true;
                continue;
            }

            changed = true;
            var activeIndex = activeIndices[key];
            var active = merged[activeIndex];
            var consumed = 0;
            var space = MaximumAliasesPerEntry - active.Aliases.Count;
            if (space > 0)
            {
                var take = Math.Min(space, newAliases.Count);
                var combinedAliases = NormalizeAliases(active.Aliases.Concat(newAliases.Take(take)));
                var combinedLearned = NormalizeLearnedAliases(
                    active.LearnedAliases.Concat(entry.LearnedAliases),
                    combinedAliases);
                merged[activeIndex] = new CustomWordEntry(
                    combinedAliases,
                    active.Replacement,
                    active.Strictness,
                    active.Category,
                    id: null,
                    combinedLearned,
                    active.LearnedAt ?? entry.LearnedAt);
                consumed = take;
            }

            while (consumed < newAliases.Count)
            {
                var slice = newAliases.Skip(consumed).Take(MaximumAliasesPerEntry).ToArray();
                consumed += slice.Length;
                activeIndices[key] = merged.Count;
                merged.Add(new CustomWordEntry(
                    slice,
                    entry.Replacement,
                    entry.Strictness,
                    entry.Category,
                    id: null,
                    entry.LearnedAliases,
                    entry.LearnedAt));
            }
        }

        return changed ? merged.ToArray() : entries;
    }

    private static string CreateDeterministicId(
        IReadOnlyList<string> aliases,
        string replacement,
        MatchStrictness strictness,
        WordCategory category)
    {
        var input = new StringBuilder();
        input.Append(replacement).Append('\0')
            .Append((int)strictness).Append('\0')
            .Append((int)category);
        foreach (var alias in aliases)
        {
            input.Append('\0').Append(alias);
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()), hash);
        return new Guid(hash[..16]).ToString("D");
    }

    /// <summary>
    /// What a screen reader announces for this row.
    /// </summary>
    public override string ToString()
    {
        var spoken = Aliases.Count > 0 ? string.Join(", ", Aliases) : Replacement;
        return Strictness switch
        {
            MatchStrictness.Loose => $"{spoken} becomes {Replacement}, matched loosely",
            MatchStrictness.Strict => $"{spoken} becomes {Replacement}, matched strictly",
            _ => $"{spoken} becomes {Replacement}",
        };
    }
}

internal sealed class CustomWordEntryJsonConverter : JsonConverter<CustomWordEntry>
{
    public override CustomWordEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected object for CustomWordEntry.");
        }

        string? id = null;
        List<string>? aliases = null;
        string? legacySpokenForm = null;
        string? replacement = null;
        var strictness = MatchStrictness.Default;
        var category = WordCategory.General;
        List<string>? learnedAliases = null;
        DateTimeOffset? learnedAt = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                var combined = new List<string>();
                if (aliases is not null)
                {
                    combined.AddRange(aliases);
                }

                if (!string.IsNullOrWhiteSpace(legacySpokenForm))
                {
                    combined.Add(legacySpokenForm);
                }

                return new CustomWordEntry(
                    combined,
                    replacement ?? string.Empty,
                    strictness,
                    category,
                    id,
                    learnedAliases,
                    learnedAt);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name in CustomWordEntry.");
            }

            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "id":
                    id = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "aliases":
                    aliases = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<List<string>>(ref reader, options);
                    break;
                case "spokenForm":
                    legacySpokenForm = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "replacement":
                    replacement = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "strictness":
                    strictness = JsonSerializer.Deserialize<MatchStrictness>(ref reader, options);
                    break;
                case "category":
                    category = JsonSerializer.Deserialize<WordCategory>(ref reader, options);
                    break;
                case "learnedAliases":
                    learnedAliases = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<List<string>>(ref reader, options);
                    break;
                case "learnedAt":
                    learnedAt = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<DateTimeOffset?>(ref reader, options);
                    break;
                default:
                    throw new JsonException($"Unmapped property '{propertyName}' on CustomWordEntry.");
            }
        }

        throw new JsonException("Unexpected end of JSON while reading CustomWordEntry.");
    }

    public override void Write(Utf8JsonWriter writer, CustomWordEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WritePropertyName("aliases");
        JsonSerializer.Serialize(writer, value.Aliases, options);
        writer.WriteString("replacement", value.Replacement);
        writer.WritePropertyName("strictness");
        JsonSerializer.Serialize(writer, value.Strictness, options);
        writer.WritePropertyName("category");
        JsonSerializer.Serialize(writer, value.Category, options);
        if (value.LearnedAliases.Count > 0)
        {
            writer.WritePropertyName("learnedAliases");
            JsonSerializer.Serialize(writer, value.LearnedAliases, options);
        }

        if (value.LearnedAt is { } learnedAt)
        {
            writer.WritePropertyName("learnedAt");
            JsonSerializer.Serialize(writer, learnedAt, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>One saved snippet.</summary>
/// <param name="Name">
/// THE TRIGGER: the words spoken after the keyword, matched word for word after
/// <see cref="SnippetText.Normalize"/>, never fuzzily. Named Name because that is what it was called on
/// disk before snippets fired, and renaming a stored member would break every saved file.
/// </param>
/// <param name="Body">The text delivered in the trigger's place, exactly as typed, line breaks and fill-ins included.</param>
/// <remarks>
/// NO COMPUTED PROPERTIES HERE. This record is written to the settings file as it stands, and the
/// store refuses a member it does not know - a derived property would be serialised by one build and
/// refused by the next. The trigger's tokens and collision key live on <see cref="SnippetText"/>.
/// </remarks>
public sealed record SnippetEntry(string Name, string Body)
{
    /// <summary>What a screen reader announces for this row. See <see cref="CustomWordEntry"/>.</summary>
    public override string ToString() => $"{Name}: {Body}";
}

public sealed class ReusableUserData : IEquatable<ReusableUserData>
{
    /// <param name="customWords">The person's corrections.</param>
    /// <param name="snippets">The person's snippets.</param>
    /// <param name="snippetKeyword">
    /// The word spoken before a snippet's trigger. Null or blank is the default, "backslash": a file
    /// written before the keyword existed has no such member, and an absent member must read as the
    /// word every snippet has always needed. The constructor's parameter is named for the member so
    /// the settings file binds to it.
    /// </param>
    /// <param name="enabledVocabularyPackIds">IDs of toggleable vocabulary packs active for deterministic correction.</param>
    /// <param name="hasSeededStarterSnippets">Whether one-time seeding of starter example snippets has been applied.</param>
    /// <remarks>
    /// THE KEYWORD LIVES WITH THE SNIPPETS, as it does on macOS (<c>SnippetVocabulary.keyword</c>, saved
    /// in the snippet store), so a portable profile carries it with the snippets it belongs to. A change
    /// to the words or the snippets goes through <see cref="WithCustomWords"/> or
    /// <see cref="WithSnippets"/>, which keep it: rebuilding this with two arguments would quietly put a
    /// changed keyword back to the default.
    /// </remarks>
    public ReusableUserData(
        IReadOnlyList<CustomWordEntry> customWords,
        IReadOnlyList<SnippetEntry> snippets,
        string? snippetKeyword = null,
        IReadOnlyList<string>? enabledVocabularyPackIds = null,
        bool hasSeededStarterSnippets = false,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? vocabularyPackOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(customWords);
        ArgumentNullException.ThrowIfNull(snippets);
        CustomWords = customWords.ToArray();
        Snippets = snippets.ToArray();
        SnippetKeyword = string.IsNullOrWhiteSpace(snippetKeyword)
            ? SnippetVocabulary.DefaultKeyword
            : snippetKeyword;
        EnabledVocabularyPackIds = NormalizePackIds(enabledVocabularyPackIds);
        HasSeededStarterSnippets = hasSeededStarterSnippets;
        VocabularyPackOverrides = VocabularyPacks.SnapshotOverrides(vocabularyPackOverrides);
    }

    public IReadOnlyList<CustomWordEntry> CustomWords { get; }

    public IReadOnlyList<SnippetEntry> Snippets { get; }

    /// <summary>The word spoken immediately before a snippet's trigger; "backslash" unless the person chose another.</summary>
    public string SnippetKeyword { get; }

    /// <summary>IDs of toggleable vocabulary packs active for deterministic correction.</summary>
    public IReadOnlyList<string> EnabledVocabularyPackIds { get; }

    /// <summary>Whether starter example snippets have already been evaluated for one-time seeding.</summary>
    public bool HasSeededStarterSnippets { get; }

    /// <summary>Sparse per-pack, per-word overrides (disabled flag and/or custom aliases) keyed by pack ID and lowercased canonical word.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> VocabularyPackOverrides { get; }

    /// <summary>The same data with other words, keeping the snippets, keyword, packs, overrides, and seeding flag.</summary>
    public ReusableUserData WithCustomWords(IReadOnlyList<CustomWordEntry> customWords) =>
        new(customWords, Snippets, SnippetKeyword, EnabledVocabularyPackIds, HasSeededStarterSnippets, VocabularyPackOverrides);

    /// <summary>The same data with other snippets, keeping the words, keyword, packs, overrides, and seeding flag.</summary>
    public ReusableUserData WithSnippets(IReadOnlyList<SnippetEntry> snippets) =>
        new(CustomWords, snippets, SnippetKeyword, EnabledVocabularyPackIds, HasSeededStarterSnippets, VocabularyPackOverrides);

    /// <summary>The same data with another keyword, keeping the words, snippets, packs, overrides, and seeding flag.</summary>
    public ReusableUserData WithSnippetKeyword(string snippetKeyword) =>
        new(CustomWords, Snippets, snippetKeyword, EnabledVocabularyPackIds, HasSeededStarterSnippets, VocabularyPackOverrides);

    /// <summary>The same data with updated enabled vocabulary packs, leaving CustomWords and overrides untouched.</summary>
    public ReusableUserData WithEnabledVocabularyPackIds(IReadOnlyList<string>? enabledVocabularyPackIds) =>
        new(CustomWords, Snippets, SnippetKeyword, enabledVocabularyPackIds, HasSeededStarterSnippets, VocabularyPackOverrides);

    /// <summary>The same data with updated sparse vocabulary pack word overrides, leaving CustomWords untouched.</summary>
    public ReusableUserData WithVocabularyPackOverrides(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>? vocabularyPackOverrides) =>
        new(CustomWords, Snippets, SnippetKeyword, EnabledVocabularyPackIds, HasSeededStarterSnippets, vocabularyPackOverrides);

    /// <summary>The same data with an updated starter-snippet seeding flag.</summary>
    public ReusableUserData WithHasSeededStarterSnippets(bool hasSeededStarterSnippets) =>
        new(CustomWords, Snippets, SnippetKeyword, EnabledVocabularyPackIds, hasSeededStarterSnippets, VocabularyPackOverrides);

    public static ReusableUserData Empty { get; } = new(
        Array.Empty<CustomWordEntry>(),
        Array.Empty<SnippetEntry>());

    public bool Equals(ReusableUserData? other) =>
        other is not null &&
        CustomWords.SequenceEqual(other.CustomWords) &&
        Snippets.SequenceEqual(other.Snippets) &&
        string.Equals(SnippetKeyword, other.SnippetKeyword, StringComparison.Ordinal) &&
        EnabledVocabularyPackIds.SequenceEqual(other.EnabledVocabularyPackIds, StringComparer.OrdinalIgnoreCase) &&
        HasSeededStarterSnippets == other.HasSeededStarterSnippets &&
        VocabularyPacks.OverridesEqual(VocabularyPackOverrides, other.VocabularyPackOverrides);

    public override bool Equals(object? obj) => Equals(obj as ReusableUserData);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in CustomWords)
        {
            hash.Add(entry);
        }

        foreach (var entry in Snippets)
        {
            hash.Add(entry);
        }

        hash.Add(SnippetKeyword, StringComparer.Ordinal);
        foreach (var packId in EnabledVocabularyPackIds)
        {
            hash.Add(packId, StringComparer.OrdinalIgnoreCase);
        }

        hash.Add(HasSeededStarterSnippets);
        VocabularyPacks.AddOverridesToHash(ref hash, VocabularyPackOverrides);
        return hash.ToHashCode();
    }

    private static string[] NormalizePackIds(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(ids.Count);
        foreach (var raw in ids)
        {
            var trimmed = raw?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized.ToArray();
    }
}

public sealed record PortableProfile(
    int SchemaVersion,
    UserPreferences Preferences,
    ReusableUserData UserData,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, LearnMemoryEntry>? LearnMemory = null)
{
    public const int CurrentSchemaVersion = 23;
}
