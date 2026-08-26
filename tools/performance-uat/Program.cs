using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;

var startupIterations = IntegerArgument(args, "--startup-iterations", defaultValue: 5, minimum: 1, maximum: 20);
var idleSeconds = IntegerArgument(args, "--idle-seconds", defaultValue: 3, minimum: 1, maximum: 20);
var recordingSeconds = IntegerArgument(args, "--recording-seconds", defaultValue: 5, minimum: 1, maximum: 30);
var requireLocalRuntime = args.Contains("--require-local-runtime", StringComparer.OrdinalIgnoreCase);
var outputPath = ArgumentValue(args, "--output");
var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var applicationExecutable = Path.Combine(
    repositoryRoot,
    "src",
    "Production",
    "EnviousWispr.App",
    "bin",
    "x64",
    "Release",
    "net10.0-windows10.0.26100.0",
    "win-x64",
    "EnviousWispr.App.exe");
if (!File.Exists(applicationExecutable))
{
    throw new FileNotFoundException("Build the Release x64 WinUI application before performance UAT.");
}

RequireNoExistingApplicationProcess();
var scratch = Path.Combine(Path.GetTempPath(), $"EnviousWispr-performance-uat-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
try
{
    var dataDirectory = Path.Combine(scratch, "app-data");
    var localModelDirectory = Path.Combine(repositoryRoot, "models", "parakeet-tdt-0.6b-v3");
    if (requireLocalRuntime && !Directory.Exists(localModelDirectory))
    {
        throw new DirectoryNotFoundException(
            "The local Parakeet model pack is required for full-runtime performance UAT.");
    }

    var startupResults = new List<StartupIteration>();
    for (var iteration = 0; iteration < startupIterations; iteration++)
    {
        startupResults.Add(await MeasureStartupAsync(
            applicationExecutable,
            dataDirectory,
            localModelDirectory,
            iteration,
            idleSeconds,
            requireLocalRuntime));
    }

    var recording = await MeasureRecordingAsync(TimeSpan.FromSeconds(recordingSeconds));
    var budgets = PerformanceBudgets.ProvisionalLaptop;
    var coldStart = startupResults[0];
    var warmStarts = startupResults.Skip(1).ToArray();
    var evaluation = new PerformanceEvaluation(
        ColdStartupPassed: coldStart.ReadyMilliseconds <= budgets.ShellReadyMilliseconds,
        WarmStartupPassed: warmStarts.Length == 0 ||
            Percentile(warmStarts.Select(result => result.ReadyMilliseconds), 0.95) <= budgets.ShellReadyMilliseconds,
        ReadyMemoryPassed: startupResults.All(result =>
            result.ReadyCombinedWorkingSetBytes <= budgets.ReadyCombinedWorkingSetBytes),
        IdleCpuPassed: startupResults.All(result => result.IdleCpuPercent <= budgets.IdleCpuPercent),
        RecordingCpuPassed: recording.CpuPercent <= budgets.RecordingCpuPercent,
        RecordingMemoryPassed: recording.WorkingSetGrowthBytes <= budgets.RecordingWorkingSetGrowthBytes,
        LifecyclePassed: startupResults.All(result => result.CleanExit && result.OrphanChildProcessCount == 0),
        RecordingPassed: recording.CapturePassed,
        RuntimeReadinessPassed: !requireLocalRuntime || startupResults.All(result =>
            result.LocalRuntimeReady && result.ChildProcessCount > 0));

    var report = new PerformanceReport(
        SchemaVersion: 1,
        CapturedAtUtc: DateTimeOffset.UtcNow,
        LogicalProcessorCount: Environment.ProcessorCount,
        requireLocalRuntime,
        Power: ProbePower(),
        budgets,
        coldStart,
        WarmReadyMedianMilliseconds: warmStarts.Length == 0
            ? null
            : Percentile(warmStarts.Select(result => result.ReadyMilliseconds), 0.50),
        WarmReadyP95Milliseconds: warmStarts.Length == 0
            ? null
            : Percentile(warmStarts.Select(result => result.ReadyMilliseconds), 0.95),
        startupResults,
        recording,
        evaluation,
        Succeeded: evaluation.AllPassed,
        Privacy: "No audio, transcript, clipboard content, device name or identifier, account, process identifier, event name, or path is recorded.");
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    });
    Console.WriteLine(json);
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        var resolvedOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedOutput)
                                  ?? throw new InvalidOperationException("Performance output has no directory."));
        await File.WriteAllTextAsync(resolvedOutput, json + Environment.NewLine);
    }

    return report.Succeeded ? 0 : 7;
}
finally
{
    DeleteOwnedScratch(scratch);
}

