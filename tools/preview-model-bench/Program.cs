// THE LIVE PREVIEW MODEL BENCH (#127). Live preview on the processor costs about two seconds a pass whatever the
// window, so the 2.5-second cadence has no headroom; the founder's direction is to choose the preview model by
// measuring candidates across hardware rather than by hand, and to expect a different model per tier. This measures,
// for each candidate on each provider, the four things that choice turns on:
//
//   - PER-PASS COST at the window lengths the preview loop actually sees, on real archived dictations;
//   - ACCURACY on the public multilingual fixtures, as word error rate per language;
//   - COLD START: starting the worker and the first pass, which a dictation pays once;
//   - MEMORY: the worker's peak working set, and its video memory on the card.
//
// IT RUNS THE PRODUCT'S OWN PATH. Each candidate goes through RuntimeWorkerTranscriptionEngine and the out-of-process
// runtime worker, with the thread count live preview picks, exactly as the preview does. A Whisper candidate that is
// not the shipped preview model is given to the worker under the preview model's file name, in a folder of its own,
// so nothing in the product has to know the bench exists. It decides nothing: it prints what each candidate cost, and
// the choice stays the founder's.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Runtime;

const int sampleRate = 16_000;
const double cadenceMilliseconds = 2_500;
// A card at or above this load, by the middle of five readings, is doing someone else's work.
const int BusyCard = 50;
double[] windowSeconds = [0.5, 2.5, 5, 10, 20];

var repositoryRoot = ArgumentValue("--repo") ?? FindRepositoryRoot(AppContext.BaseDirectory);
var audioDirectory = ArgumentValue("--audio") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Envious Labs",
    "EnviousWispr",
    "audio-archive");
var extraModels = ArgumentValue("--extra-models");
var fixturesDirectory = ArgumentValue("--fixtures") ?? Path.Combine(repositoryRoot, "tools", "whisper-uat", "fixtures");
var repeats = int.TryParse(ArgumentValue("--repeats"), out var parsedRepeats) ? Math.Max(1, parsedRepeats) : 3;
var providers = (ArgumentValue("--providers") ?? "cpu,cuda")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(name => name.Equals("cuda", StringComparison.OrdinalIgnoreCase) ? RuntimeProviderKind.Cuda : RuntimeProviderKind.Cpu)
    .Distinct()
    .ToArray();
var only = ArgumentValue("--only");
var output = ArgumentValue("--out") ?? Path.Combine(Path.GetTempPath(), "preview-model-bench.json");

// THE SAME THREAD COUNT LIVE PREVIEW USES, from the same hardware probe and the same rule (ConfigureLivePreview:
// physical cores, else half the logical processors, clamped 2 to 8). Halving the logical count alone gave a
// four-core machine without hyper-threading two threads where the preview uses four.
var hardware = await new WindowsHardwareDiscovery(CudaRuntimeDirectory.ForTooling()).ProbeAsync();
var threads = Math.Clamp(
    hardware.PhysicalCoreCount > 0 ? hardware.PhysicalCoreCount : Math.Max(1, hardware.LogicalProcessorCount / 2),
    2,
    8);

var candidates = new List<Candidate>
{
    WhisperExtra("whisper-tiny q5_1", "ggml-tiny-q5_1.bin"),
    WhisperExtra("whisper-base q5_1", "ggml-base-q5_1.bin"),
    new("whisper-small q5_1 (shipped preview)", FinalAsrEngine.Whisper, WhisperModelPack.PreviewSmall,
        Path.Combine(repositoryRoot, "models", "whisper-small"), ModelFile: null),
    new("whisper-large-v3-turbo q5_0", FinalAsrEngine.Whisper, WhisperModelPack.Quantized,
        Path.Combine(repositoryRoot, "models", "whisper-large-v3-turbo"), ModelFile: null),
    new("parakeet-tdt-0.6b-v3 int8", FinalAsrEngine.Parakeet, WhisperModelPack.Quantized,
        Path.Combine(repositoryRoot, "models", "parakeet-tdt-0.6b-v3"), ModelFile: null),
    // THE PRODUCT REFUSES THE QUANTIZED PARAKEET PACK ON THE CARD before it looks for any library, so a card
    // candidate is the full-precision pack, named as such (ParakeetTranscriptionEngine, validation before CUDA).
    new("parakeet-tdt-0.6b-v3 full precision", FinalAsrEngine.Parakeet, WhisperModelPack.Quantized,
        Path.Combine(repositoryRoot, "models", "parakeet-tdt-0.6b-v3"), ModelFile: null, ParakeetModelPack.FullPrecision),
}
.Where(candidate => candidate is not null)
.Select(candidate => candidate!)
.Where(candidate => only is null || candidate.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
.ToList();

var clips = Directory.Exists(audioDirectory)
    ? Directory.EnumerateFiles(audioDirectory, "*.wav")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => new Clip(ShortName(path), ReadWaveFile(path)))
        .Where(clip => clip.Samples.Length >= (int)(windowSeconds[0] * sampleRate))
        .ToList()
    : [];
