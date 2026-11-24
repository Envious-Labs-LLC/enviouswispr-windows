using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.PreviewModelBench;

namespace EnviousWispr.Architecture.Tests;

public sealed class PreviewModelBenchEvaluationTests
{
    [Theory]
    [InlineData(null, BenchRole.Preview)]
    [InlineData("", BenchRole.Preview)]
    [InlineData("preview", BenchRole.Preview)]
    [InlineData("PREVIEW", BenchRole.Preview)]
    [InlineData("final", BenchRole.Final)]
    [InlineData("Final", BenchRole.Final)]
    public void ParseRoleSelectsPreviewOrFinalAndRejectsUnknown(string? input, BenchRole expected)
    {
        Assert.Equal(expected, PreviewModelBenchEvaluation.ParseRole(input));
        Assert.Throws<ArgumentException>(() => PreviewModelBenchEvaluation.ParseRole("hybrid"));
    }

    [Fact]
    public void FinalAndPreviewRolesUseTheirOwnProductionWorkerOptionsAndPriorities()
    {
        var hardware = CreateHardware(physicalCores: 12, logicalProcessors: 24);
        var parakeetCandidate = new Candidate(
            $"{ParakeetModelIds.Final} full precision",
            FinalAsrEngine.Parakeet,
            WhisperModelPack.Quantized,
            ModelDirectory: @"C:\models\parakeet",
            ModelFile: null,
            ParakeetModelPack.FullPrecision);
        var whisperCandidate = new Candidate(
            $"{WhisperModelIds.Final} q5_0",
            FinalAsrEngine.Whisper,
            WhisperModelPack.Quantized,
            ModelDirectory: @"C:\models\whisper-turbo",
            ModelFile: null);

        var previewParakeet = PreviewModelBenchEvaluation.CreateWorkerOptions(
            BenchRole.Preview,
            @"C:\bin\EnviousWispr.RuntimeWorker.exe",
            parakeetCandidate,
            @"C:\models\parakeet",
            RuntimeProviderKind.Cpu,
            hardware,
            language: "de");
        Assert.Equal(ProcessPriorityClass.BelowNormal, previewParakeet.WorkerPriority);
        Assert.Equal(8, previewParakeet.IntraOpThreads);
        Assert.Equal(8, previewParakeet.CpuFallbackThreads);
        Assert.Equal(TimeSpan.FromSeconds(15), previewParakeet.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), previewParakeet.TranscriptionTimeout);
        Assert.Equal(0, previewParakeet.MaximumWorkerRestarts);
        Assert.Null(previewParakeet.Language);

