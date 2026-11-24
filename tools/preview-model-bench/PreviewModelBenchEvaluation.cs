using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.PreviewModelBench;

public enum BenchRole
{
    Preview,
    Final,
}

internal static class BenchRowClassification
{
    public const string Primary = "primary";
    public const string Fallback = "fallback";
    public const string Empty = "empty";
    public const string Error = "error";
}

public static class BenchRubricTier
{
    public const string Ship = "Ship";
    public const string Experimental = "Experimental";
    public const string Hide = "Hide";
}

internal sealed record BenchCliOptions(
    string RepositoryRoot,
    string? ModelsRoot,
    string? ReleaseChannel,
    string? AudioDirectory,
    string? ExtraModels,
    string FixturesDirectory,
    string? CorpusManifestPath,
    string? WavRoot,
    int? MaxFixtures,
    IReadOnlyList<string>? Languages,
    bool AccuracyOnly,
    int Repeats,
    RuntimeProviderKind[] Providers,
    string? Only,
    BenchRole Role,
    string RequestedLanguage,
    string? PrefixReferencesPath,
    string OutputPath,
    string? MarkdownOutputPath,
    bool AllowBusyCard,
    bool ShowHelp);

internal sealed record CorpusManifestEntry(
    string Id,
    string File,
    string Language,
    string Reference,
    string AudioPath,
    string SourceFormat);

internal sealed record LanguageMetricsSummary(
    Dictionary<string, double?> WordErrorRateByLanguage,
    double? WordErrorRate,
    Dictionary<string, double?> CharacterErrorRateByLanguage,
    double? CharacterErrorRate,
    Dictionary<string, double?> DetectedLanguageMatchRateByLanguage,
    double? DetectedLanguageMatchRate,
    Dictionary<string, string?> RubricTierByLanguage,
    string? RubricTier);

internal sealed record Candidate(
    string Name,
    FinalAsrEngine Engine,
    WhisperModelPack Pack,
    string? ModelDirectory,
    string? ModelFile,
    ParakeetModelPack ParakeetPack = ParakeetModelPack.Quantized);

internal sealed record Clip(string Name, float[] Samples);

internal sealed record Fixture(string File, string Language, string Reference, float[] Samples);

internal sealed record Pass(
    string Clip,
    double WindowSeconds,
    long Milliseconds,
    string? RequestedLanguage = null,
    string? DetectedLanguage = null,
    bool UsedFallback = false,
    string FailureCategory = "none",
    string Classification = BenchRowClassification.Primary,
    bool WorkerRestarted = false);

internal sealed record FixtureResult(
    string File,
    string Language,
    int ReferenceWords,
    int? EditDistance,
    long Milliseconds,
    string Transcript,
    string? RequestedLanguage = null,
    string? DetectedLanguage = null,
    bool UsedFallback = false,
    string FailureCategory = "none",
    string Classification = BenchRowClassification.Primary,
    int ActualWords = 0,
    double? WordErrorRate = null,
    bool WorkerRestarted = false,
    int ReferenceCharacters = 0,
    int ActualCharacters = 0,
    int? CharacterEditDistance = null,
    double? CharacterErrorRate = null,
    string PrimaryMetric = "wer",
    bool? DetectedLanguageMatched = null,
    string? RubricTier = null);

internal sealed record PrefixReferenceEntry(
    string File,
    double WindowSeconds,
    string Reference,
    string Provenance = "external-prefix-reference",
    string? Confidence = null,
    string? Modality = null,
    IReadOnlyList<string>? UnresolvedWords = null,
    double? MaxTokenEndSeconds = null,
    string? AlignedFromFullReference = null);

internal sealed record PrefixFixtureResult(
    string File,
    string Language,
    double WindowSeconds,
    string Provenance,
    int ReferenceWords,
    int ActualWords,
    int? EditDistance,
    double? WordErrorRate,
    long Milliseconds,
    string Reference,
    string Transcript,
    string? RequestedLanguage = null,
    string? DetectedLanguage = null,
    bool UsedFallback = false,
    string FailureCategory = "none",
    string Classification = BenchRowClassification.Primary,
    string? Confidence = null,
    string? Modality = null,
    IReadOnlyList<string>? UnresolvedWords = null,
    bool WorkerRestarted = false);

internal sealed record CandidateResult(
    string Model,
    string Provider,
    string? Error,
    long? ColdStartMilliseconds,
    Dictionary<string, long?>? MedianPassMilliseconds,
    int? PassesOverCadence,
    int Passes,
    Dictionary<string, double?>? WordErrorRateByLanguage,
    double? WordErrorRate,
    double? PeakWorkingSetMegabytes,
    double? VideoMemoryMegabytes,
    int? CardLoadBeforePercent,
    List<FixtureResult>? Fixtures,
    List<Pass>? PassDetails,
    string Role = "preview",
    int IntraOpThreads = 0,
    int CpuFallbackThreads = 0,
    string WorkerPriority = "BelowNormal",
    string? RequestedLanguage = null,
    string? DetectedLanguage = null,
    bool UsedFallback = false,
    string FailureCategory = "none",
    string Classification = BenchRowClassification.Primary,
    int PrimaryFixtureRows = 0,
    int FallbackFixtureRows = 0,
    int EmptyFixtureRows = 0,
    int ErrorFixtureRows = 0,
    double? PrefixWordErrorRate = null,
    Dictionary<string, double?>? PrefixWordErrorRateByWindow = null,
    Dictionary<string, double?>? PrefixWordErrorRateByLanguage = null,
    List<PrefixFixtureResult>? PrefixFixtures = null,
    int? PrefixZeroReferenceInsertions = null,
    int WorkerRestarts = 0,
    Dictionary<string, double?>? CharacterErrorRateByLanguage = null,
    double? CharacterErrorRate = null,
    Dictionary<string, double?>? DetectedLanguageMatchRateByLanguage = null,
    double? DetectedLanguageMatchRate = null,
    Dictionary<string, string?>? RubricTierByLanguage = null,
    string? RubricTier = null)
{
    public static CandidateResult Failed(
        string model,
        RuntimeProviderKind provider,
        string error,
        BenchRole role = BenchRole.Preview,
        int intraOpThreads = 0,
        int cpuFallbackThreads = 0,
        ProcessPriorityClass workerPriority = ProcessPriorityClass.BelowNormal,
        string? requestedLanguage = null,
        string? detectedLanguage = null,
        bool usedFallback = false,
        string failureCategory = "worker-error",
        List<FixtureResult>? fixtures = null,
        List<Pass>? passDetails = null,
        List<PrefixFixtureResult>? prefixFixtures = null,
        int workerRestarts = 0) =>
        new(
            model,
            provider.ToString(),
            error,
            ColdStartMilliseconds: null,
            MedianPassMilliseconds: null,
            PassesOverCadence: null,
            Passes: passDetails?.Count ?? 0,
            WordErrorRateByLanguage: null,
            WordErrorRate: null,
            PeakWorkingSetMegabytes: null,
            VideoMemoryMegabytes: null,
            CardLoadBeforePercent: null,
            Fixtures: fixtures,
            PassDetails: passDetails,
            Role: FormatRole(role),
            IntraOpThreads: intraOpThreads,
            CpuFallbackThreads: cpuFallbackThreads,
            WorkerPriority: workerPriority.ToString(),
            RequestedLanguage: requestedLanguage,
            DetectedLanguage: detectedLanguage,
            UsedFallback: usedFallback,
            FailureCategory: failureCategory,
            Classification: usedFallback ? BenchRowClassification.Fallback : BenchRowClassification.Error,
            PrimaryFixtureRows: fixtures?.Count(f => f.Classification == BenchRowClassification.Primary) ?? 0,
            FallbackFixtureRows: fixtures?.Count(f => f.Classification == BenchRowClassification.Fallback) ?? 0,
            EmptyFixtureRows: fixtures?.Count(f => f.Classification == BenchRowClassification.Empty) ?? 0,
            ErrorFixtureRows: fixtures?.Count(f => f.Classification == BenchRowClassification.Error) ?? 0,
            PrefixWordErrorRate: null,
            PrefixWordErrorRateByWindow: null,
            PrefixWordErrorRateByLanguage: null,
            PrefixFixtures: prefixFixtures,
            PrefixZeroReferenceInsertions: null,
            WorkerRestarts: workerRestarts,
            CharacterErrorRateByLanguage: null,
            CharacterErrorRate: null,
            DetectedLanguageMatchRateByLanguage: null,
            DetectedLanguageMatchRate: null,
            RubricTierByLanguage: null,
            RubricTier: null);

    public static string FormatRole(BenchRole role) =>
        role == BenchRole.Final ? "final" : "preview";
}

internal static class PreviewModelBenchEvaluation
{
    public const int SampleRate = 16_000;
    public const int MinimumWindowSamples = 257;

