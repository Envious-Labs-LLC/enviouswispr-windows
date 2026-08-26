using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Services.Runtime;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var appExecutable = Path.Combine(
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
var targetExecutable = Path.Combine(
    repositoryRoot,
    "tools",
    "delivery-target-uat",
    "bin",
    "Release",
    "net10.0-windows10.0.26100.0",
    "EnviousWispr.Delivery.Target.Uat.exe");
var modelDirectory = Path.Combine(repositoryRoot, "models", WhisperTranscriptionEngine.ModelId);
var fixturePath = Path.Combine(
    repositoryRoot,
    "tools",
    "whisper-uat",
    "fixtures",
    "fr-FR-row0.wav");

RequireFile(appExecutable, "Build the Release/x64 production WinUI app before journey UAT.");
RequireFile(targetExecutable, "Build the controlled delivery target before journey UAT.");
RequireFile(fixturePath, "The reviewed public French fixture is missing.");
if (!new LocalWhisperModelProbe().Probe(modelDirectory).QuantizedComplete)
{
    throw new DirectoryNotFoundException(
        "The gitignored Whisper large-v3-turbo quantized model is required for journey UAT.");
}

EnsureNoUnownedProcesses("EnviousWispr.App", "EnviousWispr.Delivery.Target.Uat");

var runId = Guid.NewGuid().ToString("N");
var uatDirectory = Path.Combine(Path.GetTempPath(), $"EnviousWispr-AppJourney-Uat-{runId}");
Directory.CreateDirectory(uatDirectory);
Directory.CreateDirectory(Path.Combine(uatDirectory, "no-preview-model"));
var targetResultPath = Path.Combine(uatDirectory, "target-result.json");
var readyEventName = $@"Local\EnviousLabs.EnviousWispr.PerformanceUat.{runId}.ready";
var runtimeEventName = $@"Local\EnviousLabs.EnviousWispr.PerformanceUat.{runId}.runtime";
var startEventName = $@"Local\EnviousLabs.EnviousWispr.JourneyUat.{runId}.start";
var completeEventName = $@"Local\EnviousLabs.EnviousWispr.JourneyUat.{runId}.complete";
using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
using var runtimeEvent = new EventWaitHandle(false, EventResetMode.ManualReset, runtimeEventName);
using var startEvent = new EventWaitHandle(false, EventResetMode.ManualReset, startEventName);
using var completeEvent = new EventWaitHandle(false, EventResetMode.ManualReset, completeEventName);

Process? target = null;
Process? app = null;
var timer = Stopwatch.StartNew();
var shellReady = false;
var runtimeReady = false;
var journeyCompleted = false;
var targetObserved = false;
var appExitedCleanly = false;
var ownedWorkerIds = Array.Empty<int>();
var ownedWorkerCount = 0;
try
{
    var targetStart = new ProcessStartInfo(targetExecutable)
    {
        UseShellExecute = false,
    };
    targetStart.ArgumentList.Add("--mode");
    targetStart.ArgumentList.Add("edit");
    targetStart.ArgumentList.Add("--hold-focus-ms");
    targetStart.ArgumentList.Add("30000");
    targetStart.ArgumentList.Add("--result");
    targetStart.ArgumentList.Add(targetResultPath);
    targetStart.ArgumentList.Add("--expected-substring");
    targetStart.ArgumentList.Add("adresse");
    target = Process.Start(targetStart) ?? throw new InvalidOperationException(
        "The controlled delivery target did not start.");
    WaitForWindow(target, TimeSpan.FromSeconds(10));

    var appStart = new ProcessStartInfo(appExecutable)
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(appExecutable)!,
    };
    appStart.Environment["ENVIOUSWISPR_DATA_DIRECTORY"] = Path.Combine(uatDirectory, "profile");
    appStart.Environment["ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX"] = $"journey-{runId}";
    appStart.Environment["ENVIOUSWISPR_UAT_READY_EVENT"] = readyEventName;
    appStart.Environment["ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT"] = runtimeEventName;
    appStart.Environment["ENVIOUSWISPR_ASR_ENGINE"] = "Whisper";
    appStart.Environment["ENVIOUSWISPR_ASR_LANGUAGE"] = "fr";
    appStart.Environment["ENVIOUSWISPR_MODEL_DIRECTORY"] = modelDirectory;
    appStart.Environment["ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY"] =
        Path.Combine(uatDirectory, "no-preview-model");
    appStart.Environment["ENVIOUSWISPR_POLISH_PROVIDER"] = "None";
    appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY"] = "public-fixture-v1";
    appStart.Environment["ENVIOUSWISPR_UAT_AUDIO_FIXTURE"] = fixturePath;
    appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_START_EVENT"] = startEventName;
    appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_COMPLETE_EVENT"] = completeEventName;
    appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_EXIT_AFTER_COMPLETION"] = "1";
    app = Process.Start(appStart) ?? throw new InvalidOperationException(
        "The production WinUI app did not start.");

    shellReady = readyEvent.WaitOne(TimeSpan.FromSeconds(30));
    runtimeReady = runtimeEvent.WaitOne(TimeSpan.FromSeconds(30));
    if (!shellReady || !runtimeReady || app.HasExited)
    {
        throw new InvalidOperationException(
            "The production shell or final-ASR worker did not become ready.");
    }

    ownedWorkerIds = ChildProcessIds(app.Id, "EnviousWispr.RuntimeWorker").ToArray();
    if (ownedWorkerIds.Length != 1)
    {
        throw new InvalidOperationException(
            "The production journey did not have exactly one owned final-ASR worker.");
    }

    BringToForeground(target.MainWindowHandle);
    Thread.Sleep(250);
    startEvent.Set();
    journeyCompleted = completeEvent.WaitOne(TimeSpan.FromSeconds(60));
    if (!journeyCompleted)
    {
        throw new TimeoutException("The production journey did not complete within 60 seconds.");
    }

    targetObserved = WaitForExpectedTargetResult(
        targetResultPath,
        TimeSpan.FromSeconds(5));
    if (!targetObserved)
    {
        throw new InvalidOperationException(
            "The controlled target did not observe the expected public-fixture text.");
    }

    appExitedCleanly = app.WaitForExit(15_000);
    if (!appExitedCleanly)
    {
        throw new TimeoutException("The production app did not exit cleanly after journey completion.");
    }

    if (app.ExitCode != 0)
    {
        throw new InvalidOperationException("The production app returned a non-zero exit code.");
    }

    ownedWorkerCount = ownedWorkerIds.Count(IsProcessRunning);
    if (ownedWorkerCount != 0)
    {
        throw new InvalidOperationException("The production journey left an owned runtime worker running.");
    }

    var hardware = await new WindowsHardwareDiscovery().ProbeAsync();
    var selection = WhisperRuntimeSelector.Select(
        hardware,
        new LocalWhisperModelProbe().Probe(modelDirectory));
    timer.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        passed = true,
        shellReady,
        runtimeReady,
        journeyCompleted,
        targetObserved,
        appExitedCleanly,
        ownedWorkerStartedCount = ownedWorkerIds.Length,
        ownedWorkerCount,
        elapsedMilliseconds = timer.ElapsedMilliseconds,
        windowsVersion = Environment.OSVersion.Version.ToString(),
        architecture = hardware.Architecture.ToString(),
        engine = "Whisper",
        language = "fr",
        provider = selection.Provider?.ToString() ?? "Unavailable",
        modelPack = selection.ModelPack?.ToString() ?? "Unavailable",
        polish = "None",
        fixture = "PolyAI-minds14-fr-FR-row0",
        deliveryTarget = "ControlledWinFormsEdit",
    }));
    return 0;
}
finally
{
    if (app is { HasExited: false })
    {
        app.Kill(entireProcessTree: true);
        app.WaitForExit(10_000);
    }

    if (target is { HasExited: false })
    {
        target.CloseMainWindow();
        if (!target.WaitForExit(5_000))
        {
            target.Kill(entireProcessTree: true);
            target.WaitForExit(10_000);
        }
    }

    app?.Dispose();
    target?.Dispose();
    RemoveUatDirectory(uatDirectory);
}

