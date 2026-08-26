using EnviousWispr.ASR;
using EnviousWispr.Core.Settings;
using EnviousWispr.Core.Runtime;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Services.Runtime;
using EnviousWispr.Services.Settings;
using NAudio.Codecs;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

const byte F8 = 0x77;
const int AcousticPlaybackGain = 2;
const int AcousticPlaybackRepetitions = 2;
const string ReviewedFrenchFixtureHash =
    "84DEFDC828EF59CEC10364354FBC284BC2CC683FDD4A5EDD5863B7BB2C6123A8";
const string ReviewedEnglishFixtureHash =
    "0F56F001F964D2288851A5E4063781CB5793D25F1B4FD9B55607E79873B4B20C";
var liveMicrophone = args.Any(argument => string.Equals(
    argument,
    "--live-microphone",
    StringComparison.OrdinalIgnoreCase));
var englishParakeet = args.Any(argument => string.Equals(
    argument,
    "--english-parakeet",
    StringComparison.OrdinalIgnoreCase));
var livePreview = args.Any(argument => string.Equals(
    argument,
    "--live-preview",
    StringComparison.OrdinalIgnoreCase));
var escapeRecovery = args.Any(argument => string.Equals(
    argument,
    "--escape-recovery",
    StringComparison.OrdinalIgnoreCase));
var failureArgument = ArgumentValue(args, "--failure");
var failureMode = failureArgument?.ToLowerInvariant() switch
{
    null => JourneyFailureMode.None,
    "microphone-unavailable" => JourneyFailureMode.MicrophoneUnavailable,
    "worker-startup" => JourneyFailureMode.WorkerStartup,
    "target-unavailable" => JourneyFailureMode.TargetUnavailable,
    _ => throw new ArgumentException(
        "--failure must be microphone-unavailable, worker-startup, or target-unavailable."),
};
if (escapeRecovery && liveMicrophone)
{
    throw new ArgumentException("Escape Recovery UAT uses the reviewed in-memory fixture, not live microphone mode.");
}
if (failureMode != JourneyFailureMode.None && (liveMicrophone || livePreview || escapeRecovery))
{
    throw new ArgumentException("Failure journeys cannot be combined with live microphone, Live Preview, or Escape Recovery modes.");
}
if (failureMode != JourneyFailureMode.None && englishParakeet)
{
    throw new ArgumentException("Failure journeys use the fixed reviewed Whisper fixture configuration.");
}
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
var finalEngine = englishParakeet ? FinalAsrEngine.Parakeet : FinalAsrEngine.Whisper;
var engineName = finalEngine.ToString();
var language = englishParakeet ? "en" : "fr";
var expectedSubstring = englishParakeet ? "account" : "adresse";
var fixtureFileName = englishParakeet ? "en-US-row0.wav" : "fr-FR-row0.wav";
var fixtureHash = englishParakeet ? ReviewedEnglishFixtureHash : ReviewedFrenchFixtureHash;
var fixtureIdentity = englishParakeet
    ? "PolyAI-minds14-en-US-row0"
    : "PolyAI-minds14-fr-FR-row0";
var modelDirectory = Path.Combine(
    repositoryRoot,
    "models",
    englishParakeet ? ParakeetTranscriptionEngine.ModelId : WhisperTranscriptionEngine.ModelId);
var previewModelDirectory = Path.Combine(
    repositoryRoot,
    "models",
    WhisperTranscriptionEngine.PreviewModelId);
var fixturePath = Path.Combine(
    repositoryRoot,
    "tools",
    "whisper-uat",
    "fixtures",
    fixtureFileName);

RequireFile(appExecutable, "Build the Release/x64 production WinUI app before journey UAT.");
RequireFile(targetExecutable, "Build the controlled delivery target before journey UAT.");
RequireFile(fixturePath, "The reviewed public fixture is missing.");
RequireReviewedFixture(fixturePath, fixtureHash);
if (englishParakeet && !new LocalParakeetModelProbe().Probe(modelDirectory).Int8Complete)
{
    throw new DirectoryNotFoundException(
        "The gitignored Parakeet quantized model is required for English journey UAT.");
}
if (!englishParakeet && !new LocalWhisperModelProbe().Probe(modelDirectory).QuantizedComplete)
{
    throw new DirectoryNotFoundException(
        "The gitignored Whisper large-v3-turbo quantized model is required for journey UAT.");
}
if (livePreview && !new LocalWhisperModelProbe().Probe(previewModelDirectory).PreviewSmallComplete)
{
    throw new DirectoryNotFoundException(
        "The gitignored Whisper small preview model is required for live-preview journey UAT.");
}

