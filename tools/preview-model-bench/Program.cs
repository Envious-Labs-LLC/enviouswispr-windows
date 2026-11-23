// THE LIVE PREVIEW AND FINAL ASR MODEL BENCH (#127, #258). Live preview on the processor costs about two seconds a
// pass whatever the window, so the 2.5-second cadence has no headroom; the founder's direction is to choose the
// preview model by measuring candidates across hardware rather than by hand, and to expect a different model per tier.
// With --role final|preview (#258), the same bench also measures final-engine worker accuracy and prefix-aligned
// partial-preview WER using each role's own production worker options, timeouts, and priority (Normal vs BelowNormal):
//
//   - PER-PASS COST at the window lengths the preview loop actually sees, on real archived dictations;
//   - ACCURACY on the public multilingual fixtures, as full-reference word error rate per language and optional
//     prefix-aligned word error rate (--prefix-references <json>);
//   - COLD START: starting the worker and the first pass, which a dictation pays once;
//   - MEMORY: the worker's peak working set, and its video memory on the card.
//
// IT RUNS THE PRODUCT'S OWN PATH. Each candidate goes through RuntimeWorkerTranscriptionEngine and the out-of-process
// runtime worker, with the thread count, timeouts, and priority the selected role picks in production. A Whisper
// candidate that is not the shipped preview model is given to the worker under the preview model's file name, in a
// folder of its own, so nothing in the product has to know the bench exists. It decides nothing: it prints what each
// candidate cost, and the choice stays the founder's.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.PreviewModelBench;
using EnviousWispr.Services.Runtime;

const int sampleRate = PreviewModelBenchEvaluation.SampleRate;
const double cadenceMilliseconds = 2_500;
// A card at or above this load, by the middle of five readings, is doing someone else's work.
const int BusyCard = 50;
double[] windowSeconds = [0.5, 2.5, 5, 10, 20];

var repositoryRoot = ArgumentValue("--repo") ?? FindRepositoryRoot(AppContext.BaseDirectory);
var modelsRoot = ArgumentValue("--models");
var releaseChannelArg = ArgumentValue("--channel");
var activeAppDataDirectory = PreviewModelBenchEvaluation.ResolveActiveAppDataDirectory(releaseChannel: releaseChannelArg);
var audioDirectory = ArgumentValue("--audio") ?? Path.Combine(activeAppDataDirectory, "audio-archive");
var extraModels = ArgumentValue("--extra-models");
var fixturesDirectory = ArgumentValue("--fixtures") ?? Path.Combine(repositoryRoot, "tools", "whisper-uat", "fixtures");
var repeats = int.TryParse(ArgumentValue("--repeats"), out var parsedRepeats) ? Math.Max(1, parsedRepeats) : 3;
var providers = (ArgumentValue("--providers") ?? "cpu,cuda")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(name => name.Equals("cuda", StringComparison.OrdinalIgnoreCase) ? RuntimeProviderKind.Cuda : RuntimeProviderKind.Cpu)
    .Distinct()
    .ToArray();
var only = ArgumentValue("--only");
var role = PreviewModelBenchEvaluation.ParseRole(ArgumentValue("--role"));
var requestedLanguageArg = ArgumentValue("--language") ?? "auto";
var prefixReferencesPath = ArgumentValue("--prefix-references");
var output = ArgumentValue("--out") ?? Path.Combine(Path.GetTempPath(), "preview-model-bench.json");
var markdownOutput = ArgumentValue("--markdown");

var clips = LoadClips(audioDirectory);
var fixtures = ReadFixtures(fixturesDirectory);
if (clips.Count == 0 || fixtures.Count == 0)
{
    Console.Error.WriteLine($"Need archived dictations in {audioDirectory} ({clips.Count}) and fixtures in {fixturesDirectory} ({fixtures.Count}).");
    return 2;
}

// ONE FROZEN PREFIX-REFERENCE SET ACROSS ALL CANDIDATES AND PROVIDERS (#258). Every candidate is graded against
// the exact same verified prefix references (explicit or token-timestamp-aligned), validated upfront against the
// fixture manifest and clip durations.
IReadOnlyList<PrefixReferenceEntry>? frozenPrefixReferences = null;
if (!string.IsNullOrWhiteSpace(prefixReferencesPath))
{
    if (!File.Exists(prefixReferencesPath))
    {
        Console.Error.WriteLine($"Prefix references file not found: {prefixReferencesPath}");
        return 2;
    }

    frozenPrefixReferences = PreviewModelBenchEvaluation.ValidatePrefixReferences(
        PreviewModelBenchEvaluation.ParsePrefixReferencesJson(
            await File.ReadAllTextAsync(prefixReferencesPath),
            fixtures),
        fixtures);
}

