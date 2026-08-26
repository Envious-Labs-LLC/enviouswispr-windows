using System.Diagnostics;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Runtime;

const int sampleRate = 16_000;
var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var modelDirectory = Path.Combine(repositoryRoot, "models", "whisper-large-v3-turbo");
var audioDirectory = Path.Combine(repositoryRoot, "spikes", "s1", "audio");
var clips = new Dictionary<string, float[]>(StringComparer.Ordinal)
{
    ["clip10"] = ReadWaveFile(Path.Combine(audioDirectory, "clip10.wav")),
    ["clip20"] = ReadWaveFile(Path.Combine(audioDirectory, "clip20.wav")),
    ["clip94"] = ReadWaveFile(Path.Combine(audioDirectory, "clip94.wav")),
};
var multilingual = new Dictionary<string, MultilingualFixture>(StringComparer.Ordinal)
{
    ["fr"] = new(
        ReadWaveFile(Path.Combine(repositoryRoot, "tools", "whisper-uat", "fixtures", "fr-FR-row0.wav")),
        "je souhaite changer mon adresse"),
    ["de"] = new(
        ReadWaveFile(Path.Combine(repositoryRoot, "tools", "whisper-uat", "fixtures", "de-DE-row0.wav")),
        "ich möchte gerne Geld auf mein Konto einzahlen"),
    ["es"] = new(
        ReadWaveFile(Path.Combine(repositoryRoot, "tools", "whisper-uat", "fixtures", "es-ES-row0.wav")),
        "hola buenas a ver tengo un problema con vuestra aplicación resulta que quiero hacer una transferencia bancaria a una cuenta conocida"),
};

var mode = args.FirstOrDefault()?.ToLowerInvariant();
var requestedProvider = mode is "cpu" or "cuda" ? mode : null;
var modelPack = args.Contains("--preview-small", StringComparer.OrdinalIgnoreCase)
    ? WhisperModelPack.PreviewSmall
    : args.Contains("--full-precision", StringComparer.OrdinalIgnoreCase)
        ? WhisperModelPack.FullPrecision
        : WhisperModelPack.Quantized;
if (modelPack == WhisperModelPack.PreviewSmall)
{
    modelDirectory = Path.Combine(repositoryRoot, "models", "whisper-small");
}
if (mode is "fixed-cpu" or "fixed-cuda")
{
    var provider = mode == "fixed-cpu" ? RuntimeProviderKind.Cpu : RuntimeProviderKind.Cuda;
    var fixedLanguage = await RunFixedLanguageDiagnosticsAsync(
        provider,
        modelPack,
        repositoryRoot,
        modelDirectory,
        multilingual);
    Console.WriteLine(JsonSerializer.Serialize(new { fixedLanguage }));
    return fixedLanguage.All(result => result.Passed) ? 0 : 9;
}

var cpu = requestedProvider is null or "cpu"
    ? await RunProviderAsync(
        RuntimeProviderKind.Cpu,
        modelPack,
        repositoryRoot,
        modelDirectory,
        clips,
        multilingual)
    : null;
var cuda = requestedProvider is null or "cuda"
    ? await RunProviderAsync(
        RuntimeProviderKind.Cuda,
        modelPack,
        repositoryRoot,
        modelDirectory,
        clips,
        multilingual)
    : null;
Console.WriteLine(JsonSerializer.Serialize(new { cpu, cuda }));
return (cpu?.Passed ?? true) && (cuda?.Passed ?? true) ? 0 : 8;

