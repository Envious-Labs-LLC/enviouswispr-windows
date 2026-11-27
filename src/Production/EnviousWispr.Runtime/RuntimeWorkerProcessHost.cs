using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Runtime;
using System.ComponentModel;
using System.Diagnostics;

namespace EnviousWispr.Services.Runtime;

public sealed partial class RuntimeWorkerSupervisor
{
    private ProcessStartInfo BuildWorkerStartInfo(WorkerGeneration generation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _workerExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in _workerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (_phaseJournal is not null)
        {
            generation.DiagnosticId = Guid.NewGuid();
            foreach (var entry in _phaseJournal.ChildEnvironment(generation.DiagnosticId.Value))
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        return startInfo;
    }

    private void ApplyProcessPriorityHint(Process process)
    {
        if (_processPriority is not { } priority)
        {
            return;
        }

        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Priority is a best-effort hint; worker isolation and correctness do not depend on it.
        }
    }

    /// <summary>Ends the current generation: asks the worker to leave, kills it if it does not, and observes the exit.</summary>
    /// <remarks>
    /// OWNERSHIP IS TAKEN, NOT ASSUMED. The generation's ownership is what an abort takes too; a stop
    /// that finds it taken waits its turn rather than touching the same handle. A process whose exit
    /// was not observed is not disposed: its handle stays with the generation so a later abort or
    /// stop can try again, and nothing reads a closed handle as a gone worker.
    /// </remarks>
    private async Task<bool> StopCoreAsync()
    {
        var generation = Volatile.Read(ref _generation);
        if (generation is null || generation.Ended.Task.IsCompleted)
        {
            return true;
        }

        if (generation.Process is not { } process)
        {
            // A start that has not published a process yet ends its own generation.
            return true;
        }

        await generation.Ownership.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation.Ended.Task.IsCompleted)
            {
                return true;
            }

            generation.Owner = "stop";
            RecordGeneration(UatRuntimePhase.RetirementRequested, generation, process);
            if (IsAlive(process))
            {
                await TryRequestShutdownAsync(process).ConfigureAwait(false);
            }

            var exited = await KillAndObserveAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false) ||
                await KillAndObserveAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (exited)
            {
                End(generation, process);
                await DrainStderrAsync(generation).ConfigureAwait(false);
            }

            return exited;
        }
        finally
        {
            generation.Ownership.Release();
        }
    }

    /// <summary>Kills the process if it is still there and waits, up to the deadline, to see it exit. True only when the exit was observed.</summary>
    private static async Task<bool> KillAndObserveAsync(Process process, TimeSpan deadline)
    {
        try
        {
            if (IsAlive(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // THE KILL COULD NOT BE ASKED FOR - already gone, or refused. Neither is an exit; the
            // wait below is the only thing that says whether the worker is gone.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(deadline, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return !IsAlive(process);
        }
        catch (InvalidOperationException)
        {
            // No process is associated: it was never started, so there is nothing left to observe.
            return true;
        }
    }

    /// <summary>The generation's exit was observed: the handle is closed, once, and the end published.</summary>
    private void End(WorkerGeneration generation, Process process)
    {
        int? observedExitCode = null;
        try
        {
            if (process.HasExited)
            {
                observedExitCode = process.ExitCode;
                if (_phaseJournal is not null)
                {
                    RecordGeneration(UatRuntimePhase.WorkerExitObserved, generation, process, exitCode: observedExitCode);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // No observed exit facts, no exit marker. Disposal behavior remains the existing owner's.
        }

        if (observedExitCode is { } exitCode &&
            exitCode != 0 &&
            generation.Owner is not ("abort" or "stop"))
        {
            try
            {
                AbnormalWorkerExitObserved?.Invoke(exitCode);
            }
            catch
            {
                // Telemetry limb cannot affect worker teardown.
            }
        }

        process.Dispose();
        generation.ProcessJob?.Dispose();
        generation.ProcessJob = null;
        Interlocked.Increment(ref _handlesClosed);
        generation.Ended.TrySetResult(true);
    }

    private static async Task<RuntimeWorkerAbortResult> AwaitEndAsync(WorkerGeneration generation, TimeSpan deadline)
    {
        try
        {
            await generation.Ended.Task.WaitAsync(deadline).ConfigureAwait(false);
            return new RuntimeWorkerAbortResult(RuntimeWorkerAbortOutcome.Exited, TryReadId(generation.Process));
        }
        catch (TimeoutException)
        {
            return new RuntimeWorkerAbortResult(RuntimeWorkerAbortOutcome.StillRunning, TryReadId(generation.Process));
        }
    }

    private static async Task DrainStderrAsync(WorkerGeneration generation)
    {
        if (generation.StderrDrain is not { } drain)
        {
            return;
        }

        try
        {
            await drain.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or TimeoutException)
        {
            // Stderr is intentionally discarded and cannot block teardown.
        }

        generation.StderrDrain = null;
    }

    /// <summary>The current generation's process, if it has one and it is alive.</summary>
    private Process? LiveProcess() =>
        Volatile.Read(ref _generation)?.Process is { } process && IsAlive(process) ? process : null;

    /// <summary>Whether the process is still there, without throwing for one that is gone or closed.</summary>
    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int? TryReadId(Process? process)
    {
        try
        {
            return process?.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void RecordGeneration(
        UatRuntimePhase phase,
        WorkerGeneration generation,
        Process process,
        UatRequestIdentity? identity = null,
        int? exitCode = null)
    {
        if (_phaseJournal is null || generation.DiagnosticId is not { } id)
        {
            return;
        }

        identity ??= Volatile.Read(ref generation.LastTranscription);
        _phaseJournal.Record(phase, identity?.SessionId, identity?.RequestId, id, TryReadId(process), exitCode);
    }

    /// <summary>One worker's lifetime: from the start that creates it to the exit that is observed, whoever observes it.</summary>
    /// <remarks>
    /// OWNERSHIP IS A SEMAPHORE, THE END IS A PROMISE. Whoever holds the ownership - a stop, a
    /// disposal, an abort - is the one touching the process; the others wait. The end is published
    /// only once the exit has been observed and the handle closed, so a handle is closed exactly
    /// once and a generation nobody has seen end is never reported as gone.
    /// </remarks>
    private sealed class WorkerGeneration
    {
        public Guid? DiagnosticId { get; set; }

        public UatRequestIdentity? LastTranscription;

        private Process? _process;

        public Process? Process
        {
            get => Volatile.Read(ref _process);
            set => Volatile.Write(ref _process, value);
        }

        public Task<string>? StderrDrain { get; set; }

        public WindowsProcessJob? ProcessJob { get; set; }

        /// <summary>Who took the teardown - for the tests, which need to know which owner did the work.</summary>
        public string? Owner { get; set; }

        public SemaphoreSlim Ownership { get; } = new(1, 1);

        public TaskCompletionSource<bool> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