static async Task<StartupIteration> MeasureStartupAsync(
    string executable,
    string dataDirectory,
    string localModelDirectory,
    int iteration,
    int idleSeconds,
    bool requireLocalRuntime)
{
    var eventName = $@"Local\EnviousLabs.EnviousWispr.PerformanceUat.{Guid.NewGuid():N}";
    var runtimeEventName = $@"Local\EnviousLabs.EnviousWispr.PerformanceUat.{Guid.NewGuid():N}";
    using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
    using var runtimeReadyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, runtimeEventName);
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(executable)
                           ?? throw new InvalidOperationException("The application has no working directory."),
    };
    startInfo.Environment["ENVIOUSWISPR_DATA_DIRECTORY"] = dataDirectory;
    startInfo.Environment["ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX"] = $"performance-{Guid.NewGuid():N}";
    startInfo.Environment["ENVIOUSWISPR_UAT_READY_EVENT"] = eventName;
    startInfo.Environment["ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT"] = runtimeEventName;
    if (requireLocalRuntime)
    {
        startInfo.Environment["ENVIOUSWISPR_ASR_ENGINE"] = "Parakeet";
        startInfo.Environment["ENVIOUSWISPR_MODEL_DIRECTORY"] = localModelDirectory;
    }
    else
    {
        startInfo.Environment["ENVIOUSWISPR_UAT_DISABLE_LOCAL_RUNTIME"] = "1";
    }
    startInfo.Environment["ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS"] =
        checked((idleSeconds + 1) * 1_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    var timer = Stopwatch.StartNew();
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("The WinUI application process did not start.");
    var ready = readyEvent.WaitOne(TimeSpan.FromSeconds(30));
    timer.Stop();
    if (!ready || process.HasExited)
    {
        await StopOwnedProcessAsync(process);
        return new StartupIteration(
            iteration + 1,
            EmptyDataDirectory: iteration == 0,
            LocalRuntimeReady: false,
            ReadyMilliseconds: timer.ElapsedMilliseconds,
            ReadyCombinedWorkingSetBytes: 0,
            ReadyCombinedPrivateBytes: 0,
            ReadyHandleCount: 0,
            ChildProcessCount: 0,
            IdleCpuPercent: 100,
            IdleWorkingSetGrowthBytes: 0,
            CleanExit: false,
            OrphanChildProcessCount: 0);
    }

    var localRuntimeReady = runtimeReadyEvent.WaitOne(TimeSpan.Zero);
    var readyResources = ReadProcessTreeResources(process);
    await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
    var idleResources = ReadProcessTreeResources(process);
    var idleCpuPercent = NormalizeCpu(
        idleResources.TotalProcessorTime - readyResources.TotalProcessorTime,
        TimeSpan.FromSeconds(idleSeconds));
    var observedChildren = readyResources.ChildProcessIds
        .Concat(idleResources.ChildProcessIds)
        .Distinct()
        .ToArray();
    var cleanExit = await WaitForCleanExitAsync(process, TimeSpan.FromSeconds(10));
    await Task.Delay(TimeSpan.FromMilliseconds(200));
    var orphanCount = observedChildren.Count(IsProcessRunning);
    return new StartupIteration(
        iteration + 1,
        EmptyDataDirectory: iteration == 0,
        localRuntimeReady,
        ReadyMilliseconds: timer.ElapsedMilliseconds,
        readyResources.CombinedWorkingSetBytes,
        readyResources.CombinedPrivateBytes,
        readyResources.HandleCount,
        readyResources.ChildProcessIds.Count,
        idleCpuPercent,
        Math.Max(0, idleResources.CombinedWorkingSetBytes - readyResources.CombinedWorkingSetBytes),
        cleanExit,
        orphanCount);
}

