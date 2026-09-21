using System.Diagnostics;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Services.Runtime;

public sealed class RuntimeWorkerLivePreviewEngine : IAbortableLivePreviewEngine
{
    private readonly Func<IWorkerTranscriptionRuntime>? _replacement;
    private readonly RuntimeResourceArbiter _resourceArbiter;
    private readonly RuntimeResourceKind _resource;
    private readonly TimeSpan _resourceTimeout;
    private IWorkerTranscriptionRuntime _engine;
    private IAsyncDisposable? _resourceLease;
    private bool _retired;
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
            resourceTimeout,
            () => CreateRuntime(options))
    {
    }

    /// <param name="replacement">Builds a fresh runtime after an abort retired the one in use; null leaves the engine unable to start again after an abort.</param>
    internal RuntimeWorkerLivePreviewEngine(
        IWorkerTranscriptionRuntime engine,
        RuntimeResourceArbiter resourceArbiter,
        RuntimeResourceKind resource,
        TimeSpan? resourceTimeout = null,
        Func<IWorkerTranscriptionRuntime>? replacement = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(resourceArbiter);
        _engine = engine;
        _replacement = replacement;
        _resourceArbiter = resourceArbiter;
        _resource = resource;
        _resourceTimeout = resourceTimeout ?? TimeSpan.Zero;
    }

    /// <summary>The runtime in use; after an observed abort, the replacement the next start built.</summary>
    internal IWorkerTranscriptionRuntime Runtime => _engine;

    public string EngineId => _engine.EngineId;

    public async Task<RuntimeWorkerResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_resourceLease is not null)
        {
            return new RuntimeWorkerResult(true, RuntimeWorkerState.Ready);
        }

        // AN ABORTED RUNTIME IS TERMINAL, AND THE PREVIEW IS NOT. The supervisor an abort ended
        // refuses every start after, by design; the next recording's preview needs a runtime of its
        // own, so the retired one is let go of - disposed, its process already seen gone - and a
        // fresh one built in its place. Without a way to build one, the start is refused as the
        // supervisor would refuse it.
        if (_retired)
        {
            if (_replacement is null)
            {
                return new RuntimeWorkerResult(
                    false,
                    RuntimeWorkerState.Aborted,
                    new AppError(AppErrorCode.RuntimeWorkerFailed, AppErrorStage.RuntimeWorker, CanRetry: false));
            }

            var retired = _engine;
            _engine = _replacement();
            _retired = false;
            await retired.DisposeAsync().ConfigureAwait(false);
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
                await ReleaseResourceIfWorkerGoneAsync().ConfigureAwait(false);
            }

            return started;
        }
        catch
        {
            // A START CANCELLED HALF-WAY MAY HAVE LEFT A WORKER RUNNING, on the resource this lease
            // stands for; the stop the preview controller issues next takes it down and lets go.
            await ReleaseResourceIfWorkerGoneAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Lets go of the resource only when no worker of this engine is alive to be on it.</summary>
    private async Task ReleaseResourceIfWorkerGoneAsync()
    {
        if (_engine.WorkerProcessId is null)
        {
            await ReleaseResourceAsync().ConfigureAwait(false);
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

        // THE RESOURCE FOLLOWS THE WORKER. A stop that could not see the worker go reports so, and
        // the lease stays with the preview until one that does - or an abort - sees it.
        var stopped = await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
        if (stopped.Succeeded || _engine.WorkerProcessId is null)
        {
            await ReleaseResourceAsync().ConfigureAwait(false);
        }

        return stopped;
    }

    /// <summary>Kills the preview's worker - for a release its stop refused, or a shutdown; terminal. The resource it held is let go of only once the worker is seen gone.</summary>
    /// <remarks>
    /// THE RESOURCE FOLLOWS THE WORKER, NOT THE CALL. A worker whose exit was not observed may still
    /// be on the accelerator or the CPU the lease stands for; handing that to the final engine would
    /// put two workers on it. So the lease is released on an observed exit (or when there was no
    /// worker), and kept - with the generation that still owns the process - otherwise.
    /// </remarks>
    public async Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        var result = await _engine.AbortAsync(deadline).ConfigureAwait(false);
        _retired = true;
        if (result.Outcome is RuntimeWorkerAbortOutcome.Exited or RuntimeWorkerAbortOutcome.NoWorker)
        {
            await ReleaseResourceAsync().ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // STOPPED BEFORE DISPOSED, so the disposal has a word on whether the worker went; the
            // lease is let go of only on that word.
            var stopped = await _engine.StopAsync().ConfigureAwait(false);
            await _engine.DisposeAsync().ConfigureAwait(false);
            if (stopped.Succeeded || _engine.WorkerProcessId is null)
            {
                await ReleaseResourceAsync().ConfigureAwait(false);
            }
        }
        finally
        {
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
