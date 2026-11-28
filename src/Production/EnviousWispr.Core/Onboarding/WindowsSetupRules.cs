using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Onboarding;

public sealed record SpeechCatalogRuleEntry(
    string DisplayName,
    FinalAsrEngine Engine,
    string SizeLabel,
    long SizeBytes,
    string LanguagesSummary);

public sealed record PolishCatalogRuleEntry(
    string DisplayName,
    string SizeLabel,
    long SizeBytes);

public sealed record SpeechSetupRule(
    string Id,
    bool EffectiveLanguageInParakeet,
    string PrimaryModelId,
    string PrimaryLabel,
    string? SecondaryModelId,
    string? SecondaryLabel,
    string? ParakeetUnavailableReasonTemplate);

public sealed record PolishRuleCondition(
    double? MinRamGb = null,
    double? RamLessThanGb = null,
    int? MinPhysicalCores = null,
    int? PhysicalCoresAtMost = null,
    bool? DiscreteGpuRequired = null,
    double? MinDedicatedVramGb = null,
    double? MinDedicatedVramOrDefaultGb = null,
    double? DefaultVramIfUnknownGb = null,
    bool? LockedNonEnglish = null);

public sealed record PolishUpgradeCondition(
    double MinRamGb,
    double MinDedicatedVramGb);

public enum RuleMatchMode
{
    All,
    Any,
}

public sealed record PolishSetupRule(
    string Id,
    int Tier,
    RuleMatchMode MatchMode,
    IReadOnlyList<PolishRuleCondition> Conditions,
    string PrimaryModelId,
    string PrimaryTag,
    string? SecondaryModelId,
    string? SecondaryTag,
    PolishUpgradeCondition? UpgradedSecondaryCondition = null,
    string? UpgradedSecondaryModelId = null,
    string? UpgradedSecondaryTag = null,
    string? SecondaryAdvisory = null,
    bool SecondaryPromoted = false,
    bool ParallelDownloads = false,
    bool LowSpecPolishAdvisory = false);