static async Task<RecordingMeasurement> MeasureRecordingAsync(TimeSpan requestedDuration)
{
    using var process = Process.GetCurrentProcess();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    process.Refresh();
    var beforeCpu = process.TotalProcessorTime;
    var beforeWorkingSet = process.WorkingSet64;
    var beforeHandles = process.HandleCount;
    var frequencyBefore = ProbeProcessorFrequencyRatio();
    await using var capture = new WasapiAudioCapture();
    var started = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
    if (!started.Succeeded)
    {
        return new RecordingMeasurement(
            RequestedMilliseconds: checked((long)requestedDuration.TotalMilliseconds),
            CapturedMilliseconds: 0,
            SampleRate: 0,
            Channels: 0,
            CpuPercent: 100,
            WorkingSetGrowthBytes: 0,
            HandleGrowth: 0,
            ProcessorFrequencyRatioBefore: frequencyBefore,
            ProcessorFrequencyRatioAfter: ProbeProcessorFrequencyRatio(),
            CapturePassed: false);
    }

    var wallTimer = Stopwatch.StartNew();
    await Task.Delay(requestedDuration);
    var result = await capture.StopAsync();
    wallTimer.Stop();
    process.Refresh();
    var cpuPercent = NormalizeCpu(process.TotalProcessorTime - beforeCpu, wallTimer.Elapsed);
    var capturedMilliseconds = result.SampleRate > 0
        ? result.Samples.Length * 1_000L / result.SampleRate
        : 0;
    return new RecordingMeasurement(
        RequestedMilliseconds: checked((long)requestedDuration.TotalMilliseconds),
        capturedMilliseconds,
        result.SampleRate,
        result.Channels,
        cpuPercent,
        Math.Max(0, process.WorkingSet64 - beforeWorkingSet),
        process.HandleCount - beforeHandles,
        frequencyBefore,
        ProbeProcessorFrequencyRatio(),
        CapturePassed: result.Outcome == AudioCaptureOutcome.Completed &&
            result.SampleRate == AudioSampleConverter.TargetSampleRate &&
            result.Channels == 1 &&
            capturedMilliseconds >= requestedDuration.TotalMilliseconds * 0.90);
}

static ProcessTreeResources ReadProcessTreeResources(Process root)
{
    root.Refresh();
    var children = ChildProcessIds(root.Id);
    long workingSet = root.WorkingSet64;
    long privateBytes = root.PrivateMemorySize64;
    var handles = root.HandleCount;
    var processorTime = root.TotalProcessorTime;
    foreach (var childId in children)
    {
        try
        {
            using var child = Process.GetProcessById(childId);
            child.Refresh();
            workingSet = checked(workingSet + child.WorkingSet64);
            privateBytes = checked(privateBytes + child.PrivateMemorySize64);
            handles = checked(handles + child.HandleCount);
            processorTime += child.TotalProcessorTime;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // A short-lived owned worker can exit between enumeration and sampling.
        }
    }

    return new ProcessTreeResources(workingSet, privateBytes, handles, processorTime, children);
}

static IReadOnlyList<int> ChildProcessIds(int parentProcessId)
{
    try
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentProcessId}");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>()
            .Select(result => Convert.ToInt32(result["ProcessId"], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }
    catch (Exception exception) when (exception is
                                      ManagementException or
                                      COMException or
                                      UnauthorizedAccessException)
    {
        return [];
    }
}

