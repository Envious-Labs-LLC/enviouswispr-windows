using System.Diagnostics;
using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;

const int WindowSeconds = 8;
const int OverlapSeconds = 1;
const int SampleRate = ParakeetTranscriptionEngine.RequiredSampleRate;
const int WindowSamples = WindowSeconds * SampleRate;
const int StrideSamples = (WindowSeconds - OverlapSeconds) * SampleRate;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var modelDirectory = Path.Combine(repositoryRoot, "models", "parakeet-tdt-0.6b-v3");
var audioDirectory = Path.Combine(repositoryRoot, "spikes", "s1", "audio");
var clips = new Dictionary<string, float[]>(StringComparer.Ordinal)
{
    ["smoke"] = ReadWaveFile(Path.Combine(audioDirectory, "clip20.wav")),
    ["full"] = ReadWaveFile(Path.Combine(audioDirectory, "clip94.wav")),
};

using var engine = new ParakeetTranscriptionEngine(new ParakeetEngineOptions(
    modelDirectory,
    RuntimeProviderKind.Cpu,
    ParakeetModelPack.Quantized,
    IntraOpThreads: 8));

_ = await TranscribeAsync(engine, clips["smoke"].AsMemory(0, Math.Min(WindowSamples, clips["smoke"].Length)));

var results = new List<SpikeResult>();
foreach (var (name, samples) in clips)
{
    results.Add(await RunSpikeAsync(engine, name, samples));
}

var passed = results.All(result =>
    result.FinalizationMilliseconds < 1_000 &&
    result.ReferenceWordErrorRate <= 0.10 &&
    result.HasText);
Console.WriteLine(JsonSerializer.Serialize(new
{
    windowSeconds = WindowSeconds,
    overlapSeconds = OverlapSeconds,
    results,
    passed,
}));
return passed ? 0 : 7;

static async Task<SpikeResult> RunSpikeAsync(
    ParakeetTranscriptionEngine engine,
    string name,
    float[] samples)
{
    var batchTimer = Stopwatch.StartNew();
    var batch = await TranscribeAsync(engine, samples);
    batchTimer.Stop();

    var chunks = CreateChunks(samples);
    var chunkTexts = new List<string>(chunks.Count);
    long preReleaseMilliseconds = 0;
    long finalizationMilliseconds = 0;
    for (var index = 0; index < chunks.Count; index++)
    {
        var timer = Stopwatch.StartNew();
        var transcript = await TranscribeAsync(engine, chunks[index]);
        timer.Stop();
        chunkTexts.Add(transcript.Text);
        if (index == chunks.Count - 1)
        {
            finalizationMilliseconds = timer.ElapsedMilliseconds;
        }
        else
        {
            preReleaseMilliseconds += timer.ElapsedMilliseconds;
        }
    }

    var incrementalText = chunkTexts.Aggregate(string.Empty, MergeOverlappingChunks);
    var referenceWords = Words(batch.Text);
    var incrementalWords = Words(incrementalText);
    var editDistance = LevenshteinDistance(referenceWords, incrementalWords);
    var referenceWordErrorRate = referenceWords.Length == 0
        ? incrementalWords.Length == 0 ? 0 : 1
        : editDistance / (double)referenceWords.Length;

    return new SpikeResult(
        name,
        AudioMilliseconds: samples.Length * 1_000L / SampleRate,
        chunks.Count,
        BatchMilliseconds: batchTimer.ElapsedMilliseconds,
        PreReleaseWorkMilliseconds: preReleaseMilliseconds,
        FinalizationMilliseconds: finalizationMilliseconds,
        BatchWordCount: referenceWords.Length,
        IncrementalWordCount: incrementalWords.Length,
        ReferenceWordErrorRate: referenceWordErrorRate,
        ExactMatch: string.Equals(batch.Text, incrementalText, StringComparison.Ordinal),
        HasText: !string.IsNullOrWhiteSpace(incrementalText));
}

static List<ReadOnlyMemory<float>> CreateChunks(float[] samples)
{
    var chunks = new List<ReadOnlyMemory<float>>();
    var start = 0;
    while (start < samples.Length)
    {
        var count = Math.Min(WindowSamples, samples.Length - start);
        chunks.Add(samples.AsMemory(start, count));
        if (start + count == samples.Length)
        {
            break;
        }

        start += StrideSamples;
    }

    return chunks;
}

