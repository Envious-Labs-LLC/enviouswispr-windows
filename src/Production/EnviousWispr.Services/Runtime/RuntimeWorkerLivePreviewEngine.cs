using System.Diagnostics;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Services.Runtime;

public sealed class RuntimeWorkerLivePreviewEngine : ILivePreviewEngine
{
    private readonly IWorkerTranscriptionRuntime _engine;
    private readonly RuntimeResourceArbiter _resourceArbiter;
    private readonly RuntimeResourceKind _resource;
    private readonly TimeSpan _resourceTimeout;
    private IAsyncDisposable? _resourceLease;
    private bool _disposed;

    public RuntimeWorkerLivePreviewEngine(
        RuntimeWorkerTranscriptionOptions options,
        RuntimeResourceArbiter resourceArbiter,
        TimeSpan? resourceTimeout = null)
        : this(
            CreateRuntime(options),
            resourceArbiter,
            options.Provider == RuntimeProviderKind.Cpu
                ? RuntimeResourceKind.Cpu
                : RuntimeResourceKind.Accelerator,
            resourceTimeout)
    {
    }

    internal RuntimeWorkerLivePreviewEngine(
        IWorkerTranscriptionRuntime engine,
        RuntimeResourceArbiter resourceArbiter,
        RuntimeResourceKind resource,
        TimeSpan? resourceTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(resourceArbiter);
        _engine = engine;
        _resourceArbiter = resourceArbiter;
        _resource = resource;
        _resourceTimeout = resourceTimeout ?? TimeSpan.Zero;
    }

    public string EngineId => _engine.EngineId;

    public async Task<RuntimeWorkerResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_resourceLease is not null)
        {
            return new RuntimeWorkerResult(true, RuntimeWorkerState.Ready);
        }

        var acquired = await _resourceArbiter.AcquireAsync(
            _resource,
            RuntimeWorkloadKind.LivePreview,
            _resourceTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!acquired.Succeeded)
        {
            return new RuntimeWorkerResult(
                false,
                RuntimeWorkerState.Faulted,
                acquired.Error);
        }

        _resourceLease = acquired.Lease;
        try
        {
            var started = await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!started.Succeeded)
            {
                await ReleaseResourceAsync().ConfigureAwait(false);
            }

            return started;
        }
        catch
        {
            await ReleaseResourceAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<LivePreviewUpdate> PreviewAsync(
        AudioSnapshot snapshot,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (_resourceLease is null)
        {
            return Failure(snapshot, sequence, AppErrorCode.RuntimeResourceBusy);
        }

        try
        {
            var transcript = await _engine.TranscribeAsync(new CapturedAudio(
                snapshot.SessionId,
                snapshot.Samples,
                snapshot.SampleRate,
                snapshot.Channels), cancellationToken).ConfigureAwait(false);
            return new LivePreviewUpdate(
                snapshot.SessionId.Value,
                sequence,
                Succeeded: true,
                transcript.Text,
                transcript.DetectedLanguage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TranscriptionEngineException exception)
        {
            return new LivePreviewUpdate(
                snapshot.SessionId.Value,
                sequence,
                Succeeded: false,
                string.Empty,
                Error: exception.Error);
        }
    }

    public async Task<RuntimeWorkerResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return new RuntimeWorkerResult(true, RuntimeWorkerState.Disposed);
        }

        try
        {
            return await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseResourceAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await ReleaseResourceAsync().ConfigureAwait(false);
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private async ValueTask ReleaseResourceAsync()
    {
        var lease = Interlocked.Exchange(ref _resourceLease, null);
        if (lease is not null)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static LivePreviewUpdate Failure(
        AudioSnapshot snapshot,
        long sequence,
        AppErrorCode code) => new(
        snapshot.SessionId.Value,
        sequence,
        Succeeded: false,
        string.Empty,
        Error: new AppError(code, AppErrorStage.RuntimeResource, CanRetry: true));

    private static RuntimeWorkerTranscriptionEngine CreateRuntime(
        RuntimeWorkerTranscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Engine != Core.Settings.FinalAsrEngine.Whisper ||
            options.WhisperPack != WhisperModelPack.PreviewSmall)
        {
            throw new ArgumentException(
                "Live preview requires the dedicated small Whisper model pack.",
                nameof(options));
        }

        return new RuntimeWorkerTranscriptionEngine(options with
        {
            WorkerPriority = ProcessPriorityClass.BelowNormal,
            MaximumWorkerRestarts = 0,
        });
    }
}