static async Task<bool> WaitForCleanExitAsync(Process process, TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
        await process.WaitForExitAsync(cancellation.Token);
        return process.ExitCode == 0;
    }
    catch (OperationCanceledException)
    {
        await StopOwnedProcessAsync(process);
        return false;
    }
}

static async Task StopOwnedProcessAsync(Process process)
{
    if (process.HasExited)
    {
        return;
    }

    process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
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

static void RequireNoExistingApplicationProcess()
{
    var existing = Process.GetProcessesByName("EnviousWispr.App");
    try
    {
        if (existing.Length > 0)
        {
            throw new InvalidOperationException(
                "Performance UAT requires no existing EnviousWispr.App process and will not stop one it did not create.");
        }
    }
    finally
    {
        foreach (var process in existing)
        {
            process.Dispose();
        }
    }
}

static double NormalizeCpu(TimeSpan cpuTime, TimeSpan wallTime)
{
    if (wallTime <= TimeSpan.Zero)
    {
        return 0;
    }

    return Math.Round(
        cpuTime.TotalMilliseconds / wallTime.TotalMilliseconds / Environment.ProcessorCount * 100,
        3,
        MidpointRounding.AwayFromZero);
}

static long Percentile(IEnumerable<long> source, double percentile)
{
    var values = source.Order().ToArray();
    if (values.Length == 0)
    {
        throw new ArgumentException("A percentile requires at least one value.", nameof(source));
    }

    var rank = Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1);
    return values[rank];
}

static PowerMeasurement ProbePower()
{
    if (!NativeMethods.GetSystemPowerStatus(out var status))
    {
        return new PowerMeasurement(PowerSource.Unknown, BatteryChargeTier.Unknown, null);
    }

    var source = status.AcLineStatus switch
    {
        0 => PowerSource.Battery,
        1 => PowerSource.Ac,
        _ => PowerSource.Unknown,
    };
    var battery = status.BatteryLifePercent switch
    {
        byte.MaxValue => BatteryChargeTier.Unknown,
        < 20 => BatteryChargeTier.Below20Percent,
        < 50 => BatteryChargeTier.From20To49Percent,
        < 80 => BatteryChargeTier.From50To79Percent,
        _ => BatteryChargeTier.AtLeast80Percent,
    };
    return new PowerMeasurement(source, battery, ProbeProcessorFrequencyRatio());
}

static double? ProbeProcessorFrequencyRatio()
{
    try
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
        using var results = searcher.Get();
        var ratios = results.Cast<ManagementObject>()
            .Select(result =>
            {
                var current = Convert.ToDouble(result["CurrentClockSpeed"], System.Globalization.CultureInfo.InvariantCulture);
                var maximum = Convert.ToDouble(result["MaxClockSpeed"], System.Globalization.CultureInfo.InvariantCulture);
                return maximum > 0 ? current / maximum : 0;
            })
            .Where(ratio => ratio > 0)
            .ToArray();
        return ratios.Length == 0
            ? null
            : Math.Round(ratios.Average(), 3, MidpointRounding.AwayFromZero);
    }
    catch (Exception exception) when (exception is
                                      ManagementException or
                                      COMException or
                                      UnauthorizedAccessException)
    {
        return null;
    }
}

