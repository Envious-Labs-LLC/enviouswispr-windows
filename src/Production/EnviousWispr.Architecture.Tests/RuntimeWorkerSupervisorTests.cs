using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Runtime;
using System.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

public sealed class RuntimeWorkerSupervisorTests
{
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
            ["--health-delay-ms", "1000"],
            maximumRestarts: 0);

        var result = await supervisor.StartAsync(TimeSpan.FromMilliseconds(50));
        await supervisor.DisposeAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeWorkerState.Faulted, result.State);
        Assert.Equal(AppErrorCode.RuntimeWorkerFailed, result.Error?.Code);
        Assert.Null(supervisor.WorkerProcessId);
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
