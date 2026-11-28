using System.Collections.ObjectModel;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Onboarding;

/// <summary>Content-free PC hardware and language inputs for setup recommendations.</summary>
public sealed record SetupHardwareSnapshot(
    double TotalRamGb,
    GpuAccelerationKind GpuKind,
    double? DedicatedVramGb,
    int PhysicalCoreCount,
    int PerformanceCoreCount,
    ProcessorArchitectureKind Architecture,
    double? AvailableDiskGb,
    string? SelectedLanguageCode,
    string? SystemLanguageCode,
    bool CudaRuntimeInstalled);

public sealed record SpeechRecommendation(
    string RuleId,
    string PrimaryModelId,
    FinalAsrEngine PrimaryEngine,
    string PrimaryDisplayName,
    string PrimaryLabel,
    string PrimarySizeLabel,
    long PrimarySizeBytes,
    string? SecondaryModelId,
    FinalAsrEngine? SecondaryEngine,
    string? SecondaryDisplayName,
    string? SecondaryLabel,
    string? SecondarySizeLabel,
    long? SecondarySizeBytes,
    bool IsParakeetAvailable,
    string? ParakeetUnavailableReason,
    string ParakeetSpeedNote,
    string WhisperSpeedNote,
    string HeroTimeText = "Fast local",
    string HeroSubtext = WindowsSetupRecommender.DefaultHeroSubtext,
    string HardwareBadgeText = "Runs locally on your CPU",
    string ParakeetHeroTimeText = "Fast local",
    string WhisperHeroTimeText = "99+ langs",
    string ParakeetHardwareBadgeText = "Runs locally on your CPU",
    string WhisperHardwareBadgeText = "Runs locally on your CPU");

public sealed record PolishRecommendation(
    string RuleId,
    int Tier,
    string PrimaryModelId,
    string PrimaryDisplayName,
    string PrimaryTag,
    string PrimarySizeLabel,
    long PrimarySizeBytes,
    string? SecondaryModelId,
    string? SecondaryDisplayName,
    string? SecondaryTag,
    string? SecondarySizeLabel,
    long? SecondarySizeBytes,
    string? SecondaryAdvisory,
    bool SecondaryPromoted,
    bool LowSpecPolishAdvisory,
    bool ParallelDownloads);

public sealed record WindowsSetupRecommendation(
    SpeechRecommendation Speech,
    PolishRecommendation Polish,
    LanguageEntry? EffectiveLanguageEntry,
    bool IsLockedNonEnglish,
    string RulesVersion,
    int RulesRevision);

/// <summary>Pure deterministic recommendation engine pairing setup-rules.json with Windows hardware profiling.</summary>
public static class WindowsSetupRecommender
{
    public const string DefaultHeroSubtext = "Private local transcription on this PC.";

