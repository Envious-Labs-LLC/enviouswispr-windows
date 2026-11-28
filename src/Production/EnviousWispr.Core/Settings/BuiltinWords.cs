namespace EnviousWispr.Core.Settings;

/// <summary>
/// A built-in default word shipped with the app, identified by a stable <see cref="BuiltinId"/>
/// so user deletions persist as tombstones across app updates (matching macOS <c>BuiltinWord</c>).
/// </summary>
public sealed record BuiltinWordDefinition
{
    public BuiltinWordDefinition(
        string builtinId,
        string replacement,
        IReadOnlyList<string> aliases,
        WordCategory category = WordCategory.General,
        IReadOnlyList<string>? learnedAliases = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(builtinId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        ArgumentNullException.ThrowIfNull(aliases);

        BuiltinId = builtinId.Trim().ToLowerInvariant();
        Entry = new CustomWordEntry(
            aliases: aliases,
            replacement: replacement,
            strictness: MatchStrictness.Default,
            category: category,
            id: EntryIdFor(BuiltinId),
            learnedAliases: learnedAliases,
            learnedAt: null,
            origin: CustomWordOrigin.Builtin);
    }

    public string BuiltinId { get; }

    public CustomWordEntry Entry { get; }

    public static string EntryIdFor(string builtinId) => $"builtin:{builtinId.Trim().ToLowerInvariant()}";
}

/// <summary>
/// Always-loaded pre-built "Your Words" catalog and two-tier runtime merge logic
/// (matching macOS <c>CustomWordsManager.builtinDefaults</c> + <c>CustomWordsMutationPolicy.mergedWords</c>).
/// </summary>
public static class BuiltinWords
{
    public const int MaximumDeletedBuiltinIds = 256;

    /// <summary>
    /// Shipped pre-built entries: the 12 proven macOS built-ins enriched with extra logged mishearings,
    /// plus 6 measured everyday/AI terms (<c>EnviousMeet</c>, <c>Claude Code</c>, <c>AirPods</c>, <c>Wi-Fi</c>,
    /// <c>Gmail</c>, <c>Gemini</c>).
    /// </summary>
    public static IReadOnlyList<BuiltinWordDefinition> All { get; } =
    [
        new(
            builtinId: "enviouswispr",
            replacement: "EnviousWispr",
            aliases:
            [
                "envious whisper", "envious wisper", "envious whispr",
                "envious visper", "envious cisper", "mbs cisper",
                "in vious wispr", "envy us wispr", "NVS Visper", "NBS Vesper",
                "envious wispr", "Enviousvisper", "NVSBesper", "NVSBSPur",
                "NVIS VICPRSO", "EnvyS Visper", "senvy wpr", "Dambius Bispe",
                "MBS Visper", "MVS Visper", "MBS Vesper", "MBS Whisper", "NVIS VISPR",
                "NVIS Whisper", "Envy S Whisper", "envy as whisper", "envy as whisker",
                "Envice whisper", "NvSvisker", "VS Visper",
                "Invisper", "NVSISPR", "Envy's Dispware", "Envy's Whisper",
            ],
            category: WordCategory.Brand),
        new(
            builtinId: "eg1",
            replacement: "EG-1",
            // Keep digit-bearing aliases only; spelled-out "egg one" / "E G one" corrupt ordinary sentences.
            aliases: ["EG 1", "E G 1", "EG-one", "EG1"],
            category: WordCategory.Brand),
        new(
            builtinId: "enviouslabs",
            replacement: "Envious Labs",
            aliases:
            [
                "envious laps", "MVS Labs", "NVIS Labs", "NVIS LAPS", "NVS laps", "NVS Labs",
                "MBS Labs", "NBS Labs", "Envy's labs",
            ],
            category: WordCategory.Brand),
        new(
            builtinId: "enviousmeet",
            replacement: "EnviousMeet",
            aliases: ["Envious Meet", "envious meat", "envious need", "Envious Me", "EnviousMe"],
            category: WordCategory.Brand),
        new(
            builtinId: "macos",
            replacement: "macOS",
            aliases: ["mac OS", "Mack OS"],
            category: WordCategory.Brand),
        new(
            builtinId: "ios",
            replacement: "iOS",
            aliases: ["I OS", "eye OS"],
            category: WordCategory.Brand),
        new(
            builtinId: "github",
            replacement: "GitHub",
            aliases: ["git hub", "get hub"],
            category: WordCategory.Brand),
        new(
            builtinId: "chatgpt",
            replacement: "ChatGPT",
            aliases: ["chat GPT", "chat G P T"],
            category: WordCategory.Brand),
        new(
            builtinId: "openai",
            replacement: "OpenAI",
            aliases: ["open AI", "open A I"],
            category: WordCategory.Brand),
        new(
            builtinId: "claude",
            replacement: "Claude",
            // "clod" and "clawed" are everyday English words, so they route through LearnedWordCheckStep only.
            aliases: ["clod", "clawed"],
            category: WordCategory.Brand,
            learnedAliases: ["clod", "clawed"]),
        new(
            builtinId: "claudecode",
            replacement: "Claude Code",
            // Keep unambiguous mishearings only; omit "cloud code" and "clod code" (which fuzzy-matches "cloud code").
            aliases: ["clot code", "clawed code", "clawd code", "claude coat", "claude code"],
            category: WordCategory.Brand),
        new(
            builtinId: "api",
            replacement: "API",
            aliases: ["A P I"],
            category: WordCategory.Acronym),
        new(
            builtinId: "cli",
            replacement: "CLI",
            aliases: ["C L I"],
            category: WordCategory.Acronym),
        new(
            builtinId: "vscode",
            replacement: "VS Code",
            aliases: ["vs code", "vscode", "V S code"],
            category: WordCategory.Brand),
        new(
            builtinId: "airpods",
            replacement: "AirPods",
            aliases: ["airpods", "air pods"],
            category: WordCategory.Brand),
        new(
            builtinId: "wifi",
            replacement: "Wi-Fi",
            aliases: ["wifi", "wi fi", "Wi Fi"],
            category: WordCategory.General),
        new(
            builtinId: "gmail",
            replacement: "Gmail",
            aliases: ["gmail", "g mail"],
            category: WordCategory.Brand),
        new(
            builtinId: "gemini",
            replacement: "Gemini",
            aliases: ["gemini"],
            category: WordCategory.Brand),
    ];