static async Task<ProviderResult> RunProviderAsync(
    RuntimeProviderKind provider,
    WhisperModelPack modelPack,
    string repositoryRoot,
    string modelDirectory,
    IReadOnlyDictionary<string, float[]> clips,
    IReadOnlyDictionary<string, MultilingualFixture> multilingual)
{
    var worker = Path.Combine(
        repositoryRoot,
        "src",
        "Production",
        "EnviousWispr.RuntimeWorker",
        "bin",
        "Release",
        "net10.0-windows10.0.26100.0",
        "EnviousWispr.RuntimeWorker.exe");
    await using var engine = new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
        worker,
        modelDirectory,
        provider,
        ParakeetModelPack.Quantized,
        IntraOpThreads: provider == RuntimeProviderKind.Cpu ? 8 : 4,
        CpuFallbackThreads: 8,
        Engine: FinalAsrEngine.Whisper,
        WhisperPack: modelPack,
        Language: "auto",
        CudaRuntimeDirectory: Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_CUDA_RUNTIME_DIR")));

    var loadTimer = Stopwatch.StartNew();
    var started = await engine.StartAsync();
    loadTimer.Stop();
    if (!started.Succeeded)
    {
        return new ProviderResult(
            provider.ToString(),
            loadTimer.ElapsedMilliseconds,
            Started: false,
            Clip10: null,
            Clip20: null,
            Clip94: null,
            CancellationObserved: false,
            CancellationMilliseconds: 0,
            WorkerRemovedAfterCancellation: false,
            Multilingual: [],
            Passed: false);
    }

    _ = await TranscribeAsync(engine, clips["clip10"]);
    var clip10 = await MeasureAsync(engine, clips["clip10"], "telemetry");
    var clip20 = await MeasureAsync(engine, clips["clip20"], "sentiment analysis");
    var clip94 = await MeasureAsync(engine, clips["clip94"], requiredPhrase: null);
    var multilingualResults = new List<LanguageResult>();
    foreach (var (language, fixture) in multilingual)
    {
        multilingualResults.Add(await MeasureLanguageAsync(engine, language, fixture));
    }

    var processId = engine.WorkerProcessId;
    var cancellationTimer = Stopwatch.StartNew();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    var cancellationObserved = false;
    try
    {
        _ = await TranscribeAsync(engine, clips["clip94"], cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        cancellationObserved = true;
    }

    cancellationTimer.Stop();
    var workerRemoved = processId is not null && !IsProcessRunning(processId.Value);
    return new ProviderResult(
        provider.ToString(),
        loadTimer.ElapsedMilliseconds,
        Started: true,
        clip10,
        clip20,
        clip94,
        cancellationObserved,
        cancellationTimer.ElapsedMilliseconds,
        workerRemoved,
        multilingualResults,
        Passed: clip10.Passed && clip20.Passed && clip94.Passed &&
            multilingualResults.All(result => result.Passed) &&
            cancellationObserved && workerRemoved);
}

static async Task<IReadOnlyList<LanguageResult>> RunFixedLanguageDiagnosticsAsync(
    RuntimeProviderKind provider,
    WhisperModelPack modelPack,
    string repositoryRoot,
    string modelDirectory,
    IReadOnlyDictionary<string, MultilingualFixture> multilingual)
{
    var results = new List<LanguageResult>();
    foreach (var (language, fixture) in multilingual)
    {
        var worker = Path.Combine(
            repositoryRoot,
            "src",
            "Production",
            "EnviousWispr.RuntimeWorker",
            "bin",
            "Release",
            "net10.0-windows10.0.26100.0",
            "EnviousWispr.RuntimeWorker.exe");
        await using var engine = new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
            worker,
            modelDirectory,
            provider,
            ParakeetModelPack.Quantized,
            IntraOpThreads: provider == RuntimeProviderKind.Cpu ? 8 : 4,
            CpuFallbackThreads: 8,
            Engine: FinalAsrEngine.Whisper,
            WhisperPack: modelPack,
            Language: language,
            CudaRuntimeDirectory: Environment.GetEnvironmentVariable(
                "ENVIOUSWISPR_CUDA_RUNTIME_DIR")));
        var started = await engine.StartAsync();
        if (!started.Succeeded)
        {
            results.Add(new LanguageResult(
                language,
                fixture.Samples.Length * 1_000L / sampleRate,
                0,
                LanguageDetected: false,
                Words(fixture.ExpectedText).Length,
                ActualWordCount: 0,
                WordErrorRate: 1,
                Passed: false));
            continue;
        }

        results.Add(await MeasureLanguageAsync(engine, language, fixture));
    }

    return results;
}