        var finalParakeetCpu = PreviewModelBenchEvaluation.CreateWorkerOptions(
            BenchRole.Final,
            @"C:\bin\EnviousWispr.RuntimeWorker.exe",
            parakeetCandidate,
            @"C:\models\parakeet",
            RuntimeProviderKind.Cpu,
            hardware,
            language: "de");
        Assert.Equal(ProcessPriorityClass.Normal, finalParakeetCpu.WorkerPriority);
        Assert.Equal(6, finalParakeetCpu.IntraOpThreads);
        Assert.Equal(6, finalParakeetCpu.CpuFallbackThreads);
        Assert.Equal(TimeSpan.FromSeconds(30), finalParakeetCpu.StartupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), finalParakeetCpu.TranscriptionTimeout);
        Assert.Equal(1, finalParakeetCpu.MaximumWorkerRestarts);
        Assert.Null(finalParakeetCpu.Language);

        var finalParakeetCuda = PreviewModelBenchEvaluation.CreateWorkerOptions(
            BenchRole.Final,
            @"C:\bin\EnviousWispr.RuntimeWorker.exe",
            parakeetCandidate,
            @"C:\models\parakeet",
            RuntimeProviderKind.Cuda,
            hardware,
            language: "auto");
        Assert.Equal(ProcessPriorityClass.Normal, finalParakeetCuda.WorkerPriority);
        Assert.Equal(1, finalParakeetCuda.IntraOpThreads);
        Assert.Equal(6, finalParakeetCuda.CpuFallbackThreads);

        var finalWhisper = PreviewModelBenchEvaluation.CreateWorkerOptions(
            BenchRole.Final,
            @"C:\bin\EnviousWispr.RuntimeWorker.exe",
            whisperCandidate,
            @"C:\models\whisper-turbo",
            RuntimeProviderKind.Cuda,
            hardware,
            language: "DE");
        Assert.Equal(ProcessPriorityClass.Normal, finalWhisper.WorkerPriority);
        Assert.Equal(8, finalWhisper.IntraOpThreads);
        Assert.Equal(8, finalWhisper.CpuFallbackThreads);
        Assert.Equal("de", finalWhisper.Language);
    }

    [Fact]
    public void UnicodeNormalizationCollapsesDecomposedCombiningMarksAndStripsPunctuation()
    {
        var nfc = "„Guten Tag, ich möchte eine Lastschrift für den Empfänger; aplicación, cuál y qué!“";
        var nfd = nfc.Normalize(NormalizationForm.FormD);

        var expected = PreviewModelBenchEvaluation.NormalizeWords(nfc);
        var actual = PreviewModelBenchEvaluation.NormalizeWords(nfd);

        Assert.Equal(expected, actual);
        Assert.Equal(0, PreviewModelBenchEvaluation.LevenshteinDistance(expected, actual));
        Assert.Contains("möchte", actual);
        Assert.Contains("empfänger", actual);
        Assert.Contains("aplicación", actual);
        Assert.Contains("cuál", actual);
        Assert.Contains("qué", actual);
    }

    [Fact]
    public void FrozenMinds14ManifestReconcilesTo247NormalizedReferenceWords()
    {
        var manifestPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "whisper-uat",
            "fixtures",
            "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var fixtures = document.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        Assert.Equal(12, fixtures.Length);

        var totalWords = 0;
        foreach (var row in fixtures)
        {
            var reference = row.TryGetProperty("evaluationTranscription", out var evaluation) &&
                            evaluation.ValueKind == JsonValueKind.String
                ? evaluation.GetString()!
                : row.GetProperty("transcription").GetString()!;
            totalWords += PreviewModelBenchEvaluation.NormalizeWords(reference).Length;
        }

        Assert.Equal(247, totalWords);
    }

    [Fact]
    public void RowClassificationSeparatesPrimaryFallbackEmptyAndErrorOutcomes()
    {
        var primary = PreviewModelBenchEvaluation.ScoreFixture(
            "de-DE-row0.wav",
            "de",
            "ich möchte gerne Geld auf mein Konto einzahlen",
            "Ich möchte gerne Geld auf mein Konto einzahlen.",
            elapsedMilliseconds: 120,
            requestedLanguage: "auto",
            detectedLanguage: "de",
            usedFallback: false);
        Assert.Equal(BenchRowClassification.Primary, primary.Classification);
        Assert.Equal("none", primary.FailureCategory);
        Assert.Equal(0, primary.EditDistance);
        Assert.Equal(0.0, primary.WordErrorRate);

        var empty = PreviewModelBenchEvaluation.ScoreFixture(
            "es-ES-row300.wav",
            "es",
            "mi tarjeta no funciona",
            "   ...   ",
            elapsedMilliseconds: 85,
            requestedLanguage: "auto",
            detectedLanguage: "es",
            usedFallback: false);
        Assert.Equal(BenchRowClassification.Empty, empty.Classification);
        Assert.Equal("empty-transcript", empty.FailureCategory);
        Assert.Equal(4, empty.ReferenceWords);
        Assert.Equal(0, empty.ActualWords);
        Assert.Equal(4, empty.EditDistance);
        Assert.Equal(1.0, empty.WordErrorRate);

        var fallback = PreviewModelBenchEvaluation.ScoreFixture(
            "fr-FR-row0.wav",
            "fr",
            "je souhaite changer mon adresse",
            "je souhaite changer mon adresse",
            elapsedMilliseconds: 310,
            requestedLanguage: "auto",
            detectedLanguage: "fr",
            usedFallback: true,
            errorCategory: "RuntimeProviderUnavailable");
        Assert.Equal(BenchRowClassification.Fallback, fallback.Classification);
        Assert.Equal("RuntimeProviderUnavailable", fallback.FailureCategory);

        var error = PreviewModelBenchEvaluation.ScoreFixture(
            "en-US-row0.wav",
            "en",
            "I would like to set up a joint account with my partner",
            transcriptText: null,
            elapsedMilliseconds: 40,
            requestedLanguage: "auto",
            detectedLanguage: null,
            usedFallback: false,
            errorCategory: "RuntimeWorkerFailed",
            hasError: true);
        Assert.Equal(BenchRowClassification.Error, error.Classification);
        Assert.Equal("RuntimeWorkerFailed", error.FailureCategory);
        Assert.Null(error.EditDistance);
        Assert.Null(error.WordErrorRate);

        // Aggregate Rate includes primary (0/8) and empty deletions (4/4), and fails closed (returns null) if any row in the slice is fallback or error.
        var aggregate = PreviewModelBenchEvaluation.Rate([primary, empty]);
        Assert.Equal(Math.Round(4.0 / 12.0, 3), aggregate);
        Assert.Null(PreviewModelBenchEvaluation.Rate([primary, empty, fallback]));
        Assert.Null(PreviewModelBenchEvaluation.Rate([primary, empty, error]));

        // Partial empty transcripts in a primary summary row preserve their failure category.
        Assert.Equal(
            "partial-empty-transcript",
            PreviewModelBenchEvaluation.ClassifyFailureCategory(BenchRowClassification.Primary, "partial-empty-transcript"));
        Assert.Equal(
            "none",
            PreviewModelBenchEvaluation.ClassifyFailureCategory(BenchRowClassification.Primary));

        // Summary outcome includes pass and prefix-fixture errors rather than looking only at full fixtures.
        var summaryWithPassError = PreviewModelBenchEvaluation.SummarizeCandidateOutcome(
            [primary],
            [new Pass("clip.wav", 2.5, 100, FailureCategory: "RuntimeWorkerFailed", Classification: BenchRowClassification.Error)]);
        Assert.Equal(BenchRowClassification.Error, summaryWithPassError.Classification);
        Assert.Equal("RuntimeWorkerFailed", summaryWithPassError.FailureCategory);

        var summaryWithPrefixError = PreviewModelBenchEvaluation.SummarizeCandidateOutcome(
            [primary],
            [new Pass("clip.wav", 2.5, 50)],
            [
                new PrefixFixtureResult(
                    "de-DE-row0.wav",
                    "de",
                    2.5,
                    "external-prefix-reference",
                    3,
                    0,
                    null,
                    null,
                    40,
                    "ich möchte gerne",
                    string.Empty,
                    FailureCategory: "RuntimeWorkerTimedOut",
                    Classification: BenchRowClassification.Error),
            ]);
        Assert.Equal(BenchRowClassification.Error, summaryWithPrefixError.Classification);
        Assert.Equal("RuntimeWorkerTimedOut", summaryWithPrefixError.FailureCategory);

        // Empty timed passes surface partial-empty-transcript (or empty-passes if all passes are empty) and are excluded from steady timings.
        var summaryWithPartialEmptyPass = PreviewModelBenchEvaluation.SummarizeCandidateOutcome(
            [primary],
            [
                new Pass("clip.wav", 0.5, 10, FailureCategory: "empty-transcript", Classification: BenchRowClassification.Empty),
                new Pass("clip.wav", 2.5, 55, Classification: BenchRowClassification.Primary),
            ]);
        Assert.Equal(BenchRowClassification.Primary, summaryWithPartialEmptyPass.Classification);
        Assert.Equal("partial-empty-transcript", summaryWithPartialEmptyPass.FailureCategory);

        var summaryWithAllEmptyPasses = PreviewModelBenchEvaluation.SummarizeCandidateOutcome(
            [primary],
            [new Pass("clip.wav", 0.5, 10, FailureCategory: "empty-transcript", Classification: BenchRowClassification.Empty)]);
        Assert.Equal(BenchRowClassification.Empty, summaryWithAllEmptyPasses.Classification);
        Assert.Equal("empty-passes", summaryWithAllEmptyPasses.FailureCategory);

        var filteredSteady = PreviewModelBenchEvaluation.FilterSteadyPrimaryPasses(
            [
                new Pass("clip.wav", 0.5, 10, FailureCategory: "empty-transcript", Classification: BenchRowClassification.Empty),
                new Pass("clip.wav", 2.5, 55, Classification: BenchRowClassification.Primary),
                new Pass("clip.wav", 2.5, 800, Classification: BenchRowClassification.Primary, WorkerRestarted: true),
            ]);
        var singleSteady = Assert.Single(filteredSteady);
        Assert.Equal(55, singleSteady.Milliseconds);
    }

    [Fact]
    public void PrefixReferenceAlignmentAndScoringGradePartialWindowsAgainstPrefixTruth()
    {
        var fullReference = "Guten Tag, ich möchte eine Lastschrift aufgeben und hätte noch Fragen.";
        // Subword BPE timings where "Lastschrift" is split into " Last" + "schrift" and the recognizer
        // misheard "möchte" as "moechte" — alignment must still return the ground-truth reference prefix!
        IReadOnlyList<TranscriptTokenTiming> timings =
        [
            new(" Guten", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(450)),
            new(" Tag,", TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(850)),
            new(" ich", TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1200)),
            new(" moech", TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1550)),
            new("te", TimeSpan.FromMilliseconds(1550), TimeSpan.FromMilliseconds(1850)),
            new(" eine", TimeSpan.FromMilliseconds(1900), TimeSpan.FromMilliseconds(2200)),
            new(" Last", TimeSpan.FromMilliseconds(2200), TimeSpan.FromMilliseconds(2450)),
            new("schrift", TimeSpan.FromMilliseconds(2450), TimeSpan.FromMilliseconds(2900)),
            new(" aufgeben", TimeSpan.FromMilliseconds(2950), TimeSpan.FromMilliseconds(3500)),
        ];

        var aligned25 = PreviewModelBenchEvaluation.AlignPrefixReference(
            "de-DE-row100.wav",
            fullReference,
            windowSeconds: 2.5,
            clipDurationSeconds: 8.0,
            timings);
        Assert.Equal("timestamp-aligned-prefix-reference", aligned25.Provenance);
        // "schrift" ends at 2.9s (> 2.5s), so at 2.5s the prefix stops after "eine" (2.2s) and preserves "möchte".
        Assert.Equal("Guten Tag, ich möchte eine", aligned25.Reference);

        // Missing token timings fails closed rather than guessing words from duration fractions.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row100.wav",
                fullReference,
                windowSeconds: 2.5,
                clipDurationSeconds: 8.0,
                tokenTimings: null));

        // Multi-word token timings fail closed rather than dropping words or inventing split times.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row100.wav",
                fullReference,
                windowSeconds: 2.5,
                clipDurationSeconds: 8.0,
                [
                    new TranscriptTokenTiming(" Guten Tag", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(850)),
                    new TranscriptTokenTiming(" ich", TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1200)),
                ]));

        // Per-word token timings without leading whitespace preserve each word's own boundary and timestamp.
        var alignedPerWord = PreviewModelBenchEvaluation.AlignPrefixReference(
            "de-DE-row100.wav",
            fullReference,
            windowSeconds: 1.5,
            clipDurationSeconds: 8.0,
            [
                new TranscriptTokenTiming("Guten", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(450)),
                new TranscriptTokenTiming("Tag,", TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(850)),
                new TranscriptTokenTiming("ich", TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1200)),
                new TranscriptTokenTiming("möchte", TimeSpan.FromMilliseconds(1600), TimeSpan.FromMilliseconds(1950)),
            ]);
        Assert.Equal("Guten Tag, ich", alignedPerWord.Reference);

        // Token timings from an unrelated transcript/clip with no credible lexical overlap fail closed.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row100.wav",
                fullReference,
                windowSeconds: 2.5,
                clipDurationSeconds: 8.0,
                [
                    new TranscriptTokenTiming(" je", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(450)),
                    new TranscriptTokenTiming(" souhaite", TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(850)),
                    new TranscriptTokenTiming(" changer", TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1200)),
                    new TranscriptTokenTiming(" mon", TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1550)),
                    new TranscriptTokenTiming(" adresse", TimeSpan.FromMilliseconds(1550), TimeSpan.FromMilliseconds(2100)),
                ]));

        // Out-of-order token timings fail closed instead of clamping backward timestamps.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row100.wav",
                fullReference,
                windowSeconds: 2.5,
                clipDurationSeconds: 8.0,
                [
                    new TranscriptTokenTiming(" Guten", TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(850)),
                    new TranscriptTokenTiming(" Tag,", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400)),
                ]));

        // Sparsely matched token timings (e.g. a single timed word or timings that stop before the cutoff) fail closed rather than interpolating trailing words.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row100.wav",
                fullReference,
                windowSeconds: 2.5,
                clipDurationSeconds: 8.0,
                [
                    new TranscriptTokenTiming(" Guten", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(450)),
                ]));
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row100.wav",
                fullReference,
                windowSeconds: 0.2,
                clipDurationSeconds: 8.0,
                [
                    new TranscriptTokenTiming(" Guten", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(450)),
                ]));
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row100.wav",
                fullReference,
                windowSeconds: 2.5,
                clipDurationSeconds: 8.0,
                [
                    new TranscriptTokenTiming(" Guten", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(450)),
                    new TranscriptTokenTiming(" Fragen.", TimeSpan.FromMilliseconds(7100), TimeSpan.FromMilliseconds(7600)),
                ]));

        var json = """
        {
          "schemaVersion": 1,
          "prefixes": [
            {
              "file": "de-DE-row100.wav",
              "windowSeconds": 2.5,
              "reference": "Guten Tag, ich möchte eine",
              "provenance": "ai-adjudicated-timestamp-review",
              "confidence": "high",
              "modality": "text-and-timestamps",
              "unresolvedWords": []
            }
          ]
        }
        """;
        var parsed = PreviewModelBenchEvaluation.ParsePrefixReferencesJson(json);
        var entry = Assert.Single(parsed);
        Assert.Equal("ai-adjudicated-timestamp-review", entry.Provenance);
        Assert.Equal("high", entry.Confidence);
        Assert.Equal("text-and-timestamps", entry.Modality);

        var fixture = new Fixture("de-DE-row100.wav", "de", fullReference, new float[16_000 * 8]);
        var scored = PreviewModelBenchEvaluation.ScorePrefixFixture(
            fixture,
            entry,
            transcriptText: "Guten Tag ich möchte eine",
            elapsedMilliseconds: 65,
            requestedLanguage: "auto",
            detectedLanguage: "de",
            usedFallback: false);
        Assert.Equal(BenchRowClassification.Primary, scored.Classification);
        Assert.Equal(5, scored.ReferenceWords);
        Assert.Equal(0, scored.EditDistance);
        Assert.Equal(0.0, scored.WordErrorRate);

        // Diagnostic prefix references with unresolvedWords stay visible in the report but are excluded from pooled prefix WER.
        var unresolvedEntry = new PrefixReferenceEntry(
            "de-DE-row200.wav",
            5.0,
            "guten Tag ich habe Probleme mit meiner Karten",
            Provenance: "ai-adjudicated-timestamp-review",
            Confidence: "low",
            Modality: "text-only",
            UnresolvedWords: ["Karten", "SEPA"]);
        var unresolvedFixture = new Fixture("de-DE-row200.wav", "de", unresolvedEntry.Reference, new float[16_000 * 9]);
        var scoredUnresolved = PreviewModelBenchEvaluation.ScorePrefixFixture(
            unresolvedFixture,
            unresolvedEntry,
            transcriptText: "guten Tag ich habe Probleme mit meiner Karte",
            elapsedMilliseconds: 70,
            requestedLanguage: "auto",
            detectedLanguage: "de",
            usedFallback: false);
        Assert.Equal("unresolved-reference", scoredUnresolved.FailureCategory);
        Assert.Null(scoredUnresolved.EditDistance);
        Assert.Null(scoredUnresolved.WordErrorRate);
        Assert.Null(PreviewModelBenchEvaluation.PrefixRate([scoredUnresolved]));
        Assert.Equal(0.0, PreviewModelBenchEvaluation.PrefixRate([scored, scoredUnresolved]));

        var markdown = PreviewModelBenchEvaluation.FormatMarkdownReport(
            [
                new CandidateResult(
                    Model: $"{WhisperModelIds.Preview} q5_1",
                    Provider: "Cuda",
                    Error: null,
                    ColdStartMilliseconds: 550,
                    MedianPassMilliseconds: new Dictionary<string, long?> { ["2.5"] = 54 },
                    PassesOverCadence: 0,
                    Passes: 3,
                    WordErrorRateByLanguage: new Dictionary<string, double?> { ["de"] = 0.0 },
                    WordErrorRate: 0.0,
                    PeakWorkingSetMegabytes: 517,
                    VideoMemoryMegabytes: 562,
                    CardLoadBeforePercent: 12,
                    Fixtures: [],
                    PassDetails: [],
                    Role: "preview",
                    IntraOpThreads: 8,
                    CpuFallbackThreads: 8,
                    WorkerPriority: "BelowNormal",
                    RequestedLanguage: "auto",
                    DetectedLanguage: "de",
                    UsedFallback: false,
                    FailureCategory: "none",
                    Classification: BenchRowClassification.Primary,
                    PrimaryFixtureRows: 1,
                    FallbackFixtureRows: 0,
                    EmptyFixtureRows: 0,
                    ErrorFixtureRows: 0,
                    PrefixWordErrorRate: 0.0,
                    PrefixFixtures: [scored],
                    PrefixZeroReferenceInsertions: 0),
            ],
            [2.5]);
        Assert.Contains("prefix WER", markdown, StringComparison.Ordinal);
        Assert.Contains("0-ref ins", markdown, StringComparison.Ordinal);
        Assert.Contains("over 2.5 s (preview)", markdown, StringComparison.Ordinal);
        Assert.Contains("ai-adjudicated-timestamp-review", markdown, StringComparison.Ordinal);
        Assert.Contains("BelowNormal", markdown, StringComparison.Ordinal);

        // Final-role runs do not apply the preview-only 2.5 s cadence threshold.
        Pass[] finalPasses = [new("clip.wav", 20.0, 3_100)];
        Assert.Equal(1, PreviewModelBenchEvaluation.CountPassesOverPreviewCadence(BenchRole.Preview, finalPasses));
        Assert.Null(PreviewModelBenchEvaluation.CountPassesOverPreviewCadence(BenchRole.Final, finalPasses));
        var finalMarkdown = PreviewModelBenchEvaluation.FormatMarkdownReport(
            [
                new CandidateResult(
                    Model: $"{ParakeetModelIds.Final} full precision",
                    Provider: "Cuda",
                    Error: null,
                    ColdStartMilliseconds: 610,
                    MedianPassMilliseconds: new Dictionary<string, long?> { ["20.0"] = 3_100 },
                    PassesOverCadence: PreviewModelBenchEvaluation.CountPassesOverPreviewCadence(BenchRole.Final, finalPasses),
                    Passes: 1,
                    WordErrorRateByLanguage: new Dictionary<string, double?> { ["de"] = 0.0 },
                    WordErrorRate: 0.0,
                    PeakWorkingSetMegabytes: 780,
                    VideoMemoryMegabytes: 1_240,
                    CardLoadBeforePercent: 8,
                    Fixtures: [],
                    PassDetails: [.. finalPasses],
                    Role: "final",
                    IntraOpThreads: 1,
                    CpuFallbackThreads: 6,
                    WorkerPriority: "Normal"),
            ],
            [20.0]);
        Assert.DoesNotContain("over 2.5 s", finalMarkdown, StringComparison.Ordinal);
        Assert.Contains("| passes |", finalMarkdown, StringComparison.Ordinal);

        // Failed rows preserve the exact column count of the summary table.
        var failedRow = CandidateResult.Failed(
            $"{ParakeetModelIds.Final} full precision",
            RuntimeProviderKind.Cuda,
            "model pack is not installed",
            BenchRole.Final,
            intraOpThreads: 1,
            cpuFallbackThreads: 6,
            workerPriority: ProcessPriorityClass.Normal,
            failureCategory: "ModelPackUnavailable");
        var mixedMarkdown = PreviewModelBenchEvaluation.FormatMarkdownReport(
            [
                failedRow,
                new CandidateResult(
                    Model: $"{ParakeetModelIds.Final} int8",
                    Provider: "Cpu",
                    Error: null,
                    ColdStartMilliseconds: 500,
                    MedianPassMilliseconds: new Dictionary<string, long?> { ["20.0"] = 900 },
                    PassesOverCadence: null,
                    Passes: 1,
                    WordErrorRateByLanguage: new Dictionary<string, double?> { ["de"] = 0.0 },
                    WordErrorRate: 0.0,
                    PeakWorkingSetMegabytes: 450,
                    VideoMemoryMegabytes: null,
                    CardLoadBeforePercent: null,
                    Fixtures: [],
                    PassDetails: [.. finalPasses],
                    Role: "final",
                    IntraOpThreads: 6,
                    CpuFallbackThreads: 6,
                    WorkerPriority: "Normal"),
            ],
            [20.0]);
        var tableLines = mixedMarkdown.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, tableLines.Length);
        var headerPipes = tableLines[0].Count(c => c == '|');
        Assert.Equal(headerPipes, tableLines[2].Count(c => c == '|'));
        Assert.Equal(headerPipes, tableLines[3].Count(c => c == '|'));
    }

    [Fact]
    public void PrefixReferenceValidationAndZeroReferenceWindowsFailClosedAndSurfaceInsertions()
    {
        var fixture = new Fixture("de-DE-row0.wav", "de", "ich möchte gerne Geld", new float[16_000 * 3]);

        // Unknown fixture file is rejected.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.ValidatePrefixReferences(
                [new PrefixReferenceEntry("missing-row.wav", 2.5, "ich möchte")],
                [fixture]));

        // Window longer than clip duration is rejected.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.ValidatePrefixReferences(
                [new PrefixReferenceEntry("de-DE-row0.wav", 5.0, "ich möchte")],
                [fixture]));

        // Token timings beyond fixture duration are rejected during alignment, JSON parsing, and validation.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.AlignPrefixReference(
                "de-DE-row0.wav",
                "ich möchte gerne Geld",
                windowSeconds: 1.5,
                clipDurationSeconds: 3.0,
                [new TranscriptTokenTiming(" ich", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(3500))]));
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.ParsePrefixReferencesJson(
                """
                {
                  "schemaVersion": 1,
                  "prefixes": [
                    {
                      "file": "de-DE-row0.wav",
                      "windowSeconds": 1.5,
                      "tokenTimings": [
                        { "text": " ich", "startMs": 100, "endMs": 3500 }
                      ]
                    }
                  ]
                }
                """,
                [fixture]));
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.ValidatePrefixReferences(
                [new PrefixReferenceEntry("de-DE-row0.wav", 1.5, "ich", MaxTokenEndSeconds: 3.5)],
                [fixture]));

        // Object-form prefix JSON rejects non-string/non-object window values (such as null) instead of skipping them.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.ParsePrefixReferencesJson(
                """
                {
                  "de-DE-row0.wav": {
                    "0.5": "ich",
                    "2.5": null
                  }
                }
                """));

        // Supplied fullReference in tokenTimings that disagrees with the matched fixture reference is rejected.
        Assert.Throws<InvalidDataException>(() =>
            PreviewModelBenchEvaluation.ParsePrefixReferencesJson(
                """
                {
                  "schemaVersion": 1,
                  "prefixes": [
                    {
                      "file": "de-DE-row0.wav",
                      "windowSeconds": 1.5,
                      "fullReference": "completely different reference text",
                      "tokenTimings": [
                        { "text": " completely", "startMs": 100, "endMs": 500 },
                        { "text": " different", "startMs": 500, "endMs": 900 },
                        { "text": " reference", "startMs": 900, "endMs": 1300 },
                        { "text": " text", "startMs": 1300, "endMs": 1700 }
                      ]
                    }
                  ]
                }
                """,
                [fixture]));

        // Zero-word reference window: silence is primary with 0.0 WER; hallucinated words score 1.0 and count insertions.
        var zeroRefEntry = new PrefixReferenceEntry("de-DE-row0.wav", 0.5, string.Empty, "auto-aligned-duration");
        var silent = PreviewModelBenchEvaluation.ScorePrefixFixture(
            fixture,
            zeroRefEntry,
            transcriptText: string.Empty,
            elapsedMilliseconds: 30,
            requestedLanguage: "auto",
            detectedLanguage: "de",
            usedFallback: false);
        Assert.Equal(BenchRowClassification.Primary, silent.Classification);
        Assert.Equal(0, silent.EditDistance);
        Assert.Equal(0.0, silent.WordErrorRate);
        Assert.Equal(0.0, PreviewModelBenchEvaluation.PrefixRate([silent]));
        Assert.Equal(0, PreviewModelBenchEvaluation.CountZeroReferenceInsertions([silent]));

        var hallucinated = PreviewModelBenchEvaluation.ScorePrefixFixture(
            fixture,
            zeroRefEntry,
            transcriptText: "Thank you",
            elapsedMilliseconds: 35,
            requestedLanguage: "auto",
            detectedLanguage: "en",
            usedFallback: false);
        Assert.Equal(BenchRowClassification.Primary, hallucinated.Classification);
        Assert.Equal(2, hallucinated.EditDistance);
        Assert.Equal(1.0, hallucinated.WordErrorRate);
        Assert.Equal(1.0, PreviewModelBenchEvaluation.PrefixRate([hallucinated]));
        Assert.Equal(2, PreviewModelBenchEvaluation.CountZeroReferenceInsertions([hallucinated]));
    }

    [Fact]
    public async Task CandidateModelDirectoryResolverVerifiesActiveStorePackAndHonorsConfiguredOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), "bench-locator-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dataModelsRoot = Path.Combine(root, "data-models");
            var repoRoot = Path.Combine(root, "repo");
            var configuredDir = Directory.CreateDirectory(Path.Combine(root, "configured")).FullName;
            var modelId = WhisperModelIds.Preview;
            byte[] modelBytes = [1, 2, 3, 4];
            var fileSha256 = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
            var payload = new Dictionary<string, object?>
            {
                ["schemaVersion"] = ModelManifestVerifier.CurrentManifestSchemaVersion,
                ["modelId"] = modelId,
                ["version"] = "1.0.0",
                ["minimumAppVersion"] = "1.0.0",
                ["license"] = new Dictionary<string, object?>
                {
                    ["name"] = "MIT",
                    ["url"] = "https://example.invalid/license",
                    ["notice"] = "Fixture notice.",
                },
                ["files"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["relativePath"] = WhisperModelFileNames.PreviewSmall,
                        ["sizeBytes"] = modelBytes.Length,
                        ["sha256"] = fileSha256,
                        ["sources"] = new[] { "https://models.enviouslabs.co/test/model.bin" },
                    },
                },
            };
            using var manifestDoc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var digest = ModelManifestCanonicalJson.DigestOf(manifestDoc.RootElement);
            payload[ModelManifestCanonicalJson.DigestPropertyName] = digest;

            var activeVersionDir = Path.Combine(dataModelsRoot, modelId, "versions", "1.0.0", digest);
            Directory.CreateDirectory(activeVersionDir);
            var requiredFile = Path.Combine(activeVersionDir, WhisperModelFileNames.PreviewSmall);
            await File.WriteAllBytesAsync(requiredFile, modelBytes);
            await File.WriteAllBytesAsync(
                Path.Combine(activeVersionDir, ".model-manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(payload));
            await File.WriteAllTextAsync(
                Path.Combine(activeVersionDir, ".license-notice.txt"),
                $"MIT{Environment.NewLine}https://example.invalid/license{Environment.NewLine}{Environment.NewLine}Fixture notice.{Environment.NewLine}");
            await File.WriteAllTextAsync(
                Path.Combine(dataModelsRoot, modelId, "active.json"),
                $$"""
                {
                  "schemaVersion": 1,
                  "modelId": "{{modelId}}",
                  "version": "1.0.0",
                  "manifestDigest": "{{digest}}"
                }
                """);

            var legacyDir = Directory.CreateDirectory(Path.Combine(dataModelsRoot, modelId)).FullName;
            await File.WriteAllBytesAsync(Path.Combine(legacyDir, WhisperModelFileNames.PreviewSmall), modelBytes);
            await File.WriteAllBytesAsync(Path.Combine(configuredDir, WhisperModelFileNames.PreviewSmall), modelBytes);
            var incompleteExplicitRoot = Directory.CreateDirectory(Path.Combine(root, "incomplete-explicit")).FullName;

            // Explicit --models root lacking the candidate fails closed (returns null) instead of falling through to dataModelsRoots.
            var resolvedIncompleteExplicit = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
                modelId,
                dir => File.Exists(Path.Combine(dir, WhisperModelFileNames.PreviewSmall)),
                explicitModelsRoot: incompleteExplicitRoot,
                repositoryRoot: repoRoot,
                dataModelsRoots: [dataModelsRoot]);
            Assert.Null(resolvedIncompleteExplicit);

            // Configured directory override wins ahead of the active store lookup.
            var resolvedOverride = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
                modelId,
                dir => File.Exists(Path.Combine(dir, WhisperModelFileNames.PreviewSmall)),
                explicitModelsRoot: null,
                repositoryRoot: repoRoot,
                dataModelsRoots: [dataModelsRoot],
                configuredDirectory: configuredDir);
            Assert.Equal(Path.GetFullPath(configuredDir), resolvedOverride);

            // Verified active store pack wins over legacy directory.
            var resolvedActive = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
                modelId,
                dir => File.Exists(Path.Combine(dir, WhisperModelFileNames.PreviewSmall)),
                explicitModelsRoot: null,
                repositoryRoot: repoRoot,
                dataModelsRoots: [dataModelsRoot]);
            Assert.Equal(Path.GetFullPath(activeVersionDir), resolvedActive);

            // Corrupting a byte in the active store pack causes OpenActiveOfflineAsync verification to reject it.
            await File.WriteAllBytesAsync(requiredFile, [9, 9, 9, 9]);
            var resolvedFallbackToLegacy = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
                modelId,
                dir => File.Exists(Path.Combine(dir, WhisperModelFileNames.PreviewSmall)),
                explicitModelsRoot: null,
                repositoryRoot: repoRoot,
                dataModelsRoots: [dataModelsRoot]);
            Assert.Equal(Path.GetFullPath(legacyDir), resolvedFallbackToLegacy);

            // Configured app data directory (ENVIOUSWISPR_DATA_DIRECTORY) and release channel selection determine the active models root.
            var customAppDataRoot = Directory.CreateDirectory(Path.Combine(root, "custom-appdata")).FullName;
            var customModelDir = Directory.CreateDirectory(Path.Combine(customAppDataRoot, "models", modelId)).FullName;
            await File.WriteAllBytesAsync(Path.Combine(customModelDir, WhisperModelFileNames.PreviewSmall), modelBytes);
            var resolvedFromDataDir = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
                modelId,
                dir => File.Exists(Path.Combine(dir, WhisperModelFileNames.PreviewSmall)),
                explicitModelsRoot: null,
                repositoryRoot: repoRoot,
                configuredDataDirectory: customAppDataRoot);
            Assert.Equal(Path.GetFullPath(customModelDir), resolvedFromDataDir);

            Assert.EndsWith(
                Path.Combine("Envious Labs", "EnviousWispr-Founder"),
                PreviewModelBenchEvaluation.ResolveActiveAppDataDirectory(releaseChannel: "founder"),
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                Path.Combine("Envious Labs", "EnviousWispr"),
                PreviewModelBenchEvaluation.ResolveActiveAppDataDirectory(releaseChannel: "stable"),
                StringComparison.OrdinalIgnoreCase);

            // CUDA runtime resolution is scoped to the selected channel's app data directory and does not borrow another channel's runtime.
            var stableAppData = Directory.CreateDirectory(Path.Combine(root, "EnviousWispr")).FullName;
            var founderAppData = Directory.CreateDirectory(Path.Combine(root, "EnviousWispr-Founder")).FullName;
            var stableCudaRuntime = Directory.CreateDirectory(Path.Combine(stableAppData, "runtime", "cuda")).FullName;
            Assert.Equal(
                Path.GetFullPath(stableCudaRuntime),
                PreviewModelBenchEvaluation.ResolveCudaRuntimeDirectory(stableAppData, useEnvironmentOverride: false));
            Assert.Null(PreviewModelBenchEvaluation.ResolveCudaRuntimeDirectory(founderAppData, useEnvironmentOverride: false));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MacCorpusManifestLoaderParsesSealedCasesPrimaryCasesAndStressManifestsWithLanguageAndMaxFixturesFilters()
    {
        var root = Path.Combine(Path.GetTempPath(), "mac-corpus-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Mac intl-sealed-2026-11 cases.sealed.jsonl (uses voice_text, resolves <dir>/wav/<id>.wav)
            var enSealedDir = Directory.CreateDirectory(Path.Combine(root, "speech", "en-sealed")).FullName;
            var enWavDir = Directory.CreateDirectory(Path.Combine(enSealedDir, "wav")).FullName;
            WriteMinimalPcmWaveFile(Path.Combine(enWavDir, "SB-00000.wav"));
            WriteMinimalPcmWaveFile(Path.Combine(enWavDir, "SB-00001.wav"));
            var sealedPath = Path.Combine(enSealedDir, "cases.sealed.jsonl");
            File.WriteAllText(
                sealedPath,
                """
                {"id": "SB-00000", "lang": "en", "shape": "self_correction", "voice_text": "Božidar, the couple wants the group photos before dinner—sorry, before the speeches.", "expected_output": "Bositor, the couple wants the group photos before the speeches."}
                {"id": "SB-00001", "lang": "en", "shape": "self_correction", "voice_text": "Aurel said he'd call the owner about the trailer.", "expected_output": "Orl said he'd call the owner."}
                """);

            var sealedFixtures = PreviewModelBenchEvaluation.LoadCorpusFixtures(sealedPath);
            Assert.Equal(2, sealedFixtures.Count);
            Assert.Equal("SB-00000.wav", sealedFixtures[0].File);
            Assert.Equal("en", sealedFixtures[0].Language);
            Assert.Contains("before dinner", sealedFixtures[0].Reference, StringComparison.Ordinal);

            // 2. Mac intl-sealed-2026-11 cases.jsonl (uses text + id + lang/locale, resolves <dir>/wav/<id>.wav)
            var dePrimaryDir = Directory.CreateDirectory(Path.Combine(root, "speech", "de-primary")).FullName;
            var deWavDir = Directory.CreateDirectory(Path.Combine(dePrimaryDir, "wav")).FullName;
            WriteMinimalPcmWaveFile(Path.Combine(deWavDir, "de-20260815-S-clean-0000-0.wav"));
            var deCasesPath = Path.Combine(dePrimaryDir, "cases.jsonl");
            File.WriteAllText(
                deCasesPath,
                """
                {"id": "de-20260815-S-clean-0000-0", "locale": "de-DE", "lang": "de", "bucket": "clean", "text": "Ich schicke Anna nach dem letzten Besuch noch ein kurzes Update."}
                """);

            var zhPrimaryDir = Directory.CreateDirectory(Path.Combine(root, "speech", "zh-primary")).FullName;
            var zhWavDir = Directory.CreateDirectory(Path.Combine(zhPrimaryDir, "wav")).FullName;
            WriteMinimalPcmWaveFile(Path.Combine(zhWavDir, "zh-20260815-S-clean-0000-0.wav"));
            var zhCasesPath = Path.Combine(zhPrimaryDir, "cases.jsonl");
            File.WriteAllText(
                zhCasesPath,
                """
                {"id": "zh-20260815-S-clean-0000-0", "locale": "zh-CN", "lang": "zh", "bucket": "clean", "text": "今天上门随访杨洋，血压和睡眠情况都正常。"}
                """);

            // Directory discovery across speech/ plus --languages and --max-fixtures filters
            var speechRoot = Path.Combine(root, "speech");
            var allSpeechFixtures = PreviewModelBenchEvaluation.LoadCorpusFixtures(speechRoot);
            Assert.Equal(4, allSpeechFixtures.Count);

            var filteredZhDe = PreviewModelBenchEvaluation.LoadCorpusFixtures(
                speechRoot,
                languages: ["de-DE", "ZH"]);
            Assert.Equal(2, filteredZhDe.Count);
            Assert.Equal(["de", "zh"], filteredZhDe.Select(f => f.Language).ToArray());

            var cappedFixtures = PreviewModelBenchEvaluation.LoadCorpusFixtures(
                speechRoot,
                maxFixtures: 2);
            Assert.Equal(2, cappedFixtures.Count);

            // 3. Mac german-parakeet-stress and lang-stress manifest.jsonl (uses sample_id, reference_text, locale/voice, resolves audio/<sample_id>.wav)
            var stressDir = Directory.CreateDirectory(Path.Combine(root, "german-parakeet-stress")).FullName;
            var stressAudioDir = Directory.CreateDirectory(Path.Combine(stressDir, "audio")).FullName;
            WriteMinimalPcmWaveFile(Path.Combine(stressAudioDir, "std01.wav"));
            var stressManifestPath = Path.Combine(stressDir, "manifest.jsonl");
            File.WriteAllText(
                stressManifestPath,
                """
                {"sample_id": "std01", "category": "standard", "reference_text": "Guten Morgen.", "normalized_reference_text": null, "locale": "de-DE", "voice": "de-DE-KatjaNeural"}
                """);

            var stressFixtures = PreviewModelBenchEvaluation.LoadCorpusFixtures(stressManifestPath);
            var stressSingle = Assert.Single(stressFixtures);
            Assert.Equal("std01.wav", stressSingle.File);
            Assert.Equal("de", stressSingle.Language);
            Assert.Equal("Guten Morgen.", stressSingle.Reference);

            // lang-stress row without explicit 'locale', inferred from voice / parent directory, with --wav-root override
            var elStressDir = Directory.CreateDirectory(Path.Combine(root, "lang-stress", "el")).FullName;
            var customWavRoot = Directory.CreateDirectory(Path.Combine(root, "custom-wav-root")).FullName;
            WriteMinimalPcmWaveFile(Path.Combine(customWavRoot, "el_std01.wav"));
            var elManifestPath = Path.Combine(elStressDir, "manifest.jsonl");
            File.WriteAllText(
                elManifestPath,
                """
                {"sample_id": "el_std01", "category": "standard", "reference_text": "Καλημέρα.", "voice": "el-GR-AthinaNeural", "speech_rate": "default", "duration_seconds": 1.73}
                """);

            var elFixtures = PreviewModelBenchEvaluation.LoadCorpusFixtures(elManifestPath, wavRoot: customWavRoot);
            var elSingle = Assert.Single(elFixtures);
            Assert.Equal("el_std01.wav", elSingle.File);
            Assert.Equal("el", elSingle.Language);
            Assert.Equal("Καλημέρα.", elSingle.Reference);

            // Missing WAV file fails closed with InvalidDataException
            File.Delete(Path.Combine(customWavRoot, "el_std01.wav"));
            Assert.Throws<InvalidDataException>(() =>
                PreviewModelBenchEvaluation.LoadCorpusFixtures(elManifestPath, wavRoot: customWavRoot));

            // 4. Existing tools/whisper-uat/fixtures/manifest.json remains the unchanged default
            var defaultManifestPath = Path.Combine(FindRepositoryRoot(), "tools", "whisper-uat", "fixtures", "manifest.json");
            var defaultFixtures = PreviewModelBenchEvaluation.LoadCorpusFixtures(defaultManifestPath);
            Assert.Equal(12, defaultFixtures.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CjkCharacterErrorRateStripsCjkPunctuationAndSkipsLowercasingForCaselessScripts()
    {
        // CJK punctuation stripping: 。、！？「」『』（）【】《》…—–·，；：
        var zhRef = "「今天上门随访杨洋，血压、睡眠情况都正常！（复诊：【下周二】《备注》…—–·；：）」";
        var zhHypExact = "『今天上门随访杨洋血压睡眠情况都正常复诊下周二备注』";
        Assert.Equal(
            PreviewModelBenchEvaluation.NormalizeCharacters(zhRef, "zh"),
            PreviewModelBenchEvaluation.NormalizeCharacters(zhHypExact, "zh"));
        Assert.Equal(0.0, PreviewModelBenchEvaluation.ComputeCharacterErrorRate(zhRef, zhHypExact, "zh"));

        // 1 character substitution out of 10 characters = 0.10 CER
        var scoredZh = PreviewModelBenchEvaluation.ScoreFixture(
            "zh-0001.wav",
            "zh-CN",
            "今天上门随访杨洋，血压。",
            "今天上门随访杨洋，血糖！",
            elapsedMilliseconds: 95,
            requestedLanguage: "auto",
            detectedLanguage: "zh",
            usedFallback: false);
        Assert.Equal("zh", scoredZh.Language);
        Assert.Equal("cer", scoredZh.PrimaryMetric);
        Assert.Equal(10, scoredZh.ReferenceCharacters);
        Assert.Equal(10, scoredZh.ActualCharacters);
        Assert.Equal(1, scoredZh.CharacterEditDistance);
        Assert.Equal(0.1, scoredZh.CharacterErrorRate);
        Assert.Equal(BenchRubricTier.Experimental, scoredZh.RubricTier);
        Assert.True(scoredZh.DetectedLanguageMatched);

        // Unspaced CER languages are exactly zh, ja, ko, th
        foreach (var cerLang in new[] { "zh", "ja", "ko", "th", "zh-CN", "ja-JP" })
        {
            Assert.True(PreviewModelBenchEvaluation.UsesCharacterErrorRate(cerLang));
            Assert.Equal("cer", PreviewModelBenchEvaluation.MetricForLanguage(cerLang));
        }

        foreach (var werLang in new[] { "en", "de", "fr", "es", "ru", "hi", "ar", "he", "bn", "ta", "te" })
        {
            Assert.False(PreviewModelBenchEvaluation.UsesCharacterErrorRate(werLang));
            Assert.Equal("wer", PreviewModelBenchEvaluation.MetricForLanguage(werLang));
        }

        // Caseless languages (ja, zh, ko, th, ar, he, hi, bn, ta, te) skip lowercasing; bicameral scripts lowercase
        foreach (var caseless in new[] { "ja", "zh", "ko", "th", "ar", "he", "hi", "bn", "ta", "te" })
        {
            Assert.True(PreviewModelBenchEvaluation.IsCaselessLanguage(caseless));
            var normalizedCaseless = PreviewModelBenchEvaluation.NormalizeText("API Call 測試!", caseless);
            Assert.Equal("API Call 測試", normalizedCaseless);
            Assert.Contains("API", PreviewModelBenchEvaluation.NormalizeWords("API Call", caseless));
        }

        foreach (var bicameral in new[] { "en", "de", "fr", "ru", "el", "uk" })
        {
            Assert.False(PreviewModelBenchEvaluation.IsCaselessLanguage(bicameral));
            Assert.Equal("api call", PreviewModelBenchEvaluation.NormalizeText("API Call!", bicameral));
            Assert.Contains("api", PreviewModelBenchEvaluation.NormalizeWords("API Call", bicameral));
        }
    }

    [Fact]
    public void DetectedLanguageMatchRateAndMacRubricTierClassificationRenderInJsonAndMarkdown()
    {
        // Rubric thresholds:
        // WER: Ship < 5%, Experimental 5..15%, Hide > 15%
        Assert.Equal(BenchRubricTier.Ship, PreviewModelBenchEvaluation.ClassifyRubricTier("de", 0.049));
        Assert.Equal(BenchRubricTier.Experimental, PreviewModelBenchEvaluation.ClassifyRubricTier("de", 0.05));
        Assert.Equal(BenchRubricTier.Experimental, PreviewModelBenchEvaluation.ClassifyRubricTier("de", 0.15));
        Assert.Equal(BenchRubricTier.Hide, PreviewModelBenchEvaluation.ClassifyRubricTier("de", 0.151));

        // CER (zh, ja, ko, th): Ship < 10%, Experimental 10..20%, Hide > 20%
        Assert.Equal(BenchRubricTier.Ship, PreviewModelBenchEvaluation.ClassifyRubricTier("zh", 0.099));
        Assert.Equal(BenchRubricTier.Experimental, PreviewModelBenchEvaluation.ClassifyRubricTier("zh", 0.10));
        Assert.Equal(BenchRubricTier.Experimental, PreviewModelBenchEvaluation.ClassifyRubricTier("ja", 0.20));
        Assert.Equal(BenchRubricTier.Hide, PreviewModelBenchEvaluation.ClassifyRubricTier("ko", 0.201));
        Assert.Null(PreviewModelBenchEvaluation.ClassifyRubricTier("en", null));

        // Language subtag normalization and match rate
        Assert.True(PreviewModelBenchEvaluation.IsDetectedLanguageMatch("de-DE", "de"));
        Assert.True(PreviewModelBenchEvaluation.IsDetectedLanguageMatch("German", "de-DE"));
        Assert.True(PreviewModelBenchEvaluation.IsDetectedLanguageMatch("zh_CN", "zh"));
        Assert.False(PreviewModelBenchEvaluation.IsDetectedLanguageMatch("en", "de"));
        Assert.False(PreviewModelBenchEvaluation.IsDetectedLanguageMatch("auto", "de"));
        Assert.False(PreviewModelBenchEvaluation.IsDetectedLanguageMatch(null, "de"));

        var de1 = PreviewModelBenchEvaluation.ScoreFixture(
            "de-1.wav",
            "de-DE",
            "eins zwei drei vier fünf sechs sieben acht neun zehn",
            "eins zwei drei vier fünf sechs sieben acht neun zehn",
            elapsedMilliseconds: 80,
            requestedLanguage: "auto",
            detectedLanguage: "de-DE",
            usedFallback: false);
        var de2 = PreviewModelBenchEvaluation.ScoreFixture(
            "de-2.wav",
            "de",
            "eins zwei drei vier fünf sechs sieben acht neun zehn",
            "eins zwei drei vier fünf sechs sieben acht neun zehn",
            elapsedMilliseconds: 85,
            requestedLanguage: "auto",
            detectedLanguage: "en",
            usedFallback: false);
        var zh1 = PreviewModelBenchEvaluation.ScoreFixture(
            "zh-1.wav",
            "zh",
            "今天上门随访杨洋血压和睡眠情况都正常提醒",
            "今天上门随访杨洋血压和睡眠情况都正常提醒",
            elapsedMilliseconds: 90,
            requestedLanguage: "auto",
            detectedLanguage: "zh-CN",
            usedFallback: false);

        var summary = PreviewModelBenchEvaluation.SummarizeLanguageMetrics([de1, de2, zh1]);
        Assert.Equal(0.0, summary.WordErrorRateByLanguage["de"]);
        Assert.Equal(0.0, summary.CharacterErrorRateByLanguage["zh"]);
        Assert.Equal(0.5, summary.DetectedLanguageMatchRateByLanguage["de"]);
        Assert.Equal(1.0, summary.DetectedLanguageMatchRateByLanguage["zh"]);
        Assert.Equal(Math.Round(2.0 / 3.0, 3), summary.DetectedLanguageMatchRate);
        Assert.Equal(BenchRubricTier.Ship, summary.RubricTierByLanguage["de"]);
        Assert.Equal(BenchRubricTier.Ship, summary.RubricTierByLanguage["zh"]);
        Assert.Equal(BenchRubricTier.Ship, summary.RubricTier);

        var candidateRow = new CandidateResult(
            Model: $"{WhisperModelIds.Final} q5_0",
            Provider: "Cuda",
            Error: null,
            ColdStartMilliseconds: 420,
            MedianPassMilliseconds: null,
            PassesOverCadence: null,
            Passes: 0,
            WordErrorRateByLanguage: summary.WordErrorRateByLanguage,
            WordErrorRate: summary.WordErrorRate,
            PeakWorkingSetMegabytes: 612,
            VideoMemoryMegabytes: 980,
            CardLoadBeforePercent: 5,
            Fixtures: [de1, de2, zh1],
            PassDetails: [],
            Role: "final",
            IntraOpThreads: 8,
            CpuFallbackThreads: 8,
            WorkerPriority: "Normal",
            RequestedLanguage: "auto",
            DetectedLanguage: "de-DE,en,zh-CN",
            PrimaryFixtureRows: 3,
            CharacterErrorRateByLanguage: summary.CharacterErrorRateByLanguage,
            CharacterErrorRate: summary.CharacterErrorRate,
            DetectedLanguageMatchRateByLanguage: summary.DetectedLanguageMatchRateByLanguage,
            DetectedLanguageMatchRate: summary.DetectedLanguageMatchRate,
            RubricTierByLanguage: summary.RubricTierByLanguage,
            RubricTier: summary.RubricTier);

        var json = JsonSerializer.Serialize(candidateRow);
        Assert.Contains("\"CharacterErrorRateByLanguage\"", json, StringComparison.Ordinal);
        Assert.Contains("\"DetectedLanguageMatchRateByLanguage\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RubricTierByLanguage\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Ship\"", json, StringComparison.Ordinal);

        var markdown = PreviewModelBenchEvaluation.FormatMarkdownReport([candidateRow], []);
        Assert.Contains("CER all", markdown, StringComparison.Ordinal);
        Assert.Contains("CER zh", markdown, StringComparison.Ordinal);
        Assert.Contains("lang match de", markdown, StringComparison.Ordinal);
        Assert.Contains("50.0%", markdown, StringComparison.Ordinal);
        Assert.Contains("rubric tier", markdown, StringComparison.Ordinal);
        Assert.Contains("tier de", markdown, StringComparison.Ordinal);
        Assert.Contains("tier zh", markdown, StringComparison.Ordinal);
        Assert.Contains("| Ship |", markdown, StringComparison.Ordinal);
        Assert.Contains("| CER |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCommandLineSupportsAccuracyOnlyCorpusManifestWavRootMaxFixturesAndLanguages()
    {
        var repoRoot = FindRepositoryRoot();
        var options = PreviewModelBenchEvaluation.ParseCommandLine(
            [
                "--role", "final",
                "--accuracy-only",
                "--corpus-manifest", @"C:\benchmarks\mac-corpus\speech\de-primary\cases.jsonl",
                "--wav-root", @"C:\benchmarks\mac-corpus\speech\de-primary\wav",
                "--max-fixtures", "25",
                "--languages", "de-DE, zh-CN, en",
                "--providers", "cuda",
                "--only", "parakeet",
                "--allow-busy-card",
            ],
            defaultRepositoryRoot: repoRoot);

        Assert.Equal(BenchRole.Final, options.Role);
        Assert.True(options.AccuracyOnly);
        Assert.True(options.AllowBusyCard);
        Assert.Equal(@"C:\benchmarks\mac-corpus\speech\de-primary\cases.jsonl", options.CorpusManifestPath);
        Assert.Equal(@"C:\benchmarks\mac-corpus\speech\de-primary\wav", options.WavRoot);
        Assert.Equal(25, options.MaxFixtures);
        Assert.Equal(["de", "zh", "en"], options.Languages);
        Assert.Equal([RuntimeProviderKind.Cuda], options.Providers);
        Assert.Equal("parakeet", options.Only);
        Assert.False(options.ShowHelp);

        var helpOptions = PreviewModelBenchEvaluation.ParseCommandLine(["--help"], defaultRepositoryRoot: repoRoot);
        Assert.True(helpOptions.ShowHelp);
        Assert.Contains("--corpus-manifest", PreviewModelBenchEvaluation.FormatHelpText(), StringComparison.Ordinal);
        Assert.Contains("--accuracy-only", PreviewModelBenchEvaluation.FormatHelpText(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            PreviewModelBenchEvaluation.ParseCommandLine(["--max-fixtures", "0"], defaultRepositoryRoot: repoRoot));
        Assert.Throws<ArgumentException>(() =>
            PreviewModelBenchEvaluation.ParseCommandLine(["--languages", "auto"], defaultRepositoryRoot: repoRoot));
        Assert.Throws<ArgumentException>(() =>
            PreviewModelBenchEvaluation.ParseCommandLine(["--corpus-manifest"], defaultRepositoryRoot: repoRoot));
    }

    private static void WriteMinimalPcmWaveFile(string path, int sampleCount = 1600)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataSize = sampleCount * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(16_000);   // 16 kHz
        writer.Write(16_000 * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var i = 0; i < sampleCount; i++)
        {
            writer.Write((short)0);
        }
    }

    private static HardwareSnapshot CreateHardware(int physicalCores, int logicalProcessors) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Intel,
        PhysicalCoreCount: physicalCores,
        LogicalProcessorCount: logicalProcessors,
        TotalPhysicalMemoryBytes: 64UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(true, 1, 13_000),
        IsOnnxRuntimeCudaDependencySetAvailable: true,
        IsWhisperCudaDependencySetAvailable: true);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
