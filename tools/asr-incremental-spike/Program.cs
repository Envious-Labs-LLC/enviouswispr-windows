// WHAT THIS MEASURES, AND WHY IT IS SEPARATE FROM THE PRODUCT. Live preview re-transcribes the whole
// window on every pass, so its cost is claimed to grow with how long somebody has been speaking - the
// second of the three faults on #99. That claim is arithmetic about the decoder, not about our loop,
// and #111 has already shown what happens when a decoder change is made from reasoning alone. This
// runs the SAME model pack and thread count live preview runs, over real archived dictations, at the
// window lengths the loop actually sees, and prints what it cost. It answers one question: does the
// cost grow enough, inside the 20-second cap, to matter.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Runtime;

const int sampleRate = 16_000;

var repositoryRoot = ArgumentValue("--repo") ?? FindRepositoryRoot(AppContext.BaseDirectory);
var audioDirectory = ArgumentValue("--audio") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Envious Labs",
    "EnviousWispr",
    "audio-archive");
var provider = args.Contains("cpu", StringComparer.OrdinalIgnoreCase)
    ? RuntimeProviderKind.Cpu
    : RuntimeProviderKind.Cuda;
var repeats = int.TryParse(ArgumentValue("--repeats"), out var parsed) ? Math.Max(1, parsed) : 3;
// NO PADDING, NO LOOPING, NO SYNTHESIS: A WINDOW IS ONLY MEASURED WHERE REAL AUDIO REACHES IT.
// Looping a short take up to the window length was tried and thrown away. It produced a flat curve -
// 83 ms at 20 seconds, the same as at 7.5 - and the transcripts said why: the decoder collapsed the
// repetition and emitted the SAME 56-character sentence at both lengths. Decoder cost tracks tokens
// emitted, so a tiled window measures a 20-second encode with a 7.5-second decode and understates
// exactly the growth this exists to find. Give it a directory of genuinely long recordings instead.

// THE LENGTHS THE LOOP ACTUALLY SEES. The loop starts a pass as soon as it holds 8,000 samples, so
// half a second is a real window and not a synthetic one; the cadence is a 2.5-second floor and the
// window is capped at 20, so a long dictation walks these and then stays at 20 for the rest of it.
// THE TWO SHORTEST ROWS ARE THE CONTROL. If cost tracked how long somebody has been speaking, half a
// second would be nearly free. Anything it costs there is a floor being paid on every pass whatever
// the window, which is what tells a cost that grows apart from a cost that is simply large.
double[] windowSeconds = [0.5, 1, 2.5, 5, 7.5, 10, 15, 20];

var modelDirectory = Path.Combine(repositoryRoot, "models", "whisper-small");
if (!Directory.Exists(modelDirectory))
{
    Console.Error.WriteLine($"The preview model pack is not at {modelDirectory}.");
    return 2;
}

if (!Directory.Exists(audioDirectory))
{
    Console.Error.WriteLine($"There are no archived dictations at {audioDirectory}.");
    return 2;
}

var clips = Directory.EnumerateFiles(audioDirectory, "*.wav")
    .OrderBy(path => path, StringComparer.Ordinal)
    .Select(path => new Clip(ShortName(path), ReadWaveFile(path)))
    .Where(clip => clip.Samples.Length >= (int)(windowSeconds[0] * sampleRate))
    .ToList();
if (clips.Count == 0)
{
    Console.Error.WriteLine($"No archived dictation in {audioDirectory} is long enough to measure.");
    return 2;
}

var worker = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
// THE SAME THREAD COUNT LIVE PREVIEW USES, worked out the same way, because that number is half of
// what the measurement is about. Ref: ConfigureLivePreview.
var threads = Math.Clamp(Math.Max(1, Environment.ProcessorCount / 2), 2, 8);
await using var engine = new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
    worker,
    modelDirectory,
    provider,
    ParakeetModelPack.Quantized,
    IntraOpThreads: threads,
    CpuFallbackThreads: threads,
    StartupTimeout: TimeSpan.FromSeconds(30),
    TranscriptionTimeout: TimeSpan.FromSeconds(120),
    Engine: FinalAsrEngine.Whisper,
    WhisperPack: WhisperModelPack.PreviewSmall,
    Language: "auto",
    CudaRuntimeDirectory: Environment.GetEnvironmentVariable("ENVIOUSWISPR_CUDA_RUNTIME_DIR")));

