using System.Globalization;
using System.IO.MemoryMappedFiles;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Runtime;

public sealed record RuntimeWorkerTranscriptionOptions(
    string WorkerExecutable,
    string ModelDirectory,
    RuntimeProviderKind Provider,
    ParakeetModelPack ModelPack,
    int IntraOpThreads,
    int InterOpThreads = 1,
    int MaximumTokensPerStep = 10,
    int CpuFallbackThreads = 8,
    string? CudaRuntimeDirectory = null,
    TimeSpan? StartupTimeout = null,
    TimeSpan? TranscriptionTimeout = null,
    int MaximumWorkerRestarts = 1,
    FinalAsrEngine Engine = FinalAsrEngine.Parakeet,
    WhisperModelPack WhisperPack = WhisperModelPack.Quantized,
    string? Language = null);

public sealed class RuntimeWorkerTranscriptionEngine : ITranscriptionEngine, IAsyncDisposable
{
    private const int RequiredSampleRate = 16_000;
    private readonly RuntimeWorkerSupervisor _supervisor;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _transcriptionTimeout;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _disposed;

    public RuntimeWorkerTranscriptionEngine(RuntimeWorkerTranscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        EngineId = options.Engine == FinalAsrEngine.Whisper
            ? $"whisper-large-v3-turbo:{options.Provider.ToString().ToLowerInvariant()}:isolated"
            : $"parakeet-tdt-0.6b-v3:{options.Provider.ToString().ToLowerInvariant()}:isolated";
        _startupTimeout = options.StartupTimeout ?? TimeSpan.FromSeconds(30);
        _transcriptionTimeout = options.TranscriptionTimeout ?? TimeSpan.FromMinutes(2);
        _supervisor = new RuntimeWorkerSupervisor(
            options.WorkerExecutable,
            CreateWorkerArguments(options),
            options.MaximumWorkerRestarts);
    }

    public string EngineId { get; }

    public int? WorkerProcessId => _supervisor.WorkerProcessId;

    public async Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
        await _supervisor.StartAsync(_startupTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<Transcript> TranscribeAsync(
        CapturedAudio audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (audio.Outcome == AudioCaptureOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (audio.SampleRate != RequiredSampleRate || audio.Channels != 1)
        {
            throw Failure(AppErrorCode.AudioFormatUnsupported, canRetry: false);
        }

        if (audio.Samples.IsEmpty)
        {
            return new Transcript(audio.SessionId, string.Empty, EngineId, []);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var effectiveCancellationToken = linkedCancellation.Token;

        var mapName = $"EnviousWispr.Audio.{Guid.NewGuid():N}";
        var byteLength = checked((long)audio.Samples.Length * sizeof(float));
        using var map = MemoryMappedFile.CreateNew(
            mapName,
            byteLength,
            MemoryMappedFileAccess.ReadWrite);
        using (var view = map.CreateViewAccessor(0, byteLength, MemoryMappedFileAccess.Write))
        {
            view.WriteArray(0, audio.Samples.ToArray(), 0, audio.Samples.Length);
        }

        var request = new RuntimeWorkerTranscriptionRequest(
            audio.SessionId.Value,
            mapName,
            audio.Samples.Length);
        var response = await _supervisor.TranscribeAsync(
            request,
            _transcriptionTimeout,
            effectiveCancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            var recovery = await _supervisor.EnsureHealthyAsync(
                _startupTimeout,
                effectiveCancellationToken).ConfigureAwait(false);
            if (!recovery.Succeeded)
            {
                throw Failure(AppErrorCode.RuntimeWorkerFailed);
            }

            response = await _supervisor.TranscribeAsync(
                request,
                _transcriptionTimeout,
                effectiveCancellationToken).ConfigureAwait(false);
        }

        if (response?.Status != "complete" || response.Transcript is null)
        {
            throw new TranscriptionEngineException(
                response?.Error ?? new AppError(
                    AppErrorCode.TranscriptionFailed,
                    AppErrorStage.FinalAsr,
                    CanRetry: true));
        }

        var transcript = response.Transcript;
        if (transcript.SessionId != audio.SessionId.Value)
        {
            throw Failure(AppErrorCode.TranscriptionFailed);
        }

        return new Transcript(
            audio.SessionId,
            transcript.Text,
            transcript.EngineId,
            transcript.TokenTimings,
            transcript.UsedFallback,
            transcript.DegradedError,
            transcript.DetectedLanguage);
    }

    private static string[] CreateWorkerArguments(RuntimeWorkerTranscriptionOptions options)
    {
        var arguments = new List<string>
        {
            "--asr-engine",
            options.Engine.ToString(),
            "--asr-model-directory",
            Path.GetFullPath(options.ModelDirectory),
            "--asr-provider",
            options.Provider.ToString(),
            "--asr-model-pack",
            options.ModelPack.ToString(),
            "--asr-intra-op-threads",
            options.IntraOpThreads.ToString(CultureInfo.InvariantCulture),
            "--asr-inter-op-threads",
            options.InterOpThreads.ToString(CultureInfo.InvariantCulture),
            "--asr-maximum-tokens-per-step",
            options.MaximumTokensPerStep.ToString(CultureInfo.InvariantCulture),
            "--asr-cpu-fallback-threads",
            options.CpuFallbackThreads.ToString(CultureInfo.InvariantCulture),
        };
        if (options.Engine == FinalAsrEngine.Whisper)
        {
            arguments.Add("--asr-whisper-model-pack");
            arguments.Add(options.WhisperPack.ToString());
            arguments.Add("--asr-whisper-language");
            arguments.Add(string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language);
        }
        if (!string.IsNullOrWhiteSpace(options.CudaRuntimeDirectory))
        {
            arguments.Add("--asr-cuda-runtime-directory");
            arguments.Add(Path.GetFullPath(options.CudaRuntimeDirectory));
        }

        return arguments.ToArray();
    }

    private static void Validate(RuntimeWorkerTranscriptionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkerExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.IntraOpThreads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InterOpThreads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumTokensPerStep, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CpuFallbackThreads, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumWorkerRestarts);
        if (options.StartupTimeout is { } startupTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startupTimeout, TimeSpan.Zero);
        }

        if (options.TranscriptionTimeout is { } transcriptionTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(transcriptionTimeout, TimeSpan.Zero);
        }
    }

    private static TranscriptionEngineException Failure(
        AppErrorCode code,
        bool canRetry = true) => new(
        new AppError(code, AppErrorStage.FinalAsr, canRetry));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