    /// <summary>
    /// Languages Parakeet technically lists in its 25-language lock table that should still recommend
    /// Whisper during setup advice (matching macOS 1e6ee7016: Greek drops final sigma in Parakeet).
    /// </summary>
    public static IReadOnlySet<string> ParakeetAdviceExclusions { get; } = new ReadOnlySet<string>(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "el",
        });

    public static WindowsSetupRecommendation Recommend(
        SetupHardwareSnapshot snapshot,
        WindowsSetupRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var activeRules = rules ?? WindowsSetupRules.Current;

        var selectedEntry = ResolveSelectedEntry(snapshot.SelectedLanguageCode);
        var isLockedNonEnglish = selectedEntry is not null &&
            !string.Equals(selectedEntry.LockCode, "en", StringComparison.Ordinal);

        var effectiveEntry = selectedEntry ?? ResolveSelectedEntry(
            SetupLanguageMetadata.ResolveSystemLanguageCode(snapshot.SystemLanguageCode));
        var isParakeetAdviceExcluded = effectiveEntry is not null &&
            ParakeetAdviceExclusions.Contains(effectiveEntry.LockCode);
        var isParakeetSupported = effectiveEntry is null ||
            LanguageCatalog.ParakeetSupportedCodes.Contains(effectiveEntry.LockCode);
        var recommendParakeet = isParakeetSupported && !isParakeetAdviceExcluded;

        var speech = EvaluateSpeech(snapshot, activeRules, effectiveEntry, isParakeetSupported, recommendParakeet, isParakeetAdviceExcluded);
        var polish = EvaluatePolish(snapshot, activeRules, isLockedNonEnglish);

        return new WindowsSetupRecommendation(
            Speech: speech,
            Polish: polish,
            EffectiveLanguageEntry: effectiveEntry,
            IsLockedNonEnglish: isLockedNonEnglish,
            RulesVersion: activeRules.Version,
            RulesRevision: activeRules.Revision);
    }

    public static string EstimateSpeechHeroTime(FinalAsrEngine engine, SetupHardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var usesNvidiaCuda = snapshot.GpuKind == GpuAccelerationKind.NvidiaCuda && snapshot.CudaRuntimeInstalled;
        return engine switch
        {
            FinalAsrEngine.Whisper => usesNvidiaCuda ? "GPU fast" : "99+ langs",
            _ => usesNvidiaCuda ? "Fastest" : "Fast local",
        };
    }

    public static string DescribeSpeechHardwareBadge(FinalAsrEngine engine, SetupHardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = engine;
        var usesNvidiaCuda = snapshot.GpuKind == GpuAccelerationKind.NvidiaCuda && snapshot.CudaRuntimeInstalled;
        return usesNvidiaCuda
            ? "Runs on your NVIDIA GPU"
            : "Runs locally on your CPU";
    }

    private static LanguageEntry? ResolveSelectedEntry(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode) ||
            string.Equals(rawCode.Trim(), LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return LanguageCatalog.TryGetEntry(rawCode, out var entry) ? entry : null;
    }

    private static SpeechRecommendation EvaluateSpeech(
        SetupHardwareSnapshot snapshot,
        WindowsSetupRules rules,
        LanguageEntry? effectiveEntry,
        bool isParakeetSupported,
        bool recommendParakeet,
        bool isParakeetAdviceExcluded)
    {
        var rule = rules.SpeechRules.FirstOrDefault(candidate => candidate.EffectiveLanguageInParakeet == recommendParakeet)
            ?? rules.SpeechRules[0];
        var languageDisplay = effectiveEntry?.EnglishName ?? "your language";

        var primaryCatalog = rules.SpeechCatalog[rule.PrimaryModelId];
        SpeechCatalogRuleEntry? secondaryCatalog = rule.SecondaryModelId is not null
            ? rules.SpeechCatalog[rule.SecondaryModelId]
            : null;

        var primaryLabel = FormatTemplate(rule.PrimaryLabel, languageDisplay);
        var secondaryLabel = rule.SecondaryLabel is null ? null : FormatTemplate(rule.SecondaryLabel, languageDisplay);
        var parakeetUnavailableReason = isParakeetAdviceExcluded
            ? $"Whisper is more accurate for {languageDisplay}"
            : rule.ParakeetUnavailableReasonTemplate is null
                ? null
                : FormatTemplate(rule.ParakeetUnavailableReasonTemplate, languageDisplay);

        var parakeetSpeedNote = snapshot.GpuKind == GpuAccelerationKind.NvidiaCuda
            ? "Fast local transcription across 25 European languages on this PC."
            : "Fast local transcription on your CPU across 25 European languages.";

        var whisperSpeedNote = snapshot.GpuKind == GpuAccelerationKind.NvidiaCuda
            ? snapshot.CudaRuntimeInstalled
                ? "Accelerated on your NVIDIA GPU via CUDA for fast local transcription."
                : "Accelerated on your NVIDIA GPU when CUDA is installed (runs locally on CPU otherwise)."
            : "Runs locally on your CPU. Covers 99+ languages, though slower than Parakeet on CPU.";

        var parakeetHeroTime = EstimateSpeechHeroTime(FinalAsrEngine.Parakeet, snapshot);
        var whisperHeroTime = EstimateSpeechHeroTime(FinalAsrEngine.Whisper, snapshot);
        var parakeetHardwareBadge = DescribeSpeechHardwareBadge(FinalAsrEngine.Parakeet, snapshot);
        var whisperHardwareBadge = DescribeSpeechHardwareBadge(FinalAsrEngine.Whisper, snapshot);
        var primaryHeroTime = primaryCatalog.Engine == FinalAsrEngine.Whisper ? whisperHeroTime : parakeetHeroTime;
        var primaryHardwareBadge = primaryCatalog.Engine == FinalAsrEngine.Whisper
            ? whisperHardwareBadge
            : parakeetHardwareBadge;

        return new SpeechRecommendation(
            RuleId: rule.Id,
            PrimaryModelId: rule.PrimaryModelId,
            PrimaryEngine: primaryCatalog.Engine,
            PrimaryDisplayName: primaryCatalog.DisplayName,
            PrimaryLabel: primaryLabel,
            PrimarySizeLabel: WindowsSetupRules.FormatDownloadLabel(primaryCatalog.SizeLabel, primaryCatalog.SizeBytes),
            PrimarySizeBytes: primaryCatalog.SizeBytes,
            SecondaryModelId: rule.SecondaryModelId,
            SecondaryEngine: secondaryCatalog?.Engine,
            SecondaryDisplayName: secondaryCatalog?.DisplayName,
            SecondaryLabel: secondaryLabel,
            SecondarySizeLabel: secondaryCatalog is null
                ? null
                : WindowsSetupRules.FormatDownloadLabel(secondaryCatalog.SizeLabel, secondaryCatalog.SizeBytes),
            SecondarySizeBytes: secondaryCatalog?.SizeBytes,
            IsParakeetAvailable: isParakeetSupported,
            ParakeetUnavailableReason: parakeetUnavailableReason,
            ParakeetSpeedNote: parakeetSpeedNote,
            WhisperSpeedNote: whisperSpeedNote,
            HeroTimeText: primaryHeroTime,
            HeroSubtext: DefaultHeroSubtext,
            HardwareBadgeText: primaryHardwareBadge,
            ParakeetHeroTimeText: parakeetHeroTime,
            WhisperHeroTimeText: whisperHeroTime,
            ParakeetHardwareBadgeText: parakeetHardwareBadge,
            WhisperHardwareBadgeText: whisperHardwareBadge);
    }

    private static PolishRecommendation EvaluatePolish(
        SetupHardwareSnapshot snapshot,
        WindowsSetupRules rules,
        bool isLockedNonEnglish)
    {
        var matchedRule = rules.PolishRules.FirstOrDefault(rule => MatchesPolishRule(rule, snapshot, isLockedNonEnglish))
            ?? rules.PolishRules[^1];

        var primaryCatalog = rules.PolishCatalog[matchedRule.PrimaryModelId];

        var secondaryId = matchedRule.SecondaryModelId;
        var secondaryTag = matchedRule.SecondaryTag;
        if (matchedRule.UpgradedSecondaryCondition is { } upgradeCondition &&
            matchedRule.UpgradedSecondaryModelId is not null &&
            (snapshot.TotalRamGb >= upgradeCondition.MinRamGb ||
             (snapshot.DedicatedVramGb ?? 0d) >= upgradeCondition.MinDedicatedVramGb))
        {
            secondaryId = matchedRule.UpgradedSecondaryModelId;
            secondaryTag = matchedRule.UpgradedSecondaryTag;
        }

        PolishCatalogRuleEntry? secondaryCatalog = secondaryId is not null
            ? rules.PolishCatalog[secondaryId]
            : null;

        return new PolishRecommendation(
            RuleId: matchedRule.Id,
            Tier: matchedRule.Tier,
            PrimaryModelId: matchedRule.PrimaryModelId,
            PrimaryDisplayName: primaryCatalog.DisplayName,
            PrimaryTag: matchedRule.PrimaryTag,
            PrimarySizeLabel: WindowsSetupRules.FormatDownloadLabel(primaryCatalog.SizeLabel, primaryCatalog.SizeBytes),
            PrimarySizeBytes: primaryCatalog.SizeBytes,
            SecondaryModelId: secondaryId,
            SecondaryDisplayName: secondaryCatalog?.DisplayName,
            SecondaryTag: secondaryTag,
            SecondarySizeLabel: secondaryCatalog is null
                ? null
                : WindowsSetupRules.FormatDownloadLabel(secondaryCatalog.SizeLabel, secondaryCatalog.SizeBytes),
            SecondarySizeBytes: secondaryCatalog?.SizeBytes,
            SecondaryAdvisory: matchedRule.SecondaryAdvisory,
            SecondaryPromoted: matchedRule.SecondaryPromoted,
            LowSpecPolishAdvisory: matchedRule.LowSpecPolishAdvisory,
            ParallelDownloads: matchedRule.ParallelDownloads);
    }

    private static bool MatchesPolishRule(
        PolishSetupRule rule,
        SetupHardwareSnapshot snapshot,
        bool isLockedNonEnglish) =>
        rule.MatchMode switch
        {
            RuleMatchMode.Any => rule.Conditions.Any(c => MatchesCondition(c, snapshot, isLockedNonEnglish)),
            _ => rule.Conditions.All(c => MatchesCondition(c, snapshot, isLockedNonEnglish)),
        };

    private static bool MatchesCondition(
        PolishRuleCondition condition,
        SetupHardwareSnapshot snapshot,
        bool isLockedNonEnglish)
    {
        if (condition.MinRamGb is { } minRam && snapshot.TotalRamGb < minRam)
        {
            return false;
        }

        if (condition.RamLessThanGb is { } maxRamExclusive && snapshot.TotalRamGb >= maxRamExclusive)
        {
            return false;
        }

        if (condition.MinPhysicalCores is { } minCores && snapshot.PhysicalCoreCount < minCores)
        {
            return false;
        }

        if (condition.PhysicalCoresAtMost is { } maxCores && snapshot.PhysicalCoreCount > maxCores)
        {
            return false;
        }

        if (condition.DiscreteGpuRequired == true &&
            snapshot.GpuKind is not (GpuAccelerationKind.NvidiaCuda or GpuAccelerationKind.DiscreteVulkan))
        {
            return false;
        }

        if (condition.MinDedicatedVramGb is { } minVram &&
            (snapshot.DedicatedVramGb ?? 0d) < minVram)
        {
            return false;
        }

        if (condition.MinDedicatedVramOrDefaultGb is { } minOrDefaultVram)
        {
            var effectiveVram = snapshot.DedicatedVramGb ?? condition.DefaultVramIfUnknownGb ?? 0d;
            if (effectiveVram < minOrDefaultVram)
            {
                return false;
            }
        }

        if (condition.LockedNonEnglish is { } lockedReq && isLockedNonEnglish != lockedReq)
        {
            return false;
        }

        return true;
    }

    private static string FormatTemplate(string template, string languageDisplay) =>
        template.Replace("{language}", languageDisplay, StringComparison.Ordinal);
}