static string MergeOverlappingChunks(string transcript, string nextChunk)
{
    transcript = transcript.Trim();
    nextChunk = nextChunk.Trim();
    if (transcript.Length == 0)
    {
        return nextChunk;
    }

    if (nextChunk.Length == 0)
    {
        return transcript;
    }

    var transcriptWords = transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    var nextWords = nextChunk.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    var transcriptNormalized = transcriptWords.Select(NormalizeWord).ToArray();
    var nextNormalized = nextWords.Select(NormalizeWord).ToArray();
    var maximumOverlap = Math.Min(transcriptWords.Length, nextWords.Length);

    for (var overlap = maximumOverlap; overlap >= 1; overlap--)
    {
        for (var trailingWords = 0; trailingWords <= Math.Min(6, transcriptWords.Length - overlap); trailingWords++)
        {
            var transcriptEnd = transcriptWords.Length - trailingWords;
            var transcriptStart = transcriptEnd - overlap;
            var matches = true;
            for (var index = 0; index < overlap; index++)
            {
                if (transcriptNormalized[transcriptStart + index].Length == 0 ||
                    !string.Equals(
                        transcriptNormalized[transcriptStart + index],
                        nextNormalized[index],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return string.Join(' ', transcriptWords[..transcriptEnd]
                    .Concat(nextWords[overlap..]));
            }
        }
    }

    return $"{transcript} {nextChunk}";
}

static string NormalizeWord(string word) =>
    new(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

static string[] Words(string text) =>
    text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeWord)
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
            var substitution = string.Equals(
                left[leftIndex - 1],
                right[rightIndex - 1],
                StringComparison.Ordinal) ? 0 : 1;
            current[rightIndex] = Math.Min(
                Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                previous[rightIndex - 1] + substitution);
        }

        (previous, current) = (current, previous);
    }

    return previous[right.Count];
}

static Task<Transcript> TranscribeAsync(
    ParakeetTranscriptionEngine engine,
    ReadOnlyMemory<float> samples) =>
    engine.TranscribeAsync(new CapturedAudio(
        DictationSessionId.Create(),
        samples,
        SampleRate,
        Channels: 1));

static float[] ReadWaveFile(string path)
{
    var bytes = File.ReadAllBytes(path);
    if (!bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
        !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
    {
        throw new InvalidDataException("The ASR fixture is not a RIFF WAVE file.");
    }

    byte[]? format = null;
    var position = 12;
    while (position + 8 <= bytes.Length)
    {
        var chunkId = bytes.AsSpan(position, 4);
        var chunkSize = BitConverter.ToInt32(bytes, position + 4);
        position += 8;
        if (chunkSize < 0 || position + chunkSize > bytes.Length)
        {
            throw new InvalidDataException("The ASR fixture contains an invalid chunk.");
        }

        if (chunkId.SequenceEqual("fmt "u8))
        {
            format = bytes.AsSpan(position, chunkSize).ToArray();
        }
        else if (chunkId.SequenceEqual("data"u8))
        {
            if (format is null || format.Length < 16)
            {
                throw new InvalidDataException("The ASR fixture has no valid format chunk.");
            }

            var audioFormat = BitConverter.ToInt16(format, 0);
            var channels = BitConverter.ToInt16(format, 2);
            var sampleRate = BitConverter.ToInt32(format, 4);
            var bitsPerSample = BitConverter.ToInt16(format, 14);
            if (audioFormat != 1 || channels < 1 || sampleRate != SampleRate || bitsPerSample != 16)
            {
                throw new InvalidDataException("The ASR fixture is not supported 16 kHz PCM audio.");
            }

            var sampleCount = chunkSize / sizeof(short) / channels;
            var samples = new float[sampleCount];
            for (var index = 0; index < sampleCount; index++)
            {
                samples[index] = BitConverter.ToInt16(
                    bytes,
                    position + (index * sizeof(short) * channels)) / 32768f;
            }

            return samples;
        }

        position += chunkSize + (chunkSize % 2);
    }

    throw new InvalidDataException("The ASR fixture has no audio data chunk.");
}

static string FindRepositoryRoot(string startDirectory)
{
    var directory = new DirectoryInfo(startDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new DirectoryNotFoundException(
        "The repository root could not be located.");
}

internal sealed record SpikeResult(
    string Name,
    long AudioMilliseconds,
    int WindowCount,
    long BatchMilliseconds,
    long PreReleaseWorkMilliseconds,
    long FinalizationMilliseconds,
    int BatchWordCount,
    int IncrementalWordCount,
    double ReferenceWordErrorRate,
    bool ExactMatch,
    bool HasText);