EnsureNoUnownedProcesses("EnviousWispr.App", "EnviousWispr.Delivery.Target.Uat");

var runId = Guid.NewGuid().ToString("N");
var uatDirectory = Path.Combine(Path.GetTempPath(), $"EnviousWispr-AppJourney-Uat-{runId}");
Directory.CreateDirectory(uatDirectory);
if (failureMode == JourneyFailureMode.WorkerStartup)
{
    var isolatedAppDirectory = Path.Combine(uatDirectory, "app-without-worker");
    CopyDirectoryExcept(
        Path.GetDirectoryName(appExecutable)!,
        isolatedAppDirectory,
        "EnviousWispr.RuntimeWorker.exe");
    appExecutable = Path.Combine(isolatedAppDirectory, "EnviousWispr.App.exe");
    RequireFile(appExecutable, "The isolated worker-failure app copy is incomplete.");
}
Directory.CreateDirectory(Path.Combine(uatDirectory, "no-preview-model"));
var profileDirectory = Path.Combine(uatDirectory, "profile");
Directory.CreateDirectory(profileDirectory);
if (livePreview || escapeRecovery || failureMode == JourneyFailureMode.MicrophoneUnavailable)
{
    var journeySettings = AppSettings.Default with
    {
        HasCompletedOnboarding = true,
        Preferences = AppSettings.Default.Preferences with
        {
            LivePreviewEnabled = livePreview,
            PillDesignWithWords = RecordingPillDesign.ReadingWell,
            Dictation = AppSettings.Default.Preferences.Dictation with
            {
                EscapeRecoveryEnabled = escapeRecovery,
            },
        },
    };
    await new JsonSettingsStore(Path.Combine(profileDirectory, "settings.json"))
        .SaveAsync(journeySettings);
}