var fixtures = ReadFixtures(fixturesDirectory);
if (clips.Count == 0 || fixtures.Count == 0)
{
    Console.Error.WriteLine($"Need archived dictations in {audioDirectory} ({clips.Count}) and fixtures in {fixturesDirectory} ({fixtures.Count}).");
    return 2;
}

Console.WriteLine(
    $"threads={threads} clips={clips.Count} (longest {clips.Max(clip => clip.Samples.Length) / (double)sampleRate:0.0} s) " +
    $"fixtures={fixtures.Count} repeats={repeats} logical processors={Environment.ProcessorCount}");

var worker = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
var results = new List<CandidateResult>();
foreach (var candidate in candidates)
{
    foreach (var provider in providers)
    {
        Console.Error.WriteLine($"measuring {candidate.Name} on {provider}...");
        results.Add(await MeasureAsync(candidate, provider));
    }
}

PrintTable(results);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine();
Console.WriteLine($"Details: {output}");
return 0;

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
    var modelDirectory = candidate.ModelDirectory;
    if (candidate.ModelFile is { } file)
    {
        scratch = Path.Combine(Path.GetTempPath(), "preview-model-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        File.Copy(file, Path.Combine(scratch, WhisperModelFileNames.PreviewSmall));
        modelDirectory = scratch;
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
            return CandidateResult.Failed(candidate.Name, provider, "the card could not be read (nvidia-smi) - not measured");
        }

        if (card.Utilization >= BusyCard)
        {
            return CandidateResult.Failed(candidate.Name, provider, $"card busy with other work ({card.Utilization}% used, {card.UsedMegabytes} MB held) - not measured");
        }
    }

    var cold = Stopwatch.StartNew();
    try
    {
        await using var engine = new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
            worker,
            modelDirectory!,
            provider,
            candidate.ParakeetPack,
            IntraOpThreads: threads,
            CpuFallbackThreads: threads,
            StartupTimeout: TimeSpan.FromSeconds(60),
            TranscriptionTimeout: TimeSpan.FromSeconds(120),
            Engine: candidate.Engine,
            WhisperPack: candidate.Pack,
            Language: "auto",
            CudaRuntimeDirectory: CudaRuntimeDirectory.ForTooling(),
            // AS THE PREVIEW ADAPTER RUNS IT (RuntimeWorkerLivePreviewEngine): below-normal priority, and no restart -
            // a restart mid-row would put a cold start inside a pass measured as warm.
            MaximumWorkerRestarts: 0,
            WorkerPriority: ProcessPriorityClass.BelowNormal));
        var started = await engine.StartAsync();
        if (!started.Succeeded)
        {
            return CandidateResult.Failed(candidate.Name, provider, $"did not start: {started.Error}");
        }

        var first = await TranscribeAsync(engine, Prefix(clips[0].Samples, 2.5));
        cold.Stop();
        // THE ENGINE'S OWN WORKER, by the id it reports - never a process found by name, which could be another's.
        var workerId = engine.WorkerProcessId ?? 0;
        // The card's memory with this model loaded, read now rather than after every pass: an estimate of the
        // candidate's share, stated as one, and null if something else released memory meanwhile.
        var cardLoaded = provider == RuntimeProviderKind.Cuda ? Card() : null;

        // A FALLBACK IS NOT THE CANDIDATE. A card run that fell back to the processor measured the processor.
        if (first.UsedFallback)
        {
            return CandidateResult.Failed(candidate.Name, provider, "fell back to the processor");
        }

        var passes = new List<Pass>();
        foreach (var window in windowSeconds)
        {
            foreach (var clip in clips.Where(clip => clip.Samples.Length >= (int)(window * sampleRate)))
            {
                var prefix = Prefix(clip.Samples, window);
                for (var run = 0; run < repeats; run++)
                {
                    var timer = Stopwatch.StartNew();
                    var transcript = await TranscribeAsync(engine, prefix);
                    timer.Stop();
                    // A FALLBACK MID-ROW IS PERMANENT: every later pass would be the processor under the card's name.
                    if (transcript.UsedFallback)
                    {
                        return CandidateResult.Failed(candidate.Name, provider, "fell back to the processor during the passes");
                    }

                    passes.Add(new Pass(clip.Name, window, timer.ElapsedMilliseconds));
                }
            }
        }

        var accuracy = new List<FixtureResult>();
        foreach (var fixture in fixtures)
        {
            var timer = Stopwatch.StartNew();
            var transcript = await TranscribeAsync(engine, fixture.Samples);
            timer.Stop();
            if (transcript.UsedFallback)
            {
                return CandidateResult.Failed(candidate.Name, provider, "fell back to the processor during the fixtures");
            }

            var expected = Words(fixture.Reference);
            var actual = Words(transcript.Text ?? string.Empty);
            accuracy.Add(new FixtureResult(
                fixture.File,
                fixture.Language,
                expected.Length,
                LevenshteinDistance(expected, actual),
                timer.ElapsedMilliseconds,
                transcript.Text?.Trim() ?? string.Empty));
        }

        if (engine.WorkerProcessId != workerId)
        {
            return CandidateResult.Failed(candidate.Name, provider, "the worker changed during measurement");
        }

        // THE CARD AGAIN, WITH NOTHING OF OURS RUNNING: busy now means something else used it during the row.
        if (provider == RuntimeProviderKind.Cuda && !allowBusyCard && CardLoad() is { Utilization: >= BusyCard } after)
        {
            return CandidateResult.Failed(candidate.Name, provider, $"another process used the card during the row ({after.Utilization}%) - discarded");
        }

        var (peakWorkingSetMb, _) = Memory(workerId, provider);
        // WINDOWS DOES NOT REPORT A PROCESS'S VIDEO MEMORY under the display driver model (nvidia-smi says N/A), so the
        // candidate's share is ESTIMATED as the card's total with it loaded less the total before it started. A negative
        // difference means something else released memory meanwhile, and no estimate is given.
        double? videoMemoryMb = cardBefore is { } before && cardLoaded is { } loaded && loaded.UsedMegabytes >= before.UsedMegabytes
            ? loaded.UsedMegabytes - before.UsedMegabytes
            : null;
        return new CandidateResult(
            candidate.Name,
            provider.ToString(),
            Error: null,
            ColdStartMilliseconds: cold.ElapsedMilliseconds,
            MedianPassMilliseconds: windowSeconds.ToDictionary(
                window => window.ToString("0.0", CultureInfo.InvariantCulture),
                window => Median(passes.Where(pass => pass.WindowSeconds == window).Select(pass => pass.Milliseconds))),
            PassesOverCadence: passes.Count(pass => pass.Milliseconds > cadenceMilliseconds),
            Passes: passes.Count,
            WordErrorRateByLanguage: accuracy
                .GroupBy(result => result.Language)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Rate(group)),
            WordErrorRate: Rate(accuracy),
            CardLoadBeforePercent: cardBefore?.Utilization,
            PeakWorkingSetMegabytes: peakWorkingSetMb,
            VideoMemoryMegabytes: videoMemoryMb,
            Fixtures: accuracy,
            PassDetails: passes);
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