    public static BenchRole ParseRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "preview", StringComparison.OrdinalIgnoreCase))
        {
            return BenchRole.Preview;
        }

        if (string.Equals(value.Trim(), "final", StringComparison.OrdinalIgnoreCase))
        {
            return BenchRole.Final;
        }

        throw new ArgumentException(
            $"Unsupported --role '{value}'. Expected 'preview' or 'final'.",
            nameof(value));
    }

    public static int ChoosePreviewThreads(HardwareSnapshot hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        return Math.Clamp(
            hardware.PhysicalCoreCount > 0
                ? hardware.PhysicalCoreCount
                : Math.Max(1, hardware.LogicalProcessorCount / 2),
            2,
            8);
    }

    public static string? EffectiveRequestedLanguage(FinalAsrEngine engine, string? language)
    {
        if (engine != FinalAsrEngine.Whisper)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(language) ||
            string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : language.Trim().ToLowerInvariant();
    }

    private static readonly HttpClient ModelStoreHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Resolves the active application data directory using the same precedence as <c>App</c>:
    /// <c>ENVIOUSWISPR_DATA_DIRECTORY</c> override first, then
    /// <c>%LocalAppData%\Envious Labs\&lt;ReleaseIdentity.DataDirectoryName&gt;</c> for the active release
    /// channel (<c>--channel</c>, <c>ENVIOUSWISPR_RELEASE_CHANNEL</c>, or assembly metadata).
    /// </summary>
    public static string ResolveActiveAppDataDirectory(
        string? configuredDataDirectory = null,
        string? releaseChannel = null)
    {
        var dataDirectory = configuredDataDirectory
            ?? Environment.GetEnvironmentVariable(CudaRuntimeDirectory.DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            return Path.GetFullPath(dataDirectory);
        }

        var channelText = releaseChannel
            ?? Environment.GetEnvironmentVariable("ENVIOUSWISPR_RELEASE_CHANNEL")
            ?? typeof(PreviewModelBenchEvaluation).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => string.Equals(
                    attribute.Key,
                    "EnviousWisprReleaseChannel",
                    StringComparison.Ordinal))
                ?.Value;
        if (!ReleaseIdentity.TryParse(channelText, out var identity))
        {
            throw new ArgumentException($"Unsupported release channel '{channelText}'.", nameof(releaseChannel));
        }

        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Envious Labs",
            identity.DataDirectoryName));
    }

    /// <summary>
    /// Resolves the CUDA runtime directory for the selected app data directory using the same verified
    /// store lookup and <see cref="CudaRuntimeDirectory.ForApplication"/> order as <c>App</c>, without
    /// crossing into another release channel's data directory.
    /// </summary>
    public static string? ResolveCudaRuntimeDirectory(
        string activeAppDataDirectory,
        string? configuredCudaDirectory = null,
        bool useEnvironmentOverride = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeAppDataDirectory);
        var modelsRoot = Path.Combine(activeAppDataDirectory, "models");
        var verifiedPackDirectory = TryReadActiveStoreDirectory(modelsRoot, CudaRuntimeDirectory.PackId);
        var effectiveConfigured = configuredCudaDirectory
            ?? (useEnvironmentOverride ? Environment.GetEnvironmentVariable(CudaRuntimeDirectory.EnvironmentVariable) : null);
        return CudaRuntimeDirectory.Resolve(
            effectiveConfigured,
            verifiedPackDirectory,
            [activeAppDataDirectory],
            Directory.Exists,
            CudaRuntimeDirectory.HoldsRuntime);
    }

    /// <summary>
    /// Resolves a candidate's model directory using the store's verified active pointer and
    /// <see cref="InstalledModelLocator.Resolve"/> in the production order: explicit <c>--models</c> root
    /// or <c>ENVIOUSWISPR_MODEL_DIRECTORY</c> / <c>ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY</c> override,
    /// store-activated version verified by <see cref="ModelStore.OpenActiveOfflineAsync"/> under the
    /// active app data directory (<c>ENVIOUSWISPR_DATA_DIRECTORY</c> or the selected release channel),
    /// complete legacy copy under <c>models/&lt;modelId&gt;</c>, and finally the repository checkout's
    /// <c>models/&lt;modelId&gt;</c>. Fails closed (<see langword="null"/>) when an explicit <c>--models</c>
    /// root or configured override lacks a complete copy of the candidate model.
    /// </summary>
    public static string? ResolveCandidateModelDirectory(
        string modelId,
        Func<string, bool> isComplete,
        string? explicitModelsRoot,
        string repositoryRoot,
        IReadOnlyList<string>? dataModelsRoots = null,
        string? configuredDirectory = null,
        string? configuredDataDirectory = null,
        string? releaseChannel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(isComplete);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (!string.IsNullOrWhiteSpace(explicitModelsRoot))
        {
            return ResolveFromSingleRoot(explicitModelsRoot, modelId, isComplete);
        }

        var configuredOverride = configuredDirectory ?? GetConfiguredModelDirectoryOverride(modelId);
        if (!string.IsNullOrWhiteSpace(configuredOverride))
        {
            return ResolveFromSingleRoot(configuredOverride, modelId, isComplete);
        }

        var developmentDirectory = Path.Combine(repositoryRoot, "models", modelId);
        var roots = dataModelsRoots ??
        [
            Path.Combine(ResolveActiveAppDataDirectory(configuredDataDirectory, releaseChannel), "models"),
        ];

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var activeStoreDirectory = TryReadActiveStoreDirectory(root, modelId);
            var resolved = InstalledModelLocator.Resolve(
                configuredDirectory: null,
                activeStoreDirectory: activeStoreDirectory,
                legacyDirectory: Path.Combine(root, modelId),
                developmentDirectory: null,
                isComplete);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return InstalledModelLocator.Resolve(
            configuredDirectory: null,
            activeStoreDirectory: null,
            legacyDirectory: developmentDirectory,
            developmentDirectory: developmentDirectory,
            isComplete);
    }

    private static string? ResolveFromSingleRoot(
        string rootOrModelDirectory,
        string modelId,
        Func<string, bool> isComplete)
    {
        var active = TryReadActiveStoreDirectory(rootOrModelDirectory, modelId);
        var resolved = InstalledModelLocator.Resolve(
            configuredDirectory: null,
            activeStoreDirectory: active,
            legacyDirectory: Path.Combine(rootOrModelDirectory, modelId),
            developmentDirectory: null,
            isComplete);
        if (resolved is not null)
        {
            return resolved;
        }

        return Directory.Exists(rootOrModelDirectory) && isComplete(rootOrModelDirectory)
            ? Path.GetFullPath(rootOrModelDirectory)
            : null;
    }

    private static string? GetConfiguredModelDirectoryOverride(string modelId)
    {
        var variable = string.Equals(modelId, WhisperModelIds.Preview, StringComparison.Ordinal)
            ? "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY"
            : "ENVIOUSWISPR_MODEL_DIRECTORY";
        return Environment.GetEnvironmentVariable(variable);
    }

    private static string? TryReadActiveStoreDirectory(string modelsRoot, string modelId)
    {
        if (!ModelManifestVerifier.IsSafeModelId(modelId))
        {
            return null;
        }

        try
        {
            var store = new ModelStore(
                modelsRoot,
                ModelStoreHttpClient,
                new ModelManifestVerifier(new Dictionary<string, string>()),
                typeof(PreviewModelBenchEvaluation).Assembly.GetName().Version ?? new Version(1, 0, 0));
            var active = store.OpenActiveOfflineAsync(modelId).GetAwaiter().GetResult();
            return active.Succeeded ? active.Installed?.DirectoryPath : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds worker options for the requested role so preview runs match <c>ConfigureLivePreview</c>
    /// (<c>BelowNormal</c> priority, preview thread rule, 15 s startup and transcription timeouts) and
    /// final runs match the production final selectors (<c>Normal</c> priority,
    /// <c>ParakeetRuntimeSelector</c> / <c>WhisperRuntimeSelector</c> thread counts, 30 s startup and
    /// 2 min transcription timeouts).
    /// </summary>
    public static RuntimeWorkerTranscriptionOptions CreateWorkerOptions(
        BenchRole role,
        string workerExecutable,
        Candidate candidate,
        string modelDirectory,
        RuntimeProviderKind provider,
        HardwareSnapshot hardware,
        string? language = "auto",
        string? cudaRuntimeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutable);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(hardware);

        var requestedLanguage = EffectiveRequestedLanguage(candidate.Engine, language);
        if (role == BenchRole.Preview)
        {
            var previewThreads = ChoosePreviewThreads(hardware);
            return new RuntimeWorkerTranscriptionOptions(
                workerExecutable,
                modelDirectory,
                provider,
                candidate.ParakeetPack,
                IntraOpThreads: previewThreads,
                InterOpThreads: 1,
                CpuFallbackThreads: previewThreads,
                CudaRuntimeDirectory: cudaRuntimeDirectory,
                StartupTimeout: TimeSpan.FromSeconds(15),
                TranscriptionTimeout: TimeSpan.FromSeconds(15),
                MaximumWorkerRestarts: 0,
                Engine: candidate.Engine,
                WhisperPack: candidate.Pack,
                Language: requestedLanguage,
                WorkerPriority: ProcessPriorityClass.BelowNormal);
        }

        int intraOpThreads;
        int cpuFallbackThreads;
        if (candidate.Engine == FinalAsrEngine.Parakeet)
        {
            cpuFallbackThreads = ParakeetRuntimeSelector.ChooseCpuIntraOpThreads(hardware);
            intraOpThreads = provider == RuntimeProviderKind.Cuda ? 1 : cpuFallbackThreads;
        }
        else
        {
            var whisperThreads = ChoosePreviewThreads(hardware);
            intraOpThreads = whisperThreads;
            cpuFallbackThreads = whisperThreads;
        }

        return new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            provider,
            candidate.ParakeetPack,
            IntraOpThreads: intraOpThreads,
            InterOpThreads: 1,
            CpuFallbackThreads: cpuFallbackThreads,
            CudaRuntimeDirectory: cudaRuntimeDirectory,
            StartupTimeout: TimeSpan.FromSeconds(30),
            TranscriptionTimeout: TimeSpan.FromMinutes(2),
            MaximumWorkerRestarts: 1,
            Engine: candidate.Engine,
            WhisperPack: candidate.Pack,
            Language: requestedLanguage,
            WorkerPriority: ProcessPriorityClass.Normal);
    }

    /// <summary>
    /// Unspaced scripts evaluated with Character Error Rate (CER) rather than Word Error Rate (WER),
    /// matching macOS <c>scripts/multilingual-eval/score.py</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> CerLanguages = new HashSet<string>(
        ["zh", "ja", "ko", "th"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Caseless scripts for which lowercasing is skipped during normalization, matching macOS
    /// <c>scripts/multilingual-eval/score.py</c> (<c>NO_LOWER_LANGS</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> CaselessLanguages = new HashSet<string>(
        ["ja", "zh", "ko", "th", "ar", "he", "hi", "bn", "ta", "te"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CJK and ideographic punctuation stripped alongside ASCII and general Unicode punctuation.
    /// </summary>
    public const string CjkPunctuation = "。、！？「」『』（）【】《》…—–·，；：・〜［］〈〉．～";

    public const double ShipWerThreshold = 0.05;
    public const double ExperimentalWerThreshold = 0.15;
    public const double ShipCerThreshold = 0.10;
    public const double ExperimentalCerThreshold = 0.20;

    private static readonly HashSet<char> CjkPunctuationSet = [.. CjkPunctuation];

    private static readonly Dictionary<string, string> KnownLanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en",
        ["german"] = "de",
        ["chinese"] = "zh",
        ["mandarin"] = "zh",
        ["spanish"] = "es",
        ["french"] = "fr",
        ["italian"] = "it",
        ["portuguese"] = "pt",
        ["russian"] = "ru",
        ["dutch"] = "nl",
        ["polish"] = "pl",
        ["japanese"] = "ja",
        ["korean"] = "ko",
        ["thai"] = "th",
        ["arabic"] = "ar",
        ["hebrew"] = "he",
        ["hindi"] = "hi",
        ["bengali"] = "bn",
        ["tamil"] = "ta",
        ["telugu"] = "te",
        ["greek"] = "el",
        ["ukrainian"] = "uk",
    };

    /// <summary>
    /// Normalizes a language tag, locale, or recognizer language name (e.g. <c>de-DE</c>, <c>zh_CN</c>,
    /// <c>German</c>) to its lowercase base ISO 639 subtag (<c>de</c>, <c>zh</c>).
    /// </summary>
    public static string? NormalizeLanguageSubtag(string? languageOrLocale)
    {
        if (string.IsNullOrWhiteSpace(languageOrLocale))
        {
            return null;
        }

        var trimmed = languageOrLocale.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (KnownLanguageNames.TryGetValue(trimmed, out var mappedIso))
        {
            return mappedIso;
        }

        var primary = trimmed.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0];
        if (KnownLanguageNames.TryGetValue(primary, out var mappedPrimary))
        {
            return mappedPrimary;
        }

        return primary.ToLowerInvariant();
    }

    public static bool UsesCharacterErrorRate(string? language)
    {
        var baseLang = NormalizeLanguageSubtag(language);
        return baseLang is not null && CerLanguages.Contains(baseLang);
    }

    public static bool IsCaselessLanguage(string? language)
    {
        var baseLang = NormalizeLanguageSubtag(language);
        return baseLang is not null && CaselessLanguages.Contains(baseLang);
    }

    public static string MetricForLanguage(string? language) =>
        UsesCharacterErrorRate(language) ? "cer" : "wer";

    public static bool IsDetectedLanguageMatch(string? detectedLanguage, string? expectedLanguage)
    {
        var normalizedDetected = NormalizeLanguageSubtag(detectedLanguage);
        var normalizedExpected = NormalizeLanguageSubtag(expectedLanguage);
        if (string.IsNullOrWhiteSpace(normalizedDetected) ||
            string.IsNullOrWhiteSpace(normalizedExpected) ||
            string.Equals(normalizedDetected, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedExpected, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(normalizedDetected, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies a language's WER or CER into the macOS <c>rubric.md</c> / <c>score.py</c> tiers:
    /// <c>Ship</c> (&lt;5% WER / &lt;10% CER), <c>Experimental</c> (5 to &lt;15% WER / 10 to &lt;20% CER),
    /// or <c>Hide</c> (&gt;=15% WER / &gt;=20% CER).
    /// </summary>
    public static string? ClassifyRubricTier(string? language, double? errorRate)
    {
        if (errorRate is not { } score || !double.IsFinite(score))
        {
            return null;
        }

        if (score < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(errorRate), "Error rate cannot be negative.");
        }

        if (UsesCharacterErrorRate(language))
        {
            if (score < ShipCerThreshold)
            {
                return BenchRubricTier.Ship;
            }

            if (score < ExperimentalCerThreshold)
            {
                return BenchRubricTier.Experimental;
            }

            return BenchRubricTier.Hide;
        }

        if (score < ShipWerThreshold)
        {
            return BenchRubricTier.Ship;
        }

        if (score < ExperimentalWerThreshold)
        {
            return BenchRubricTier.Experimental;
        }

        return BenchRubricTier.Hide;
    }

    private static bool IsStrippedAsciiOrCjkPunctuation(char character) =>
        CjkPunctuationSet.Contains(character) ||
        (character <= 0x7F && !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character));

    /// <summary>
    /// Normalizes text following macOS <c>scripts/multilingual-eval/score.py</c>: Unicode NFC,
    /// strips ASCII and CJK punctuation, skips lowercasing for caseless scripts
    /// (<see cref="CaselessLanguages"/>), lowercases bicameral scripts with invariant culture, and
    /// collapses internal whitespace.
    /// </summary>
    public static string NormalizeText(string? text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalizedText = text.Normalize(NormalizationForm.FormC);
        var skipLowercase = IsCaselessLanguage(language);
        var builder = new StringBuilder(normalizedText.Length);
        var previousWasSpace = true;
        foreach (var character in normalizedText)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            if (IsStrippedAsciiOrCjkPunctuation(character))
            {
                continue;
            }

            builder.Append(skipLowercase ? character : char.ToLowerInvariant(character));
            previousWasSpace = false;
        }

        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Frozen Unicode-aware lexical normalizer (#258): normalizes input to NFC first so decomposed
    /// combining-mark sequences (NFD) collapse to the same precomposed letters as NFC references,
    /// strips ASCII and CJK punctuation while retaining letters, digits, and any attached combining
    /// marks, and lowercases with invariant culture unless <paramref name="language"/> is in
    /// <see cref="CaselessLanguages"/>.
    /// </summary>
    public static string[] NormalizeWords(string? text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalizedText = text.Normalize(NormalizationForm.FormC);
        var skipLowercase = IsCaselessLanguage(language);
        var rawTokens = normalizedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>(rawTokens.Length);
        var builder = new StringBuilder();
        foreach (var rawToken in rawTokens)
        {
            builder.Clear();
            var hasLetterOrDigit = false;
            foreach (var character in rawToken)
            {
                if (IsStrippedAsciiOrCjkPunctuation(character))
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(skipLowercase ? character : char.ToLowerInvariant(character));
                    hasLetterOrDigit = true;
                }
                else if (hasLetterOrDigit &&
                         CharUnicodeInfo.GetUnicodeCategory(character) is
                             UnicodeCategory.NonSpacingMark or
                             UnicodeCategory.SpacingCombiningMark)
                {
                    builder.Append(character);
                }
            }

            if (hasLetterOrDigit && builder.Length > 0)
            {
                words.Add(builder.ToString().Normalize(NormalizationForm.FormC));
            }
        }

        return words.ToArray();
    }

    /// <summary>
    /// Normalizes text into individual character tokens for Character Error Rate (CER) scoring on
    /// unspaced scripts (<c>zh</c>, <c>ja</c>, <c>ko</c>, <c>th</c>), stripping all whitespace and
    /// ASCII/CJK punctuation after NFC normalization and caseless-aware lowercasing.
    /// </summary>
    public static string[] NormalizeCharacters(string? text, string? language = null)
    {
        var normalized = NormalizeText(text, language);
        if (string.IsNullOrEmpty(normalized))
        {
            return [];
        }

        var characters = new List<string>(normalized.Length);
        foreach (var character in normalized)
        {
            if (!char.IsWhiteSpace(character))
            {
                characters.Add(character.ToString());
            }
        }

        return characters.ToArray();
    }

    public static double ComputeCharacterErrorRate(
        string? referenceText,
        string? transcriptText,
        string? language = "zh")
    {
        var expected = NormalizeCharacters(referenceText, language);
        var actual = NormalizeCharacters(transcriptText, language);
        if (expected.Length == 0)
        {
            return actual.Length == 0 ? 0.0 : 1.0;
        }

        var edits = LevenshteinDistance(expected, actual);
        return Math.Round(edits / (double)expected.Length, 4);
    }

    public static int LevenshteinDistance(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var previous = Enumerable.Range(0, right.Count + 1).ToArray();
        var current = new int[right.Count + 1];
        for (var leftIndex = 1; leftIndex <= left.Count; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Count; rightIndex++)
            {
                var substitution = string.Equals(left[leftIndex - 1], right[rightIndex - 1], StringComparison.Ordinal) ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Count];
    }

    public static string ClassifyRow(
        bool hasError,
        bool usedFallback,
        int actualWordCount,
        int referenceWordCount = 1)
    {
        if (usedFallback)
        {
            return BenchRowClassification.Fallback;
        }

        if (hasError)
        {
            return BenchRowClassification.Error;
        }

        if (actualWordCount <= 0 && referenceWordCount > 0)
        {
            return BenchRowClassification.Empty;
        }

        return BenchRowClassification.Primary;
    }

    public static string ClassifyRow(bool hasError, bool usedFallback, string? transcriptText) =>
        ClassifyRow(hasError, usedFallback, NormalizeWords(transcriptText).Length, referenceWordCount: 1);

    public static string ClassifyFailureCategory(
        string classification,
        string? explicitErrorCategory = null) =>
        classification switch
        {
            BenchRowClassification.Primary => string.IsNullOrWhiteSpace(explicitErrorCategory) ? "none" : explicitErrorCategory,
            BenchRowClassification.Fallback => string.IsNullOrWhiteSpace(explicitErrorCategory) ? "fallback" : explicitErrorCategory,
            BenchRowClassification.Empty => string.IsNullOrWhiteSpace(explicitErrorCategory) ? "empty-transcript" : explicitErrorCategory,
            BenchRowClassification.Error => string.IsNullOrWhiteSpace(explicitErrorCategory) ? "transcription-error" : explicitErrorCategory,
            _ => "none",
        };

    public static (
        string Classification,
        string FailureCategory,
        int PrimaryFixtureRows,
        int FallbackFixtureRows,
        int EmptyFixtureRows,
        int ErrorFixtureRows) SummarizeCandidateOutcome(
        IReadOnlyList<FixtureResult> fixtures,
        IReadOnlyList<Pass>? passes = null,
        IReadOnlyList<PrefixFixtureResult>? prefixFixtures = null,
        int workerRestarts = 0)
    {
        ArgumentNullException.ThrowIfNull(fixtures);

        var primaryRows = fixtures.Count(f => f.Classification == BenchRowClassification.Primary);
        var fallbackRows = fixtures.Count(f => f.Classification == BenchRowClassification.Fallback);
        var emptyRows = fixtures.Count(f => f.Classification == BenchRowClassification.Empty);
        var errorRows = fixtures.Count(f => f.Classification == BenchRowClassification.Error);

        var passPrimaries = passes?.Count(p => p.Classification == BenchRowClassification.Primary) ?? 0;
        var passErrors = passes?.Count(p => p.Classification == BenchRowClassification.Error) ?? 0;
        var passFallbacks = passes?.Count(p => p.Classification == BenchRowClassification.Fallback) ?? 0;
        var passEmpties = passes?.Count(p => p.Classification == BenchRowClassification.Empty) ?? 0;
        var prefixErrors = prefixFixtures?.Count(p => p.Classification == BenchRowClassification.Error) ?? 0;
        var prefixFallbacks = prefixFixtures?.Count(p => p.Classification == BenchRowClassification.Fallback) ?? 0;
        var prefixEmpties = prefixFixtures?.Count(p => p.Classification == BenchRowClassification.Empty) ?? 0;

        var totalErrors = errorRows + passErrors + prefixErrors;
        var totalFallbacks = fallbackRows + passFallbacks + prefixFallbacks;
        var allPassesEmpty = passes is { Count: > 0 } && passPrimaries == 0 && passEmpties > 0;

        var rowClassification = totalErrors > 0
            ? BenchRowClassification.Error
            : totalFallbacks > 0
                ? BenchRowClassification.Fallback
                : (primaryRows == 0 && emptyRows > 0) || allPassesEmpty
                    ? BenchRowClassification.Empty
                    : BenchRowClassification.Primary;

        string? explicitRowFailure = null;
        if (rowClassification == BenchRowClassification.Error)
        {
            explicitRowFailure =
                fixtures.FirstOrDefault(f => f.Classification == BenchRowClassification.Error && f.FailureCategory != "none")?.FailureCategory ??
                prefixFixtures?.FirstOrDefault(p => p.Classification == BenchRowClassification.Error && p.FailureCategory != "none")?.FailureCategory ??
                passes?.FirstOrDefault(p => p.Classification == BenchRowClassification.Error && p.FailureCategory != "none")?.FailureCategory;
        }
        else if (rowClassification == BenchRowClassification.Fallback)
        {
            explicitRowFailure =
                fixtures.FirstOrDefault(f => f.Classification == BenchRowClassification.Fallback && f.FailureCategory != "none")?.FailureCategory ??
                prefixFixtures?.FirstOrDefault(p => p.Classification == BenchRowClassification.Fallback && p.FailureCategory != "none")?.FailureCategory ??
                passes?.FirstOrDefault(p => p.Classification == BenchRowClassification.Fallback && p.FailureCategory != "none")?.FailureCategory;
        }
        else if (rowClassification == BenchRowClassification.Empty && allPassesEmpty && primaryRows > 0)
        {
            explicitRowFailure = "empty-passes";
        }
        else if (rowClassification == BenchRowClassification.Primary)
        {
            if (emptyRows + prefixEmpties + passEmpties > 0)
            {
                explicitRowFailure = "partial-empty-transcript";
            }
            else if (workerRestarts > 0)
            {
                explicitRowFailure = "worker-restarted";
            }
        }

        var rowFailureCategory = ClassifyFailureCategory(rowClassification, explicitRowFailure);
        return (rowClassification, rowFailureCategory, primaryRows, fallbackRows, emptyRows, errorRows);
    }

    public static IReadOnlyList<Pass> FilterSteadyPrimaryPasses(IEnumerable<Pass> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);
        return passes
            .Where(pass => !pass.WorkerRestarted && pass.Classification == BenchRowClassification.Primary)
            .ToList();
    }

    public static FixtureResult ScoreFixture(
        string file,
        string language,
        string referenceText,
        string? transcriptText,
        long elapsedMilliseconds,
        string? requestedLanguage,
        string? detectedLanguage,
        bool usedFallback,
        string? errorCategory = null,
        bool hasError = false,
        bool workerRestarted = false)
    {
        var normalizedLanguage = NormalizeLanguageSubtag(language) ?? language;
        var usesCer = UsesCharacterErrorRate(normalizedLanguage);
        var expectedWords = NormalizeWords(referenceText, normalizedLanguage);
        var actualWords = NormalizeWords(transcriptText, normalizedLanguage);
        var expectedChars = NormalizeCharacters(referenceText, normalizedLanguage);
        var actualChars = NormalizeCharacters(transcriptText, normalizedLanguage);
        var actualUnits = usesCer ? actualChars.Length : actualWords.Length;
        var expectedUnits = usesCer ? expectedChars.Length : expectedWords.Length;
        var classification = ClassifyRow(hasError, usedFallback, actualUnits, expectedUnits);
        var failureCategory = ClassifyFailureCategory(classification, errorCategory);
        int? editDistance = classification == BenchRowClassification.Error
            ? null
            : LevenshteinDistance(expectedWords, actualWords);
        double? rawWer = editDistance is { } edits
            ? expectedWords.Length == 0
                ? (actualWords.Length == 0 ? 0.0 : 1.0)
                : edits / (double)expectedWords.Length
            : null;
        double? wer = rawWer is { } rw ? Math.Round(rw, 4) : null;
        int? charEditDistance = classification == BenchRowClassification.Error
            ? null
            : LevenshteinDistance(expectedChars, actualChars);
        double? rawCer = charEditDistance is { } cEdits
            ? expectedChars.Length == 0
                ? (actualChars.Length == 0 ? 0.0 : 1.0)
                : cEdits / (double)expectedChars.Length
            : null;
        double? cer = rawCer is { } rc ? Math.Round(rc, 4) : null;
        var primaryMetric = MetricForLanguage(normalizedLanguage);
        var rawPrimaryScore = usesCer ? rawCer : rawWer;
        var rubricTier = classification == BenchRowClassification.Error
            ? null
            : ClassifyRubricTier(normalizedLanguage, rawPrimaryScore);
        bool? detectedLanguageMatched =
            classification == BenchRowClassification.Error || string.IsNullOrWhiteSpace(detectedLanguage)
                ? null
                : IsDetectedLanguageMatch(detectedLanguage, normalizedLanguage);
        return new FixtureResult(
            file,
            normalizedLanguage,
            expectedWords.Length,
            editDistance,
            elapsedMilliseconds,
            transcriptText?.Trim() ?? string.Empty,
            RequestedLanguage: requestedLanguage,
            DetectedLanguage: detectedLanguage,
            UsedFallback: usedFallback,
            FailureCategory: failureCategory,
            Classification: classification,
            ActualWords: actualWords.Length,
            WordErrorRate: wer,
            WorkerRestarted: workerRestarted,
            ReferenceCharacters: expectedChars.Length,
            ActualCharacters: actualChars.Length,
            CharacterEditDistance: charEditDistance,
            CharacterErrorRate: cer,
            PrimaryMetric: primaryMetric,
            DetectedLanguageMatched: detectedLanguageMatched,
            RubricTier: rubricTier);
    }

    public static PrefixFixtureResult ScorePrefixFixture(
        Fixture fixture,
        PrefixReferenceEntry prefixReference,
        string? transcriptText,
        long elapsedMilliseconds,
        string? requestedLanguage,
        string? detectedLanguage,
        bool usedFallback,
        string? errorCategory = null,
        bool hasError = false,
        bool workerRestarted = false)
    {
        var expected = NormalizeWords(prefixReference.Reference, fixture.Language);
        var actual = NormalizeWords(transcriptText, fixture.Language);
        var hasUnresolvedWords = prefixReference.UnresolvedWords is { Count: > 0 };
        var classification = ClassifyRow(hasError, usedFallback, actual.Length, expected.Length);
        var failureCategory = ClassifyFailureCategory(
            classification,
            errorCategory ?? (hasUnresolvedWords ? "unresolved-reference" : null));
        int? editDistance = classification == BenchRowClassification.Error || hasUnresolvedWords
            ? null
            : LevenshteinDistance(expected, actual);
        double? wer = editDistance is { } edits
            ? expected.Length == 0
                ? (actual.Length == 0 ? 0.0 : 1.0)
                : Math.Round(edits / (double)expected.Length, 4)
            : null;
        return new PrefixFixtureResult(
            fixture.File,
            fixture.Language,
            prefixReference.WindowSeconds,
            prefixReference.Provenance,
            expected.Length,
            actual.Length,
            editDistance,
            wer,
            elapsedMilliseconds,
            prefixReference.Reference,
            transcriptText?.Trim() ?? string.Empty,
            RequestedLanguage: requestedLanguage,
            DetectedLanguage: detectedLanguage,
            UsedFallback: usedFallback,
            FailureCategory: failureCategory,
            Classification: classification,
            Confidence: prefixReference.Confidence,
            Modality: prefixReference.Modality,
            UnresolvedWords: prefixReference.UnresolvedWords,
            WorkerRestarted: workerRestarted);
    }

    /// <summary>
    /// Computes aggregate pooled WER over a complete slice of primary and empty rows. Successful empty
    /// outputs count as deletions; if any row in the slice failed (<c>error</c>) or degraded
    /// (<c>fallback</c>), returns <see langword="null"/> so an incomplete measurement never publishes
    /// a survivor-biased accuracy number.
    /// </summary>
    public static double? Rate(IEnumerable<FixtureResult> results) =>
         RawRate(results) is { } raw ? Math.Round(raw, 3) : null;

    private static double? RawRate(IEnumerable<FixtureResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var list = results.ToList();
        if (list.Count == 0 ||
            list.Any(result =>
                result.EditDistance is null ||
                result.Classification is not (BenchRowClassification.Primary or BenchRowClassification.Empty)))
        {
            return null;
        }

        var words = list.Sum(result => result.ReferenceWords);
        var edits = list.Sum(result => result.EditDistance!.Value);
        return words == 0 ? (edits == 0 ? 0.0 : 1.0) : edits / (double)words;
    }

    /// <summary>
    /// Computes aggregate pooled Character Error Rate (CER) over a complete slice of primary and empty rows.
    /// Returns <see langword="null"/> if the slice is empty or contains any error/fallback row.
    /// </summary>
    public static double? CharacterRate(IEnumerable<FixtureResult> results) =>
        RawCharacterRate(results) is { } raw ? Math.Round(raw, 3) : null;

    private static double? RawCharacterRate(IEnumerable<FixtureResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var list = results.ToList();
        if (list.Count == 0 ||
            list.Any(result =>
                result.CharacterEditDistance is null ||
                result.Classification is not (BenchRowClassification.Primary or BenchRowClassification.Empty)))
        {
            return null;
        }

        var characters = list.Sum(result => result.ReferenceCharacters);
        var edits = list.Sum(result => result.CharacterEditDistance!.Value);
        return characters == 0 ? (edits == 0 ? 0.0 : 1.0) : edits / (double)characters;
    }

    public static double? PrimaryErrorRate(IEnumerable<FixtureResult> results, string? language) =>
        UsesCharacterErrorRate(language) ? CharacterRate(results) : Rate(results);

    /// <summary>
    /// Computes the detected-language match rate (<c>DetectedLanguage == expectedLang</c> normalized to
    /// base ISO subtag) across a complete slice of primary and empty rows. Returns <see langword="null"/>
    /// if the slice is empty, contains any error/fallback row, or the engine did not report a detected
    /// language on any row in the slice (e.g. Parakeet).
    /// </summary>
    public static double? DetectedLanguageMatchRate(IEnumerable<FixtureResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var list = results.ToList();
        if (list.Count == 0 ||
            list.Any(result =>
                result.Classification is not (BenchRowClassification.Primary or BenchRowClassification.Empty)))
        {
            return null;
        }

        var hasAnyDetection = list.Any(result =>
            result.DetectedLanguageMatched is not null || !string.IsNullOrWhiteSpace(result.DetectedLanguage));
        if (!hasAnyDetection)
        {
            return null;
        }

        var matches = list.Count(result =>
            result.DetectedLanguageMatched ?? IsDetectedLanguageMatch(result.DetectedLanguage, result.Language));
        return Math.Round(matches / (double)list.Count, 3);
    }

    public static LanguageMetricsSummary SummarizeLanguageMetrics(IReadOnlyList<FixtureResult> fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);

        var groups = fixtures
            .GroupBy(result => NormalizeLanguageSubtag(result.Language) ?? result.Language, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        var werByLang = new Dictionary<string, double?>(StringComparer.Ordinal);
        var cerByLang = new Dictionary<string, double?>(StringComparer.Ordinal);
        var detMatchByLang = new Dictionary<string, double?>(StringComparer.Ordinal);
        var tierByLang = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var lang = group.Key;
            var rawWer = RawRate(group);
            var rawCer = RawCharacterRate(group);
            var rawPrimary = UsesCharacterErrorRate(lang) ? rawCer : rawWer;
            werByLang[lang] = rawWer is { } rw ? Math.Round(rw, 3) : null;
            cerByLang[lang] = rawCer is { } rc ? Math.Round(rc, 3) : null;
            detMatchByLang[lang] = DetectedLanguageMatchRate(group);
            tierByLang[lang] = ClassifyRubricTier(lang, rawPrimary);
        }

        string? overallTier = null;
        if (tierByLang.Count > 0 && tierByLang.Values.All(t => t is not null))
        {
            if (tierByLang.Values.Any(t => string.Equals(t, BenchRubricTier.Hide, StringComparison.Ordinal)))
            {
                overallTier = BenchRubricTier.Hide;
            }
            else if (tierByLang.Values.Any(t => string.Equals(t, BenchRubricTier.Experimental, StringComparison.Ordinal)))
            {
                overallTier = BenchRubricTier.Experimental;
            }
            else
            {
                overallTier = BenchRubricTier.Ship;
            }
        }

        return new LanguageMetricsSummary(
            WordErrorRateByLanguage: werByLang,
            WordErrorRate: Rate(fixtures),
            CharacterErrorRateByLanguage: cerByLang,
            CharacterErrorRate: CharacterRate(fixtures),
            DetectedLanguageMatchRateByLanguage: detMatchByLang,
            DetectedLanguageMatchRate: DetectedLanguageMatchRate(fixtures),
            RubricTierByLanguage: tierByLang,
            RubricTier: overallTier);
    }

    /// <summary>
    /// Computes aggregate pooled prefix WER over a complete slice of primary and empty prefix rows.
    /// If any row in the slice failed (<c>error</c>) or degraded (<c>fallback</c>), returns
    /// <see langword="null"/>. When a short-window bucket has zero reference words across its valid rows,
    /// staying silent returns <c>0.0</c> while any hallucinated insertion returns <c>1.0</c>, and
    /// zero-reference insertion edits are included in pooled numerators so invented words are never dropped.
    /// </summary>
    public static double? PrefixRate(IEnumerable<PrefixFixtureResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var all = results.ToList();
        if (all.Count == 0 ||
            all.Any(result =>
                result.Classification is not (BenchRowClassification.Primary or BenchRowClassification.Empty)))
        {
            return null;
        }

        var resolved = all
            .Where(result => result.UnresolvedWords is not { Count: > 0 })
            .ToList();
        if (resolved.Count == 0 || resolved.Any(result => result.EditDistance is null))
        {
            return null;
        }

        var words = resolved.Sum(result => result.ReferenceWords);
        var edits = resolved.Sum(result => result.EditDistance!.Value);
        return words == 0 ? (edits == 0 ? 0.0 : 1.0) : Math.Round(edits / (double)words, 3);
    }

    public static int CountZeroReferenceInsertions(IEnumerable<PrefixFixtureResult> results) =>
        results
            .Where(result =>
                result.UnresolvedWords is not { Count: > 0 } &&
                result.ReferenceWords == 0 &&
                result.EditDistance is not null &&
                result.Classification is BenchRowClassification.Primary or BenchRowClassification.Empty)
            .Sum(result => result.ActualWords);

    public static string? SummarizeDetectedLanguages(IEnumerable<FixtureResult> fixtures)
    {
        var distinct = fixtures
            .Select(fixture => fixture.DetectedLanguage)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length == 0 ? null : string.Join(",", distinct);
    }

    /// <summary>
    /// Validates that every prefix reference entry maps to an existing fixture and a window duration
    /// within that fixture's audio length, failing closed instead of silently skipping rows.
    /// </summary>
    public static IReadOnlyList<PrefixReferenceEntry> ValidatePrefixReferences(
        IReadOnlyList<PrefixReferenceEntry> entries,
        IReadOnlyList<Fixture> fixtures)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fixtures);
        if (entries.Count == 0)
        {
            throw new InvalidDataException("Prefix references set is empty.");
        }

        var fixturesByFile = fixtures.ToDictionary(f => f.File, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string File, long WindowMillis)>();
        var canonicalEntries = new List<PrefixReferenceEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.File) ||
                !fixturesByFile.TryGetValue(entry.File, out var fixture))
            {
                throw new InvalidDataException($"Prefix reference names unknown fixture '{entry.File}'.");
            }

            if (!double.IsFinite(entry.WindowSeconds) || entry.WindowSeconds <= 0)
            {
                throw new InvalidDataException(
                    $"Prefix reference '{fixture.File}@{entry.WindowSeconds.ToString(CultureInfo.InvariantCulture)}s' has an invalid window duration.");
            }

            var requiredSamples = (int)(entry.WindowSeconds * SampleRate);
            if (requiredSamples < MinimumWindowSamples)
            {
                throw new InvalidDataException(
                    $"Prefix reference '{fixture.File}@{entry.WindowSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s' is shorter than the minimum preprocessor window ({MinimumWindowSamples} samples).");
            }

            if (requiredSamples > fixture.Samples.Length)
            {
                throw new InvalidDataException(
                    $"Prefix reference '{fixture.File}@{entry.WindowSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s' exceeds fixture duration ({fixture.Samples.Length / (double)SampleRate:0.00}s).");
            }

            var fixtureDurationSeconds = fixture.Samples.Length / (double)SampleRate;
            if (entry.MaxTokenEndSeconds is { } maxTokenEndSeconds &&
                maxTokenEndSeconds > fixtureDurationSeconds)
            {
                throw new InvalidDataException(
                    $"Prefix reference '{fixture.File}@{entry.WindowSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s' contains token timings ending at {maxTokenEndSeconds:0.00}s beyond fixture duration ({fixtureDurationSeconds:0.00}s).");
            }

            if (entry.AlignedFromFullReference is { } alignedFullReference &&
                !NormalizeWords(alignedFullReference).SequenceEqual(NormalizeWords(fixture.Reference), StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Prefix reference '{fixture.File}@{entry.WindowSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s' was aligned from a fullReference that disagrees with the fixture reference.");
            }

            var windowMillis = (long)Math.Round(entry.WindowSeconds * 1000.0);
            if (!seen.Add((fixture.File, windowMillis)))
            {
                throw new InvalidDataException(
                    $"Duplicate prefix reference for '{fixture.File}@{entry.WindowSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s'.");
            }

            canonicalEntries.Add(entry with { File = fixture.File });
        }

        return canonicalEntries;
    }

    public static IReadOnlyList<PrefixReferenceEntry> ParsePrefixReferencesJson(
        string json,
        IReadOnlyList<Fixture>? fixtures = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = new List<PrefixReferenceEntry>();
        var fixturesByFile = fixtures?.ToDictionary(f => f.File, StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("prefixes", out var prefixesElement) &&
            prefixesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prefixesElement.EnumerateArray())
            {
                var file = item.GetProperty("file").GetString()
                    ?? throw new InvalidDataException("Prefix reference entry is missing 'file'.");
                var windowSeconds = item.GetProperty("windowSeconds").GetDouble();
                var provenance = item.TryGetProperty("provenance", out var prov) && prov.ValueKind == JsonValueKind.String
                    ? prov.GetString()!
                    : "external-prefix-reference";
                var confidence = item.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.String
                    ? conf.GetString()
                    : null;
                var modality = item.TryGetProperty("modality", out var mod) && mod.ValueKind == JsonValueKind.String
                    ? mod.GetString()
                    : null;
                IReadOnlyList<string>? unresolvedWords = null;
                if (item.TryGetProperty("unresolvedWords", out var unresolved) &&
                    unresolved.ValueKind == JsonValueKind.Array)
                {
                    unresolvedWords = unresolved.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .ToArray();
                }

                string reference;
                double? maxTokenEndSeconds = null;
                string? alignedFromFullReference = null;
                if (item.TryGetProperty("reference", out var refProp) && refProp.ValueKind == JsonValueKind.String)
                {
                    reference = refProp.GetString()!;
                }
                else if (item.TryGetProperty("tokenTimings", out var timingsProp) &&
                         timingsProp.ValueKind == JsonValueKind.Array)
                {
                    Fixture? matchedFixture = null;
                    if (fixturesByFile is not null && !fixturesByFile.TryGetValue(file, out matchedFixture))
                    {
                        throw new InvalidDataException($"Prefix reference names unknown fixture '{file}'.");
                    }

                    string? suppliedFullReference = null;
                    if (item.TryGetProperty("fullReference", out var fullRefProp) &&
                        fullRefProp.ValueKind == JsonValueKind.String)
                    {
                        suppliedFullReference = fullRefProp.GetString()!;
                        if (matchedFixture is not null &&
                            !NormalizeWords(suppliedFullReference).SequenceEqual(NormalizeWords(matchedFixture.Reference), StringComparer.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"Prefix reference entry for '{file}' supplies a fullReference that disagrees with the fixture reference.");
                        }
                    }

                    var fullReference = matchedFixture?.Reference
                        ?? suppliedFullReference
                        ?? throw new InvalidDataException($"Prefix reference entry for {file} with tokenTimings requires fullReference or a matching fixture.");
                    alignedFromFullReference = suppliedFullReference ?? fullReference;
                    var clipDuration = matchedFixture is not null
                        ? matchedFixture.Samples.Length / (double)SampleRate
                        : 0.0;
                    var timings = ParseTokenTimings(timingsProp, file, clipDuration > 0 ? clipDuration : null);
                    var aligned = AlignPrefixReference(file, fullReference, windowSeconds, clipDuration, timings);
                    reference = aligned.Reference;
                    maxTokenEndSeconds = aligned.MaxTokenEndSeconds;
                    if (!item.TryGetProperty("provenance", out _))
                    {
                        provenance = aligned.Provenance;
                    }
                }
                else
                {
                    throw new InvalidDataException($"Prefix reference entry for {file} must specify 'reference' or 'tokenTimings'.");
                }

                entries.Add(new PrefixReferenceEntry(
                    file,
                    windowSeconds,
                    reference,
                    provenance,
                    confidence,
                    modality,
                    unresolvedWords,
                    maxTokenEndSeconds,
                    alignedFromFullReference));
            }

            return entries;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var fileProp in root.EnumerateObject())
            {
                if (string.Equals(fileProp.Name, "schemaVersion", StringComparison.Ordinal) &&
                    fileProp.Value.ValueKind == JsonValueKind.Number)
                {
                    continue;
                }

                if (fileProp.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Prefix reference entry for '{fileProp.Name}' must be a JSON object mapping windowSeconds to references.");
                }

                foreach (var windowProp in fileProp.Value.EnumerateObject())
                {
                    if (!double.TryParse(windowProp.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out var windowSeconds))
                    {
                        throw new InvalidDataException($"Invalid windowSeconds key '{windowProp.Name}' for '{fileProp.Name}'.");
                    }

                    if (windowProp.Value.ValueKind == JsonValueKind.String)
                    {
                        entries.Add(new PrefixReferenceEntry(
                            fileProp.Name,
                            windowSeconds,
                            windowProp.Value.GetString()!));
                    }
                    else if (windowProp.Value.ValueKind == JsonValueKind.Object)
                    {
                        var reference = windowProp.Value.GetProperty("reference").GetString()
                            ?? throw new InvalidDataException($"Prefix reference entry for {fileProp.Name}@{windowSeconds} is missing 'reference'.");
                        var provenance = windowProp.Value.TryGetProperty("provenance", out var prov) && prov.ValueKind == JsonValueKind.String
                            ? prov.GetString()!
                            : "external-prefix-reference";
                        entries.Add(new PrefixReferenceEntry(
                            fileProp.Name,
                            windowSeconds,
                            reference,
                            provenance));
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"Prefix reference entry for '{fileProp.Name}@{windowProp.Name}' must be a string or object, not {windowProp.Value.ValueKind}.");
                    }
                }
            }

            return entries;
        }

        throw new InvalidDataException("Unsupported prefix-references JSON schema.");
    }

    private static List<TranscriptTokenTiming> ParseTokenTimings(
        JsonElement arrayElement,
        string file,
        double? maxDurationSeconds = null)
    {
        var timings = new List<TranscriptTokenTiming>();
        var previousStartMs = 0.0;
        var previousEndMs = 0.0;
        foreach (var token in arrayElement.EnumerateArray())
        {
            var text = token.GetProperty("text").GetString() ?? string.Empty;
            var startMs = token.GetProperty("startMs").GetDouble();
            var endMs = token.GetProperty("endMs").GetDouble();
            if (!double.IsFinite(startMs) || !double.IsFinite(endMs) || startMs < 0 || endMs < startMs)
            {
                throw new InvalidDataException($"Invalid token timing interval [{startMs}, {endMs}] for '{file}'.");
            }

            if (startMs < previousStartMs || endMs < previousEndMs)
            {
                throw new InvalidDataException(
                    $"Out-of-order token timing interval [{startMs}, {endMs}]ms after [{previousStartMs}, {previousEndMs}]ms for '{file}'.");
            }

            if (maxDurationSeconds is > 0 && endMs > maxDurationSeconds.Value * 1000.0)
            {
                throw new InvalidDataException(
                    $"Token timing interval [{startMs}, {endMs}]ms for '{file}' exceeds fixture duration ({maxDurationSeconds.Value * 1000.0:0.##}ms).");
            }

            previousStartMs = startMs;
            previousEndMs = endMs;
            timings.Add(new TranscriptTokenTiming(
                text,
                TimeSpan.FromMilliseconds(startMs),
                TimeSpan.FromMilliseconds(endMs)));
        }

        return timings;
    }

    /// <summary>
    /// Aligns a prefix of <paramref name="fullReference"/> up to <paramref name="windowSeconds"/> using
    /// verified token timings, without replacing the ground-truth words with recognizer output. Subword
    /// tokens are grouped into whole words and aligned to the reference words via edit-distance traceback
    /// so the prefix cut lands on the last reference word whose aligned end time is within the window.
    /// Fails closed when <paramref name="tokenTimings"/> has no timed words, is out of chronological order,
    /// or contains intervals beyond <paramref name="clipDurationSeconds"/>.
    /// </summary>
    public static PrefixReferenceEntry AlignPrefixReference(
        string file,
        string fullReference,
        double windowSeconds,
        double clipDurationSeconds,
        IReadOnlyList<TranscriptTokenTiming>? tokenTimings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(fullReference);
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0)
        {
            throw new InvalidDataException($"Invalid prefix windowSeconds '{windowSeconds}' for '{file}'.");
        }

        double? maxTokenEndSeconds = null;
        if (tokenTimings is not null)
        {
            var previousStart = TimeSpan.Zero;
            var previousEnd = TimeSpan.Zero;
            foreach (var token in tokenTimings)
            {
                if (token.Start < TimeSpan.Zero ||
                    token.End < token.Start ||
                    (clipDurationSeconds > 0 && token.End.TotalSeconds > clipDurationSeconds))
                {
                    throw new InvalidDataException(
                        $"Token timing [{token.Start.TotalMilliseconds:0.##}, {token.End.TotalMilliseconds:0.##}]ms for '{file}' exceeds fixture duration ({clipDurationSeconds * 1000.0:0.##}ms).");
                }

                if (token.Start < previousStart || token.End < previousEnd)
                {
                    throw new InvalidDataException(
                        $"Out-of-order token timing [{token.Start.TotalMilliseconds:0.##}, {token.End.TotalMilliseconds:0.##}]ms after [{previousStart.TotalMilliseconds:0.##}, {previousEnd.TotalMilliseconds:0.##}]ms for '{file}'.");
                }

                previousStart = token.Start;
                previousEnd = token.End;
                maxTokenEndSeconds = maxTokenEndSeconds is { } currentMax
                    ? Math.Max(currentMax, token.End.TotalSeconds)
                    : token.End.TotalSeconds;
            }
        }

        var surfaceWords = SplitSurfaceWords(fullReference);
        if (surfaceWords.Count == 0)
        {
            return new PrefixReferenceEntry(
                file,
                windowSeconds,
                string.Empty,
                Provenance: "timestamp-aligned-prefix-reference",
                MaxTokenEndSeconds: maxTokenEndSeconds);
        }

        var timedWords = GroupTimedWords(tokenTimings);
        if (timedWords.Count == 0)
        {
            throw new InvalidDataException(
                $"Prefix reference alignment for '{file}@{windowSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s' requires non-empty token timings.");
        }

        if (clipDurationSeconds > 0 && windowSeconds > clipDurationSeconds)
        {
            throw new InvalidDataException(
                $"Prefix windowSeconds '{windowSeconds}' exceeds fixture duration ({clipDurationSeconds:0.##}s) for '{file}'.");
        }

        var cutoff = TimeSpan.FromSeconds(windowSeconds);
        var includeCount = ResolvePrefixWordCountAtCutoff(surfaceWords, timedWords, cutoff, file, windowSeconds);

        return new PrefixReferenceEntry(
            file,
            windowSeconds,
            includeCount == 0
                ? string.Empty
                : string.Join(" ", surfaceWords.Take(includeCount).Select(w => w.Surface)),
            Provenance: "timestamp-aligned-prefix-reference",
            MaxTokenEndSeconds: maxTokenEndSeconds);
    }

    private static List<(string Surface, string Normalized)> SplitSurfaceWords(string reference)
    {
        var tokens = reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<(string Surface, string Normalized)>(tokens.Length);
        foreach (var token in tokens)
        {
            var normalized = NormalizeWords(token);
            if (normalized.Length > 0)
            {
                result.Add((token, normalized[0]));
            }
        }

        return result;
    }

    private static List<(string Normalized, TimeSpan Start, TimeSpan End)> GroupTimedWords(
        IReadOnlyList<TranscriptTokenTiming>? tokenTimings)
    {
        if (tokenTimings is null || tokenTimings.Count == 0)
        {
            return [];
        }

        var grouped = new List<(string Normalized, TimeSpan Start, TimeSpan End)>();
        var currentText = new StringBuilder();
        var currentStart = TimeSpan.Zero;
        var currentEnd = TimeSpan.Zero;
        var hasCurrent = false;

        void Flush()
        {
            if (!hasCurrent)
            {
                return;
            }

            var normalized = NormalizeWords(currentText.ToString());
            if (normalized.Length > 1)
            {
                throw new InvalidDataException(
                    $"Token timing '{currentText}' spans multiple words ({string.Join(", ", normalized)}); per-word or subword token timings are required.");
            }

            if (normalized.Length == 1)
            {
                grouped.Add((normalized[0], currentStart, currentEnd));
            }

            currentText.Clear();
            hasCurrent = false;
        }

        var usesExplicitPrefixMarkers = tokenTimings.Any(
            t => !string.IsNullOrEmpty(t.Text) && (char.IsWhiteSpace(t.Text[0]) || t.Text[0] == '\u2581'));

        foreach (var timing in tokenTimings)
        {
            if (timing.Start < TimeSpan.Zero || timing.End < timing.Start)
            {
                throw new InvalidDataException($"Invalid token timing interval [{timing.Start}, {timing.End}].");
            }

            if (string.IsNullOrEmpty(timing.Text))
            {
                continue;
            }

            var startsNewWord = !usesExplicitPrefixMarkers ||
                char.IsWhiteSpace(timing.Text[0]) ||
                timing.Text[0] == '\u2581';
            if (startsNewWord && hasCurrent)
            {
                Flush();
            }

            if (!hasCurrent)
            {
                currentStart = timing.Start;
                currentEnd = timing.End;
                hasCurrent = true;
            }
            else if (timing.End > currentEnd)
            {
                currentEnd = timing.End;
            }

            currentText.Append(timing.Text.TrimStart('\u2581'));
        }

        Flush();
        return grouped;
    }

    private static int ResolvePrefixWordCountAtCutoff(
        List<(string Surface, string Normalized)> referenceWords,
        List<(string Normalized, TimeSpan Start, TimeSpan End)> timedWords,
        TimeSpan cutoff,
        string file,
        double windowSeconds)
    {
        var n = referenceWords.Count;
        var m = timedWords.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++)
        {
            dp[i, 0] = i;
        }

        for (var j = 0; j <= m; j++)
        {
            dp[0, j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = string.Equals(referenceWords[i - 1].Normalized, timedWords[j - 1].Normalized, StringComparison.Ordinal)
                    ? 0
                    : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        var matchedTimedIndex = new int?[n];
        var exactMatches = 0;
        var r = n;
        var c = m;
        while (r > 0 && c > 0)
        {
            var cost = string.Equals(referenceWords[r - 1].Normalized, timedWords[c - 1].Normalized, StringComparison.Ordinal)
                ? 0
                : 1;
            if (dp[r, c] == dp[r - 1, c - 1] + cost)
            {
                if (cost == 0)
                {
                    matchedTimedIndex[r - 1] = c - 1;
                    exactMatches++;
                }

                r--;
                c--;
            }
            else if (dp[r, c] == dp[r - 1, c] + 1)
            {
                r--;
            }
            else
            {
                c--;
            }
        }

        // Only trust direct lexical matches or a single-word 1-to-1 substitution tightly bracketed
        // on both sides by exact lexical matches. Never extrapolate or linearly interpolate across
        // multi-word gaps or unanchored trailing reference words.
        var knownEndTimes = new TimeSpan?[n];
        for (var index = 0; index < n; index++)
        {
            if (matchedTimedIndex[index] is { } timedIdx)
            {
                knownEndTimes[index] = timedWords[timedIdx].End;
            }
            else if (index > 0 &&
                     index + 1 < n &&
                     matchedTimedIndex[index - 1] is { } prevTimedIdx &&
                     matchedTimedIndex[index + 1] is { } nextTimedIdx &&
                     nextTimedIdx - prevTimedIdx == 2)
            {
                knownEndTimes[index] = timedWords[prevTimedIdx + 1].End;
            }
        }

        var includeCount = 0;
        var boundaryIndex = -1;
        var exactMatchesInCoveredSpan = 0;
        for (var index = 0; index < n; index++)
        {
            if (knownEndTimes[index] is not { } endTime)
            {
                throw new InvalidDataException(
                    $"Token timings do not provide contiguous word timing coverage across the {windowSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s cutoff for '{file}' (missing timing at reference word {index + 1} of {n}: '{referenceWords[index].Surface}').");
            }

            if (matchedTimedIndex[index] is not null)
            {
                exactMatchesInCoveredSpan++;
            }

            if (endTime <= cutoff)
            {
                includeCount = index + 1;
            }
            else
            {
                boundaryIndex = index;
                break;
            }
        }

        if (boundaryIndex < 0)
        {
            boundaryIndex = n - 1;
        }

        var coveredReferenceWords = boundaryIndex + 1;
        var minimumRequiredMatches = Math.Max(
            Math.Min(2, n),
            (Math.Max(coveredReferenceWords, m) + 1) / 2);
        if (exactMatches < minimumRequiredMatches ||
            exactMatchesInCoveredSpan < (coveredReferenceWords + 1) / 2)
        {
            throw new InvalidDataException(
                $"Token timings have insufficient lexical overlap with reference ({exactMatches} exact word matches across {n} reference words and {m} timed words).");
        }

        return includeCount;
    }

    public static BenchCliOptions ParseCommandLine(
        IReadOnlyList<string> args,
        string? defaultRepositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var showHelp = false;
        var allowBusyCard = false;
        var accuracyOnly = false;
        string? repoArg = null;
        string? modelsArg = null;
        string? channelArg = null;
        string? audioArg = null;
        string? extraModelsArg = null;
        string? fixturesArg = null;
        string? corpusManifestArg = null;
        string? wavRootArg = null;
        int? maxFixturesArg = null;
        List<string>? languagesArg = null;
        var repeats = 3;
        var providersText = "cpu,cuda";
        string? onlyArg = null;
        string? roleArg = null;
        var requestedLanguage = "auto";
        string? prefixReferencesArg = null;
        string? outputArg = null;
        string? markdownArg = null;

        string ReadRequiredValue(int index, string flagName)
        {
            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{flagName}' requires a value.", nameof(args));
            }

            return args[index + 1];
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
            {
                showHelp = true;
            }
            else if (string.Equals(arg, "--allow-busy-card", StringComparison.OrdinalIgnoreCase))
            {
                allowBusyCard = true;
            }
            else if (string.Equals(arg, "--accuracy-only", StringComparison.OrdinalIgnoreCase))
            {
                accuracyOnly = true;
            }
            else if (string.Equals(arg, "--repo", StringComparison.OrdinalIgnoreCase))
            {
                repoArg = ReadRequiredValue(i++, "--repo");
            }
            else if (string.Equals(arg, "--models", StringComparison.OrdinalIgnoreCase))
            {
                modelsArg = ReadRequiredValue(i++, "--models");
            }
            else if (string.Equals(arg, "--channel", StringComparison.OrdinalIgnoreCase))
            {
                channelArg = ReadRequiredValue(i++, "--channel");
            }
            else if (string.Equals(arg, "--audio", StringComparison.OrdinalIgnoreCase))
            {
                audioArg = ReadRequiredValue(i++, "--audio");
            }
            else if (string.Equals(arg, "--extra-models", StringComparison.OrdinalIgnoreCase))
            {
                extraModelsArg = ReadRequiredValue(i++, "--extra-models");
            }
            else if (string.Equals(arg, "--fixtures", StringComparison.OrdinalIgnoreCase))
            {
                fixturesArg = ReadRequiredValue(i++, "--fixtures");
            }
            else if (string.Equals(arg, "--corpus-manifest", StringComparison.OrdinalIgnoreCase))
            {
                corpusManifestArg = ReadRequiredValue(i++, "--corpus-manifest");
            }
            else if (string.Equals(arg, "--wav-root", StringComparison.OrdinalIgnoreCase))
            {
                wavRootArg = ReadRequiredValue(i++, "--wav-root");
            }
            else if (string.Equals(arg, "--max-fixtures", StringComparison.OrdinalIgnoreCase))
            {
                var rawMax = ReadRequiredValue(i++, "--max-fixtures");
                if (!int.TryParse(rawMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax) || parsedMax <= 0)
                {
                    throw new ArgumentException($"Invalid --max-fixtures value '{rawMax}'. Expected a positive integer.", nameof(args));
                }

                maxFixturesArg = parsedMax;
            }
            else if (string.Equals(arg, "--languages", StringComparison.OrdinalIgnoreCase))
            {
                var rawLanguages = ReadRequiredValue(i++, "--languages");
                var parsedLanguages = rawLanguages
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeLanguageSubtag)
                    .Where(lang => !string.IsNullOrWhiteSpace(lang) && !string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
                    .Select(lang => lang!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (parsedLanguages.Count == 0)
                {
                    throw new ArgumentException($"Invalid --languages value '{rawLanguages}'. Expected comma-separated ISO language codes.", nameof(args));
                }

                languagesArg = parsedLanguages;
            }
            else if (string.Equals(arg, "--repeats", StringComparison.OrdinalIgnoreCase))
            {
                var rawRepeats = ReadRequiredValue(i++, "--repeats");
                if (int.TryParse(rawRepeats, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRepeats))
                {
                    repeats = Math.Max(1, parsedRepeats);
                }
            }
            else if (string.Equals(arg, "--providers", StringComparison.OrdinalIgnoreCase))
            {
                providersText = ReadRequiredValue(i++, "--providers");
            }
            else if (string.Equals(arg, "--only", StringComparison.OrdinalIgnoreCase))
            {
                onlyArg = ReadRequiredValue(i++, "--only");
            }
            else if (string.Equals(arg, "--role", StringComparison.OrdinalIgnoreCase))
            {
                roleArg = ReadRequiredValue(i++, "--role");
            }
            else if (string.Equals(arg, "--language", StringComparison.OrdinalIgnoreCase))
            {
                requestedLanguage = ReadRequiredValue(i++, "--language");
            }
            else if (string.Equals(arg, "--prefix-references", StringComparison.OrdinalIgnoreCase))
            {
                prefixReferencesArg = ReadRequiredValue(i++, "--prefix-references");
            }
            else if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase))
            {
                outputArg = ReadRequiredValue(i++, "--out");
            }
            else if (string.Equals(arg, "--markdown", StringComparison.OrdinalIgnoreCase))
            {
                markdownArg = ReadRequiredValue(i++, "--markdown");
            }
        }

        var repositoryRoot = repoArg ?? defaultRepositoryRoot ?? FindRepositoryRoot(AppContext.BaseDirectory);
        var fixturesDirectory = fixturesArg ?? Path.Combine(repositoryRoot, "tools", "whisper-uat", "fixtures");
        var providers = providersText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Equals("cuda", StringComparison.OrdinalIgnoreCase) ? RuntimeProviderKind.Cuda : RuntimeProviderKind.Cpu)
            .Distinct()
            .ToArray();
        var role = ParseRole(roleArg);
        var outputPath = outputArg ?? Path.Combine(Path.GetTempPath(), "preview-model-bench.json");

        return new BenchCliOptions(
            RepositoryRoot: repositoryRoot,
            ModelsRoot: modelsArg,
            ReleaseChannel: channelArg,
            AudioDirectory: audioArg,
            ExtraModels: extraModelsArg,
            FixturesDirectory: fixturesDirectory,
            CorpusManifestPath: corpusManifestArg,
            WavRoot: wavRootArg,
            MaxFixtures: maxFixturesArg,
            Languages: languagesArg,
            AccuracyOnly: accuracyOnly,
            Repeats: repeats,
            Providers: providers,
            Only: onlyArg,
            Role: role,
            RequestedLanguage: requestedLanguage,
            PrefixReferencesPath: prefixReferencesArg,
            OutputPath: outputPath,
            MarkdownOutputPath: markdownArg,
            AllowBusyCard: allowBusyCard,
            ShowHelp: showHelp);
    }

    public static string FormatHelpText() =>
        """
        Usage: preview-model-bench [options]

        Options:
          --role <preview|final>         Worker role and thread/priority profile (default: preview)
          --providers <cpu,cuda>         Comma-separated runtime providers to measure (default: cpu,cuda)
          --only <substring>             Filter candidate models by name substring
          --language <lang|auto>         Requested Whisper recognition language (default: auto)
          --corpus-manifest <path>       Manifest file or corpus directory (cases.sealed.jsonl, cases.jsonl, manifest.jsonl, or manifest.json)
          --wav-root <dir>               Optional root directory for resolving corpus WAV files
          --max-fixtures <N>             Maximum number of corpus fixtures to evaluate
          --languages <csv>              Comma-separated base ISO language filter (e.g. en,de,zh)
          --accuracy-only                Skip archived window-timing passes and evaluate corpus accuracy only
          --fixtures <dir>               Legacy MINDS-14 fixtures directory (default: tools/whisper-uat/fixtures)
          --audio <dir|file>             Archived dictation WAV directory for window-timing passes
          --prefix-references <json>     Frozen prefix-reference JSON for partial-window WER grading
          --repeats <N>                  Window-timing pass repeats per clip (default: 3)
          --models <dir>                 Explicit model store root override
          --extra-models <dir>           Directory containing optional extra Whisper GGML binaries
          --channel <stable|founder>     Release channel for app data and model store lookup
          --allow-busy-card              Measure CUDA even if GPU utilization is at or above 50%
          --out <path>                   JSON report output path
          --markdown <path>              Optional Markdown report output path
          --help, -h                     Show this help message
        """;

    public static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? start;
    }

    /// <summary>
    /// Parses corpus manifest entries from a file or directory supporting:
    /// <list type="bullet">
    /// <item>Mac <c>intl-sealed-2026-11</c> JSONL (<c>cases.sealed.jsonl</c> with <c>voice_text</c> or <c>cases.jsonl</c> with <c>text</c> + <c>id</c>, <c>lang</c>, resolving <c>&lt;dir&gt;/wav/&lt;id&gt;.wav</c>),</item>
    /// <item>Mac <c>lang-stress</c> / <c>german-parakeet-stress</c> JSONL (<c>manifest.jsonl</c> with <c>sample_id</c>, <c>reference_text</c>, <c>locale</c>/<c>voice</c>, resolving <c>&lt;dir&gt;/audio/&lt;sample_id&gt;.wav</c>), and</item>
    /// <item>Windows <c>tools/whisper-uat/fixtures/manifest.json</c> (<c>fixtures</c> array with <c>file</c>, <c>config</c>, <c>evaluationTranscription</c>/<c>transcription</c>).</item>
    /// </list>
    /// Applies optional <paramref name="languages"/> filtering (normalized to base ISO subtags) and
    /// <paramref name="maxFixtures"/> capping in manifest order.
    /// </summary>
    public static IReadOnlyList<CorpusManifestEntry> ParseCorpusManifestEntries(
        string manifestPathOrDirectory,
        string? wavRoot = null,
        IReadOnlyCollection<string>? languages = null,
        int? maxFixtures = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPathOrDirectory);
        if (maxFixtures is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFixtures), "maxFixtures must be positive when specified.");
        }

        HashSet<string>? languageFilter = null;
        if (languages is { Count: > 0 })
        {
            languageFilter = languages
                .Select(NormalizeLanguageSubtag)
                .Where(lang => !string.IsNullOrWhiteSpace(lang))
                .Select(lang => lang!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var manifestFiles = ResolveManifestFiles(manifestPathOrDirectory);
        var entries = new List<CorpusManifestEntry>();
        foreach (var manifestFile in manifestFiles)
        {
            foreach (var entry in ParseSingleManifestFile(manifestFile, wavRoot))
            {
                if (languageFilter is not null && !languageFilter.Contains(entry.Language))
                {
                    continue;
                }

                entries.Add(entry);
                if (maxFixtures is { } max && entries.Count >= max)
                {
                    return entries;
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Loads corpus fixtures and their WAV audio samples from <paramref name="manifestPathOrDirectory"/>,
    /// applying optional <paramref name="wavRoot"/>, <paramref name="languages"/>, and
    /// <paramref name="maxFixtures"/> filters. Fails closed with <see cref="InvalidDataException"/> if any
    /// selected fixture's WAV file is missing.
    /// </summary>
    public static List<Fixture> LoadCorpusFixtures(
        string manifestPathOrDirectory,
        string? wavRoot = null,
        IReadOnlyCollection<string>? languages = null,
        int? maxFixtures = null,
        Func<string, float[]>? waveReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPathOrDirectory);
        if (!File.Exists(manifestPathOrDirectory) && !Directory.Exists(manifestPathOrDirectory))
        {
            return [];
        }

        var entries = ParseCorpusManifestEntries(manifestPathOrDirectory, wavRoot, languages, maxFixtures);
        var reader = waveReader ?? ReadWaveFile;
        var fixtures = new List<Fixture>(entries.Count);
        foreach (var entry in entries)
        {
            if (!File.Exists(entry.AudioPath))
            {
                throw new InvalidDataException($"Missing fixture: {entry.File} (expected at '{entry.AudioPath}').");
            }

            fixtures.Add(new Fixture(entry.File, entry.Language, entry.Reference, reader(entry.AudioPath)));
        }

        return fixtures;
    }

    private static List<string> ResolveManifestFiles(string manifestPathOrDirectory)
    {
        if (File.Exists(manifestPathOrDirectory))
        {
            return [Path.GetFullPath(manifestPathOrDirectory)];
        }

        if (!Directory.Exists(manifestPathOrDirectory))
        {
            throw new FileNotFoundException($"Corpus manifest path not found: {manifestPathOrDirectory}", manifestPathOrDirectory);
        }

        var dir = Path.GetFullPath(manifestPathOrDirectory);
        string[] directCandidates =
        [
            Path.Combine(dir, "manifest.json"),
            Path.Combine(dir, "cases.sealed.jsonl"),
            Path.Combine(dir, "cases.jsonl"),
            Path.Combine(dir, "manifest.jsonl"),
        ];
        foreach (var direct in directCandidates)
        {
            if (File.Exists(direct))
            {
                return [direct];
            }
        }

        var discovered = new List<string>();
        DiscoverManifestsRecursive(dir, discovered);
        return discovered;
    }

    private static void DiscoverManifestsRecursive(string directory, List<string> discovered)
    {
        string[] candidates =
        [
            Path.Combine(directory, "cases.sealed.jsonl"),
            Path.Combine(directory, "cases.jsonl"),
            Path.Combine(directory, "manifest.jsonl"),
            Path.Combine(directory, "manifest.json"),
        ];
        var matched = candidates.FirstOrDefault(File.Exists);
        if (matched is not null)
        {
            discovered.Add(matched);
            return;
        }

        foreach (var subDir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var folderName = Path.GetFileName(subDir);
            if (string.Equals(folderName, "results", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folderName, "wav", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folderName, "audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DiscoverManifestsRecursive(subDir, discovered);
        }
    }

    private static IEnumerable<CorpusManifestEntry> ParseSingleManifestFile(string manifestFilePath, string? wavRoot)
    {
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestFilePath)) ?? string.Empty;
        var content = File.ReadAllText(manifestFilePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        if (manifestFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("fixtures", out var fixturesArray) &&
                fixturesArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in fixturesArray.EnumerateArray())
                {
                    var file = GetOptionalString(row, "file")
                        ?? throw new InvalidDataException($"Manifest row in '{manifestFilePath}' is missing 'file'.");
                    var rawLanguage = GetOptionalString(row, "lang")
                        ?? GetOptionalString(row, "locale")
                        ?? GetOptionalString(row, "config")
                        ?? InferLanguageFromPath(manifestFilePath)
                        ?? throw new InvalidDataException($"Manifest row '{file}' in '{manifestFilePath}' is missing language/config.");
                    var language = NormalizeLanguageSubtag(rawLanguage)
                        ?? throw new InvalidDataException($"Manifest row '{file}' in '{manifestFilePath}' has invalid language '{rawLanguage}'.");
                    var reference = GetOptionalString(row, "evaluationTranscription")
                        ?? GetOptionalString(row, "transcription")
                        ?? throw new InvalidDataException($"Manifest row '{file}' in '{manifestFilePath}' is missing transcription.");
                    var audioPath = ResolveCorpusAudioPath(manifestDir, wavRoot, file, preferredSubfolder: string.Empty);
                    yield return new CorpusManifestEntry(
                        Id: Path.GetFileNameWithoutExtension(file),
                        File: file,
                        Language: language,
                        Reference: reference,
                        AudioPath: audioPath,
                        SourceFormat: "whisper-uat-manifest-json");
                }

                yield break;
            }
        }

        var lineNumber = 0;
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            using var lineDoc = JsonDocument.Parse(line);
            var row = lineDoc.RootElement;
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Line {lineNumber} in '{manifestFilePath}' must be a JSON object.");
            }

            var id = GetOptionalString(row, "id");
            var sampleId = GetOptionalString(row, "sample_id");
            var voiceText = GetOptionalString(row, "voice_text");
            var text = GetOptionalString(row, "text");
            var referenceText = GetOptionalString(row, "reference_text") ?? GetOptionalString(row, "normalized_reference_text");
            var explicitAudio = GetOptionalString(row, "wav") ?? GetOptionalString(row, "audio") ?? GetOptionalString(row, "file");

            if (!string.IsNullOrWhiteSpace(id) && (voiceText is not null || text is not null))
            {
                var reference = voiceText ?? text!;
                var rawLang = GetOptionalString(row, "lang")
                    ?? GetOptionalString(row, "locale")
                    ?? TryExtractLocaleFromVoice(GetOptionalString(row, "voice"))
                    ?? InferLanguageFromPath(manifestFilePath)
                    ?? TryExtractLanguageFromIdentifier(id)
                    ?? throw new InvalidDataException($"Line {lineNumber} ('{id}') in '{manifestFilePath}' is missing 'lang' or 'locale'.");
                var language = NormalizeLanguageSubtag(rawLang)!;
                var file = id.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? id : $"{id}.wav";
                var audioPath = ResolveCorpusAudioPath(manifestDir, wavRoot, file, preferredSubfolder: "wav", explicitRelativePath: explicitAudio);
                yield return new CorpusManifestEntry(
                    Id: id,
                    File: file,
                    Language: language,
                    Reference: reference,
                    AudioPath: audioPath,
                    SourceFormat: voiceText is not null ? "mac-intl-cases-sealed-jsonl" : "mac-intl-cases-jsonl");
            }
            else if (!string.IsNullOrWhiteSpace(sampleId) && referenceText is not null)
            {
                var rawLang = GetOptionalString(row, "lang")
                    ?? GetOptionalString(row, "locale")
                    ?? TryExtractLocaleFromVoice(GetOptionalString(row, "voice"))
                    ?? InferLanguageFromPath(manifestFilePath)
                    ?? TryExtractLanguageFromIdentifier(sampleId)
                    ?? throw new InvalidDataException($"Line {lineNumber} ('{sampleId}') in '{manifestFilePath}' is missing 'locale' or 'lang'.");
                var language = NormalizeLanguageSubtag(rawLang)!;
                var file = sampleId.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? sampleId : $"{sampleId}.wav";
                var audioPath = ResolveCorpusAudioPath(manifestDir, wavRoot, file, preferredSubfolder: "audio", explicitRelativePath: explicitAudio);
                yield return new CorpusManifestEntry(
                    Id: sampleId,
                    File: file,
                    Language: language,
                    Reference: referenceText,
                    AudioPath: audioPath,
                    SourceFormat: "mac-stress-manifest-jsonl");
            }
            else
            {
                throw new InvalidDataException(
                    $"Line {lineNumber} in '{manifestFilePath}' does not match a supported corpus schema (expected id+voice_text/text or sample_id+reference_text).");
            }
        }
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? TryExtractLocaleFromVoice(string? voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
        {
            return null;
        }

        var parts = voice.Trim().Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Length is 2 or 3 && parts[0].All(char.IsLetter)
            ? parts[0].ToLowerInvariant()
            : null;
    }

    private static string? TryExtractLanguageFromIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var prefix = identifier.Trim().Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0];
        return prefix.Length == 2 && prefix.All(char.IsLetter) && !string.Equals(prefix, "SB", StringComparison.OrdinalIgnoreCase)
            ? prefix.ToLowerInvariant()
            : null;
    }

    private static string? InferLanguageFromPath(string manifestFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestFilePath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var folderName = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        if (folderName.Contains("german", StringComparison.OrdinalIgnoreCase))
        {
            return "de";
        }

        var token = folderName.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0];
        return token.Length == 2 && token.All(char.IsLetter)
            ? token.ToLowerInvariant()
            : null;
    }

    private static string ResolveCorpusAudioPath(
        string manifestDir,
        string? wavRoot,
        string file,
        string preferredSubfolder,
        string? explicitRelativePath = null)
    {
        var sliceName = Path.GetFileName(manifestDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(wavRoot))
        {
            var root = Path.GetFullPath(wavRoot);
            if (!string.IsNullOrWhiteSpace(explicitRelativePath))
            {
                candidates.Add(Path.Combine(root, explicitRelativePath));
            }

            if (!string.IsNullOrEmpty(preferredSubfolder))
            {
                candidates.Add(Path.Combine(root, preferredSubfolder, file));
            }

            candidates.Add(Path.Combine(root, file));
            if (!string.IsNullOrWhiteSpace(sliceName))
            {
                if (!string.IsNullOrEmpty(preferredSubfolder))
                {
                    candidates.Add(Path.Combine(root, sliceName, preferredSubfolder, file));
                }

                candidates.Add(Path.Combine(root, sliceName, file));
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitRelativePath))
        {
            candidates.Add(Path.Combine(manifestDir, explicitRelativePath));
        }

        if (!string.IsNullOrEmpty(preferredSubfolder))
        {
            candidates.Add(Path.Combine(manifestDir, preferredSubfolder, file));
        }

        candidates.Add(Path.Combine(manifestDir, file));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(candidates[0]);
    }

    public static float[] ReadWaveFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        byte[]? format = null;
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var chunkId = bytes.AsSpan(position, 4);
            var chunkSize = BitConverter.ToInt32(bytes, position + 4);
            position += 8;
            if (chunkId.SequenceEqual("fmt "u8))
            {
                format = bytes.AsSpan(position, chunkSize).ToArray();
            }
            else if (chunkId.SequenceEqual("data"u8) && format is { Length: >= 16 })
            {
                var audioFormat = BitConverter.ToInt16(format, 0);
                var channels = BitConverter.ToInt16(format, 2);
                var sourceRate = BitConverter.ToInt32(format, 4);
                var bitsPerSample = BitConverter.ToInt16(format, 14);
                var bytesPerSample = bitsPerSample / 8;
                var sampleCount = Math.Min(chunkSize, bytes.Length - position) / bytesPerSample / channels;
                var samples = new float[sampleCount];
                for (var index = 0; index < sampleCount; index++)
                {
                    var offset = position + (index * bytesPerSample * channels);
                    samples[index] = audioFormat switch
                    {
                        1 when bitsPerSample == 16 => BitConverter.ToInt16(bytes, offset) / 32768f,
                        3 when bitsPerSample == 32 => BitConverter.ToSingle(bytes, offset),
                        7 when bitsPerSample == 8 => DecodeMuLaw(bytes[offset]),
                        _ => throw new InvalidDataException($"{Path.GetFileName(path)} uses an unsupported codec."),
                    };
                }

                return sourceRate == SampleRate ? samples : Resample(samples, sourceRate, SampleRate);
            }

            position += chunkSize + (chunkSize % 2);
        }

        throw new InvalidDataException($"{Path.GetFileName(path)} has no supported audio data.");
    }

    private static float DecodeMuLaw(byte value)
    {
        var decoded = (byte)~value;
        var sign = (decoded & 0x80) == 0 ? 1 : -1;
        var exponent = (decoded >> 4) & 0x07;
        var mantissa = decoded & 0x0F;
        var magnitude = (((mantissa << 3) + 0x84) << exponent) - 0x84;
        return sign * magnitude / 32768f;
    }

    private static float[] Resample(float[] source, int sourceRate, int destinationRate)
    {
        var destinationLength = checked((int)Math.Round(source.Length * (destinationRate / (double)sourceRate)));
        var destination = new float[destinationLength];
        for (var index = 0; index < destinationLength; index++)
        {
            var sourcePosition = index * (sourceRate / (double)destinationRate);
            var lower = Math.Min((int)sourcePosition, source.Length - 1);
            var upper = Math.Min(lower + 1, source.Length - 1);
            var fraction = sourcePosition - lower;
            destination[index] = (float)(source[lower] + ((source[upper] - source[lower]) * fraction));
        }

        return destination;
    }

    public static int? CountPassesOverPreviewCadence(
        BenchRole role,
        IEnumerable<Pass> steadyPasses,
        double previewCadenceMilliseconds = 2_500)
    {
        ArgumentNullException.ThrowIfNull(steadyPasses);
        return role == BenchRole.Preview
            ? steadyPasses.Count(pass => pass.Milliseconds > previewCadenceMilliseconds)
            : null;
    }

    public static string FormatMarkdownReport(
        IReadOnlyList<CandidateResult> rows,
        IReadOnlyList<double> windowSeconds)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(windowSeconds);

        var sb = new StringBuilder();
        var languages = rows
            .SelectMany(row =>
                (row.WordErrorRateByLanguage?.Keys ?? Enumerable.Empty<string>())
                .Concat(row.CharacterErrorRateByLanguage?.Keys ?? Enumerable.Empty<string>())
                .Concat(row.RubricTierByLanguage?.Keys ?? Enumerable.Empty<string>()))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(lang => lang, StringComparer.Ordinal)
            .ToList();
        var hasPrefixWer = rows.Any(row => row.PrefixWordErrorRate is not null || row.PrefixFixtures is { Count: > 0 });
        var hasPreviewCadence = rows.Any(row => row.PassesOverCadence is not null);
        var hasCer = rows.Any(row =>
            row.CharacterErrorRate is not null ||
            row.CharacterErrorRateByLanguage is { Count: > 0 } ||
            languages.Any(UsesCharacterErrorRate));
        var hasDetectedLangMatch = rows.Any(row =>
            row.DetectedLanguageMatchRate is not null ||
            row.DetectedLanguageMatchRateByLanguage is { Count: > 0 });
        var hasRubricTier = rows.Any(row =>
            row.RubricTier is not null ||
            row.RubricTierByLanguage is { Count: > 0 });

        var headerParts = new List<string>
        {
            "model",
            "on",
            "role",
            "threads",
            "priority",
            "req lang",
            "det lang",
            "fallback",
            "failure",
            "status",
            "cold start",
        };
        headerParts.AddRange(windowSeconds.Select(window => $"pass @ {window.ToString("0.0", CultureInfo.InvariantCulture)} s"));
        headerParts.Add(hasPreviewCadence ? "over 2.5 s (preview)" : "passes");
        headerParts.Add("WER all");
        headerParts.AddRange(languages.Select(language => $"WER {language}"));
        if (hasCer)
        {
            headerParts.Add("CER all");
            headerParts.AddRange(languages.Select(language => $"CER {language}"));
        }

        if (hasDetectedLangMatch)
        {
            headerParts.Add("lang match");
            headerParts.AddRange(languages.Select(language => $"lang match {language}"));
        }

        if (hasRubricTier)
        {
            headerParts.Add("rubric tier");
            headerParts.AddRange(languages.Select(language => $"tier {language}"));
        }

        if (hasPrefixWer)
        {
            headerParts.Add("prefix WER");
            headerParts.Add("0-ref ins");
        }

        headerParts.AddRange(["rows (p/fb/e/err)", "restarts", "peak RAM", "VRAM", "card load before"]);

        sb.AppendLine("| " + string.Join(" | ", headerParts) + " |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", headerParts.Count)));

        foreach (var row in rows)
        {
            if (row.Error is { } error)
            {
                var failedCells = new List<string>
                {
                    row.Model,
                    row.Provider,
                    row.Role,
                    row.IntraOpThreads.ToString(CultureInfo.InvariantCulture),
                    row.WorkerPriority,
                    row.RequestedLanguage ?? "-",
                    row.DetectedLanguage ?? "-",
                    row.UsedFallback ? "yes" : "no",
                    $"{row.FailureCategory} ({error})",
                    row.Classification,
                    "-",
                };
                failedCells.AddRange(windowSeconds.Select(_ => "-"));
                failedCells.Add("-");
                failedCells.Add("-");
                failedCells.AddRange(languages.Select(_ => "-"));
                if (hasCer)
                {
                    failedCells.Add("-");
                    failedCells.AddRange(languages.Select(_ => "-"));
                }

                if (hasDetectedLangMatch)
                {
                    failedCells.Add("-");
                    failedCells.AddRange(languages.Select(_ => "-"));
                }

                if (hasRubricTier)
                {
                    failedCells.Add("-");
                    failedCells.AddRange(languages.Select(_ => "-"));
                }

                if (hasPrefixWer)
                {
                    failedCells.Add("-");
                    failedCells.Add("-");
                }

                failedCells.Add($"{row.PrimaryFixtureRows}/{row.FallbackFixtureRows}/{row.EmptyFixtureRows}/{row.ErrorFixtureRows}");
                failedCells.Add(row.WorkerRestarts.ToString(CultureInfo.InvariantCulture));
                failedCells.Add(row.PeakWorkingSetMegabytes is { } errRam ? $"{errRam} MB" : "-");
                failedCells.Add(row.VideoMemoryMegabytes is { } errVideo ? $"{errVideo} MB" : "-");
                failedCells.Add(row.CardLoadBeforePercent is { } errLoad ? $"{errLoad}%" : "-");
                sb.AppendLine("| " + string.Join(" | ", failedCells) + " |");
                continue;
            }

            var cells = new List<string>
            {
                row.Model,
                row.Provider,
                row.Role,
                row.IntraOpThreads.ToString(CultureInfo.InvariantCulture),
                row.WorkerPriority,
                row.RequestedLanguage ?? "-",
                row.DetectedLanguage ?? "-",
                row.UsedFallback ? "yes" : "no",
                row.FailureCategory,
                row.Classification,
                row.ColdStartMilliseconds is { } coldMs ? $"{coldMs} ms" : "-",
            };
            cells.AddRange(windowSeconds.Select(window =>
                row.MedianPassMilliseconds is not null &&
                row.MedianPassMilliseconds.TryGetValue(window.ToString("0.0", CultureInfo.InvariantCulture), out var median) &&
                median is { } m
                    ? $"{m} ms"
                    : "-"));
            cells.Add(hasPreviewCadence
                ? row.PassesOverCadence is { } over
                    ? $"{over} of {row.Passes}"
                    : $"n/a ({row.Passes})"
                : row.Passes.ToString(CultureInfo.InvariantCulture));
            cells.Add(Percent(row.WordErrorRate));
            cells.AddRange(languages.Select(language =>
                Percent(row.WordErrorRateByLanguage?.GetValueOrDefault(language))));
            if (hasCer)
            {
                cells.Add(Percent(row.CharacterErrorRate));
                cells.AddRange(languages.Select(language =>
                    Percent(row.CharacterErrorRateByLanguage?.GetValueOrDefault(language))));
            }

            if (hasDetectedLangMatch)
            {
                cells.Add(Percent(row.DetectedLanguageMatchRate));
                cells.AddRange(languages.Select(language =>
                    Percent(row.DetectedLanguageMatchRateByLanguage?.GetValueOrDefault(language))));
            }

            if (hasRubricTier)
            {
                cells.Add(row.RubricTier ?? "-");
                cells.AddRange(languages.Select(language =>
                    row.RubricTierByLanguage?.GetValueOrDefault(language)
                    ?? ClassifyRubricTier(
                        language,
                        UsesCharacterErrorRate(language)
                            ? row.CharacterErrorRateByLanguage?.GetValueOrDefault(language)
                            : row.WordErrorRateByLanguage?.GetValueOrDefault(language))
                    ?? "-"));
            }

            if (hasPrefixWer)
            {
                cells.Add(Percent(row.PrefixWordErrorRate));
                cells.Add((row.PrefixZeroReferenceInsertions ?? 0).ToString(CultureInfo.InvariantCulture));
            }

            cells.Add($"{row.PrimaryFixtureRows}/{row.FallbackFixtureRows}/{row.EmptyFixtureRows}/{row.ErrorFixtureRows}");
            cells.Add(row.WorkerRestarts.ToString(CultureInfo.InvariantCulture));
            cells.Add(row.PeakWorkingSetMegabytes is { } ram ? $"{ram} MB" : "-");
            cells.Add(row.VideoMemoryMegabytes is { } video ? $"{video} MB" : "-");
            cells.Add(row.CardLoadBeforePercent is { } load ? $"{load}%" : "-");
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }

        var fixtureRows = rows
            .Where(row => row.Fixtures is { Count: > 0 })
            .SelectMany(row => row.Fixtures!.Select(fixture => (Row: row, Fixture: fixture)))
            .ToList();
        if (fixtureRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| model | on | role | fixture | lang | req lang | det lang | lang match | fallback | failure | status | edits / ref | WER | CER | metric | tier | time |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---:|---:|---:|---|---|---:|");
            foreach (var (row, fixture) in fixtureRows)
            {
                var usesCer = UsesCharacterErrorRate(fixture.Language);
                var editsCell = usesCer
                    ? (fixture.CharacterEditDistance is { } charEdits
                        ? $"{charEdits} / {fixture.ReferenceCharacters}"
                        : $"- / {fixture.ReferenceCharacters}")
                    : (fixture.EditDistance is { } wordEdits
                        ? $"{wordEdits} / {fixture.ReferenceWords}"
                        : $"- / {fixture.ReferenceWords}");
                var langMatchCell = fixture.DetectedLanguageMatched is { } matched
                    ? (matched ? "yes" : "no")
                    : "-";
                var tierCell = fixture.RubricTier
                    ?? ClassifyRubricTier(fixture.Language, usesCer ? fixture.CharacterErrorRate : fixture.WordErrorRate)
                    ?? "-";
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {row.Model} | {row.Provider} | {row.Role} | {fixture.File} | {fixture.Language} | " +
                    $"{fixture.RequestedLanguage ?? "-"} | {fixture.DetectedLanguage ?? "-"} | {langMatchCell} | " +
                    $"{(fixture.UsedFallback ? "yes" : "no")} | {fixture.FailureCategory} | {fixture.Classification} | " +
                    $"{editsCell} | {Percent(fixture.WordErrorRate)} | {Percent(fixture.CharacterErrorRate)} | " +
                    $"{fixture.PrimaryMetric.ToUpperInvariant()} | {tierCell} | {fixture.Milliseconds} ms |");
            }
        }

        var prefixRows = rows
            .Where(row => row.PrefixFixtures is { Count: > 0 })
            .SelectMany(row => row.PrefixFixtures!.Select(prefix => (Row: row, Prefix: prefix)))
            .ToList();
        if (prefixRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| model | on | role | fixture | window | source | unresolved | req lang | det lang | fallback | failure | status | edits / ref | prefix WER | time |");
            sb.AppendLine("|---|---|---|---|---:|---|---|---|---|---|---|---|---|---:|---:|---:|");
            foreach (var (row, prefix) in prefixRows)
            {
                var editsCell = prefix.EditDistance is { } edits
                    ? $"{edits} / {prefix.ReferenceWords}"
                    : $"- / {prefix.ReferenceWords}";
                var unresolvedCell = prefix.UnresolvedWords is { Count: > 0 } unresolved
                    ? string.Join(",", unresolved)
                    : "-";
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {row.Model} | {row.Provider} | {row.Role} | {prefix.File} | " +
                    $"{prefix.WindowSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s | {prefix.Provenance} | {unresolvedCell} | " +
                    $"{prefix.RequestedLanguage ?? "-"} | {prefix.DetectedLanguage ?? "-"} | " +
                    $"{(prefix.UsedFallback ? "yes" : "no")} | {prefix.FailureCategory} | {prefix.Classification} | " +
                    $"{editsCell} | {Percent(prefix.WordErrorRate)} | {prefix.Milliseconds} ms |");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string Percent(double? rate) =>
        rate is { } value ? $"{value * 100:0.0}%" : "-";
}