/// <summary>Schema-validated data-driven setup rules loaded from the bundled setup-rules.json resource.</summary>
public sealed record WindowsSetupRules(
    int SchemaVersion,
    string Version,
    int Revision,
    double ParallelDownloadsMinRamGb,
    IReadOnlyDictionary<string, SpeechCatalogRuleEntry> SpeechCatalog,
    IReadOnlyDictionary<string, PolishCatalogRuleEntry> PolishCatalog,
    IReadOnlyList<SpeechSetupRule> SpeechRules,
    IReadOnlyList<PolishSetupRule> PolishRules)
{
    public const int ExpectedSchemaVersion = 1;
    public const string EmbeddedResourceLogicalName = "onboarding/setup-rules.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    private static readonly Lazy<WindowsSetupRules> CachedCurrent = new(LoadDefaultOrFallback);

    public static WindowsSetupRules Current => CachedCurrent.Value;

    public static WindowsSetupRules LoadEmbeddedStrict()
    {
        var assembly = typeof(WindowsSetupRules).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceLogicalName)
            ?? throw new InvalidOperationException(
                $"Embedded setup rules resource '{EmbeddedResourceLogicalName}' was not found.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static WindowsSetupRules Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var rules = JsonSerializer.Deserialize<WindowsSetupRules>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize setup rules.");
        Validate(rules);
        return rules;
    }

    public static WindowsSetupRules CreateBuiltInFallback() => new(
        SchemaVersion: ExpectedSchemaVersion,
        Version: "1.0.0",
        Revision: 1,
        ParallelDownloadsMinRamGb: 16d,
        SpeechCatalog: new Dictionary<string, SpeechCatalogRuleEntry>(StringComparer.Ordinal)
        {
            [ParakeetModelIds.Final] = new("Parakeet", FinalAsrEngine.Parakeet, "638 MB", 667_974_615L, "25 languages"),
            [WhisperModelIds.Final] = new("Whisper", FinalAsrEngine.Whisper, "548 MB", 574_041_195L, "99+ languages"),
        },
        PolishCatalog: new Dictionary<string, PolishCatalogRuleEntry>(StringComparer.Ordinal)
        {
            [EgOneModelCatalog.FullId] = new("EG-1", "3.12 GB", 3_161_421_632L),
            [EgOneModelCatalog.MiniId] = new("EG-1 Mini", "1.45 GB", 1_454_782_528L),
            [EgOneModelCatalog.MicroId] = new("EG-1 Micro", "542 MB", 541_898_336L),
        },
        SpeechRules:
        [
            new(
                Id: "speech-parakeet-supported",
                EffectiveLanguageInParakeet: true,
                PrimaryModelId: ParakeetModelIds.Final,
                PrimaryLabel: "Recommended",
                SecondaryModelId: WhisperModelIds.Final,
                SecondaryLabel: "99+ languages",
                ParakeetUnavailableReasonTemplate: null),
            new(
                Id: "speech-whisper-only",
                EffectiveLanguageInParakeet: false,
                PrimaryModelId: WhisperModelIds.Final,
                PrimaryLabel: "Recommended for {language}",
                SecondaryModelId: null,
                SecondaryLabel: null,
                ParakeetUnavailableReasonTemplate: "Doesn't support {language}"),
        ],
        PolishRules:
        [
            new(
                Id: "tier-6-low-spec",
                Tier: 6,
                MatchMode: RuleMatchMode.Any,
                Conditions: [new(RamLessThanGb: 8d), new(PhysicalCoresAtMost: 2)],
                PrimaryModelId: EgOneModelCatalog.MicroId,
                PrimaryTag: "Lightest local option",
                SecondaryModelId: null,
                SecondaryTag: null,
                SecondaryAdvisory: "Consider Cloud API polish or skipping local AI polish on this PC.",
                SecondaryPromoted: false,
                ParallelDownloads: false,
                LowSpecPolishAdvisory: true),
            new(
                Id: "tier-1-discrete-vram6-ram24",
                Tier: 1,
                MatchMode: RuleMatchMode.All,
                Conditions: [new(DiscreteGpuRequired: true, MinDedicatedVramGb: 6d, MinRamGb: 24d)],
                PrimaryModelId: EgOneModelCatalog.FullId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MiniId,
                SecondaryTag: "Smaller download",
                ParallelDownloads: true,
                LowSpecPolishAdvisory: false),
            new(
                Id: "tier-2-discrete-vram4-or-heavy-cpu",
                Tier: 2,
                MatchMode: RuleMatchMode.Any,
                Conditions:
                [
                    new(DiscreteGpuRequired: true, MinDedicatedVramOrDefaultGb: 4d, DefaultVramIfUnknownGb: 4d, MinRamGb: 16d),
                    new(MinRamGb: 32d, MinPhysicalCores: 8),
                ],
                PrimaryModelId: EgOneModelCatalog.MiniId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MicroId,
                SecondaryTag: "Smaller download",
                UpgradedSecondaryCondition: new(MinRamGb: 24d, MinDedicatedVramGb: 6d),
                UpgradedSecondaryModelId: EgOneModelCatalog.FullId,
                UpgradedSecondaryTag: "Even better results",
                ParallelDownloads: true,
                LowSpecPolishAdvisory: false),
            new(
                Id: "tier-3-16gb-quad-plus",
                Tier: 3,
                MatchMode: RuleMatchMode.All,
                Conditions: [new(MinRamGb: 16d, MinPhysicalCores: 4)],
                PrimaryModelId: EgOneModelCatalog.MiniId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MicroId,
                SecondaryTag: "Smaller download",
                ParallelDownloads: true,
                LowSpecPolishAdvisory: false),
            new(
                Id: "tier-3-low-core-fallback",
                Tier: 3,
                MatchMode: RuleMatchMode.All,
                Conditions: [new(MinRamGb: 16d)],
                PrimaryModelId: EgOneModelCatalog.MicroId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MiniId,
                SecondaryTag: "Even better results",
                ParallelDownloads: false,
                LowSpecPolishAdvisory: false),
            new(
                Id: "tier-4-12to16gb-locked-non-english",
                Tier: 4,
                MatchMode: RuleMatchMode.All,
                Conditions: [new(MinRamGb: 12d, RamLessThanGb: 16d, LockedNonEnglish: true)],
                PrimaryModelId: EgOneModelCatalog.MiniId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MicroId,
                SecondaryTag: "Smaller download",
                ParallelDownloads: false,
                LowSpecPolishAdvisory: false),
            new(
                Id: "tier-4-12to16gb-english-or-auto",
                Tier: 4,
                MatchMode: RuleMatchMode.All,
                Conditions: [new(MinRamGb: 12d, RamLessThanGb: 16d, LockedNonEnglish: false)],
                PrimaryModelId: EgOneModelCatalog.MicroId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MiniId,
                SecondaryTag: "Even better results",
                ParallelDownloads: false,
                LowSpecPolishAdvisory: false),
            new(
                Id: "tier-5-10to12gb-locked-non-english",
                Tier: 5,
                MatchMode: RuleMatchMode.All,
                Conditions: [new(MinRamGb: 10d, RamLessThanGb: 12d, MinPhysicalCores: 4, LockedNonEnglish: true)],
                PrimaryModelId: EgOneModelCatalog.MicroId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MiniId,
                SecondaryTag: "Even better results",
                SecondaryAdvisory: "Strong secondary choice for non-English accuracy; uses extra memory on 10 to 12 GB PCs.",
                SecondaryPromoted: true,
                ParallelDownloads: false,
                LowSpecPolishAdvisory: false),
            new(
                Id: "tier-5-8to12gb",
                Tier: 5,
                MatchMode: RuleMatchMode.All,
                Conditions: [new(MinRamGb: 8d, RamLessThanGb: 12d)],
                PrimaryModelId: EgOneModelCatalog.MicroId,
                PrimaryTag: "Recommended for your PC",
                SecondaryModelId: EgOneModelCatalog.MiniId,
                SecondaryTag: "Even better results",
                SecondaryAdvisory: "Uses extra memory on 8 to 12 GB PCs.",
                SecondaryPromoted: false,
                ParallelDownloads: false,
                LowSpecPolishAdvisory: false),
        ]);

    private static WindowsSetupRules LoadDefaultOrFallback()
    {
        try
        {
            return LoadEmbeddedStrict();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException)
        {
            return CreateBuiltInFallback();
        }
    }

    private static void Validate(WindowsSetupRules rules)
    {
        if (rules.SchemaVersion != ExpectedSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported setup-rules schema version {rules.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(rules.Version) || rules.Revision < 1)
        {
            throw new InvalidOperationException("Setup rules must specify a valid version and revision.");
        }

        if (!rules.SpeechCatalog.ContainsKey(ParakeetModelIds.Final) ||
            !rules.SpeechCatalog.ContainsKey(WhisperModelIds.Final))
        {
            throw new InvalidOperationException("Speech catalog must include Parakeet and Whisper model definitions.");
        }

        foreach (var egOneId in EgOneModelCatalog.AllIds)
        {
            if (!rules.PolishCatalog.ContainsKey(egOneId))
            {
                throw new InvalidOperationException($"Polish catalog is missing required model '{egOneId}'.");
            }
        }

        if (rules.SpeechRules.Count == 0 || rules.PolishRules.Count == 0)
        {
            throw new InvalidOperationException("Setup rules must define non-empty speech and polish rule sets.");
        }

        foreach (var rule in rules.SpeechRules)
        {
            if (!rules.SpeechCatalog.ContainsKey(rule.PrimaryModelId) ||
                (rule.SecondaryModelId is not null && !rules.SpeechCatalog.ContainsKey(rule.SecondaryModelId)))
            {
                throw new InvalidOperationException($"Speech rule '{rule.Id}' references unknown model ID.");
            }
        }

        foreach (var rule in rules.PolishRules)
        {
            if (rule.Conditions.Count == 0)
            {
                throw new InvalidOperationException($"Polish rule '{rule.Id}' must declare at least one condition.");
            }

            if (!rules.PolishCatalog.ContainsKey(rule.PrimaryModelId) ||
                (rule.SecondaryModelId is not null && !rules.PolishCatalog.ContainsKey(rule.SecondaryModelId)) ||
                (rule.UpgradedSecondaryModelId is not null && !rules.PolishCatalog.ContainsKey(rule.UpgradedSecondaryModelId)))
            {
                throw new InvalidOperationException($"Polish rule '{rule.Id}' references unknown model ID.");
            }
        }
    }
}
