using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Runtime;
using System.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

public sealed class RuntimeWorkerSupervisorTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WorkerHandshakeCrashRecoveryAndTeardownAreIsolated()
    {
        var workerPath = WorkerPath();
        var supervisor = new RuntimeWorkerSupervisor(workerPath, maximumRestarts: 1);

        var started = await supervisor.StartAsync(TimeSpan.FromSeconds(2));
        var firstProcessId = supervisor.WorkerProcessId;
        var health = await supervisor.CheckHealthAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(firstProcessId);
        using (var worker = Process.GetProcessById(firstProcessId.Value))
        {
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync();
        }

        var recovered = await supervisor.EnsureHealthyAsync(TimeSpan.FromSeconds(2));
        var secondProcessId = supervisor.WorkerProcessId;
        await supervisor.DisposeAsync();

        Assert.True(started.Succeeded);
        Assert.True(health.Succeeded);
        Assert.True(recovered.Succeeded);
        Assert.NotNull(secondProcessId);
        Assert.NotEqual(firstProcessId, secondProcessId);
        Assert.Equal(RuntimeWorkerState.Disposed, supervisor.State);
        Assert.Null(supervisor.WorkerProcessId);
        AssertProcessIsGone(secondProcessId.Value);
    }

    [Fact]
    public async Task StartupTimeoutKillsWedgedWorkerAndReturnsTypedFailure()
    {
        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            // Keep the simulated wedge far beyond the timeout even on a loaded CI runner,
            // where timer callbacks can be delayed long enough for a one-second worker to win.
            ["--health-delay-ms", "10000"],
            maximumRestarts: 0);

        var result = await supervisor.StartAsync(TimeSpan.FromMilliseconds(50));
        await supervisor.DisposeAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeWorkerState.Faulted, result.State);
        Assert.Equal(AppErrorCode.RuntimeWorkerFailed, result.Error?.Code);
        Assert.Null(supervisor.WorkerProcessId);
    }

    [Fact]
    public async Task AStartCancelledDuringTheHealthWaitLeavesTheProcessForStopToKill()
    {
        // Step 8 makes a cancelled preview start reachable. The supervisor lets the caller's cancel
        // out of its health wait without touching the process: the worker is alive, the state is
        // Starting, and the gate is free. The stop that the preview controller issues next is what
        // takes the process down. This proves that stop does, on the real worker.
        // DISPOSED IN A FINALLY, AND ONLY AFTER THE ASSERTIONS. Disposal stops the worker too, so a
        // disposal before the check would let a stop that stopped nothing pass on disposal's work.
        // The worker is held as a Process object from the moment its id is known, so the exit that is
        // asserted is that process's and not a reused id's.
        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--health-delay-ms", "10000"],
            maximumRestarts: 0);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var start = supervisor.StartAsync(TimeSpan.FromSeconds(30), cancellation.Token);
            await WaitForAsync(() => supervisor.WorkerProcessId is not null, TimeSpan.FromSeconds(10));
            var processId = supervisor.WorkerProcessId!.Value;
            using var worker = Process.GetProcessById(processId);
            // PINNED BY HANDLE, NOT BY ID. A Process found by id holds no handle until one is asked
            // for; every later question would reopen the id, which the system may have handed to
            // something else once the worker is gone. Asking for the handle now keeps it.
            _ = worker.SafeHandle;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(RuntimeWorkerState.Starting, supervisor.State);
            Assert.Equal(processId, supervisor.WorkerProcessId);
            Assert.False(worker.HasExited, "the cancelled start left the worker running for the stop to take down");

            var stopped = await supervisor.StopAsync();

            Assert.True(stopped.Succeeded);
            Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);
            Assert.Null(supervisor.WorkerProcessId);
            Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(10)), "the stop after a cancelled start killed the worker it left behind");
        }
        finally
        {
            await supervisor.DisposeAsync();
        }
    }

    [Fact]
    public async Task AbortUnblocksWedgedRequest()
    {
        // A TRANSCRIPTION THE WORKER WILL NOT ANSWER holds the request gate for as long as its timeout.
        // The abort does not wait for that gate: it kills the worker of the generation in flight and
        // observes its exit, the wedged request ends as a failed one, and the gate is free again.
        await using var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--test-transcribe-stub", "--transcribe-delay-ms", "60000"],
            maximumRestarts: 1);
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        var processId = supervisor.WorkerProcessId!.Value;
        using var worker = Process.GetProcessById(processId);
        _ = worker.SafeHandle;
        var wedged = supervisor.TranscribeAsync(TranscriptionRequest(), TimeSpan.FromMinutes(2));
        await WaitForAsync(() => supervisor.State == RuntimeWorkerState.Ready && !wedged.IsCompleted, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.False(wedged.IsCompleted, "the request is wedged behind the worker's delay");

        var abort = await supervisor.AbortAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, abort.Outcome);
        Assert.Equal(processId, abort.WorkerProcessId);
        Assert.True(worker.HasExited, "the exit the abort reports is the worker's own");
        Assert.Null(await wedged.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(RuntimeWorkerState.Aborted, supervisor.State);
        Assert.Null(supervisor.WorkerProcessId);
        // The gate is free: a further call returns at once rather than waiting on the old request.
        Assert.Null(await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AbortCannotRestartWorker()
    {
        // TERMINAL. After the abort no start, health repair or lazy request recovery brings a worker
        // back - not with budget to spare, not with an explicit start - and the state says so.
        await using var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--test-transcribe-stub"],
            maximumRestarts: 3);
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        var processId = supervisor.WorkerProcessId!.Value;

        var abort = await supervisor.AbortAsync(TimeSpan.FromSeconds(10));
        var explicitStart = await supervisor.StartAsync(RequestTimeout);
        var repair = await supervisor.EnsureHealthyAsync(RequestTimeout);
        var request = await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout);
        var health = await supervisor.CheckHealthAsync(RequestTimeout);

        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, abort.Outcome);
        AssertProcessIsGone(processId);
        Assert.False(explicitStart.Succeeded);
        Assert.Equal(RuntimeWorkerState.Aborted, explicitStart.State);
        Assert.False(repair.Succeeded);
        Assert.Equal(RuntimeWorkerState.Aborted, repair.State);
        Assert.Null(request);
        Assert.False(health.Succeeded);
        Assert.Equal(RuntimeWorkerState.Aborted, supervisor.State);
        Assert.Null(supervisor.WorkerProcessId);
        Assert.Equal(RuntimeWorkerAbortOutcome.NoWorker, (await supervisor.AbortAsync(TimeSpan.FromSeconds(1))).Outcome);
    }

    [Fact]
    public async Task ConcurrentAbortAndDisposeCloseHandlesOnce()
    {
        // THE ABORT AND THE DISPOSAL RACE FOR THE SAME WORKER. Whichever takes the process out of
        // the field owns its handle; the other finds nothing. Both complete, neither throws, the
        // worker's exit is observed, and the disposal that queued behind the wedged request is let
        // through by the abort rather than waiting the request's two minutes.
        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--test-transcribe-stub", "--transcribe-delay-ms", "60000"],
            maximumRestarts: 1);
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        var processId = supervisor.WorkerProcessId!.Value;
        using var worker = Process.GetProcessById(processId);
        _ = worker.SafeHandle;
        var wedged = supervisor.TranscribeAsync(TranscriptionRequest(), TimeSpan.FromMinutes(2));
        await Task.Delay(300);
        Assert.False(wedged.IsCompleted);

        var dispose = Task.Run(async () => await supervisor.DisposeAsync());
        var abort = Task.Run(() => supervisor.AbortAsync(TimeSpan.FromSeconds(10)));
        await Task.WhenAll(dispose, abort).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, (await abort).Outcome);
        Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(10)), "the worker's exit was observed");
        Assert.Null(await wedged.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(RuntimeWorkerState.Disposed, supervisor.State);
        Assert.Null(supervisor.WorkerProcessId);
        Assert.Equal(1, supervisor.HandlesClosed);
        // Once more of each, on a supervisor with nothing left: no throw, nothing to abort.
        await supervisor.DisposeAsync();
        Assert.Equal(RuntimeWorkerAbortOutcome.NoWorker, (await supervisor.AbortAsync(TimeSpan.FromSeconds(1))).Outcome);
        Assert.Equal(1, supervisor.HandlesClosed);
    }

    [Fact]
    public async Task AnAbortDuringADisposalAlreadyTearingTheWorkerDownWaitsForThatEndAndClosesNothingItself()
    {
        // THE OTHER ORDERING: the disposal owns the generation first - its graceful shutdown request
        // is honoured slowly by this worker - and the abort arrives while it is tearing the worker
        // down. The abort does not touch the handle; it waits for the disposal's observed end inside
        // its deadline and reports the exit. One handle closed, by the disposal.
        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--shutdown-delay-ms", "3000"],
            maximumRestarts: 1);
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        var processId = supervisor.WorkerProcessId!.Value;
        using var worker = Process.GetProcessById(processId);
        _ = worker.SafeHandle;

        var dispose = Task.Run(async () => await supervisor.DisposeAsync());
        await Task.Delay(150);
        var abort = await supervisor.AbortAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));
        await dispose.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, abort.Outcome);
        Assert.Equal(processId, abort.WorkerProcessId);
        Assert.True(worker.HasExited);
        Assert.Equal(1, supervisor.HandlesClosed);
        Assert.Equal(RuntimeWorkerState.Disposed, supervisor.State);
    }

    [Fact]
    public async Task AnAbortOfAWorkerAlreadyGoneObservesTheExitRatherThanAssumingIt()
    {
        // THE WORKER DIED ON ITS OWN before the abort. The kill has nothing to ask for; the exit is
        // still observed on the handle, not inferred from the kill's refusal, and the handle is
        // closed once.
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 1);
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        var processId = supervisor.WorkerProcessId!.Value;
        using (var worker = Process.GetProcessById(processId))
        {
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(RequestTimeout);
        }

        var abort = await supervisor.AbortAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, abort.Outcome);
        Assert.Equal(processId, abort.WorkerProcessId);
        Assert.Equal(1, supervisor.HandlesClosed);
        Assert.Equal(RuntimeWorkerState.Aborted, supervisor.State);
    }

    [Fact]
    public async Task AnAbortDuringAStartWaitsForThatGenerationToEndAndNoWorkerOutlivesIt()
    {
        // THE START IS IN FLIGHT - the worker is up and its health answer is slow - when the abort
        // lands. The generation exists, the abort waits for its end inside the deadline, the start
        // ends it by taking its own worker down, and nothing is left running: not that worker, and
        // no worker started after it.
        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--health-delay-ms", "10000"],
            maximumRestarts: 1);
        try
        {
            var start = supervisor.StartAsync(TimeSpan.FromSeconds(30));
            await WaitForAsync(() => supervisor.WorkerProcessId is not null, TimeSpan.FromSeconds(10));
            var processId = supervisor.WorkerProcessId!.Value;
            using var worker = Process.GetProcessById(processId);
            _ = worker.SafeHandle;

            var abort = await supervisor.AbortAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));
            var started = await start.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(RuntimeWorkerAbortOutcome.Exited, abort.Outcome);
            Assert.Equal(processId, abort.WorkerProcessId);
            Assert.True(worker.HasExited, "the worker of the generation the abort found is gone");
            Assert.False(started.Succeeded);
            Assert.Equal(RuntimeWorkerState.Aborted, started.State);
            Assert.Null(supervisor.WorkerProcessId);
            Assert.Equal(1, supervisor.HandlesClosed);
            Assert.False((await supervisor.StartAsync(RequestTimeout)).Succeeded);
            Assert.Null(supervisor.WorkerProcessId);
        }
        finally
        {
            await supervisor.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheAbortReachesTheWorkerThroughBothProductionAdapters()
    {
        // THE ADAPTERS THE SHELL HOLDS, over the real worker. The transcription engine's abort ends a
        // wedged transcription; the preview engine's abort ends its worker and lets go of the
        // resource it held - and only because the exit was observed.
        var transcriptionSupervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--test-transcribe-stub", "--transcribe-delay-ms", "60000"],
            maximumRestarts: 1);
        await using var engine = new RuntimeWorkerTranscriptionEngine(
            transcriptionSupervisor,
            "test:isolated",
            transcriptionTimeout: TimeSpan.FromMinutes(2));
        Assert.True((await engine.StartAsync()).Succeeded);
        var wedged = engine.TranscribeAsync(new CapturedAudio(DictationSessionId.Create(), new float[16_000], 16_000, 1));
        await Task.Delay(300);
        Assert.False(wedged.IsCompleted);

        var abort = await engine.AbortAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, abort.Outcome);
        await Assert.ThrowsAnyAsync<Exception>(() => wedged.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(engine.WorkerProcessId);
        Assert.Equal(1, transcriptionSupervisor.HandlesClosed);

        using var arbiter = new RuntimeResourceArbiter();
        var previewSupervisor = new RuntimeWorkerSupervisor(WorkerPath(), ["--test-transcribe-stub"], maximumRestarts: 1);
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            new RuntimeWorkerTranscriptionEngine(previewSupervisor, "test:isolated"),
            arbiter,
            RuntimeResourceKind.Cpu);
        Assert.True((await preview.StartAsync()).Succeeded);
        var previewProcessId = previewSupervisor.WorkerProcessId!.Value;
        Assert.False((await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero)).Succeeded);

        var previewAbort = await preview.AbortAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));
        var afterAbort = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);

        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, previewAbort.Outcome);
        Assert.Equal(previewProcessId, previewAbort.WorkerProcessId);
        AssertProcessIsGone(previewProcessId);
        Assert.True(afterAbort.Succeeded, "the resource the preview held is free once its worker is seen gone");
        await afterAbort.Lease!.DisposeAsync();
    }

    /// <summary>The one poll in this file: the supervisor exposes the process id and nothing else about its start.</summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan patience)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < patience, "the condition was not met in time");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task LazyTranscriptionRecoveryCannotExceedCrashLoopBudget()
    {
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 1);
        var observedProcessIds = new List<int>();
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        await KillWorkerAsync(supervisor);

        // Without a model, transcription fails but the replacement stays alive. A failed
        // request must not replenish the budget, even if subsequent health checks pass.
        var response = await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout);
        Assert.Equal("failed", response?.Status);
        Assert.Equal(AppErrorCode.TranscriptionFailed, response?.Error?.Code);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(2, observedProcessIds.Count);
        Assert.NotEqual(observedProcessIds[0], observedProcessIds[1]);
        Assert.True((await supervisor.EnsureHealthyAsync(RequestTimeout)).Succeeded);
        Assert.Equal(observedProcessIds[1], supervisor.WorkerProcessId);
        await KillWorkerAsync(supervisor);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout));
            Assert.Null(supervisor.WorkerProcessId);
            Assert.Equal(RuntimeWorkerState.Faulted, supervisor.State);
            AssertWorkerFailure(await supervisor.EnsureHealthyAsync(RequestTimeout));
            Assert.Null(supervisor.WorkerProcessId);
        }

        Assert.Equal(2, observedProcessIds.Count);
    }

    [Fact]
    public async Task ZeroBudgetDeniesAutomaticRecoveryUntilExplicitStart()
    {
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 0);
        var observedProcessIds = new List<int>();
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        await KillWorkerAsync(supervisor);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout));
            Assert.Null(supervisor.WorkerProcessId);
            Assert.Equal(RuntimeWorkerState.Faulted, supervisor.State);
            AssertWorkerFailure(await supervisor.EnsureHealthyAsync(RequestTimeout));
            Assert.Null(supervisor.WorkerProcessId);
        }

        Assert.Single(observedProcessIds);
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(2, observedProcessIds.Count);
        Assert.NotEqual(observedProcessIds[0], observedProcessIds[1]);
    }

    [Fact]
    public async Task AnExplicitlyStoppedWorkerRestartsLazilyWithoutSpendingOrReplenishingTheBudget()
    {
        // Windows suspend stops the worker; the next dictation brings it back. That is not a crash and
        // must leave the budget exactly as it was: a fresh one still has its allowance afterwards, and a
        // spent one does not get it back merely by being stopped and started for free.
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 1);
        var observedProcessIds = new List<int>();
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.True((await supervisor.StopAsync()).Succeeded);
        Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);

        // A probe on a deliberately stopped worker reports failure and leaves it Stopped.
        Assert.False((await supervisor.CheckHealthAsync(RequestTimeout)).Succeeded);
        Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);

        var response = await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout);
        Assert.Equal("failed", response?.Status);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(2, observedProcessIds.Count);

        // The free start spent nothing: one crash still recovers.
        await KillWorkerAsync(supervisor);
        Assert.Equal("failed", (await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout))?.Status);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(3, observedProcessIds.Count);

        // The allowance is now spent. Stop on purpose, start for free, crash: still denied.
        Assert.True((await supervisor.StopAsync()).Succeeded);
        Assert.Equal("failed", (await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout))?.Status);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(4, observedProcessIds.Count);
        await KillWorkerAsync(supervisor);
        Assert.Null(await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout));
        AssertWorkerFailure(await supervisor.EnsureHealthyAsync(RequestTimeout));
        Assert.Null(supervisor.WorkerProcessId);
        Assert.Equal(4, observedProcessIds.Distinct().Count());
    }

    [Fact]
    public async Task ANeverStartedWorkerWithZeroBudgetStillStartsLazily()
    {
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 0);
        Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);
        Assert.False((await supervisor.CheckHealthAsync(RequestTimeout)).Succeeded);
        Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);

        Assert.Equal("failed", (await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout))?.Status);
        Assert.Equal(RuntimeWorkerState.Ready, supervisor.State);
        Assert.NotNull(supervisor.WorkerProcessId);
    }

    [Fact]
    public async Task AnExplicitStartOnAHealthyReplacementStillResetsTheBudget()
    {
        // Choosing an engine again, or the app relaunching a runtime, is a new lifetime even when the
        // worker it finds is the replacement from the last crash and perfectly healthy.
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 1);
        var observedProcessIds = new List<int>();
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        await KillWorkerAsync(supervisor);
        Assert.True((await supervisor.EnsureHealthyAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);

        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        Assert.Equal(observedProcessIds[1], supervisor.WorkerProcessId);

        await KillWorkerAsync(supervisor);
        Assert.True((await supervisor.EnsureHealthyAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(3, observedProcessIds.Distinct().Count());
    }

    [Fact]
    public async Task SuccessfulTranscriptionReplenishesCrashLoopBudget()
    {
        await using var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(), ["--test-transcribe-stub"], maximumRestarts: 1);
        var observedProcessIds = new List<int>();
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        await KillWorkerAsync(supervisor);

        Assert.True((await supervisor.EnsureHealthyAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(2, observedProcessIds.Count);
        var request = TranscriptionRequest();
        var response = await supervisor.TranscribeAsync(request, RequestTimeout);
        Assert.Equal("complete", response?.Status);
        Assert.NotNull(response?.Transcript);
        Assert.Equal(request.SessionId, response.Transcript.SessionId);
        Assert.Equal(string.Empty, response.Transcript.Text);
        Assert.Equal("test-transcribe-stub", response.Transcript.EngineId);
        Assert.Null(response.Error);
        Assert.Equal(observedProcessIds[1], supervisor.WorkerProcessId);
        await KillWorkerAsync(supervisor);

        Assert.True((await supervisor.EnsureHealthyAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(3, observedProcessIds.Count);
        Assert.Equal(3, observedProcessIds.Distinct().Count());

        // The replenished budget is still bounded if no further request completes.
        await KillWorkerAsync(supervisor);
        AssertWorkerFailure(await supervisor.EnsureHealthyAsync(RequestTimeout));
        Assert.Null(supervisor.WorkerProcessId);
    }

    [Fact]
    public async Task ExplicitStartAfterExhaustionResetsCrashLoopBudget()
    {
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 1);
        var observedProcessIds = new List<int>();
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        await KillWorkerAsync(supervisor);
        Assert.True((await supervisor.EnsureHealthyAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        await KillWorkerAsync(supervisor);
        AssertWorkerFailure(await supervisor.EnsureHealthyAsync(RequestTimeout));
        Assert.Null(supervisor.WorkerProcessId);

        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        await KillWorkerAsync(supervisor);
        Assert.True((await supervisor.EnsureHealthyAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(4, observedProcessIds.Count);
        Assert.Equal(4, observedProcessIds.Distinct().Count());
        await KillWorkerAsync(supervisor);
        AssertWorkerFailure(await supervisor.EnsureHealthyAsync(RequestTimeout));
        Assert.Null(supervisor.WorkerProcessId);
    }

    private static RuntimeWorkerTranscriptionRequest TranscriptionRequest() => new(
        Guid.NewGuid(), "unused-test-audio", SampleCount: 1);

    private static void ObserveReadyWorker(RuntimeWorkerSupervisor supervisor, List<int> processIds)
    {
        Assert.Equal(RuntimeWorkerState.Ready, supervisor.State);
        Assert.NotNull(supervisor.WorkerProcessId);
        processIds.Add(supervisor.WorkerProcessId.Value);
    }

    private static async Task KillWorkerAsync(RuntimeWorkerSupervisor supervisor)
    {
        Assert.NotNull(supervisor.WorkerProcessId);
        using var worker = Process.GetProcessById(supervisor.WorkerProcessId.Value);
        Assert.Equal(WorkerPath(), worker.MainModule?.FileName, ignoreCase: true);
        worker.Kill(entireProcessTree: true);
        await worker.WaitForExitAsync().WaitAsync(RequestTimeout);
        Assert.True(worker.HasExited);
        Assert.Null(supervisor.WorkerProcessId);
    }

    private static void AssertWorkerFailure(RuntimeWorkerResult result)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeWorkerState.Faulted, result.State);
        Assert.Equal(AppErrorCode.RuntimeWorkerFailed, result.Error?.Code);
    }

    private static string WorkerPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        Assert.True(File.Exists(path), $"Worker apphost missing: {path}");
        return path;
    }

    private static void AssertProcessIsGone(int processId) =>
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
}
