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
    public async Task AnExplicitlyStoppedWorkerRestartsLazilyWithoutSpendingTheBudget()
    {
        // Windows suspend stops the worker; the next dictation brings it back. That is not a crash and
        // must leave the whole budget for one, so a crash after it still recovers.
        await using var supervisor = new RuntimeWorkerSupervisor(WorkerPath(), maximumRestarts: 1);
        var observedProcessIds = new List<int>();
        Assert.True((await supervisor.StartAsync(RequestTimeout)).Succeeded);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.True((await supervisor.StopAsync()).Succeeded);
        Assert.Equal(RuntimeWorkerState.Stopped, supervisor.State);

        var response = await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout);
        Assert.NotNull(response);
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(2, observedProcessIds.Count);

        await KillWorkerAsync(supervisor);
        Assert.NotNull(await supervisor.TranscribeAsync(TranscriptionRequest(), RequestTimeout));
        ObserveReadyWorker(supervisor, observedProcessIds);
        Assert.Equal(3, observedProcessIds.Count);
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