// THE SAME HARDWARE PROBE PRODUCTION USES (ConfigureLivePreview / ParakeetRuntimeSelector / WhisperRuntimeSelector),
// scoped to the selected release channel's app data directory so one channel never borrows another channel's CUDA pack.
var cudaRuntimeDirectory = PreviewModelBenchEvaluation.ResolveCudaRuntimeDirectory(activeAppDataDirectory);
var hardware = await new WindowsHardwareDiscovery(cudaRuntimeDirectory).ProbeAsync();
var previewThreads = PreviewModelBenchEvaluation.ChoosePreviewThreads(hardware);
var whisperProbe = new LocalWhisperModelProbe();
var parakeetProbe = new LocalParakeetModelProbe();

var whisperSmallDirectory = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
    WhisperModelIds.Preview,
    directory => whisperProbe.Probe(directory).PreviewSmallComplete,
    modelsRoot,
    repositoryRoot,
    releaseChannel: releaseChannelArg);
var whisperTurboDirectory = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
    WhisperModelIds.Final,
    directory => whisperProbe.Probe(directory).QuantizedComplete,
    modelsRoot,
    repositoryRoot,
    releaseChannel: releaseChannelArg);
var parakeetInt8Directory = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
    ParakeetModelIds.Final,
    directory => parakeetProbe.Probe(directory).Int8Complete,
    modelsRoot,
    repositoryRoot,
    releaseChannel: releaseChannelArg);
var parakeetFp32Directory = PreviewModelBenchEvaluation.ResolveCandidateModelDirectory(
    ParakeetModelIds.Final,
    directory => parakeetProbe.Probe(directory).Fp32Complete,
    modelsRoot,
    repositoryRoot,
    releaseChannel: releaseChannelArg);