var diagnosticPath = Path.Combine(profileDirectory, "diagnostics", "app.jsonl");
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
ClipboardGuard? clipboardGuard = null;
var usesPublicFixtureJourney = !liveMicrophone &&
    failureMode is JourneyFailureMode.None or JourneyFailureMode.TargetUnavailable;
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
    targetStart.ArgumentList.Add(expectedSubstring);
    target = Process.Start(targetStart) ?? throw new InvalidOperationException(
        "The controlled delivery target did not start.");
    WaitForWindow(target, TimeSpan.FromSeconds(10));

    var appStart = new ProcessStartInfo(appExecutable)
    {
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(appExecutable)!,
    };
    appStart.Environment["ENVIOUSWISPR_DATA_DIRECTORY"] = profileDirectory;
    appStart.Environment["ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX"] = $"journey-{runId}";
    appStart.Environment["ENVIOUSWISPR_UAT_READY_EVENT"] = readyEventName;
    appStart.Environment["ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT"] = runtimeEventName;
    appStart.Environment["ENVIOUSWISPR_ASR_ENGINE"] = engineName;
    appStart.Environment["ENVIOUSWISPR_ASR_LANGUAGE"] = language;
    appStart.Environment["ENVIOUSWISPR_MODEL_DIRECTORY"] = modelDirectory;
    appStart.Environment["ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY"] = livePreview
        ? previewModelDirectory
        : Path.Combine(uatDirectory, "no-preview-model");
    if (livePreview)
    {
        appStart.Environment["ENVIOUSWISPR_UAT_LIVE_PREVIEW"] = "1";
    }
    if (escapeRecovery)
    {
        appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_CANCEL"] = "1";
    }
    appStart.Environment["ENVIOUSWISPR_POLISH_PROVIDER"] = "None";
    if (failureMode == JourneyFailureMode.MicrophoneUnavailable)
    {
        appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY"] = "failure-v1";
        appStart.Environment["ENVIOUSWISPR_UAT_AUDIO_FAILURE"] = "access-denied";
    }
    if (failureMode is JourneyFailureMode.MicrophoneUnavailable or JourneyFailureMode.WorkerStartup)
    {
        appStart.Environment["ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS"] = "5000";
    }
    else if (liveMicrophone)
    {
        appStart.Environment["ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS"] = "30000";
    }
    else if (usesPublicFixtureJourney)
    {
        appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY"] = "public-fixture-v1";
        appStart.Environment["ENVIOUSWISPR_UAT_AUDIO_FIXTURE"] = fixturePath;
        appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_START_EVENT"] = startEventName;
        appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_COMPLETE_EVENT"] = completeEventName;
        appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_EXIT_AFTER_COMPLETION"] = "1";
        if (failureMode == JourneyFailureMode.TargetUnavailable)
        {
            appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_HOLD_MILLISECONDS"] = "2000";
        }
    }
    app = Process.Start(appStart) ?? throw new InvalidOperationException(
        "The production WinUI app did not start.");

    shellReady = readyEvent.WaitOne(TimeSpan.FromSeconds(30));
    runtimeReady = runtimeEvent.WaitOne(
        failureMode == JourneyFailureMode.WorkerStartup
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromSeconds(30));
    var expectedRuntimeReady = failureMode != JourneyFailureMode.WorkerStartup;
    if (!shellReady || runtimeReady != expectedRuntimeReady ||
        app.HasExited && failureMode != JourneyFailureMode.WorkerStartup)
    {
        var startupEvents = string.Join(",", ReadDiagnosticEvents(diagnosticPath));
        throw new InvalidOperationException(
            $"The production shell or final-ASR worker did not become ready " +
            $"(shellReady={shellReady}, runtimeReady={runtimeReady}, " +
            $"appExited={app.HasExited}, exitCode={(app.HasExited ? app.ExitCode : null)}, " +
            $"events={startupEvents}).");
    }

    ownedWorkerIds = ChildProcessIds(app.Id, "EnviousWispr.RuntimeWorker").ToArray();
    var expectedWorkerCount = failureMode == JourneyFailureMode.WorkerStartup ? 0 : 1;
    if (ownedWorkerIds.Length != expectedWorkerCount)
    {
        throw new InvalidOperationException(
            $"The production journey started {ownedWorkerIds.Length} owned final-ASR workers; " +
            $"expected {expectedWorkerCount}.");
    }

    if (failureMode == JourneyFailureMode.WorkerStartup)
    {
        journeyCompleted = true;
    }
    else
    {
        BringToForeground(target.MainWindowHandle);
        Thread.Sleep(250);
        if (failureMode == JourneyFailureMode.MicrophoneUnavailable)
        {
            SendKey(F8, keyDown: true);
            Thread.Sleep(500);
            SendKey(F8, keyDown: false);
            journeyCompleted = WaitForDiagnosticEvent(
                diagnosticPath,
                "DictationSessionFailed/AudioUnavailable/AccessDenied",
                TimeSpan.FromSeconds(5));
            targetObserved = WaitForExpectedTargetResult(
                targetResultPath,
                TimeSpan.FromMilliseconds(500));
        }
        else if (liveMicrophone)
        {
            SendKey(F8, keyDown: true);
            try
            {
                Thread.Sleep(500);
                await PlayPublicFixtureAsync(fixturePath);
                Thread.Sleep(500);
            }
            finally
            {
                SendKey(F8, keyDown: false);
            }

            targetObserved = WaitForExpectedTargetResult(
                targetResultPath,
                TimeSpan.FromSeconds(20));
            journeyCompleted = targetObserved;
        }
        else
        {
            if (failureMode == JourneyFailureMode.TargetUnavailable)
            {
                clipboardGuard = ClipboardGuard.CaptureOrThrow();
            }

            startEvent.Set();
            if (failureMode == JourneyFailureMode.TargetUnavailable)
            {
                if (!WaitForDiagnosticEvent(
                        diagnosticPath,
                        "DictationRecordingStarted/",
                        TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "The target-unavailable journey did not reach recording before target teardown.");
                }

                target.CloseMainWindow();
                if (!target.WaitForExit(5_000))
                {
                    target.Kill(entireProcessTree: true);
                    target.WaitForExit(10_000);
                }
            }

            journeyCompleted = completeEvent.WaitOne(TimeSpan.FromSeconds(60));
            if (!journeyCompleted)
            {
                throw new TimeoutException("The production journey did not complete within 60 seconds.");
            }

            targetObserved = WaitForExpectedTargetResult(
                targetResultPath,
                escapeRecovery || failureMode == JourneyFailureMode.TargetUnavailable
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromSeconds(5));
        }
    }

    if (!targetObserved && !escapeRecovery && failureMode == JourneyFailureMode.None)
    {
        if (liveMicrophone)
        {
            _ = app.WaitForExit(35_000);
        }

        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            failedJourney = liveMicrophone ? "acoustic-playback" : "reviewed-fixture",
            engine = engineName,
            language,
            diagnosticEvents = ReadDiagnosticEvents(diagnosticPath),
            targetCharacterCount = ReadTargetCharacterCount(targetResultPath),
        }));

        throw new InvalidOperationException(
            liveMicrophone
                ? "The controlled target did not observe the expected public microphone phrase."
                : "The controlled target did not observe the expected public-fixture text.");
    }

    appExitedCleanly = app.WaitForExit(liveMicrophone ? 35_000 : 15_000);
    if (!appExitedCleanly)
    {
        throw new TimeoutException("The production app did not exit cleanly after journey completion.");
    }

    if (app.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"The production app returned exit code {app.ExitCode}; " +
            $"events={string.Join(',', ReadDiagnosticEvents(diagnosticPath))}.");
    }

    var diagnosticEvents = ReadDiagnosticEvents(diagnosticPath);
    if (failureMode != JourneyFailureMode.None)
    {
        RequireFailureJourneyEvents(failureMode, diagnosticEvents);
        if (targetObserved)
        {
            throw new InvalidOperationException("A failure journey unexpectedly delivered text to the target.");
        }
    }
    else if (escapeRecovery)
    {
        RequireEscapeRecoveryJourneyEvents(diagnosticEvents);
    }
    else
    {
        RequireProductionJourneyEvents(diagnosticEvents);
    }
    if (livePreview)
    {
        RequireLivePreviewJourneyEvents(diagnosticEvents);
    }
    var productionStagesObserved = failureMode is JourneyFailureMode.None or JourneyFailureMode.TargetUnavailable;
    var recoveryHistoryObserved = escapeRecovery &&
        ReadEscapeRecoveryHistory(Path.Combine(profileDirectory, "history.json"));
    if (escapeRecovery && (!recoveryHistoryObserved || targetObserved))
    {
        throw new InvalidOperationException(
            "Escape Recovery must save one 24-hour undelivered History entry and deliver nothing to the target.");
    }

    ownedWorkerCount = ownedWorkerIds.Count(IsProcessRunning);
    if (ownedWorkerCount != 0)
    {
        throw new InvalidOperationException("The production journey left an owned runtime worker running.");
    }

    var hardware = await new WindowsHardwareDiscovery().ProbeAsync();
    string provider;
    string modelPack;
    if (englishParakeet)
    {
        var selection = ParakeetRuntimeSelector.Select(
            hardware,
            new LocalParakeetModelProbe().Probe(modelDirectory));
        provider = selection.Provider?.ToString() ?? "Unavailable";
        modelPack = selection.ModelPack?.ToString() ?? "Unavailable";
    }
    else
    {
        var selection = WhisperRuntimeSelector.Select(
            hardware,
            new LocalWhisperModelProbe().Probe(modelDirectory));
        provider = selection.Provider?.ToString() ?? "Unavailable";
        modelPack = selection.ModelPack?.ToString() ?? "Unavailable";
    }
    timer.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        passed = true,
        shellReady,
        runtimeReady,
        journeyCompleted,
        targetObserved,
        failureMode = failureMode == JourneyFailureMode.None ? null : FailureModeName(failureMode),
        escapeRecovery,
        recoveryHistoryObserved,
        productionStagesObserved,
        livePreview,
        livePreviewUpdated = livePreview && diagnosticEvents.Any(value => value.StartsWith(
            "LivePreviewUpdated/",
            StringComparison.Ordinal)),
        appExitedCleanly,
        ownedWorkerStartedCount = ownedWorkerIds.Length,
        ownedWorkerCount,
        elapsedMilliseconds = timer.ElapsedMilliseconds,
        windowsVersion = Environment.OSVersion.Version.ToString(),
        architecture = hardware.Architecture.ToString(),
        engine = engineName,
        language,
        provider,
        modelPack,
        polish = "None",
        inputKind = failureMode switch
        {
            JourneyFailureMode.MicrophoneUnavailable => "SyntheticF8-AllowlistedAccessDeniedAudioFault",
            JourneyFailureMode.WorkerStartup => "StartupFault-MissingOwnedWorkerExecutable",
            _ when liveMicrophone => "SyntheticF8-ReviewedFixturePlayback-ProductionWasapi",
            _ => "NamedEvents-ReviewedFixtureAudioCapture",
        },
        audioCapture = failureMode switch
        {
            JourneyFailureMode.WorkerStartup => "NotStarted",
            JourneyFailureMode.MicrophoneUnavailable => "AllowlistedAccessDeniedFault",
            _ when liveMicrophone => "ProductionWasapi",
            _ => "ReviewedFixture",
        },
        fixture = failureMode is JourneyFailureMode.MicrophoneUnavailable or JourneyFailureMode.WorkerStartup
            ? "None"
            : liveMicrophone
                ? $"{fixtureIdentity}-acoustic-playback"
                : fixtureIdentity,
        deliveryTarget = failureMode == JourneyFailureMode.TargetUnavailable
            ? "ControlledWinFormsEditClosedDuringRecording"
            : failureMode is JourneyFailureMode.MicrophoneUnavailable or JourneyFailureMode.WorkerStartup
                ? "NotReached"
                : escapeRecovery
                    ? "SuppressedForEscapeRecovery"
                    : "ControlledWinFormsEdit",
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
    try
    {
        clipboardGuard?.Dispose();
    }
    finally
    {
        RemoveUatDirectory(uatDirectory);
    }
}

