using System.Diagnostics;
using System.Text.Json;
using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Services.Runtime;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var modelDirectory = Path.Combine(repositoryRoot, "models", "parakeet-tdt-0.6b-v3");
var workerExecutable = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");

var hardware = await new WindowsHardwareDiscovery().ProbeAsync();
var models = new LocalParakeetModelProbe().Probe(modelDirectory);
var selection = ParakeetRuntimeSelector.Select(hardware, models);

var workerStarted = false;
var workerRecovered = false;
var workerStopped = false;
await using (var supervisor = new RuntimeWorkerSupervisor(workerExecutable, maximumRestarts: 1))
{
    var start = await supervisor.StartAsync(TimeSpan.FromSeconds(2));
    workerStarted = start.Succeeded && supervisor.WorkerProcessId is not null;
    var originalProcessId = supervisor.WorkerProcessId;
    if (originalProcessId is not null)
    {
        using var worker = Process.GetProcessById(originalProcessId.Value);
        worker.Kill(entireProcessTree: true);
        await worker.WaitForExitAsync();
        var recovery = await supervisor.EnsureHealthyAsync(TimeSpan.FromSeconds(2));
        workerRecovered = recovery.Succeeded &&
            supervisor.WorkerProcessId is { } recoveredProcessId &&
            recoveredProcessId != originalProcessId.Value;
    }

    var activeProcessId = supervisor.WorkerProcessId;
    var stop = await supervisor.StopAsync();
    workerStopped = stop.Succeeded &&
        activeProcessId is not null &&
        !IsProcessRunning(activeProcessId.Value);
}

var timeoutRejected = false;
await using (var delayedSupervisor = new RuntimeWorkerSupervisor(
    workerExecutable,
    ["--health-delay-ms", "1000"],
    maximumRestarts: 0))
{
    var delayedStart = await delayedSupervisor.StartAsync(TimeSpan.FromMilliseconds(100));
    timeoutRejected = !delayedStart.Succeeded &&
        delayedStart.State == RuntimeWorkerState.Faulted &&
        delayedSupervisor.WorkerProcessId is null;
}

var previewBlocksFinal = false;
var finalRunsAfterRelease = false;
using (var arbiter = new RuntimeResourceArbiter())
{
    var preview = await arbiter.AcquireAsync(
        RuntimeResourceKind.Accelerator,
        RuntimeWorkloadKind.LivePreview,
        TimeSpan.FromSeconds(1));
    if (preview.Lease is not null)
    {
        var blockedFinal = await arbiter.AcquireAsync(
            RuntimeResourceKind.Accelerator,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.FromMilliseconds(50));
        previewBlocksFinal = !blockedFinal.Succeeded;
        await preview.Lease.DisposeAsync();
        var allowedFinal = await arbiter.AcquireAsync(
            RuntimeResourceKind.Accelerator,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.FromSeconds(1));
        finalRunsAfterRelease = allowedFinal.Succeeded;
        if (allowedFinal.Lease is not null)
        {
            await allowedFinal.Lease.DisposeAsync();
        }
    }
}

var summary = new
{
    hardware = new
    {
        status = hardware.Status.ToString(),
        architecture = hardware.Architecture.ToString(),
        processorVendor = hardware.ProcessorVendor.ToString(),
        physicalCores = hardware.PhysicalCoreCount,
        logicalProcessors = hardware.LogicalProcessorCount,
        physicalMemoryGiB = Math.Round(hardware.TotalPhysicalMemoryBytes / 1024d / 1024d / 1024d, 1),
        graphicsVendors = hardware.GraphicsAdapters.Select(adapter => adapter.Vendor.ToString()).ToArray(),
        directMlRuntime = hardware.IsDirectMlRuntimeAvailable,
        cudaAvailable = hardware.Cuda.IsDriverAvailable,
        cudaDevices = hardware.Cuda.DeviceCount,
        cudaDriverVersion = hardware.Cuda.DriverVersion,
    },
    models = new
    {
        int8Complete = models.Int8Complete,
        fp32Complete = models.Fp32Complete,
    },
    selection = new
    {
        succeeded = selection.Succeeded,
        provider = selection.Provider?.ToString(),
        modelPack = selection.ModelPack?.ToString(),
        reason = selection.Reason.ToString(),
        intraOpThreads = selection.IntraOpThreads,
        interOpThreads = selection.InterOpThreads,
    },
    worker = new
    {
        started = workerStarted,
        recoveredAfterCrash = workerRecovered,
        stoppedCleanly = workerStopped,
        startupTimeoutRejected = timeoutRejected,
    },
    resources = new
    {
        previewBlocksFinal,
        finalRunsAfterRelease,
    },
};

Console.WriteLine(JsonSerializer.Serialize(summary));
return hardware.Status == HardwareProbeStatus.Complete &&
    selection.Succeeded &&
    workerStarted &&
    workerRecovered &&
    workerStopped &&
    timeoutRejected &&
    previewBlocksFinal &&
    finalRunsAfterRelease
    ? 0
    : 5;

static string FindRepositoryRoot(string startDirectory)
{
    var directory = new DirectoryInfo(startDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new DirectoryNotFoundException(
        "The repository root could not be located.");
}

static bool IsProcessRunning(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}