var candidates = new List<Candidate?>
{
    WhisperExtra("whisper-tiny q5_1", "ggml-tiny-q5_1.bin"),
    WhisperExtra("whisper-base q5_1", "ggml-base-q5_1.bin"),
    new("whisper-small q5_1 (shipped preview)", FinalAsrEngine.Whisper, WhisperModelPack.PreviewSmall,
        whisperSmallDirectory, ModelFile: null),
    new("whisper-large-v3-turbo q5_0", FinalAsrEngine.Whisper, WhisperModelPack.Quantized,
        whisperTurboDirectory, ModelFile: null),
    new("parakeet-tdt-0.6b-v3 int8", FinalAsrEngine.Parakeet, WhisperModelPack.Quantized,
        parakeetInt8Directory, ModelFile: null),
    // THE PRODUCT REFUSES THE QUANTIZED PARAKEET PACK ON THE CARD before it looks for any library, so a card
    // candidate is the full-precision pack, named as such (ParakeetTranscriptionEngine, validation before CUDA).
    new("parakeet-tdt-0.6b-v3 full precision", FinalAsrEngine.Parakeet, WhisperModelPack.Quantized,
        parakeetFp32Directory, ModelFile: null, ParakeetModelPack.FullPrecision),
}
.Where(candidate => candidate is not null)
.Select(candidate => candidate!)
.Where(candidate => only is null || candidate.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
.ToList();

Console.WriteLine(
    $"role={CandidateResult.FormatRole(role)} language={requestedLanguageArg} previewThreads={previewThreads} " +
    $"clips={clips.Count} (longest {clips.Max(clip => clip.Samples.Length) / (double)sampleRate:0.0} s) " +
    $"fixtures={fixtures.Count} repeats={repeats} logical processors={Environment.ProcessorCount}");

var worker = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
var results = new List<CandidateResult>();
foreach (var candidate in candidates)
{
    foreach (var provider in providers)
    {
        Console.Error.WriteLine($"measuring {candidate.Name} on {provider} (role={CandidateResult.FormatRole(role)})...");
        results.Add(await MeasureAsync(candidate, provider));
    }
}

var markdownReport = PreviewModelBenchEvaluation.FormatMarkdownReport(results, windowSeconds);
Console.WriteLine();
Console.WriteLine(markdownReport);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
if (!string.IsNullOrWhiteSpace(markdownOutput))
{
    var markdownDirectory = Path.GetDirectoryName(Path.GetFullPath(markdownOutput));
    if (!string.IsNullOrWhiteSpace(markdownDirectory))
    {
        Directory.CreateDirectory(markdownDirectory);
    }

    await File.WriteAllTextAsync(markdownOutput, markdownReport + Environment.NewLine);
}

Console.WriteLine();
Console.WriteLine($"Details: {output}");
if (!string.IsNullOrWhiteSpace(markdownOutput))
{
    Console.WriteLine($"Markdown: {markdownOutput}");
}

return 0;

List<Clip> LoadClips(string path)
{
    if (File.Exists(path))
    {
        var clip = new Clip(ShortName(path), ReadWaveFile(path));
        return clip.Samples.Length >= (int)(windowSeconds[0] * sampleRate) ? [clip] : [];
    }

    if (!Directory.Exists(path))
    {
        return [];
    }

    return Directory.EnumerateFiles(path, "*.wav")
        .OrderBy(file => file, StringComparer.Ordinal)
        .Select(file => new Clip(ShortName(file), ReadWaveFile(file)))
        .Where(clip => clip.Samples.Length >= (int)(windowSeconds[0] * sampleRate))
        .ToList();
}

Candidate? WhisperExtra(string name, string file)
{
    if (extraModels is null || !File.Exists(Path.Combine(extraModels, file)))
    {
        Console.Error.WriteLine($"skipping {name}: {file} is not in --extra-models");
        return null;
    }

    return new Candidate(name, FinalAsrEngine.Whisper, WhisperModelPack.PreviewSmall, ModelDirectory: null, Path.Combine(extraModels, file));
}

async Task<CandidateResult> MeasureAsync(Candidate candidate, RuntimeProviderKind provider)
{
    // A WHISPER CANDIDATE THAT IS NOT THE SHIPPED PREVIEW MODEL gets a folder of its own, holding it under the preview
    // model's name: the worker loads exactly what the preview loads, and the product never learns another name.
    string? scratch = null;
    try
    {
        var initialOptions = PreviewModelBenchEvaluation.CreateWorkerOptions(
            role,
            worker,
            candidate,
            string.IsNullOrWhiteSpace(candidate.ModelDirectory)
                ? Path.Combine(repositoryRoot, "models", "<unresolved>")
                : candidate.ModelDirectory,
            provider,
            hardware,
            requestedLanguageArg,
            cudaRuntimeDirectory);
        var workerPriority = initialOptions.WorkerPriority ?? ProcessPriorityClass.Normal;
        var requestedLanguage = initialOptions.Language;

        if (string.IsNullOrWhiteSpace(candidate.ModelDirectory) && string.IsNullOrWhiteSpace(candidate.ModelFile))
        {
            return CandidateResult.Failed(
                candidate.Name,
                provider,
                "model pack is not installed or incomplete in the requested models root",
                role,
                initialOptions.IntraOpThreads,
                initialOptions.CpuFallbackThreads,
                workerPriority,
                requestedLanguage,
                failureCategory: "ModelPackUnavailable");
        }

        // A CARD SOMETHING ELSE IS SATURATING MEASURES THE OTHER WORK, NOT THE CANDIDATE. The first run of this bench
        // printed card figures slower than the processor's while another process held the card at 100% - an instrument
        // that answered, well-formed, about the wrong thing. So a busy card is refused by name unless asked for.
        var allowBusyCard = args.Contains("--allow-busy-card", StringComparer.OrdinalIgnoreCase);
        var cardBefore = provider == RuntimeProviderKind.Cuda ? CardLoad() : null;
        if (provider == RuntimeProviderKind.Cuda && !allowBusyCard)
        {
            // FAILS CLOSED: a card the bench cannot read is a card it cannot say was free.
            if (cardBefore is not { } card)
            {
                return CandidateResult.Failed(
                    candidate.Name,
                    provider,
                    "the card could not be read (nvidia-smi) - not measured",
                    role,
                    initialOptions.IntraOpThreads,
                    initialOptions.CpuFallbackThreads,
                    workerPriority,
                    requestedLanguage,
                    failureCategory: "card-unreadable");
            }

            if (card.Utilization >= BusyCard)
            {
                return CandidateResult.Failed(
                    candidate.Name,
                    provider,
                    $"card busy with other work ({card.Utilization}% used, {card.UsedMegabytes} MB held) - not measured",
                    role,
                    initialOptions.IntraOpThreads,
                    initialOptions.CpuFallbackThreads,
                    workerPriority,
                    requestedLanguage,
                    failureCategory: "busy-card");
            }
        }

        var modelDirectory = candidate.ModelDirectory;
        if (candidate.ModelFile is { } file)
        {
            scratch = Path.Combine(Path.GetTempPath(), "preview-model-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            File.Copy(file, Path.Combine(scratch, WhisperModelFileNames.PreviewSmall));
            modelDirectory = scratch;
        }

        var workerOptions = PreviewModelBenchEvaluation.CreateWorkerOptions(
            role,
            worker,
            candidate,
            modelDirectory!,
            provider,
            hardware,
            requestedLanguageArg,
            cudaRuntimeDirectory);
        var cold = Stopwatch.StartNew();
        await using var engine = new RuntimeWorkerTranscriptionEngine(workerOptions);
        var started = await engine.StartAsync();
        if (!started.Succeeded)
        {
            return CandidateResult.Failed(
                candidate.Name,
                provider,
                $"did not start: {started.Error}",
                role,
                workerOptions.IntraOpThreads,
                workerOptions.CpuFallbackThreads,
                workerPriority,
                requestedLanguage,
                failureCategory: started.Error?.Code.ToString() ?? "worker-start");
        }

        // THE ENGINE'S OWN WORKER, captured immediately after StartAsync so any recovery during the cold pass is counted.
        var initialWorkerId = engine.WorkerProcessId ?? 0;
        var currentWorkerId = initialWorkerId;
        var workerRestarts = 0;
        double? maxPeakWorkingSetMb = null;
        void RecordWorkerGenerationAndMemory(out bool restartedOnThisStep)
        {
            restartedOnThisStep = false;
            var latestId = engine.WorkerProcessId ?? 0;
            if (latestId != 0 && currentWorkerId != 0 && latestId != currentWorkerId)
            {
                restartedOnThisStep = true;
                workerRestarts++;
                currentWorkerId = latestId;
            }
            else if (currentWorkerId == 0 && latestId != 0)
            {
                currentWorkerId = latestId;
            }

            if (currentWorkerId != 0)
            {
                var (samplePeakMb, _) = Memory(currentWorkerId, provider);
                if (samplePeakMb is { } sample)
                {
                    maxPeakWorkingSetMb = maxPeakWorkingSetMb is { } existing
                        ? Math.Max(existing, sample)
                        : sample;
                }
            }
        }

        RecordWorkerGenerationAndMemory(out _);

        Transcript first;
        try
        {
            first = await TranscribeAsync(engine, Prefix(clips[0].Samples, 2.5));
        }
        catch (TranscriptionEngineException exception)
        {
            cold.Stop();
            return CandidateResult.Failed(
                candidate.Name,
                provider,
                $"first pass failed: {exception.Error.Code}",
                role,
                workerOptions.IntraOpThreads,
                workerOptions.CpuFallbackThreads,
                workerPriority,
                requestedLanguage,
                failureCategory: exception.Error.Code.ToString());
        }

        cold.Stop();
        RecordWorkerGenerationAndMemory(out var coldPassRestarted);
        // The card's memory with this model loaded, read now rather than after every pass: an estimate of the
        // candidate's share, stated as one, and null if something else released memory meanwhile.
        var cardLoaded = provider == RuntimeProviderKind.Cuda ? Card() : null;

        // A FALLBACK IS NOT THE CANDIDATE. A card run that fell back to the processor measured the processor.
        if (first.UsedFallback)
        {
            return CandidateResult.Failed(
                candidate.Name,
                provider,
                "fell back to the processor",
                role,
                workerOptions.IntraOpThreads,
                workerOptions.CpuFallbackThreads,
                workerPriority,
                first.RecognitionLanguage ?? requestedLanguage,
                first.DetectedLanguage,
                usedFallback: true,
                failureCategory: first.DegradedError?.Code.ToString() ?? "fallback");
        }

        var passes = new List<Pass>();
        var consecutiveWorkerFailures = 0;
        foreach (var window in windowSeconds)
        {
            foreach (var clip in clips.Where(clip => clip.Samples.Length >= (int)(window * sampleRate)))
            {
                var prefix = Prefix(clip.Samples, window);
                for (var run = 0; run < repeats; run++)
                {
                    var timer = Stopwatch.StartNew();
                    Transcript transcript;
                    try
                    {
                        transcript = await TranscribeAsync(engine, prefix);
                    }
                    catch (TranscriptionEngineException exception)
                    {
                        timer.Stop();
                        consecutiveWorkerFailures++;
                        passes.Add(new Pass(
                            clip.Name,
                            window,
                            timer.ElapsedMilliseconds,
                            requestedLanguage,
                            DetectedLanguage: null,
                            UsedFallback: false,
                            FailureCategory: exception.Error.Code.ToString(),
                            Classification: BenchRowClassification.Error));
                        if (workerOptions.MaximumWorkerRestarts == 0 ||
                            consecutiveWorkerFailures > workerOptions.MaximumWorkerRestarts)
                        {
                            return CandidateResult.Failed(
                                candidate.Name,
                                provider,
                                $"pass {clip.Name}@{window:0.0}s failed: {exception.Error.Code}",
                                role,
                                workerOptions.IntraOpThreads,
                                workerOptions.CpuFallbackThreads,
                                workerPriority,
                                requestedLanguage,
                                usedFallback: false,
                                failureCategory: exception.Error.Code.ToString(),
                                passDetails: passes,
                                workerRestarts: workerRestarts);
                        }

                        continue;
                    }

                    timer.Stop();
                    consecutiveWorkerFailures = 0;
                    RecordWorkerGenerationAndMemory(out var passRestarted);
                    var passClassification = PreviewModelBenchEvaluation.ClassifyRow(
                        hasError: false,
                        transcript.UsedFallback,
                        transcript.Text);
                    var passFailure = PreviewModelBenchEvaluation.ClassifyFailureCategory(
                        passClassification,
                        transcript.DegradedError?.Code.ToString() ?? (passRestarted ? "worker-restarted" : null));
                    passes.Add(new Pass(
                        clip.Name,
                        window,
                        timer.ElapsedMilliseconds,
                        transcript.RecognitionLanguage ?? requestedLanguage,
                        transcript.DetectedLanguage,
                        transcript.UsedFallback,
                        passFailure,
                        passClassification,
                        WorkerRestarted: passRestarted));

                    // A FALLBACK MID-ROW IS PERMANENT: every later pass would be the processor under the card's name.
                    if (transcript.UsedFallback)
                    {
                        return CandidateResult.Failed(
                            candidate.Name,
                            provider,
                            "fell back to the processor during the passes",
                            role,
                            workerOptions.IntraOpThreads,
                            workerOptions.CpuFallbackThreads,
                            workerPriority,
                            transcript.RecognitionLanguage ?? requestedLanguage,
                            transcript.DetectedLanguage,
                            usedFallback: true,
                            failureCategory: passFailure,
                            passDetails: passes,
                            workerRestarts: workerRestarts);
                    }
                }
            }
        }

        var accuracy = new List<FixtureResult>();
        foreach (var fixture in fixtures)
        {
            var timer = Stopwatch.StartNew();
            Transcript transcript;
            try
            {
                transcript = await TranscribeAsync(engine, fixture.Samples);
            }
            catch (TranscriptionEngineException exception)
            {
                timer.Stop();
                consecutiveWorkerFailures++;
                var failedFixture = PreviewModelBenchEvaluation.ScoreFixture(
                    fixture.File,
                    fixture.Language,
                    fixture.Reference,
                    transcriptText: null,
                    timer.ElapsedMilliseconds,
                    requestedLanguage,
                    detectedLanguage: null,
                    usedFallback: false,
                    errorCategory: exception.Error.Code.ToString(),
                    hasError: true);
                accuracy.Add(failedFixture);
                if (workerOptions.MaximumWorkerRestarts == 0 ||
                    consecutiveWorkerFailures > workerOptions.MaximumWorkerRestarts)
                {
                    return CandidateResult.Failed(
                        candidate.Name,
                        provider,
                        $"fixture {fixture.File} failed: {exception.Error.Code}",
                        role,
                        workerOptions.IntraOpThreads,
                        workerOptions.CpuFallbackThreads,
                        workerPriority,
                        requestedLanguage,
                        usedFallback: false,
                        failureCategory: exception.Error.Code.ToString(),
                        fixtures: accuracy,
                        passDetails: passes,
                        workerRestarts: workerRestarts);
                }

                continue;
            }

            timer.Stop();
            consecutiveWorkerFailures = 0;
            RecordWorkerGenerationAndMemory(out var fixtureRestarted);
            var scoredFixture = PreviewModelBenchEvaluation.ScoreFixture(
                fixture.File,
                fixture.Language,
                fixture.Reference,
                transcript.Text,
                timer.ElapsedMilliseconds,
                transcript.RecognitionLanguage ?? requestedLanguage,
                transcript.DetectedLanguage,
                transcript.UsedFallback,
                transcript.DegradedError?.Code.ToString() ?? (fixtureRestarted ? "worker-restarted" : null),
                workerRestarted: fixtureRestarted);
            accuracy.Add(scoredFixture);
            if (transcript.UsedFallback)
            {
                return CandidateResult.Failed(
                    candidate.Name,
                    provider,
                    "fell back to the processor during the fixtures",
                    role,
                    workerOptions.IntraOpThreads,
                    workerOptions.CpuFallbackThreads,
                    workerPriority,
                    transcript.RecognitionLanguage ?? requestedLanguage,
                    transcript.DetectedLanguage,
                    usedFallback: true,
                    failureCategory: scoredFixture.FailureCategory,
                    fixtures: accuracy,
                    passDetails: passes,
                    workerRestarts: workerRestarts);
            }
        }

        List<PrefixFixtureResult>? prefixAccuracy = null;
        if (frozenPrefixReferences is { Count: > 0 })
        {
            prefixAccuracy = [];
            var fixturesByFile = fixtures.ToDictionary(f => f.File, StringComparer.OrdinalIgnoreCase);
            foreach (var prefixEntry in frozenPrefixReferences)
            {
                var fixture = fixturesByFile[prefixEntry.File];
                var prefixSamples = Prefix(fixture.Samples, prefixEntry.WindowSeconds);
                var timer = Stopwatch.StartNew();
                Transcript prefixTranscript;
                try
                {
                    prefixTranscript = await TranscribeAsync(engine, prefixSamples);
                }
                catch (TranscriptionEngineException exception)
                {
                    timer.Stop();
                    consecutiveWorkerFailures++;
                    var failedPrefix = PreviewModelBenchEvaluation.ScorePrefixFixture(
                        fixture,
                        prefixEntry,
                        transcriptText: null,
                        timer.ElapsedMilliseconds,
                        requestedLanguage,
                        detectedLanguage: null,
                        usedFallback: false,
                        errorCategory: exception.Error.Code.ToString(),
                        hasError: true);
                    prefixAccuracy.Add(failedPrefix);
                    if (workerOptions.MaximumWorkerRestarts == 0 ||
                        consecutiveWorkerFailures > workerOptions.MaximumWorkerRestarts)
                    {
                        return CandidateResult.Failed(
                            candidate.Name,
                            provider,
                            $"prefix fixture {fixture.File}@{prefixEntry.WindowSeconds:0.0}s failed: {exception.Error.Code}",
                            role,
                            workerOptions.IntraOpThreads,
                            workerOptions.CpuFallbackThreads,
                            workerPriority,
                            requestedLanguage,
                            usedFallback: false,
                            failureCategory: exception.Error.Code.ToString(),
                            fixtures: accuracy,
                            passDetails: passes,
                            prefixFixtures: prefixAccuracy,
                            workerRestarts: workerRestarts);
                    }

                    continue;
                }

                timer.Stop();
                consecutiveWorkerFailures = 0;
                RecordWorkerGenerationAndMemory(out var prefixRestarted);
                var scoredPrefix = PreviewModelBenchEvaluation.ScorePrefixFixture(
                    fixture,
                    prefixEntry,
                    prefixTranscript.Text,
                    timer.ElapsedMilliseconds,
                    prefixTranscript.RecognitionLanguage ?? requestedLanguage,
                    prefixTranscript.DetectedLanguage,
                    prefixTranscript.UsedFallback,
                    prefixTranscript.DegradedError?.Code.ToString() ?? (prefixRestarted ? "worker-restarted" : null),
                    workerRestarted: prefixRestarted);
                prefixAccuracy.Add(scoredPrefix);
                if (prefixTranscript.UsedFallback)
                {
                    return CandidateResult.Failed(
                        candidate.Name,
                        provider,
                        "fell back to the processor during the prefix fixtures",
                        role,
                        workerOptions.IntraOpThreads,
                        workerOptions.CpuFallbackThreads,
                        workerPriority,
                        prefixTranscript.RecognitionLanguage ?? requestedLanguage,
                        prefixTranscript.DetectedLanguage,
                        usedFallback: true,
                        failureCategory: scoredPrefix.FailureCategory,
                        fixtures: accuracy,
                        passDetails: passes,
                        prefixFixtures: prefixAccuracy,
                        workerRestarts: workerRestarts);
                }
            }
        }

        if (workerOptions.MaximumWorkerRestarts == 0 && engine.WorkerProcessId != initialWorkerId)
        {
            return CandidateResult.Failed(
                candidate.Name,
                provider,
                "the worker changed during measurement",
                role,
                workerOptions.IntraOpThreads,
                workerOptions.CpuFallbackThreads,
                workerPriority,
                requestedLanguage,
                failureCategory: "worker-restarted",
                fixtures: accuracy,
                passDetails: passes,
                prefixFixtures: prefixAccuracy,
                workerRestarts: workerRestarts);
        }

        // THE CARD AGAIN, WITH NOTHING OF OURS RUNNING. Busy now, unreadable now, or holding more memory than it did with
        // this model loaded means something else was on the card during the row, and the row is discarded. What this
        // cannot see is work that came and went between the two looks without touching memory: during the row the card's
        // load includes the candidate's own, and Windows reports only the total.
        if (provider == RuntimeProviderKind.Cuda && !allowBusyCard)
        {
            if (CardLoad() is not { } after)
            {
                return CandidateResult.Failed(
                    candidate.Name,
                    provider,
                    "the card could not be read after the row - discarded",
                    role,
                    workerOptions.IntraOpThreads,
                    workerOptions.CpuFallbackThreads,
                    workerPriority,
                    requestedLanguage,
                    failureCategory: "card-unreadable",
                    fixtures: accuracy,
                    passDetails: passes,
                    prefixFixtures: prefixAccuracy);
            }

            if (after.Utilization >= BusyCard)
            {
                return CandidateResult.Failed(
                    candidate.Name,
                    provider,
                    $"another process used the card during the row ({after.Utilization}%) - discarded",
                    role,
                    workerOptions.IntraOpThreads,
                    workerOptions.CpuFallbackThreads,
                    workerPriority,
                    requestedLanguage,
                    failureCategory: "busy-card",
                    fixtures: accuracy,
                    passDetails: passes,
                    prefixFixtures: prefixAccuracy);
            }

            if (cardLoaded is { } loadedNow && after.UsedMegabytes > loadedNow.UsedMegabytes + 256)
            {
                return CandidateResult.Failed(
                    candidate.Name,
                    provider,
                    $"card memory grew by {after.UsedMegabytes - loadedNow.UsedMegabytes} MB during the row - discarded",
                    role,
                    workerOptions.IntraOpThreads,
                    workerOptions.CpuFallbackThreads,
                    workerPriority,
                    requestedLanguage,
                    failureCategory: "card-memory-contended",
                    fixtures: accuracy,
                    passDetails: passes,
                    prefixFixtures: prefixAccuracy);
            }
        }

        RecordWorkerGenerationAndMemory(out _);
        var peakWorkingSetMb = maxPeakWorkingSetMb;
        // WINDOWS DOES NOT REPORT A PROCESS'S VIDEO MEMORY under the display driver model (nvidia-smi says N/A), so the
        // candidate's share is ESTIMATED as the card's total with it loaded less the total before it started. A negative
        // difference means something else released memory meanwhile, and no estimate is given.
        double? videoMemoryMb = cardBefore is { } before && cardLoaded is { } loaded && loaded.UsedMegabytes >= before.UsedMegabytes
            ? loaded.UsedMegabytes - before.UsedMegabytes
            : null;
        var (rowClassification, rowFailureCategory, primaryRows, fallbackRows, emptyRows, errorRows) =
            PreviewModelBenchEvaluation.SummarizeCandidateOutcome(
                accuracy,
                passes,
                prefixAccuracy,
                workerRestarts);
        var steadyPasses = PreviewModelBenchEvaluation.FilterSteadyPrimaryPasses(passes);

        return new CandidateResult(
            candidate.Name,
            provider.ToString(),
            Error: null,
            ColdStartMilliseconds: coldPassRestarted ? null : cold.ElapsedMilliseconds,
            MedianPassMilliseconds: windowSeconds.ToDictionary(
                window => window.ToString("0.0", CultureInfo.InvariantCulture),
                window => Median(steadyPasses.Where(pass => pass.WindowSeconds == window).Select(pass => pass.Milliseconds))),
            PassesOverCadence: PreviewModelBenchEvaluation.CountPassesOverPreviewCadence(
                role,
                steadyPasses,
                cadenceMilliseconds),
            Passes: steadyPasses.Count,
            WordErrorRateByLanguage: accuracy
                .GroupBy(result => result.Language)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => PreviewModelBenchEvaluation.Rate(group)),
            WordErrorRate: PreviewModelBenchEvaluation.Rate(accuracy),
            CardLoadBeforePercent: cardBefore?.Utilization,
            PeakWorkingSetMegabytes: peakWorkingSetMb,
            VideoMemoryMegabytes: videoMemoryMb,
            Fixtures: accuracy,
            PassDetails: passes,
            Role: CandidateResult.FormatRole(role),
            IntraOpThreads: workerOptions.IntraOpThreads,
            CpuFallbackThreads: workerOptions.CpuFallbackThreads,
            WorkerPriority: workerPriority.ToString(),
            RequestedLanguage: requestedLanguage,
            DetectedLanguage: PreviewModelBenchEvaluation.SummarizeDetectedLanguages(accuracy),
            UsedFallback: false,
            FailureCategory: rowFailureCategory,
            Classification: rowClassification,
            PrimaryFixtureRows: primaryRows,
            FallbackFixtureRows: fallbackRows,
            EmptyFixtureRows: emptyRows,
            ErrorFixtureRows: errorRows,
            PrefixWordErrorRate: prefixAccuracy is { Count: > 0 }
                ? PreviewModelBenchEvaluation.PrefixRate(prefixAccuracy)
                : null,
            PrefixWordErrorRateByWindow: prefixAccuracy is { Count: > 0 }
                ? prefixAccuracy
                    .GroupBy(result => result.WindowSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => PreviewModelBenchEvaluation.PrefixRate(group))
                : null,
            PrefixWordErrorRateByLanguage: prefixAccuracy is { Count: > 0 }
                ? prefixAccuracy
                    .GroupBy(result => result.Language)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => PreviewModelBenchEvaluation.PrefixRate(group))
                : null,
            PrefixFixtures: prefixAccuracy,
            PrefixZeroReferenceInsertions: prefixAccuracy is { Count: > 0 }
                ? PreviewModelBenchEvaluation.CountZeroReferenceInsertions(prefixAccuracy)
                : null,
            WorkerRestarts: workerRestarts);
    }
    finally
    {
        if (scratch is not null)
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // The worker may still be letting go of the file; the temp folder is the operating system's to clean.
            }
        }
    }
}

