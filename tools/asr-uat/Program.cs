using System.Diagnostics;
using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Runtime;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var modelDirectory = Path.Combine(repositoryRoot, "models", "parakeet-tdt-0.6b-v3");
var audioDirectory = Path.Combine(repositoryRoot, "spikes", "s1", "audio");
var clips = new Dictionary<string, float[]>(StringComparer.Ordinal)
{
    ["clip10"] = ReadWaveFile(Path.Combine(audioDirectory, "clip10.wav")),
    ["clip20"] = ReadWaveFile(Path.Combine(audioDirectory, "clip20.wav")),
    ["clip94"] = ReadWaveFile(Path.Combine(audioDirectory, "clip94.wav")),
};
var requestedProvider = args.FirstOrDefault()?.ToLowerInvariant() switch
{
    null => null,
    "cpu" => "cpu",
    "cuda" => "cuda",
    _ => throw new ArgumentException("Use cpu or cuda, or omit the provider to test both."),
};

var cpu = requestedProvider is null or "cpu"
    ? await RunProviderSafelyAsync(
        new ParakeetEngineOptions(
            modelDirectory,
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            IntraOpThreads: 8),
        clips)
    : null;
var cuda = requestedProvider is null or "cuda"
    ? await RunProviderSafelyAsync(
        new ParakeetEngineOptions(
            modelDirectory,
            RuntimeProviderKind.Cuda,
            ParakeetModelPack.FullPrecision,
            IntraOpThreads: 1,
            CudaRuntimeDirectory: CudaRuntimeDirectory.ForTooling()),
        clips)
    : null;
var isolated = requestedProvider is null or "cuda"
    ? await RunIsolatedAsync(repositoryRoot, modelDirectory, clips)
    : null;

Console.WriteLine(JsonSerializer.Serialize(new { cpu, cuda, isolated }));
return (cpu?.Passed ?? true) && (cuda?.Passed ?? true) && (isolated?.Passed ?? true) ? 0 : 6;

static async Task<IsolatedResult> RunIsolatedAsync(
    string repositoryRoot,
    string modelDirectory,
    IReadOnlyDictionary<string, float[]> clips)
{
    var workerExecutable = Path.Combine(
        repositoryRoot,
        "src",
        "Production",
        "EnviousWispr.RuntimeWorker",
        "bin",
        "Release",
        "net10.0-windows10.0.26100.0",
        "EnviousWispr.RuntimeWorker.exe");
    var cudaRuntimeDirectory = CudaRuntimeDirectory.ForTooling();
    await using var engine = new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
        workerExecutable,
        modelDirectory,
        RuntimeProviderKind.Cuda,
        ParakeetModelPack.FullPrecision,
        IntraOpThreads: 1,
        CpuFallbackThreads: 8,
        CudaRuntimeDirectory: cudaRuntimeDirectory));
    var start = await engine.StartAsync();
    var first = await TranscribeIsolatedAsync(engine, clips["clip10"]);
    var firstPassed = first.Text.Contains("telemetry", StringComparison.OrdinalIgnoreCase) &&
        !first.UsedFallback;

    var crashRecoveryTimer = Stopwatch.StartNew();
    var longTranscription = TranscribeIsolatedAsync(engine, clips["clip94"]);
    await Task.Delay(TimeSpan.FromMilliseconds(100));
    var processId = engine.WorkerProcessId;
    if (processId is not null)
    {
        using var process = Process.GetProcessById(processId.Value);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    var recovered = await longTranscription;
    crashRecoveryTimer.Stop();
    var crashRecoveryPassed = processId is not null &&
        !string.IsNullOrWhiteSpace(recovered.Text) &&
        engine.WorkerProcessId is { } recoveredProcessId &&
        recoveredProcessId != processId.Value;

    var shutdownTranscription = TranscribeIsolatedAsync(engine, clips["clip94"]);
    await Task.Delay(TimeSpan.FromMilliseconds(100));
    var shutdownProcessId = engine.WorkerProcessId;
    var shutdownTimer = Stopwatch.StartNew();
    await engine.DisposeAsync();
    var shutdownCancelled = false;
    try
    {
        _ = await shutdownTranscription;
    }
    catch (OperationCanceledException)
    {
        shutdownCancelled = true;
    }

    shutdownTimer.Stop();
    var shutdownCancellationPassed = shutdownCancelled &&
        shutdownProcessId is not null &&
        !IsProcessRunning(shutdownProcessId.Value) &&
        shutdownTimer.Elapsed < TimeSpan.FromSeconds(2);

    await using var fallbackEngine = new RuntimeWorkerTranscriptionEngine(
        new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            RuntimeProviderKind.Cuda,
            ParakeetModelPack.FullPrecision,
            IntraOpThreads: 1,
            CpuFallbackThreads: 8,
            CudaRuntimeDirectory: Path.Combine(repositoryRoot, "missing-cuda-runtime")));
    var fallbackTranscript = await TranscribeIsolatedAsync(fallbackEngine, clips["clip10"]);
    var providerFallbackPassed = fallbackTranscript.UsedFallback &&
        fallbackTranscript.DegradedError?.Code == EnviousWispr.Core.Errors.AppErrorCode.RuntimeProviderUnavailable &&
        fallbackTranscript.Text.Contains("telemetry", StringComparison.OrdinalIgnoreCase);

    return new IsolatedResult(
        Started: start.Succeeded,
        FirstTranscriptionPassed: firstPassed,
        CrashRecoveryPassed: crashRecoveryPassed,
        CrashRecoveryMilliseconds: crashRecoveryTimer.ElapsedMilliseconds,
        ShutdownCancellationPassed: shutdownCancellationPassed,
        ShutdownCancellationMilliseconds: shutdownTimer.ElapsedMilliseconds,
        ProviderFallbackPassed: providerFallbackPassed,
        Passed: start.Succeeded &&
            firstPassed &&
            crashRecoveryPassed &&
            shutdownCancellationPassed &&
            providerFallbackPassed);
}

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