var started = await engine.StartAsync();
if (!started.Succeeded)
{
    Console.Error.WriteLine($"The preview engine did not start on {provider}: {started.Error}.");
    return 3;
}

// DISCARDED ON PURPOSE. The first call through this engine pays for warm-up that no later pass pays,
// and #91 was measured wrong once by keeping it.
_ = await TranscribeAsync(engine, Prefix(clips[0].Samples, 2.5));

var measurements = new List<Measurement>();
foreach (var window in windowSeconds)
{
    foreach (var clip in clips)
    {
        var prefix = Prefix(clip.Samples, window);
        if (prefix.Length < (int)(window * sampleRate))
        {
            continue;
        }

        for (var run = 0; run < repeats; run++)
        {
            var timer = Stopwatch.StartNew();
            var transcript = await TranscribeAsync(engine, prefix);
            timer.Stop();
            measurements.Add(new Measurement(
                clip.Name,
                window,
                run,
                timer.ElapsedMilliseconds,
                transcript.Text?.Trim() ?? string.Empty));
        }
    }
}

Console.WriteLine(
    $"provider={provider} threads={threads} clips={clips.Count} " +
    $"repeats={repeats}");
Console.WriteLine();
Console.WriteLine("window  clips  fastest  median  slowest   over the 2.5s cadence");
foreach (var window in windowSeconds)
{
    var forWindow = measurements.Where(measurement => measurement.WindowSeconds == window).ToList();
    if (forWindow.Count == 0)
    {
        continue;
    }

    var costs = forWindow.Select(measurement => measurement.Milliseconds).Order().ToList();
    Console.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "{0,5:0.0}s  {1,5}  {2,7}  {3,6}  {4,7}   {5} of {6}",
        window,
        forWindow.Select(measurement => measurement.Clip).Distinct().Count(),
        costs[0],
        costs[costs.Count / 2],
        costs[^1],
        costs.Count(cost => cost > 2_500),
        costs.Count));
}

Console.WriteLine();
Console.WriteLine(JsonSerializer.Serialize(measurements));
return 0;

// A NAME SHORT ENOUGH TO READ IN A TABLE, WITHOUT ASSUMING ONE. The archived dictations are named by
// a 32-character id and the spike clips are named clip10; truncating to a fixed width threw on the
// second directory this was ever pointed at.
static string ShortName(string path)
{
    var name = Path.GetFileNameWithoutExtension(path);
    return name.Length <= 8 ? name : name[..8];
}

static float[] Prefix(float[] samples, double seconds)
{
    var wanted = (int)(seconds * sampleRate);
    return wanted >= samples.Length ? samples : samples[..wanted];
}

static Task<Transcript> TranscribeAsync(RuntimeWorkerTranscriptionEngine engine, float[] samples) =>
    engine.TranscribeAsync(new CapturedAudio(
        DictationSessionId.Create(),
        samples,
        sampleRate,
        Channels: 1));

string? ArgumentValue(string name)
{
    var index = Array.FindIndex(
        args,
        value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null &&
        !Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
        !File.Exists(Path.Combine(directory.FullName, ".git")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? start;
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
            var channels = BitConverter.ToInt16(format, 2);
            var sourceRate = BitConverter.ToInt32(format, 4);
            var bitsPerSample = BitConverter.ToInt16(format, 14);
            var bytesPerSample = bitsPerSample / 8;
            var sampleCount = chunkSize / bytesPerSample / channels;
            var samples = new float[sampleCount];
            for (var index = 0; index < sampleCount; index++)
            {
                var offset = position + (index * bytesPerSample * channels);
                samples[index] = bitsPerSample switch
                {
                    16 => BitConverter.ToInt16(bytes, offset) / 32768f,
                    32 => BitConverter.ToSingle(bytes, offset),
                    _ => throw new InvalidDataException($"{path} uses an unsupported sample width."),
                };
            }

            if (sourceRate != sampleRate)
            {
                throw new InvalidDataException($"{path} is {sourceRate} Hz, not {sampleRate} Hz.");
            }

            return samples;
        }

        position += chunkSize + (chunkSize % 2);
    }

    throw new InvalidDataException($"{path} has no supported audio data.");
}

internal sealed record Clip(string Name, float[] Samples);

internal sealed record Measurement(
    string Clip,
    double WindowSeconds,
    int Run,
    long Milliseconds,
    string Text);