/// The upper middle observation of an even count, so a reported median is always a pass that happened.
static long? Median(IEnumerable<long> values)
{
    var sorted = values.Order().ToList();
    return sorted.Count == 0 ? null : sorted[sorted.Count / 2];
}

/// The worker's peak working set, by the id the engine reported.
static (double? PeakWorkingSet, double? VideoMemory) Memory(int workerId, RuntimeProviderKind provider)
{
    if (workerId == 0)
    {
        return (null, null);
    }

    double? peak = null;
    try
    {
        using var worker = Process.GetProcessById(workerId);
        worker.Refresh();
        peak = Math.Round(worker.PeakWorkingSet64 / 1_048_576.0);
    }
    catch (ArgumentException)
    {
    }

    if (provider != RuntimeProviderKind.Cuda)
    {
        return (peak, null);
    }

    try
    {
        using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        var text = smi!.StandardOutput.ReadToEnd();
        smi.WaitForExit(5_000);
        var line = text.Split('\n').Select(row => row.Split(',')).FirstOrDefault(parts =>
            parts.Length == 2 && int.TryParse(parts[0].Trim(), out var pid) && pid == workerId);
        return (peak, line is not null && double.TryParse(line[1].Trim(), CultureInfo.InvariantCulture, out var mb) ? mb : null);
    }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return (peak, null);
    }
}

