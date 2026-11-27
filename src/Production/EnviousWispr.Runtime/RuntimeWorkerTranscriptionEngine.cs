using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Runtime;

/// <param name="Language">The Whisper language the worker loads with.</param>
/// <param name="CurrentLanguage">Read at every take and sent with it, so a language changed in the running app reaches the next take with no restart (#241); null sends <paramref name="Language"/>. Ignored for Parakeet, which takes no language.</param>
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
    string? Language = null,
    ProcessPriorityClass? WorkerPriority = null,
    Func<string?>? CurrentLanguage = null,
    bool AllowFullPrecisionFallback = true,
    IRuntimeResourceAdmission? ResourceAdmission = null,
    TimeSpan? ResourceWaitTimeout = null);

internal interface IWorkerTranscriptionRuntime : ITranscriptionEngine, IAsyncDisposable
{
    /// <summary>The worker's process id while one is alive, or null: the one fact the preview's resource follows.</summary>
    int? WorkerProcessId { get; }

    Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default);

    Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Kills the worker for a shutdown without waiting for a request in flight; terminal.</summary>
    Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline);
}

public sealed class RuntimeWorkerTranscriptionEngine : IWorkerTranscriptionRuntime
{
    private const int RequiredSampleRate = 16_000;
    private static readonly TimeSpan DefaultResourceWaitTimeout = TimeSpan.FromSeconds(2);
    private readonly RuntimeWorkerSupervisor _supervisor;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _transcriptionTimeout;
    private readonly Func<string?> _language;
    private readonly IRuntimeResourceAdmission? _resourceAdmission;
    private readonly RuntimeResourceKind _resourceKind;
    private readonly TimeSpan _resourceWaitTimeout;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _disposed;

    public RuntimeWorkerTranscriptionEngine(RuntimeWorkerTranscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        EngineId = options.Engine == FinalAsrEngine.Whisper
            ? $"{WhisperModelIds.For(options.WhisperPack)}:{options.Provider.ToString().ToLowerInvariant()}:isolated"
            : $"{ParakeetModelIds.Final}:{options.Provider.ToString().ToLowerInvariant()}:isolated";
        _startupTimeout = options.StartupTimeout ?? TimeSpan.FromSeconds(30);
        _transcriptionTimeout = options.TranscriptionTimeout ?? TimeSpan.FromMinutes(2);
        _language = LanguageFor(options);
        _resourceAdmission = options.ResourceAdmission;
        _resourceKind = options.Provider == RuntimeProviderKind.Cpu
            ? RuntimeResourceKind.Cpu
            : RuntimeResourceKind.Accelerator;
        _resourceWaitTimeout = options.ResourceWaitTimeout ?? DefaultResourceWaitTimeout;
        _supervisor = new RuntimeWorkerSupervisor(
            options.WorkerExecutable,
            CreateWorkerArguments(options),
            options.MaximumWorkerRestarts,
            options.WorkerPriority);
    }

    /// <summary>The engine over a supervisor a test built: the production adapter, a worker of the test's choosing.</summary>
    internal RuntimeWorkerTranscriptionEngine(
        RuntimeWorkerSupervisor supervisor,
        string engineId,
        TimeSpan? startupTimeout = null,
        TimeSpan? transcriptionTimeout = null,
        Func<string?>? language = null,
        IRuntimeResourceAdmission? resourceAdmission = null,
        RuntimeResourceKind resourceKind = RuntimeResourceKind.Cpu,
        TimeSpan? resourceWaitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        _supervisor = supervisor;
        _language = language ?? (static () => null);
        EngineId = engineId;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        _transcriptionTimeout = transcriptionTimeout ?? TimeSpan.FromMinutes(2);
        _resourceAdmission = resourceAdmission;
        _resourceKind = resourceKind;
        _resourceWaitTimeout = resourceWaitTimeout ?? DefaultResourceWaitTimeout;
    }

    public string EngineId { get; }

    public int? WorkerProcessId => _supervisor.WorkerProcessId;

    public async Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
        await _supervisor.StartAsync(_startupTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default) =>
        await _supervisor.StopAsync(cancellationToken).ConfigureAwait(false);

    public Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline) => _supervisor.AbortAsync(deadline);

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
        using (var view = map.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Write))
        {
            view.Write(MemoryMarshal.AsBytes(audio.Samples.Span));
        }

        var request = new RuntimeWorkerTranscriptionRequest(
            audio.SessionId.Value,
            mapName,
            audio.Samples.Length,
            // READ NOW, AT THE TAKE, not when the worker was built: the setting as it stands.
            _language());

        // Dictation-first front door: acquire the shared processor/accelerator lease (which preempts
        // background warmups/judges). Heart is sacred — if an unrelated limb fails to yield inside
        // the deadline, raw transcription still proceeds rather than failing the user's take.
        IAsyncDisposable? resourceLease = null;
        IAsyncDisposable? cpuFallbackLease = null;
        RuntimeWorkerResponse? response;
        try
        {
            if (_resourceAdmission is not null)
            {
                var acquired = await _resourceAdmission.AcquireAsync(
                    _resourceKind,
                    RuntimeWorkloadKind.FinalAsr,
                    _resourceWaitTimeout,
                    effectiveCancellationToken).ConfigureAwait(false);
                if (acquired.Succeeded)
                {
                    resourceLease = acquired.Lease;
                }

                if (_resourceKind == RuntimeResourceKind.Accelerator)
                {
                    // GPU workers build an internal CPU fallback when accelerator load or inference fails,
                    // so reserve Cpu as well in fixed Accelerator -> Cpu order to block CPU-bound limbs.
                    var cpuAcquired = await _resourceAdmission.AcquireAsync(
                        RuntimeResourceKind.Cpu,
                        RuntimeWorkloadKind.FinalAsr,
                        _resourceWaitTimeout,
                        effectiveCancellationToken).ConfigureAwait(false);
                    if (cpuAcquired.Succeeded)
                    {
                        cpuFallbackLease = cpuAcquired.Lease;
                    }
                }
            }

            response = await _supervisor.TranscribeAsync(
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
        }
        finally
        {
            if (cpuFallbackLease is not null)
            {
                await cpuFallbackLease.DisposeAsync().ConfigureAwait(false);
            }

            if (resourceLease is not null)
            {
                await resourceLease.DisposeAsync().ConfigureAwait(false);
            }
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
            transcript.DetectedLanguage,
            transcript.RecognitionLanguage);
    }

    /// <summary>What each take is told: the live language preference, or the load-time fallback.</summary>
    internal static Func<string?> LanguageFor(RuntimeWorkerTranscriptionOptions options) =>
        options.CurrentLanguage ?? (() => options.Language);

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
        else if (!options.AllowFullPrecisionFallback)
        {
            arguments.Add("--asr-disable-fp32-fallback");
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