static void RequireFile(string path, string message)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException(message, path);
    }
}

static void EnsureNoUnownedProcesses(params string[] processNames)
{
    var existing = processNames
        .SelectMany(Process.GetProcessesByName)
        .ToArray();
    try
    {
        if (existing.Length > 0)
        {
            throw new InvalidOperationException(
                "Journey UAT requires no existing EnviousWispr app or controlled target and will not stop one it did not create.");
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

static void WaitForWindow(Process process, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException("The controlled delivery target exited before it was ready.");
        }

        process.Refresh();
        if (process.MainWindowHandle != 0)
        {
            return;
        }

        Thread.Sleep(100);
    }

    throw new TimeoutException("The controlled delivery target did not show a window.");
}

static bool WaitForExpectedTargetResult(string path, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.GetProperty("containsExpected").GetBoolean())
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }

        Thread.Sleep(100);
    }

    return false;
}

static IReadOnlyList<int> ChildProcessIds(int parentProcessId, string processName)
{
    using var searcher = new ManagementObjectSearcher(
        $"SELECT ProcessId, Name FROM Win32_Process WHERE ParentProcessId = {parentProcessId}");
    using var results = searcher.Get();
    return results
        .Cast<ManagementObject>()
        .Where(process => string.Equals(
            Convert.ToString(
                process["Name"],
                System.Globalization.CultureInfo.InvariantCulture),
            $"{processName}.exe",
            StringComparison.OrdinalIgnoreCase))
        .Select(process => Convert.ToInt32((uint)process["ProcessId"]))
        .ToArray();
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

static void RemoveUatDirectory(string path)
{
    var fullPath = Path.GetFullPath(path);
    var temporaryRoot = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(fullPath).StartsWith(
            "EnviousWispr-AppJourney-Uat-",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Refusing to remove an unexpected journey UAT directory.");
    }

    if (Directory.Exists(fullPath))
    {
        Directory.Delete(fullPath, recursive: true);
    }
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

static void BringToForeground(nint window)
{
    if (window == 0)
    {
        throw new InvalidOperationException("The controlled delivery target has no window handle.");
    }

    var foreground = NativeMethods.GetForegroundWindow();
    var foregroundThread = foreground == 0
        ? 0
        : NativeMethods.GetWindowThreadProcessId(foreground, out _);
    var currentThread = NativeMethods.GetCurrentThreadId();
    var attached = foregroundThread != 0 &&
        foregroundThread != currentThread &&
        NativeMethods.AttachThreadInput(currentThread, foregroundThread, attach: true);
    try
    {
        _ = NativeMethods.BringWindowToTop(window);
        _ = NativeMethods.SetForegroundWindow(window);
    }
    finally
    {
        if (attached)
        {
            _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, attach: false);
        }
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);
}