/// THE CARD'S LOAD IS SAMPLED, NOT READ ONCE. A desktop with ordinary windows open moves the card between about 20%
/// and 40% from one second to the next, so a single reading refused a free card; five readings over two and a half
/// seconds, the middle one, say what the card is doing. Another job saturating it - the case this exists for, a
/// process at 100% - is far above the line. The load before each row is printed, so a reader can judge it.
static (int Utilization, double UsedMegabytes)? CardLoad()
{
    var readings = new List<(int Utilization, double UsedMegabytes)>();
    for (var i = 0; i < 5; i++)
    {
        if (Card() is { } reading)
        {
            readings.Add(reading);
        }

        Thread.Sleep(500);
    }

    // ALL FIVE OR NOTHING: a failed reading is not a low one, and four failures around one quiet sample are not
    // "the middle of five".
    return readings.Count < 5
        ? null
        : (readings.Select(reading => reading.Utilization).Order().ElementAt(readings.Count / 2), readings.Max(reading => reading.UsedMegabytes));
}

/// The card's utilization and the memory held on it, or null without an NVIDIA card.
static (int Utilization, double UsedMegabytes)? Card()
{
    try
    {
        using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        var parts = smi!.StandardOutput.ReadToEnd().Split((char)10)[0].Split(',');
        smi.WaitForExit(5_000);
        return parts.Length == 2 &&
            int.TryParse(parts[0].Trim(), out var utilization) &&
            double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var used)
                ? (utilization, used)
                : null;
    }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return null;
    }
}

