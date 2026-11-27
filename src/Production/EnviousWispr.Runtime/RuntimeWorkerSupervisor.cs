using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using System.ComponentModel;
using System.Diagnostics;

namespace EnviousWispr.Services.Runtime;

public sealed partial class RuntimeWorkerSupervisor : IRuntimeWorkerSupervisor
{
    private const int ProtocolVersion = 1;

    private readonly UatRuntimePhaseJournal? _phaseJournal;
    private readonly string _workerExecutable;
    private readonly string[] _workerArguments;
    private readonly int _maximumRestarts;
    private readonly ProcessPriorityClass? _processPriority;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>The handshake between closing admission and publishing a generation or its process; never held across an await.</summary>
    private readonly object _lifecycle = new();
    private WorkerGeneration? _generation;
    private int _restartCount;
    private bool _disposed;
    private bool _aborted;
    private int _handlesClosed;
    private RuntimeWorkerState _state = RuntimeWorkerState.Stopped;

    /// <summary>Raised when an owned runtime worker process exits with a non-zero code outside a normal shutdown.</summary>
    public static event Action<int>? AbnormalWorkerExitObserved;

    public RuntimeWorkerSupervisor(
        string workerExecutable,
        IEnumerable<string>? workerArguments = null,
        int maximumRestarts = 1,
        ProcessPriorityClass? processPriority = null,
        UatRuntimePhaseJournal? phaseJournal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutable);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRestarts);
        _workerExecutable = Path.GetFullPath(workerExecutable);
        _workerArguments = workerArguments?.ToArray() ?? [];
        _maximumRestarts = maximumRestarts;
        _processPriority = processPriority;
        _phaseJournal = phaseJournal ?? UatRuntimePhaseJournal.FromEnvironment();
    }

    /// <summary>The worker's state; Aborted once an abort has run, whatever a request in flight writes after it.</summary>
    public RuntimeWorkerState State
    {
        get => _disposed
            ? RuntimeWorkerState.Disposed
            : Volatile.Read(ref _aborted) ? RuntimeWorkerState.Aborted : _state;
        private set => _state = value;
    }

    public int? WorkerProcessId =>
        Volatile.Read(ref _generation)?.Process is { } process && IsAlive(process) ? TryReadId(process) : null;

    /// <summary>How many worker handles this supervisor has closed; a test's way of seeing that a race closed one exactly once.</summary>
    internal int HandlesClosed => Volatile.Read(ref _handlesClosed);

    /// <summary>Who last took the current generation's teardown - "stop" or "abort"; a test's way of seeing which owner did the work.</summary>
    internal string? LastTeardownOwner => Volatile.Read(ref _generation)?.Owner;

    /// <summary>Whether the current generation holds an active Windows Job Object handle.</summary>
    internal bool HasProcessJob => Volatile.Read(ref _generation)?.ProcessJob is not null;

    public async Task<RuntimeWorkerResult> StartAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // AN EXPLICIT START RESETS THE BUDGET WHETHER OR NOT IT HAS TO START ANYTHING. Somebody
            // choosing an engine, or the app relaunching a runtime, is a new lifetime; a replacement
            // worker that happens to be healthy at that moment does not carry the old loop's count.
            _restartCount = 0;
            if (LiveProcess() is not null && State == RuntimeWorkerState.Ready)
            {
                return Success(RuntimeWorkerState.Ready);
            }

            return await StartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeWorkerResult> CheckHealthAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await CheckHealthCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeWorkerResult> EnsureHealthyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var health = await CheckHealthCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (health.Succeeded)
            {
                return health;
            }

            return await RestartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<RuntimeWorkerResponse?> TranscribeAsync(
        RuntimeWorkerTranscriptionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _aborted))
            {
                return null;
            }

            if (LiveProcess() is null || State is not RuntimeWorkerState.Ready)
            {
                var start = await RestartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (!start.Succeeded)
                {
                    return null;
                }
            }

            var response = await SendRawRequestAsync(
                "transcribe",
                request,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                await StopCoreAsync().ConfigureAwait(false);
                State = RuntimeWorkerState.Faulted;
            }
            else if (string.Equals(response.Status, "complete", StringComparison.Ordinal))
            {
                // A successful request ends the crash loop: one transient crash must not strand
                // the user until the app restarts, but a worker that crashes on every request
                // must not be restarted on every dictation forever.
                _restartCount = 0;
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State = await StopCoreAsync().ConfigureAwait(false)
                ? RuntimeWorkerState.Stopped
                : RuntimeWorkerState.Faulted;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline)
    {
        ValidateTimeout(deadline);
        // TERMINAL FIRST, THEN THE KILL. Written before the generation is read so that a start
        // racing this - one that passed its own check already - finds the flag once it has
        // published its process and takes that worker down itself, and no start after this can
        // create another. This never waits for the request gate: a wedged request holds that for
        // as long as its timeout, and the shutdown cannot.
        // ONE LOCK FOR THE HANDSHAKE. The flag is written and the generation and its process read
        // under the same lock the start publishes them under, so either this abort sees what the
        // start published and takes it down, or the start sees the flag and takes its own down; two
        // volatile operations in each direction could each read the older value, and did.
        WorkerGeneration? generation;
        Process? process;
        lock (_lifecycle)
        {
            _aborted = true;
            generation = _generation;
            process = generation?.Process;
        }

        if (generation is null || generation.Ended.Task.IsCompleted)
        {
            return new RuntimeWorkerAbortResult(RuntimeWorkerAbortOutcome.NoWorker, null);
        }

        // THE GENERATION IS THE UNIT, NOT A PROCESS FIELD. A start that has not yet published its
        // process, or a stop already tearing the process down, is a generation that is not over; the
        // abort waits for its end inside the deadline rather than reporting nothing to abort.
        if (process is null)
        {
            return await AwaitEndAsync(generation, deadline).ConfigureAwait(false);
        }

        var processId = TryReadId(process);
        var started = Stopwatch.GetTimestamp();
        if (!await generation.Ownership.WaitAsync(deadline).ConfigureAwait(false))
        {
            // Another owner - a stop or a disposal - is tearing this generation down and did not
            // finish inside the deadline; the exit was not seen by this abort.
            return new RuntimeWorkerAbortResult(RuntimeWorkerAbortOutcome.StillRunning, processId);
        }

        try
        {
            if (generation.Ended.Task.IsCompleted)
            {
                return new RuntimeWorkerAbortResult(RuntimeWorkerAbortOutcome.Exited, processId);
            }

            generation.Owner = "abort";
            RecordGeneration(UatRuntimePhase.RetirementRequested, generation, process);
            // ONE DEADLINE FOR THE WHOLE ABORT: what the wait for ownership used comes off the kill's.
            var remaining = deadline - Stopwatch.GetElapsedTime(started);
            var exited = await KillAndObserveAsync(
                process,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
            if (exited)
            {
                End(generation, process);
            }

            return new RuntimeWorkerAbortResult(
                exited ? RuntimeWorkerAbortOutcome.Exited : RuntimeWorkerAbortOutcome.StillRunning,
                processId);
        }
        finally
        {
            generation.Ownership.Release();
        }
    }

    public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                // DISPOSED IS NOT GONE. A disposal that could not see its worker go left the
                // generation un-ended; a stop queued behind it says so rather than reporting a
                // worker stopped on the strength of the disposal having run.
                return WorkerGone ? Success(RuntimeWorkerState.Disposed) : Failure();
            }

            if (!await StopCoreAsync().ConfigureAwait(false))
            {
                // THE WORKER DID NOT GO. Said so, rather than reported stopped: the generation stays,
                // with its handle, for an abort or a later stop to try again.
                State = RuntimeWorkerState.Faulted;
                return Failure();
            }

            State = RuntimeWorkerState.Stopped;
            return Success(State);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether the current generation is over: no worker, or one whose exit was observed.</summary>
    /// <remarks>A disposal that could not see its worker go leaves the generation reachable here, for an abort.</remarks>
    internal bool WorkerGone => Volatile.Read(ref _generation) is not { } generation || generation.Ended.Task.IsCompleted;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
            State = RuntimeWorkerState.Disposed;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The one admission every automatic start goes through; the gate is held across it.</summary>
    private async Task<RuntimeWorkerResult> RestartCoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // A WORKER THAT WAS NEVER STARTED, OR WAS STOPPED ON PURPOSE, IS NOT A CRASH. Windows suspend
        // stops the worker and the next dictation brings it back; that start is the ordinary lazy one
        // the old code made and spends nothing. Every other way to be here - an exited process, a
        // Faulted state - is recovery, and recovery is what the budget bounds. The exemption lives
        // here, inside the admission, so it cannot depend on which caller reached it first.
        if (State == RuntimeWorkerState.Stopped)
        {
            return await StartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        if (_restartCount >= _maximumRestarts)
        {
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }

        _restartCount++;
        return await StartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeWorkerResult> StartCoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _aborted))
        {
            return Failure();
        }

        if (!await StopCoreAsync().ConfigureAwait(false))
        {
            // THE LAST WORKER HAS NOT BEEN SEEN TO GO. Its generation is not replaced - a replaced
            // generation would take its handle out of reach - so nothing starts until it has.
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }

        // THE GENERATION EXISTS FROM HERE, before the process does, so an abort that lands during
        // the start has something to wait for: the generation ends either with the observed exit of
        // the process it publishes or, if it never publishes one, with the start. Published under
        // the lifecycle lock with the flag read in the same breath: an abort that closed admission
        // before this saw the last, ended generation and reported no worker, and this start now
        // ends without starting anything; one that closes it after this sees this generation.
        var generation = new WorkerGeneration();
        lock (_lifecycle)
        {
            _generation = generation;
            if (_aborted)
            {
                generation.Ended.TrySetResult(true);
                return Failure();
            }
        }

        State = RuntimeWorkerState.Starting;
        if (!File.Exists(_workerExecutable))
        {
            generation.Ended.TrySetResult(true);
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }

        var startInfo = BuildWorkerStartInfo(generation);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                generation.Ended.TrySetResult(true);
                State = RuntimeWorkerState.Faulted;
                return Failure();
            }

            // Assign to a kill-on-close Windows Job Object so an abrupt parent crash never orphans
            // the runtime worker process. The handle stays open for the lifetime of the generation
            // and is disposed ONLY in End(...) after exit has been observed.
            generation.ProcessJob = WindowsProcessJob.TryCreateFor(process);

            // PUBLISHED UNDER THE SAME LOCK, WITH THE SAME READ. An abort that closed admission
            // before this found a generation with no process and is waiting for its end; this start
            // sees the flag and takes its own worker down, which ends that generation. One that
            // closes admission after this sees the process and takes it down itself.
            bool abortedMeanwhile;
            lock (_lifecycle)
            {
                generation.Process = process;
                abortedMeanwhile = _aborted;
            }

            if (abortedMeanwhile)
            {
                await StopCoreAsync().ConfigureAwait(false);
                return Failure();
            }

            ApplyProcessPriorityHint(process);

            generation.StderrDrain = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var health = await SendRequestAsync("health", timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!health.Succeeded)
            {
                await StopCoreAsync().ConfigureAwait(false);
                State = RuntimeWorkerState.Faulted;
                return Failure();
            }

            State = RuntimeWorkerState.Ready;
            return Success(State);
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidOperationException)
        {
            if (generation.Process is null)
            {
                // The process never started: nothing to observe, the generation ends with the start.
                generation.ProcessJob?.Dispose();
                generation.ProcessJob = null;
                process.Dispose();
                generation.Ended.TrySetResult(true);
            }

            await StopCoreAsync().ConfigureAwait(false);
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }
    }

    private async Task<RuntimeWorkerResult> CheckHealthCoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (LiveProcess() is null || State is not RuntimeWorkerState.Ready)
        {
            // A PROBE DOES NOT TURN A DELIBERATE STOP INTO A FAULT. Stopped means never started or
            // stopped on purpose; a health check finding no process there reports failure and leaves
            // the reason alone, so the lazy start that follows is still the free one.
            if (State != RuntimeWorkerState.Stopped)
            {
                State = RuntimeWorkerState.Faulted;
            }

            return Failure();
        }

        var result = await SendRequestAsync("health", timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }

        State = RuntimeWorkerState.Ready;
        return Success(State);
    }

    private static void ValidateTimeout(TimeSpan timeout) =>
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

    private static RuntimeWorkerResult Success(RuntimeWorkerState state) => new(true, state);

    private RuntimeWorkerResult Failure() => new(
        Succeeded: false,
        Volatile.Read(ref _aborted) ? RuntimeWorkerState.Aborted : RuntimeWorkerState.Faulted,
        new AppError(
            AppErrorCode.RuntimeWorkerFailed,
            AppErrorStage.RuntimeWorker,
            CanRetry: true));
}