    private static readonly Dictionary<string, BuiltinWordDefinition> ByBuiltinId =
        All.ToDictionary(item => item.BuiltinId, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, BuiltinWordDefinition> ByEntryId =
        All.ToDictionary(item => item.Entry.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, BuiltinWordDefinition> ByCanonical =
        All.ToDictionary(item => item.Entry.Replacement, StringComparer.OrdinalIgnoreCase);

    public static BuiltinWordDefinition? FindById(string? builtinId) =>
        string.IsNullOrWhiteSpace(builtinId) ? null : ByBuiltinId.GetValueOrDefault(builtinId.Trim());

    public static BuiltinWordDefinition? FindByEntryId(string? entryId) =>
        string.IsNullOrWhiteSpace(entryId) ? null : ByEntryId.GetValueOrDefault(entryId.Trim());

    public static BuiltinWordDefinition? FindByCanonical(string? canonical) =>
        string.IsNullOrWhiteSpace(canonical) ? null : ByCanonical.GetValueOrDefault(canonical.Trim());

    public static bool IsBuiltinEntryId(string? entryId) => FindByEntryId(entryId) is not null;

    public static string[] NormalizeDeletedIds(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in ids)
        {
            var trimmed = raw?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                var lower = trimmed.ToLowerInvariant();
                if (seen.Add(lower))
                {
                    normalized.Add(lower);
                }
            }
        }

        return normalized.ToArray();
    }

    public static IReadOnlyList<string> WithTombstone(IReadOnlyList<string> existing, string builtinId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentException.ThrowIfNullOrWhiteSpace(builtinId);

        var normalizedId = builtinId.Trim().ToLowerInvariant();
        return existing.Any(id => string.Equals(id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ? existing
            : [.. existing, normalizedId];
    }

    public static IReadOnlyList<string> WithoutTombstone(IReadOnlyList<string> existing, string builtinId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentException.ThrowIfNullOrWhiteSpace(builtinId);

        var normalizedId = builtinId.Trim().ToLowerInvariant();
        return existing.Any(id => string.Equals(id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ? existing.Where(id => !string.Equals(id, normalizedId, StringComparison.OrdinalIgnoreCase)).ToArray()
            : existing;
    }

    /// <summary>
    /// Merges active non-tombstoned, non-overridden built-ins ahead of <paramref name="userWords"/> so
    /// user-authored words and overrides always win alias precedence.
    /// </summary>
    public static IReadOnlyList<CustomWordEntry> Merge(
        IReadOnlyList<CustomWordEntry> userWords,
        IReadOnlyList<string>? deletedBuiltinIds)
    {
        ArgumentNullException.ThrowIfNull(userWords);

        var tombstones = deletedBuiltinIds is { Count: > 0 }
            ? deletedBuiltinIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var userCanonicals = userWords.Count > 0
            ? userWords
                .Where(word => word is not null && !string.IsNullOrWhiteSpace(word.Replacement))
                .Select(word => word.Replacement.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var merged = new List<CustomWordEntry>(All.Count + userWords.Count);
        foreach (var builtin in All)
        {
            if (tombstones is not null && tombstones.Contains(builtin.BuiltinId))
            {
                continue;
            }

            if (userCanonicals is not null && userCanonicals.Contains(builtin.Entry.Replacement))
            {
                continue;
            }

            merged.Add(builtin.Entry);
        }

        if (userWords.Count > 0)
        {
            merged.AddRange(userWords);
        }

        return merged.ToArray();
    }
}