static async Task<LanguageResult> MeasureLanguageAsync(
    RuntimeWorkerTranscriptionEngine engine,
    string expectedLanguage,
    MultilingualFixture fixture)
{
    var timer = Stopwatch.StartNew();
    var transcript = await TranscribeAsync(engine, fixture.Samples);
    timer.Stop();
    var expected = Words(fixture.ExpectedText);
    var actual = Words(transcript.Text);
    var wordErrorRate = expected.Length == 0
        ? actual.Length == 0 ? 0 : 1
        : LevenshteinDistance(expected, actual) / (double)expected.Length;
    var detected = string.Equals(
        transcript.DetectedLanguage,
        expectedLanguage,
        StringComparison.OrdinalIgnoreCase);
    return new LanguageResult(
        expectedLanguage,
        fixture.Samples.Length * 1_000L / sampleRate,
        timer.ElapsedMilliseconds,
        detected,
        expected.Length,
        actual.Length,
        wordErrorRate,
        Passed: detected && wordErrorRate <= 0.35);
}

static async Task<ClipResult> MeasureAsync(
    RuntimeWorkerTranscriptionEngine engine,
    float[] samples,
    string? requiredPhrase)
{
    var timer = Stopwatch.StartNew();
    var transcript = await TranscribeAsync(engine, samples);
    timer.Stop();
    var duration = TimeSpan.FromSeconds(samples.Length / (double)sampleRate);
    var timingsValid = transcript.TokenTimings is { Count: > 0 } timings &&
        timings.All(timing =>
            timing.Start >= TimeSpan.Zero &&
            timing.End >= timing.Start &&
            timing.End <= duration);
    var phrasePresent = requiredPhrase is null ||
        transcript.Text.Contains(requiredPhrase, StringComparison.OrdinalIgnoreCase);
    var languageDetected = string.Equals(transcript.DetectedLanguage, "en", StringComparison.OrdinalIgnoreCase);
    return new ClipResult(
        samples.Length * 1_000L / sampleRate,
        timer.ElapsedMilliseconds,
        !string.IsNullOrWhiteSpace(transcript.Text),
        phrasePresent,
        languageDetected,
        transcript.TokenTimings?.Count ?? 0,
        timingsValid,
        !transcript.UsedFallback,
        Passed: !string.IsNullOrWhiteSpace(transcript.Text) &&
            phrasePresent && languageDetected && timingsValid);
}

static Task<Transcript> TranscribeAsync(
    RuntimeWorkerTranscriptionEngine engine,
    float[] samples,
    CancellationToken cancellationToken = default) => engine.TranscribeAsync(new CapturedAudio(
    DictationSessionId.Create(),
    samples,
    sampleRate,
    Channels: 1), cancellationToken);

static bool IsProcessRunning(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static string[] Words(string text) =>
    text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray()))
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
            var sampleCount = chunkSize / bytesPerSample / channels;
            var samples = new float[sampleCount];
            for (var index = 0; index < sampleCount; index++)
            {
                var sampleOffset = position + (index * bytesPerSample * channels);
                samples[index] = audioFormat switch
                {
                    1 when bitsPerSample == 16 => BitConverter.ToInt16(bytes, sampleOffset) / 32768f,
                    7 when bitsPerSample == 8 => DecodeMuLaw(bytes[sampleOffset]),
                    _ => throw new InvalidDataException("The Whisper fixture uses an unsupported codec."),
                };
            }

            return sourceRate == sampleRate ? samples : Resample(samples, sourceRate, sampleRate);
        }

        position += chunkSize + (chunkSize % 2);
    }

    throw new InvalidDataException("The Whisper fixture has no supported audio data.");
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
    var destinationLength = checked((int)Math.Round(
        source.Length * (destinationRate / (double)sourceRate)));
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

internal sealed record ClipResult(
    long AudioMilliseconds,
    long TranscriptionMilliseconds,
    bool HasText,
    bool PhrasePresent,
    bool LanguageDetected,
    int TimingCount,
    bool TimingsValid,
    bool PrimaryProviderUsed,
    bool Passed);

internal sealed record ProviderResult(
    string Provider,
    long LoadMilliseconds,
    bool Started,
    ClipResult? Clip10,
    ClipResult? Clip20,
    ClipResult? Clip94,
    bool CancellationObserved,
    long CancellationMilliseconds,
    bool WorkerRemovedAfterCancellation,
    IReadOnlyList<LanguageResult> Multilingual,
    bool Passed);

internal sealed record MultilingualFixture(float[] Samples, string ExpectedText);

internal sealed record LanguageResult(
    string ExpectedLanguage,
    long AudioMilliseconds,
    long TranscriptionMilliseconds,
    bool LanguageDetected,
    int ReferenceWordCount,
    int ActualWordCount,
    double WordErrorRate,
    bool Passed);
