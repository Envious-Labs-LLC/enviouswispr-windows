using System.Diagnostics;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Lifecycle;
using EnviousWispr.Services.Reliability;
using EnviousWispr.Services.Settings;

var iterations = ParseIterations(args);
var tempDirectory = Path.Combine(
    Path.GetTempPath(),
    $"EnviousWispr-reliability-uat-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDirectory);
var process = Process.GetCurrentProcess();
var baselineHandles = 0;
var stopwatch = Stopwatch.StartNew();
var interruptedRunsRecovered = 0;
var encryptedTextsRecovered = 0;
var activationsReceived = 0;

try
{
    await ValidateCorruptSettingsRecoveryAsync(tempDirectory);
    ValidateResourceFaultPolicies();

    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var runStatePath = Path.Combine(tempDirectory, "run-state.json");
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(iteration * 3L);
        Guid runId;
        using (var runStore = new JsonApplicationRunStateStore(runStatePath))
        {
            var started = await runStore.BeginRunAsync(timestamp);
            Require(started.Status == RunStateLoadStatus.Started, "A clean cycle did not start cleanly.");
            runId = started.RunId;
            Require(
                await runStore.HeartbeatAsync(runId, timestamp.AddSeconds(1)),
                "A run-state heartbeat was not persisted.");
            if ((iteration & 1) == 1)
            {
                Require(
                    await runStore.CompleteRunAsync(runId, timestamp.AddSeconds(2)),
                    "A clean run marker was not persisted.");
            }
        }

        if ((iteration & 1) == 0)
        {
            using var recoveredStore = new JsonApplicationRunStateStore(runStatePath);
            var recovered = await recoveredStore.BeginRunAsync(timestamp.AddSeconds(2));
            Require(
                recovered.Status == RunStateLoadStatus.PreviousRunInterrupted,
                "An interrupted run was not detected.");
            Require(
                await recoveredStore.CompleteRunAsync(
                    recovered.RunId,
                    timestamp.AddSeconds(3)),
                "An interrupted run could not be recovered cleanly.");
            interruptedRunsRecovered++;
        }

        var recoveryPath = Path.Combine(tempDirectory, "recovery.json");
        var syntheticText = $"Synthetic reliability recovery {iteration:D6}";
        using (var recoveryStore = new WindowsRecoveryTextStore(recoveryPath))
        {
            var record = new RecoveryTextRecord(
                DictationSessionId.Create(),
                timestamp,
                syntheticText);
            Require(await recoveryStore.SaveAsync(record), "Encrypted recovery save failed.");
            var envelope = await File.ReadAllTextAsync(recoveryPath);
            Require(
                !envelope.Contains(syntheticText, StringComparison.Ordinal),
                "Recovery text appeared in plaintext at rest.");
            var loaded = await recoveryStore.LoadAsync();
            Require(
                loaded.Status == RecoveryTextLoadStatus.Found &&
                loaded.Record?.Text == syntheticText,
                "Encrypted recovery text did not round-trip exactly.");
            Require(await recoveryStore.ClearAsync(), "Encrypted recovery clear failed.");
            encryptedTextsRecovered++;
        }

        var activationKey = $"EnviousLabs.EnviousWispr.ReliabilityUat.{Guid.NewGuid():N}";
        await using (var activation = new SingleInstanceActivationChannel(activationKey))
        {
            var received = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            activation.ActivationRequested += (_, _) => received.TrySetResult();
            activation.Start();
            Require(
                await SingleInstanceActivationChannel.RequestActivationAsync(
                    activationKey,
                    TimeSpan.FromSeconds(2)),
                "The duplicate-instance activation message was not sent.");
            await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            activationsReceived++;
        }

        if (iteration == 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            baselineHandles = process.HandleCount;
        }
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    process.Refresh();
    var finalHandles = process.HandleCount;
    var handleDelta = finalHandles - baselineHandles;
    Require(handleDelta <= 12, $"Handle count grew unexpectedly by {handleDelta}.");
    stopwatch.Stop();

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "passed",
        iterations,
        elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        interruptedRunsRecovered,
        encryptedTextsRecovered,
        activationsReceived,
        faultInjection = new
        {
            corruptSettings = "recovered-with-source-preserved",
            lowMemory = "capture-refused-before-allocation",
            lowDisk = "capture-allowed-recovery-disabled",
            resourceProbeUnavailable = "application-remains-usable",
        },
        processResources = new
        {
            baselineHandles,
            finalHandles,
            handleDelta,
            childProcessesStarted = 0,
            inputHooksInstalled = 0,
        },
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    if (Directory.Exists(tempDirectory))
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

static async Task ValidateCorruptSettingsRecoveryAsync(string directory)
{
    var path = Path.Combine(directory, "settings.json");
    const string corruptSettings = "{not valid json";
    await File.WriteAllTextAsync(path, corruptSettings);
    var store = new JsonSettingsStore(path);
    var loaded = await store.LoadAsync();
    Require(loaded.Status == SettingsLoadStatus.Invalid, "Corrupt settings were not rejected.");
    var reset = await store.ResetAsync(AppSettings.Default);
    Require(reset.PreservedPreviousData, "Corrupt settings were not preserved.");
    Require(
        await File.ReadAllTextAsync(path + ".previous") == corruptSettings,
        "The preserved corrupt settings source changed.");
    Require(
        (await store.LoadAsync()).Status == SettingsLoadStatus.Loaded,
        "Default settings did not load after recovery.");
}

static void ValidateResourceFaultPolicies()
{
    var lowMemory = SystemResourceAdmissionPolicy.Evaluate(new SystemResourceSnapshot(
        AvailableDiskBytes: 1024L * 1024 * 1024,
        AvailablePhysicalMemoryBytes: SystemResourceAdmissionPolicy.MinimumDictationMemoryBytes - 1,
        MemoryLoadPercent: 99));
    Require(!lowMemory.CanStart, "Critical memory pressure did not block capture.");

    var lowDisk = SystemResourceAdmissionPolicy.Evaluate(new SystemResourceSnapshot(
        AvailableDiskBytes: SystemResourceAdmissionPolicy.MinimumRecoveryDiskBytes - 1,
        AvailablePhysicalMemoryBytes: 1024UL * 1024 * 1024,
        MemoryLoadPercent: 50));
    Require(
        lowDisk.CanStart && !lowDisk.CanPersistRecovery,
        "Low disk did not degrade only durable recovery.");

    var unavailable = SystemResourceAdmissionPolicy.Evaluate(new SystemResourceSnapshot(
        AvailableDiskBytes: 0,
        AvailablePhysicalMemoryBytes: 0,
        MemoryLoadPercent: 0,
        IsAvailable: false));
    Require(unavailable.CanStart, "A failed resource probe disabled the application.");
}

static int ParseIterations(string[] arguments)
{
    if (arguments.Length == 0)
    {
        return 250;
    }

    if (arguments.Length != 2 ||
        !string.Equals(arguments[0], "--iterations", StringComparison.Ordinal) ||
        !int.TryParse(arguments[1], out var parsed) ||
        parsed is < 1 or > 100_000)
    {
        throw new ArgumentException("Use --iterations with a value from 1 through 100000.");
    }

    return parsed;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
