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
        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--health-delay-ms", "10000"],
            maximumRestarts: 0);
        using var cancellation = new CancellationTokenSource();

        var start = supervisor.StartAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        await WaitForAsync(() => supervisor.WorkerProcessId is not null, TimeSpan.FromSeconds(10));
        var processId = supervisor.WorkerProcessId!.Value;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(RuntimeWorkerState.Starting, supervisor.State);
        Assert.Equal(processId, supervisor.WorkerProcessId);
        using (var worker = Process.GetProcessById(processId))
        {
            Assert.False(worker.HasExited, "the cancelled start left the worker running for the stop to take down");
        }

        var stopped = await supervisor.StopAsync();
        await supervisor.DisposeAsync();

        Assert.True(stopped.Succeeded);
        Assert.Null(supervisor.WorkerProcessId);
        Assert.True(HasExited(processId), "the stop after a cancelled start killed the worker it left behind");
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

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
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