static async Task<ProviderResult> RunProviderAsync(
    ParakeetEngineOptions options,
    IReadOnlyDictionary<string, float[]> clips)
{
    var loadTimer = Stopwatch.StartNew();
    using var engine = new ParakeetTranscriptionEngine(options);
    loadTimer.Stop();

    _ = await TranscribeAsync(engine, clips["clip10"]);
    var clip10 = await MeasureAsync(engine, clips["clip10"], "telemetry");
    var clip20 = await MeasureAsync(engine, clips["clip20"], "sentiment analysis");
    var clip94 = await MeasureAsync(engine, clips["clip94"], requiredPhrase: null);

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
    return new ProviderResult(
        options.Provider.ToString(),
        loadTimer.ElapsedMilliseconds,
        clip10,
        clip20,
        clip94,
        cancellationObserved,
        cancellationTimer.ElapsedMilliseconds,
        Passed: clip10.PhrasePresent &&
            clip20.PhrasePresent &&
            clip94.HasText &&
            clip10.TimingsValid &&
            clip20.TimingsValid &&
            clip94.TimingsValid &&
            cancellationObserved,
        FailureCode: null);
}

static async Task<ProviderResult> RunProviderSafelyAsync(
    ParakeetEngineOptions options,
    IReadOnlyDictionary<string, float[]> clips)
{
    try
    {
        return await RunProviderAsync(options, clips);
    }
    catch (TranscriptionEngineException exception)
    {
        return new ProviderResult(
            options.Provider.ToString(),
            LoadMilliseconds: 0,
            Clip10: null,
            Clip20: null,
            Clip94: null,
            CancellationObserved: false,
            CancellationMilliseconds: 0,
            Passed: false,
            FailureCode: exception.Error.Code.ToString());
    }
}

static async Task<ClipResult> MeasureAsync(
    ParakeetTranscriptionEngine engine,
    float[] samples,
    string? requiredPhrase)
{
    var timer = Stopwatch.StartNew();
    var transcript = await TranscribeAsync(engine, samples);
    timer.Stop();
    var duration = TimeSpan.FromSeconds(samples.Length / (double)ParakeetTranscriptionEngine.RequiredSampleRate);
    return new ClipResult(
        AudioMilliseconds: checked((long)duration.TotalMilliseconds),
        TranscriptionMilliseconds: timer.ElapsedMilliseconds,
        HasText: !string.IsNullOrWhiteSpace(transcript.Text),
        PhrasePresent: requiredPhrase is null ||
            transcript.Text.Contains(requiredPhrase, StringComparison.OrdinalIgnoreCase),
        TokenCount: transcript.TokenTimings?.Count ?? 0,
        TimingsValid: TimingsAreValid(transcript.TokenTimings, duration),
        UnderOneSecond: timer.Elapsed < TimeSpan.FromSeconds(1));
}

static Task<Transcript> TranscribeAsync(
    ParakeetTranscriptionEngine engine,
    float[] samples,
    CancellationToken cancellationToken = default) =>
    engine.TranscribeAsync(
        new CapturedAudio(
            DictationSessionId.Create(),
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1),
        cancellationToken);

static Task<Transcript> TranscribeIsolatedAsync(
    RuntimeWorkerTranscriptionEngine engine,
    float[] samples,
    CancellationToken cancellationToken = default) =>
    engine.TranscribeAsync(
        new CapturedAudio(
            DictationSessionId.Create(),
            samples,
            ParakeetTranscriptionEngine.RequiredSampleRate,
            Channels: 1),
        cancellationToken);

static bool TimingsAreValid(
    IReadOnlyList<TranscriptTokenTiming>? timings,
    TimeSpan audioDuration)
{
    if (timings is null || timings.Count == 0)
    {
        return false;
    }

    var previousStart = TimeSpan.Zero;
    foreach (var timing in timings)
    {
        if (string.IsNullOrEmpty(timing.Text) ||
            timing.Start < TimeSpan.Zero ||
            timing.End < timing.Start ||
            timing.End > audioDuration ||
            timing.Start < previousStart)
        {
            return false;
        }

        previousStart = timing.Start;
    }

    return true;
}

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
            if (audioFormat != 1 || channels < 1 || sampleRate != 16_000 || bitsPerSample != 16)
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

internal sealed record ClipResult(
    long AudioMilliseconds,
    long TranscriptionMilliseconds,
    bool HasText,
    bool PhrasePresent,
    int TokenCount,
    bool TimingsValid,
    bool UnderOneSecond);

internal sealed record ProviderResult(
    string Provider,
    long LoadMilliseconds,
    ClipResult? Clip10,
    ClipResult? Clip20,
    ClipResult? Clip94,
    bool CancellationObserved,
    long CancellationMilliseconds,
    bool Passed,
    string? FailureCode);

internal sealed record IsolatedResult(
    bool Started,
    bool FirstTranscriptionPassed,
    bool CrashRecoveryPassed,
    long CrashRecoveryMilliseconds,
    bool ShutdownCancellationPassed,
    long ShutdownCancellationMilliseconds,
    bool ProviderFallbackPassed,
    bool Passed);