static double? Rate(IEnumerable<FixtureResult> results)
{
    var list = results.ToList();
    var words = list.Sum(result => result.ReferenceWords);
    return words == 0 ? null : Math.Round(list.Sum(result => result.EditDistance) / (double)words, 3);
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

void PrintTable(List<CandidateResult> rows)
{
    Console.WriteLine();
    var header = "| model | on | cold start | " +
        string.Join(" | ", windowSeconds.Select(window => $"pass @ {window.ToString("0.0", CultureInfo.InvariantCulture)} s")) +
        " | over 2.5 s | WER all | " + string.Join(" | ", rows.SelectMany(row => row.WordErrorRateByLanguage?.Keys ?? Enumerable.Empty<string>()).Distinct().Order().Select(language => $"WER {language}")) +
        " | peak RAM | VRAM | card load before |";
    Console.WriteLine(header);
    Console.WriteLine("|" + string.Concat(Enumerable.Repeat("---|", header.Count(character => character == '|') - 1)));
    var languages = rows.SelectMany(row => row.WordErrorRateByLanguage?.Keys ?? Enumerable.Empty<string>()).Distinct().Order().ToList();
    foreach (var row in rows)
    {
        if (row.Error is { } error)
        {
            Console.WriteLine($"| {row.Model} | {row.Provider} | {error} |");
            continue;
        }

        Console.WriteLine(
            $"| {row.Model} | {row.Provider} | {row.ColdStartMilliseconds} ms | " +
            string.Join(" | ", windowSeconds.Select(window => row.MedianPassMilliseconds![window.ToString("0.0", CultureInfo.InvariantCulture)] is { } median ? $"{median} ms" : "-")) +
            $" | {row.PassesOverCadence} of {row.Passes} | {Percent(row.WordErrorRate)} | " +
            string.Join(" | ", languages.Select(language => Percent(row.WordErrorRateByLanguage!.GetValueOrDefault(language)))) +
            $" | {row.PeakWorkingSetMegabytes} MB | {(row.VideoMemoryMegabytes is { } video ? $"{video} MB" : "-")} | {(row.CardLoadBeforePercent is { } load ? $"{load}%" : "-")} |");
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

    return readings.Count == 0
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

static string Percent(double? rate) => rate is { } value ? $"{value * 100:0.0}%" : "-";

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

static string[] Words(string text) =>
    text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()))
        .Where(word => word.Length > 0)
        .ToArray();

static int LevenshteinDistance(IReadOnlyList<string> left, IReadOnlyList<string> right)
{
    var previous = Enumerable.Range(0, right.Count + 1).ToArray();
    var current = new int[right.Count + 1];
    for (var leftIndex = 1; leftIndex <= left.Count; leftIndex++)
    {
        current[0] = leftIndex;
        for (var rightIndex = 1; rightIndex <= right.Count; rightIndex++)
        {
            var substitution = string.Equals(left[leftIndex - 1], right[rightIndex - 1], StringComparison.Ordinal) ? 0 : 1;
            current[rightIndex] = Math.Min(Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1), previous[rightIndex - 1] + substitution);
        }

        (previous, current) = (current, previous);
    }

    return previous[right.Count];
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
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? start;
}