static string? ArgumentValue(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static int IntegerArgument(
    string[] arguments,
    string name,
    int defaultValue,
    int minimum,
    int maximum)
{
    var value = ArgumentValue(arguments, name);
    if (value is null)
    {
        return defaultValue;
    }

    if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
    {
        throw new ArgumentException($"{name} must be from {minimum} through {maximum}.");
    }

    return parsed;
}

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

static void DeleteOwnedScratch(string scratch)
{
    if (!Directory.Exists(scratch))
    {
        return;
    }

    var resolved = Path.GetFullPath(scratch);
    var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    var actualParent = Directory.GetParent(resolved)?.FullName;
    if (!string.Equals(actualParent, expectedParent, StringComparison.OrdinalIgnoreCase) ||
        !new DirectoryInfo(resolved).Name.StartsWith("EnviousWispr-performance-uat-", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Refusing to remove an unexpected performance UAT directory.");
    }

    Directory.Delete(resolved, recursive: true);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte AcLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

internal enum PowerSource
{
    Unknown,
    Ac,
    Battery,
}

internal enum BatteryChargeTier
{
    Unknown,
    Below20Percent,
    From20To49Percent,
    From50To79Percent,
    AtLeast80Percent,
}

internal sealed record PerformanceBudget(
    long ShellReadyMilliseconds,
    long ReadyCombinedWorkingSetBytes,
    double IdleCpuPercent,
    double RecordingCpuPercent,
    long RecordingWorkingSetGrowthBytes);

internal static class PerformanceBudgets
{
    public static PerformanceBudget ProvisionalLaptop { get; } = new(
        ShellReadyMilliseconds: 5_000,
        ReadyCombinedWorkingSetBytes: 2L * 1024 * 1024 * 1024,
        IdleCpuPercent: 5,
        RecordingCpuPercent: 15,
        RecordingWorkingSetGrowthBytes: 256L * 1024 * 1024);
}

internal sealed record StartupIteration(
    int Iteration,
    bool EmptyDataDirectory,
    bool LocalRuntimeReady,
    long ReadyMilliseconds,
    long ReadyCombinedWorkingSetBytes,
    long ReadyCombinedPrivateBytes,
    int ReadyHandleCount,
    int ChildProcessCount,
    double IdleCpuPercent,
    long IdleWorkingSetGrowthBytes,
    bool CleanExit,
    int OrphanChildProcessCount);

internal sealed record RecordingMeasurement(
    long RequestedMilliseconds,
    long CapturedMilliseconds,
    int SampleRate,
    int Channels,
    double CpuPercent,
    long WorkingSetGrowthBytes,
    int HandleGrowth,
    double? ProcessorFrequencyRatioBefore,
    double? ProcessorFrequencyRatioAfter,
    bool CapturePassed);

internal sealed record PowerMeasurement(
    PowerSource Source,
    BatteryChargeTier BatteryCharge,
    double? ProcessorFrequencyRatio);

internal sealed record PerformanceEvaluation(
    bool ColdStartupPassed,
    bool WarmStartupPassed,
    bool ReadyMemoryPassed,
    bool IdleCpuPassed,
    bool RecordingCpuPassed,
    bool RecordingMemoryPassed,
    bool LifecyclePassed,
    bool RecordingPassed,
    bool RuntimeReadinessPassed)
{
    public bool AllPassed => ColdStartupPassed &&
        WarmStartupPassed &&
        ReadyMemoryPassed &&
        IdleCpuPassed &&
        RecordingCpuPassed &&
        RecordingMemoryPassed &&
        LifecyclePassed &&
        RecordingPassed &&
        RuntimeReadinessPassed;
}

internal sealed record PerformanceReport(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    int LogicalProcessorCount,
    bool RequireLocalRuntime,
    PowerMeasurement Power,
    PerformanceBudget ProvisionalLaptopBudget,
    StartupIteration ColdStart,
    long? WarmReadyMedianMilliseconds,
    long? WarmReadyP95Milliseconds,
    IReadOnlyList<StartupIteration> StartupIterations,
    RecordingMeasurement Recording,
    PerformanceEvaluation Evaluation,
    bool Succeeded,
    string Privacy);

internal sealed record ProcessTreeResources(
    long CombinedWorkingSetBytes,
    long CombinedPrivateBytes,
    int HandleCount,
    TimeSpan TotalProcessorTime,
    IReadOnlyList<int> ChildProcessIds);