static float[] Prefix(float[] samples, double seconds)
{
    var wanted = (int)(seconds * sampleRate);
    return wanted >= samples.Length ? samples : samples[..wanted];
}

static Task<Transcript> TranscribeAsync(RuntimeWorkerTranscriptionEngine engine, float[] samples) =>
    engine.TranscribeAsync(new CapturedAudio(DictationSessionId.Create(), samples, sampleRate, Channels: 1));

string? ArgumentValue(string name)
{
    var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string ShortName(string path)
{
    var name = Path.GetFileNameWithoutExtension(path);
    return name.Length <= 8 ? name : name[..8];
}

/// The public MINDS-14 fixtures and the reference the Whisper UAT grades against (its evaluation transcription where
/// the manifest has one - the source reference stops early on some rows).
static List<Fixture> ReadFixtures(string directory)
{
    var manifest = Path.Combine(directory, "manifest.json");
    if (!File.Exists(manifest))
    {
        return [];
    }

    using var document = JsonDocument.Parse(File.ReadAllText(manifest));
    var fixtures = new List<Fixture>();
    foreach (var row in document.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        var file = row.GetProperty("file").GetString()!;
        var language = row.GetProperty("config").GetString()!.Split('-')[0];
        var reference = row.TryGetProperty("evaluationTranscription", out var evaluation) && evaluation.ValueKind == JsonValueKind.String
            ? evaluation.GetString()!
            : row.GetProperty("transcription").GetString()!;
        var path = Path.Combine(directory, file);
        // A MISSING FIXTURE IS A DIFFERENT CORPUS: skipped, the pooled error would be over whatever survived.
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Missing fixture: {file}");
        }

        fixtures.Add(new Fixture(file, language, reference, ReadWaveFile(path)));
    }

    return fixtures;
}

static float[] ReadWaveFile(string path)
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

            return sourceRate == sampleRate ? samples : Resample(samples, sourceRate, sampleRate);
        }

        position += chunkSize + (chunkSize % 2);
    }

    throw new InvalidDataException($"{Path.GetFileName(path)} has no supported audio data.");
}

static float DecodeMuLaw(byte value)
{
    var decoded = (byte)~value;
    var sign = (decoded & 0x80) == 0 ? 1 : -1;
    var exponent = (decoded >> 4) & 0x07;
    var mantissa = decoded & 0x0F;
    var magnitude = (((mantissa << 3) + 0x84) << exponent) - 0x84;
    return sign * magnitude / 32768f;
}

static float[] Resample(float[] source, int sourceRate, int destinationRate)
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

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? start;
}