internal sealed record Candidate(
    string Name,
    FinalAsrEngine Engine,
    WhisperModelPack Pack,
    string? ModelDirectory,
    string? ModelFile,
    ParakeetModelPack ParakeetPack = ParakeetModelPack.Quantized);

internal sealed record Clip(string Name, float[] Samples);

internal sealed record Fixture(string File, string Language, string Reference, float[] Samples);

internal sealed record Pass(string Clip, double WindowSeconds, long Milliseconds);

internal sealed record FixtureResult(string File, string Language, int ReferenceWords, int EditDistance, long Milliseconds, string Transcript);

internal sealed record CandidateResult(
    string Model,
    string Provider,
    string? Error,
    long? ColdStartMilliseconds,
    Dictionary<string, long?>? MedianPassMilliseconds,
    int PassesOverCadence,
    int Passes,
    Dictionary<string, double?>? WordErrorRateByLanguage,
    double? WordErrorRate,
    double? PeakWorkingSetMegabytes,
    double? VideoMemoryMegabytes,
    int? CardLoadBeforePercent,
    List<FixtureResult>? Fixtures,
    List<Pass>? PassDetails)
{
    public static CandidateResult Failed(string model, RuntimeProviderKind provider, string error) =>
        new(model, provider.ToString(), error, null, null, 0, 0, null, null, null, null, null, null, null);
}