static void RequireFile(string path, string message)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException(message, path);
    }
}

static void RequireReviewedFixture(string path, string expectedHash)
{
    var file = new FileInfo(path);
    if (file.Length is <= 0 or > 1_000_000)
    {
        throw new InvalidDataException("The reviewed public fixture has an unexpected size.");
    }

    using var stream = file.OpenRead();
    var actualHash = Convert.ToHexString(SHA256.HashData(stream));
    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException("The reviewed public fixture hash does not match.");
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

static IReadOnlyList<string> ReadDiagnosticEvents(string path)
{
    if (!File.Exists(path))
    {
        return ["DiagnosticsUnavailable"];
    }

    var events = new List<string>();
    foreach (var line in File.ReadLines(path))
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var eventName = root.TryGetProperty("event", out var eventElement)
            ? eventElement.GetString()
            : "UnknownEvent";
        var failure = root.TryGetProperty("failure", out var failureElement)
            ? failureElement.GetString()
            : null;
        var error = root.TryGetProperty("errorCode", out var errorElement)
            ? errorElement.GetString()
            : null;
        events.Add(string.Join(
            '/',
            new[] { eventName, failure, error }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    return events;
}

static int? ReadTargetCharacterCount(string path)
{
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("characterCount").GetInt32();
    }
    catch (Exception exception) when (
        exception is IOException or JsonException or InvalidOperationException)
    {
        return null;
    }
}

static void RequireProductionJourneyEvents(IReadOnlyList<string> events)
{
    var requiredEvents = new[]
    {
        "HotkeyReady",
        "DictationRecordingStarted",
        "DictationCaptureFinalized",
        "DictationTranscriptionStarted",
        "DeterministicProcessingStarted",
        "TextDeliveryStarted",
        "TextDeliveryCompleted",
        "ApplicationCleanShutdown",
    };
    var missing = requiredEvents
        .Where(required => !events.Any(value => value.StartsWith(
            required + '/',
            StringComparison.Ordinal)))
        .ToList();
    if (!events.Any(value => value.StartsWith(
            "DictationTranscriptionCompleted/",
            StringComparison.Ordinal)) &&
        !events.Any(value => value.StartsWith(
            "DictationTranscriptionDegraded/",
            StringComparison.Ordinal)))
    {
        missing.Add("DictationTranscriptionCompletedOrDegraded");
    }

    if (!events.Any(value => value.StartsWith(
            "DeterministicProcessingCompleted/",
            StringComparison.Ordinal)) &&
        !events.Any(value => value.StartsWith(
            "DeterministicProcessingDegraded/",
            StringComparison.Ordinal)))
    {
        missing.Add("DeterministicProcessingCompletedOrDegraded");
    }

    if (missing.Count > 0)
    {
        throw new InvalidOperationException(
            $"The production journey omitted required content-free stages: {string.Join(", ", missing)}.");
    }
}

static void RequireLivePreviewJourneyEvents(IReadOnlyList<string> events)
{
    var required = new[] { "LivePreviewStarted", "LivePreviewUpdated", "LivePreviewStopped" };
    var missing = required.Where(eventName => !events.Any(value => value.StartsWith(
        eventName + '/',
        StringComparison.Ordinal))).ToArray();
    if (missing.Length > 0)
    {
        throw new InvalidOperationException(
            $"The live-preview journey omitted content-free stages: {string.Join(", ", missing)}.");
    }
}

static void RequireEscapeRecoveryJourneyEvents(IReadOnlyList<string> events)
{
    var required = new[]
    {
        "HotkeyReady",
        "DictationRecordingStarted",
        "DictationCaptureFinalized",
        "DictationTranscriptionStarted",
        "DeterministicProcessingStarted",
        "ApplicationCleanShutdown",
    };
    var missing = required.Where(eventName => !events.Any(value => value.StartsWith(
        eventName + '/',
        StringComparison.Ordinal))).ToArray();
    if (missing.Length > 0 || events.Any(value => value.StartsWith("TextDeliveryStarted/", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            $"Escape Recovery journey stages were invalid: missing={string.Join(",", missing)}.");
    }
}

static bool ReadEscapeRecoveryHistory(string path)
{
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = document.RootElement.GetProperty("entries");
        if (entries.GetArrayLength() != 1)
        {
            return false;
        }

        var entry = entries[0];
        var createdAt = entry.GetProperty("createdAt").GetDateTimeOffset();
        var expiresAt = entry.GetProperty("expiresAt").GetDateTimeOffset();
        return !entry.GetProperty("wasDelivered").GetBoolean() &&
            expiresAt - createdAt >= TimeSpan.FromHours(23.9) &&
            expiresAt - createdAt <= TimeSpan.FromHours(24.1);
    }
    catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
    {
        return false;
    }
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

static async Task PlayPublicFixtureAsync(string fixturePath)
{
    var (pcmBytes, sampleRate) = ReadReviewedMuLawFixture(
        fixturePath,
        AcousticPlaybackGain);
    pcmBytes = RepeatPcm(pcmBytes, sampleRate, AcousticPlaybackRepetitions);
    using var stream = new MemoryStream(pcmBytes, writable: false);
    using var source = new RawSourceWaveStream(stream, new WaveFormat(sampleRate, 16, 1));
    await using var output = await new WasapiPlayerBuilder()
        .WithDefaultDeviceStreamRouting()
        .BuildAsync();
    var completed = new TaskCompletionSource<Exception?>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    output.PlaybackStopped += (_, args) => completed.TrySetResult(args.Exception);
    output.Init(source);
    output.Volume = 1f;
    output.Play();
    var playbackDuration = TimeSpan.FromSeconds(
        pcmBytes.Length / (sampleRate * sizeof(short) * 1d));
    var failure = await completed.Task.WaitAsync(playbackDuration + TimeSpan.FromSeconds(5));
    if (failure is not null)
    {
        throw new InvalidOperationException("The reviewed public fixture could not be played.", failure);
    }
}

static (byte[] PcmBytes, int SampleRate) ReadReviewedMuLawFixture(string path, int gain)
{
    ArgumentOutOfRangeException.ThrowIfLessThan(gain, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(gain, 8);
    var bytes = File.ReadAllBytes(path);
    if (bytes.Length < 12 ||
        !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
        !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
    {
        throw new InvalidDataException("The reviewed fixture is not a RIFF WAVE file.");
    }

    byte[]? format = null;
    var position = 12;
    while (position + 8 <= bytes.Length)
    {
        var chunkId = bytes.AsSpan(position, 4);
        var chunkSize = BitConverter.ToInt32(bytes, position + 4);
        position += 8;
        if (chunkSize < 0 || position + chunkSize > bytes.Length)
        {
            throw new InvalidDataException("The reviewed fixture has an invalid chunk.");
        }

        if (chunkId.SequenceEqual("fmt "u8))
        {
            format = bytes.AsSpan(position, chunkSize).ToArray();
        }
        else if (chunkId.SequenceEqual("data"u8) && format is { Length: >= 16 })
        {
            var audioFormat = BitConverter.ToInt16(format, 0);
            var channels = BitConverter.ToInt16(format, 2);
            var sampleRate = BitConverter.ToInt32(format, 4);
            var bitsPerSample = BitConverter.ToInt16(format, 14);
            if (audioFormat != 7 || channels != 1 || sampleRate <= 0 || bitsPerSample != 8)
            {
                throw new InvalidDataException("The reviewed fixture is not mono 8-bit mu-law audio.");
            }

            var pcmBytes = new byte[checked(chunkSize * sizeof(short))];
            for (var index = 0; index < chunkSize; index++)
            {
                var decoded = MuLawDecoder.MuLawToLinearSample(bytes[position + index]);
                var sample = checked((short)Math.Clamp(
                    decoded * gain,
                    short.MinValue,
                    short.MaxValue));
                BitConverter.TryWriteBytes(pcmBytes.AsSpan(index * sizeof(short)), sample);
            }

            return (pcmBytes, sampleRate);
        }

        position += chunkSize + (chunkSize % 2);
    }

    throw new InvalidDataException("The reviewed fixture has no supported audio data.");
}

static byte[] RepeatPcm(byte[] pcmBytes, int sampleRate, int repetitions)
{
    ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(repetitions, 3);
    var silenceBytes = checked(sampleRate / 4 * sizeof(short));
    var repeated = new byte[checked(
        (pcmBytes.Length * repetitions) + (silenceBytes * (repetitions - 1)))];
    for (var repetition = 0; repetition < repetitions; repetition++)
    {
        var offset = checked(repetition * (pcmBytes.Length + silenceBytes));
        pcmBytes.CopyTo(repeated, offset);
    }

    return repeated;
}

static void SendKey(byte virtualKey, bool keyDown)
{
    if (Marshal.SizeOf<Input>() != 40)
    {
        throw new InvalidOperationException("The synthetic keyboard input does not match the Win64 ABI.");
    }

    var input = new Input
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyDown ? 0u : 0x0002u,
            },
        },
    };
    if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
    {
        throw new InvalidOperationException("Synthetic keyboard input was rejected.");
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

static void CopyDirectoryExcept(string source, string destination, string excludedFileName)
{
    var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
    var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
    Directory.CreateDirectory(destinationRoot);
    foreach (var directory in Directory.EnumerateDirectories(
                 sourceRoot,
                 "*",
                 SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(
            destinationRoot,
            Path.GetRelativePath(sourceRoot, directory)));
    }

    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
    {
        if (string.Equals(Path.GetFileName(file), excludedFileName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var target = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: false);
    }
}

static bool WaitForDiagnosticEvent(string path, string expectedPrefix, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        try
        {
            if (ReadDiagnosticEvents(path).Any(value => value.StartsWith(
                    expectedPrefix,
                    StringComparison.Ordinal)))
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }

        Thread.Sleep(100);
    }

    return false;
}

static void RequireFailureJourneyEvents(
    JourneyFailureMode failureMode,
    IReadOnlyList<string> events)
{
    var required = failureMode switch
    {
        JourneyFailureMode.MicrophoneUnavailable => new[]
        {
            "HotkeyReady/",
            "DictationSessionFailed/AudioUnavailable/AccessDenied",
            "ApplicationCleanShutdown/",
        },
        JourneyFailureMode.WorkerStartup => new[]
        {
            "DictationTranscriptionFailed/RuntimeWorker",
            "HotkeyReady/",
            "ApplicationCleanShutdown/",
        },
        JourneyFailureMode.TargetUnavailable => new[]
        {
            "HotkeyReady/",
            "DictationRecordingStarted/",
            "DictationCaptureFinalized/",
            "DictationTranscriptionStarted/",
            "DeterministicProcessingStarted/",
            "TextDeliveryStarted/",
            "TextDeliveryRefused/TextDelivery/DeliveryTargetChanged",
            "ApplicationCleanShutdown/",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(failureMode)),
    };
    var missing = required.Where(expected => !events.Any(value => value.StartsWith(
        expected,
        StringComparison.Ordinal))).ToList();
    if (failureMode == JourneyFailureMode.TargetUnavailable)
    {
        if (!events.Any(value => value.StartsWith(
                "DictationTranscriptionCompleted/",
                StringComparison.Ordinal)) &&
            !events.Any(value => value.StartsWith(
                "DictationTranscriptionDegraded/",
                StringComparison.Ordinal)))
        {
            missing.Add("DictationTranscriptionCompletedOrDegraded");
        }

        if (!events.Any(value => value.StartsWith(
                "DeterministicProcessingCompleted/",
                StringComparison.Ordinal)) &&
            !events.Any(value => value.StartsWith(
                "DeterministicProcessingDegraded/",
                StringComparison.Ordinal)))
        {
            missing.Add("DeterministicProcessingCompletedOrDegraded");
        }
    }

    var forbiddenObserved = failureMode switch
    {
        JourneyFailureMode.MicrophoneUnavailable => events.Any(value => value.StartsWith(
            "DictationRecordingStarted/",
            StringComparison.Ordinal)),
        JourneyFailureMode.WorkerStartup => events.Any(value => value.StartsWith(
            "DictationRecordingStarted/",
            StringComparison.Ordinal)),
        JourneyFailureMode.TargetUnavailable => events.Any(value => value.StartsWith(
            "TextDeliveryCompleted/",
            StringComparison.Ordinal)),
        _ => false,
    };
    if (missing.Count > 0 || forbiddenObserved)
    {
        throw new InvalidOperationException(
            $"The {FailureModeName(failureMode)} journey evidence was invalid: " +
            $"missing={string.Join(',', missing)}, forbiddenObserved={forbiddenObserved}, " +
            $"events={string.Join(',', events)}.");
    }
}

static string FailureModeName(JourneyFailureMode failureMode) => failureMode switch
{
    JourneyFailureMode.MicrophoneUnavailable => "microphone-unavailable",
    JourneyFailureMode.WorkerStartup => "worker-startup",
    JourneyFailureMode.TargetUnavailable => "target-unavailable",
    _ => "none",
};

[StructLayout(LayoutKind.Sequential)]
internal struct Input
{
    public uint Type;
    public InputUnion Data;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct InputUnion
{
    [FieldOffset(0)]
    public KeyboardInput Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

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

internal enum JourneyFailureMode
{
    None,
    MicrophoneUnavailable,
    WorkerStartup,
    TargetUnavailable,
}

internal sealed class ClipboardGuard : IDisposable
{
    private readonly ClipboardSnapshot _snapshot;
    private bool _disposed;

    private ClipboardGuard(ClipboardSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    internal static ClipboardGuard CaptureOrThrow()
    {
        var snapshot = RunSta(CaptureOnSta) ?? throw new InvalidOperationException(
            "The target-unavailable UAT could not safely snapshot every clipboard format.");
        return new ClipboardGuard(snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!RunSta(() => RestoreOnSta(_snapshot)))
        {
            throw new InvalidOperationException(
                "The target-unavailable UAT could not restore the original clipboard.");
        }
    }

    private static ClipboardSnapshot? CaptureOnSta()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source is null)
            {
                return new ClipboardSnapshot(IsEmpty: true, Data: null);
            }

            var copy = new DataObject();
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                var value = source.GetData(format, autoConvert: false);
                var cloned = value is null ? null : CloneClipboardValue(value);
                if (cloned is null)
                {
                    return null;
                }

                copy.SetData(format, autoConvert: false, cloned);
            }

            return new ClipboardSnapshot(IsEmpty: false, copy);
        }
        catch (Exception exception) when (exception is ExternalException or
                                           ThreadStateException or
                                           InvalidOperationException or
                                           ArgumentException)
        {
            return null;
        }
    }

    private static bool RestoreOnSta(ClipboardSnapshot snapshot)
    {
        try
        {
            if (snapshot.IsEmpty)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetDataObject(
                    snapshot.Data!,
                    copy: true,
                    retryTimes: 10,
                    retryDelay: 50);
            }

            return true;
        }
        catch (Exception exception) when (exception is ExternalException or
                                           ThreadStateException or
                                           ArgumentException)
        {
            return false;
        }
    }

    private static object? CloneClipboardValue(object value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        MemoryStream memory => new MemoryStream(memory.ToArray(), writable: false),
        Bitmap bitmap => bitmap.Clone(),
        StringCollection strings => CloneStrings(strings),
        ICloneable cloneable => cloneable.Clone(),
        string or char or bool or byte or sbyte or short or ushort or int or uint or
            long or ulong or float or double or decimal or DateTime or DateTimeOffset or
            TimeSpan or Guid => value,
        Stream stream => CloneStream(stream),
        _ => null,
    };

    private static StringCollection CloneStrings(StringCollection strings)
    {
        var clone = new StringCollection();
        clone.AddRange(strings.Cast<string>().ToArray());
        return clone;
    }

    private static MemoryStream CloneStream(Stream stream)
    {
        var originalPosition = stream.CanSeek ? stream.Position : 0;
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        copy.Position = 0;
        return copy;
    }

    private static T RunSta<T>(Func<T> operation)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = operation();
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "EnviousWispr journey clipboard guard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The clipboard guard operation failed.", failure);
        }

        return result!;
    }

    private sealed record ClipboardSnapshot(bool IsEmpty, DataObject? Data);
}
