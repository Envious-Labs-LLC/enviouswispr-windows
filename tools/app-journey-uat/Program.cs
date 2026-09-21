using EnviousWispr.AppJourney.Uat;
using EnviousWispr.Audio;
using EnviousWispr.ASR;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Core.Runtime;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Runtime;
using EnviousWispr.Services.Settings;
using NAudio.Codecs;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

const byte F8 = 0x77;
const int DefaultAcousticPlaybackGain = 2;
const int AcousticPlaybackRepetitions = 2;
const string SynthesizedAcousticPhrase =
    "This is an Envious Wispr microphone test. The quick brown fox jumps over the lazy dog.";
const string ReviewedFrenchFixtureHash =
    "84DEFDC828EF59CEC10364354FBC284BC2CC683FDD4A5EDD5863B7BB2C6123A8";
const string ReviewedEnglishFixtureHash =
    "0F56F001F964D2288851A5E4063781CB5793D25F1B4FD9B55607E79873B4B20C";
// THE REPORTER WRAPS THE WHOLE PROGRAM, NOT THE PART SOMEBODY REMEMBERED. Three review rounds each
// named one more ordinary failure that still terminated the process - a helper below the journey
// block, then the cleanup, then the preflight that runs before the journey even starts. Positioning
// a catch around the parts already found has no end; starting it at the first statement does.
try
{
RequireKnownArguments(args);
var syntheticMicrophonePlayback = args.Any(argument => string.Equals(
    argument,
    "--live-microphone",
    StringComparison.OrdinalIgnoreCase));
var manualMicrophone = args.Any(argument => string.Equals(
    argument,
    "--manual-microphone",
    StringComparison.OrdinalIgnoreCase));
if (syntheticMicrophonePlayback && manualMicrophone)
{
    throw new JourneyExpectationException(
        "--live-microphone and --manual-microphone are mutually exclusive.");
}
var liveMicrophone = syntheticMicrophonePlayback || manualMicrophone;
var englishParakeet = args.Any(argument => string.Equals(
    argument,
    "--english-parakeet",
    StringComparison.OrdinalIgnoreCase));
var livePreview = args.Any(argument => string.Equals(
    argument,
    "--live-preview",
    StringComparison.OrdinalIgnoreCase));
// HOLDS THE RECORDING LONG ENOUGH FOR THE HEAD START TO POLL, WHICH NOTHING DID BEFORE. The
// streaming head start polls every 500 ms and the ordinary journey holds a recording for a fraction
// of that, so no run this harness has ever made reached the first poll. The feature was broken from
// the day it shipped - abandoned 511 ms into every recording - and every gate stayed green, because
// the only thing that could have noticed was never given a long enough take. Ref: #96.
var headStart = args.Any(argument => string.Equals(
    argument,
    "--head-start",
    StringComparison.OrdinalIgnoreCase));
if (headStart && livePreview)
{
    throw new JourneyExpectationException(
        "--head-start and --live-preview are mutually exclusive: the head start deliberately stands "
            + "down when Live Preview is on, so asking for both asserts a state the app refuses.");
}
// THE ONE TAKE THAT PRESSES THE REAL KEY WITHOUT A SOUND. Every other synthetic mode either asks the
// app to press for itself over a named event, or presses F8 and plays audio through the speakers. This
// one injects the resolved recording key with the silent fixture as the microphone, so the installed
// global hook - the path a finger takes - is what starts the take, and nothing is heard in the room.
// Verdicts come from app.jsonl and the controlled target; the injector's own account is never one.
var syntheticHotkey = args.Any(argument => string.Equals(
    argument,
    "--synthetic-hotkey",
    StringComparison.OrdinalIgnoreCase));
// THE KEY-UP LANDS WHILE THE PRESS IS STILL STARTING. The ordinary synthetic take waits for the app to
// say it is recording before it lets go, so the release always finds the session gate free. A tap that
// lets go at once puts the release inside the window where the shell used to probe that gate with a
// zero timeout and discard the signal (#86): the recording then ran on and no delivery ever came. This
// variant exists to make that window a test, and it MUST fail against a build with the old gate.
var quickTap = args.Any(argument => string.Equals(
    argument,
    "--quick-tap",
    StringComparison.OrdinalIgnoreCase));
var deterministicProfileArgument = ArgumentValue(args, "--deterministic-profile");
var deterministicProfile = deterministicProfileArgument?.ToLowerInvariant() switch
{
    null => DeterministicJourneyProfile.None,
    "enabled" => DeterministicJourneyProfile.Enabled,
    "disabled" => DeterministicJourneyProfile.Disabled,
    _ => throw new JourneyExpectationException(
        "--deterministic-profile must be enabled or disabled."),
};
var polishArgument = ArgumentValue(args, "--polish");
var polishProvider = polishArgument?.ToLowerInvariant() switch
{
    null or "none" => PolishProvider.None,
    "eg-1" or "eg1" => PolishProvider.EgOne,
    "ollama" => PolishProvider.Ollama,
    "openai" => PolishProvider.OpenAI,
    "anthropic" or "claude" => PolishProvider.Anthropic,
    "gemini" => PolishProvider.Gemini,
    _ => throw new JourneyExpectationException(
        "--polish must be none, eg-1, ollama, openai, anthropic, or gemini."),
};
var egOneServerExecutable = ArgumentValue(args, "--eg1-server");
var egOneModelFile = ArgumentValue(args, "--eg1-model");
var ollamaEndpoint = ArgumentValue(args, "--ollama-endpoint") ?? "http://localhost:11434";
var ollamaModel = ArgumentValue(args, "--ollama-model");
var escapeRecovery = args.Any(argument => string.Equals(
    argument,
    "--escape-recovery",
    StringComparison.OrdinalIgnoreCase));
var acousticPlaybackGain = ParseBoundedIntArgument(
    args,
    "--acoustic-gain",
    DefaultAcousticPlaybackGain,
    minimum: 1,
    maximum: 8);
var synthesizedAcoustic = args.Any(argument => string.Equals(
    argument,
    "--synthesized-acoustic",
    StringComparison.OrdinalIgnoreCase));
if (synthesizedAcoustic && (!syntheticMicrophonePlayback || !englishParakeet))
{
    throw new JourneyExpectationException(
        "--synthesized-acoustic requires --english-parakeet --live-microphone.");
}
if (manualMicrophone && !englishParakeet)
{
    throw new JourneyExpectationException(
        "--manual-microphone requires --english-parakeet for the fixed English acceptance phrase.");
}
// THE SPEAKERS STAY SILENT AND THE MICROPHONE STAYS UNTOUCHED. Acoustic journeys played the fixture
// through whatever the machine's default playback device was and recorded it through whatever its
// default microphone was - the founder's monitor speakers and webcam microphone, in the same room as
// the founder. `--virtual-cable` routes the same fixture, through the same production capture code,
// across VB-Audio's virtual cable instead: playback goes to the cable's render endpoint and the
// journey profile names the cable's capture endpoint as its preferred microphone. Both endpoints are
// opened by their own identity; the machine's default devices are never changed (the app's own device
// picker still reads which one is the default, as it always has).
var virtualCable = args.Any(argument => string.Equals(
    argument,
    "--virtual-cable",
    StringComparison.OrdinalIgnoreCase));
if (virtualCable && !syntheticMicrophonePlayback)
{
    throw new JourneyExpectationException("--virtual-cable requires --live-microphone.");
}
if (virtualCable && synthesizedAcoustic)
{
    throw new JourneyExpectationException(
        "--virtual-cable plays the reviewed fixture; Windows speech synthesis cannot be routed to the cable.");
}
if (manualMicrophone && ArgumentValue(args, "--acoustic-gain") is not null)
{
    throw new JourneyExpectationException(
        "--acoustic-gain applies only to synthetic fixture playback, not --manual-microphone.");
}
var failureArgument = ArgumentValue(args, "--failure");
var failureMode = failureArgument?.ToLowerInvariant() switch
{
    null => JourneyFailureMode.None,
    "microphone-unavailable" => JourneyFailureMode.MicrophoneUnavailable,
    "worker-startup" => JourneyFailureMode.WorkerStartup,
    "target-unavailable" => JourneyFailureMode.TargetUnavailable,
    _ => throw new JourneyExpectationException(
        "--failure must be microphone-unavailable, worker-startup, or target-unavailable."),
};
if (escapeRecovery && liveMicrophone)
{
    throw new JourneyExpectationException("Escape Recovery UAT uses the reviewed in-memory fixture, not live microphone mode.");
}
if (failureMode != JourneyFailureMode.None && (liveMicrophone || livePreview || escapeRecovery))
{
    throw new JourneyExpectationException("Failure journeys cannot be combined with live microphone, Live Preview, or Escape Recovery modes.");
}
if (failureMode != JourneyFailureMode.None && englishParakeet)
{
    throw new JourneyExpectationException("Failure journeys use the fixed reviewed Whisper fixture configuration.");
}
if (polishProvider != PolishProvider.None &&
    (liveMicrophone || livePreview || escapeRecovery || failureMode != JourneyFailureMode.None))
{
    throw new JourneyExpectationException(
        "Local-polish UAT uses the reviewed fixture success journey without Live Preview, live microphone, Escape Recovery, or failure injection.");
}
if (deterministicProfile != DeterministicJourneyProfile.None &&
    (!englishParakeet || liveMicrophone || livePreview || escapeRecovery ||
     failureMode != JourneyFailureMode.None || polishProvider != PolishProvider.None))
{
    throw new JourneyExpectationException(
        "Deterministic-profile UAT requires --english-parakeet and cannot be combined with local polish, Live Preview, live microphone, Escape Recovery, or failure injection.");
}
if (quickTap && !syntheticHotkey)
{
    throw new JourneyExpectationException(
        "--quick-tap modifies --synthetic-hotkey and means nothing without it: only the injected key has a "
            + "key-up to time.");
}
if (syntheticHotkey &&
    (liveMicrophone || (livePreview && !quickTap) || headStart || escapeRecovery || failureMode != JourneyFailureMode.None))
{
    throw new JourneyExpectationException(
        "--synthetic-hotkey drives the installed global hook with the silent reviewed fixture and cannot be "
            + "combined with live or manual microphone, the head start, Escape Recovery, or failure "
            + "injection: each of those owns the trigger or the hold in a different way. Live Preview is "
            + "accepted only with --quick-tap, where the question is what a key-up does to a preview "
            + "worker that is still starting.");
}
if (polishProvider == PolishProvider.EgOne)
{
    RequireAbsoluteFileArgument(
        egOneServerExecutable,
        "--eg1-server must identify an existing fully qualified llama-server.exe.",
        expectedFileName: "llama-server.exe");
    RequireAbsoluteFileArgument(
        egOneModelFile,
        "--eg1-model must identify an existing fully qualified GGUF model.",
        expectedExtension: ".gguf");
}
else if (egOneServerExecutable is not null || egOneModelFile is not null)
{
    throw new JourneyExpectationException("--eg1-server and --eg1-model require --polish eg-1.");
}
if (polishProvider == PolishProvider.Ollama)
{
    RequireLoopbackEndpoint(ollamaEndpoint);
    if (string.IsNullOrWhiteSpace(ollamaModel) || ollamaModel.Length > 256)
    {
        throw new JourneyExpectationException("--ollama-model must name one installed local model.");
    }
}
else if (ArgumentValue(args, "--ollama-endpoint") is not null || ollamaModel is not null)
{
    throw new JourneyExpectationException("--ollama-endpoint and --ollama-model require --polish ollama.");
}
var appExecutableArgument = ArgumentValue(args, "--app-executable");
if (appExecutableArgument is not null &&
    (!Path.IsPathFullyQualified(appExecutableArgument) ||
     !string.Equals(
         Path.GetFileName(appExecutableArgument),
         "EnviousWispr.App.exe",
         StringComparison.OrdinalIgnoreCase)))
{
    throw new JourneyExpectationException(
        "--app-executable must be a fully qualified EnviousWispr.App.exe path.");
}
var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var appExecutable = appExecutableArgument is null
    ? Path.Combine(
        repositoryRoot,
        "src",
        "Production",
        "EnviousWispr.App",
        "bin",
        "x64",
        "Release",
        "net10.0-windows10.0.26100.0",
        "win-x64",
        "EnviousWispr.App.exe")
    : Path.GetFullPath(appExecutableArgument);
var appSource = appExecutableArgument is null
    ? "RepositoryReleaseBuild"
    : "ExplicitCandidateExecutable";
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
var expectedSubstring = synthesizedAcoustic || manualMicrophone
    ? "microphone"
    : deterministicProfile == DeterministicJourneyProfile.Enabled
        ? "👍."
        : englishParakeet
            ? "account"
            : "adresse";
var forbiddenSubstring = deterministicProfile switch
{
    DeterministicJourneyProfile.Enabled => "um ",
    DeterministicJourneyProfile.Disabled => "👍",
    _ => null,
};
var fixtureFileName = englishParakeet ? "en-US-row0.wav" : "fr-FR-row0.wav";
var fixtureHash = englishParakeet ? ReviewedEnglishFixtureHash : ReviewedFrenchFixtureHash;
var fixtureIdentity = manualMicrophone
    ? "Founder-spoken-fixed-public-phrase"
    : synthesizedAcoustic
        ? "Windows-SAPI-fixed-public-phrase"
        : englishParakeet
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
RequireFile(
    Path.Combine(Path.GetDirectoryName(appExecutable)!, "EnviousWispr.RuntimeWorker.exe"),
    "The selected production app directory is missing its runtime worker executable.");
RequireFile(targetExecutable, "Build the controlled delivery target before journey UAT.");
RequireFile(fixturePath, "The reviewed public fixture is missing.");
RequireReviewedFixture(fixturePath, fixtureHash);
var appVersion = FileVersionInfo.GetVersionInfo(appExecutable).ProductVersion ?? "Unknown";
var appSha256 = Sha256Hex(appExecutable);
if (englishParakeet && !new LocalParakeetModelProbe().Probe(modelDirectory).Int8Complete)
{
    throw new JourneyExpectationException(
        "The gitignored Parakeet quantized model is required for English journey UAT.");
}
if (!englishParakeet && !new LocalWhisperModelProbe().Probe(modelDirectory).QuantizedComplete)
{
    throw new JourneyExpectationException(
        "The gitignored Whisper large-v3-turbo quantized model is required for journey UAT.");
}
if (livePreview && !new LocalWhisperModelProbe().Probe(previewModelDirectory).PreviewSmallComplete)
{
    throw new JourneyExpectationException(
        "The gitignored Whisper small preview model is required for live-preview journey UAT.");
}

EnsureNoUnownedProcesses("EnviousWispr.App", "EnviousWispr.Delivery.Target.Uat");
var audioRoute = virtualCable ? FindVirtualCableEndpoints() : null;
Func<Task> acousticStimulus = synthesizedAcoustic
    ? () => SpeakPublicPhraseAsync(SynthesizedAcousticPhrase)
    : () => PlayPublicFixtureAsync(
        fixturePath,
        acousticPlaybackGain,
        AcousticPlaybackRepetitions,
        audioRoute?.RenderId);
var acousticProbe = syntheticMicrophonePlayback
    ? await MeasureAcousticPlaybackAsync(acousticStimulus, audioRoute?.CaptureId)
    : null;
if (audioRoute is not null)
{
    RequireVirtualCableCarriedTheProbe(acousticProbe!);
}

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
if (livePreview || escapeRecovery || failureMode == JourneyFailureMode.MicrophoneUnavailable ||
    deterministicProfile != DeterministicJourneyProfile.None)
{
    var deterministicFeaturesEnabled = deterministicProfile != DeterministicJourneyProfile.Disabled;
    var journeySettings = AppSettings.Default with
    {
        HasCompletedOnboarding = true,
        PreferredMicrophoneId = audioRoute?.CaptureId,
        Preferences = AppSettings.Default.Preferences with
        {
            LivePreviewEnabled = livePreview,
            PillDesignWithWords = RecordingPillDesign.ReadingWell,
            Dictation = AppSettings.Default.Preferences.Dictation with
            {
                EscapeRecoveryEnabled = escapeRecovery,
                WordCorrectionEnabled = deterministicFeaturesEnabled,
                FillerRemovalEnabled = deterministicFeaturesEnabled,
                EmojiFormatterEnabled = deterministicFeaturesEnabled,
                SpokenPunctuationEnabled = deterministicFeaturesEnabled,
            },
        },
        UserData = deterministicProfile == DeterministicJourneyProfile.None
            ? ReusableUserData.Empty
            : new ReusableUserData(
                [new CustomWordEntry("account", "um thumbs up emoji period")],
                []),
    };
    await new JsonSettingsStore(Path.Combine(profileDirectory, "settings.json"))
        .SaveAsync(journeySettings);
}
else if (audioRoute is not null)
{
    // THE DEFAULT PROFILE PLUS ONE LINE. The block above is a staged profile - onboarding done, the
    // deterministic text features switched on - and the first cut of this mode fell into it just to name
    // a microphone, so adding `--virtual-cable` to the audible journey also changed what the text
    // pipeline did to the words. The silent journey must differ from the audible one by the endpoint
    // and nothing else.
    await new JsonSettingsStore(Path.Combine(profileDirectory, "settings.json"))
        .SaveAsync(AppSettings.Default with { PreferredMicrophoneId = audioRoute.CaptureId });
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
var exitEventName = $@"Local\EnviousLabs.EnviousWispr.JourneyUat.{runId}.exit";
using var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, exitEventName);

Process? target = null;
Process? app = null;
var timer = Stopwatch.StartNew();
var shellReady = false;
var runtimeReady = false;
var journeyCompleted = false;
var targetObserved = false;
var appExitedCleanly = false;
var strayWorkerCount = 0;
Process[] preexistingWorkers = [];
var pinnedWorkers = new List<Process>();
var ownedWorkerIds = Array.Empty<int>();
var ownedWorkerCount = 0;
var ownedPolishWorkerIds = Array.Empty<int>();
var ownedPolishWorkerCount = 0;
ClipboardGuard? clipboardGuard = null;
SyntheticHotkeyEvidence? syntheticHotkeyEvidence = null;
var usesPublicFixtureJourney = !liveMicrophone &&
    failureMode is JourneyFailureMode.None or JourneyFailureMode.TargetUnavailable;

// THE CATCH SITS OUTSIDE THE CLEANUP, and the first version did not. A `catch` before a `finally`
// runs first, so anything the cleanup itself threw - restoring the clipboard, removing the working
// directory - escaped past the report and replaced exit 2 with the crash this change exists to
// remove. Cleanup is the part most likely to fail on a machine that is already having a bad day.
try
{
    var targetStart = new ProcessStartInfo(targetExecutable)
    {
        UseShellExecute = false,
    };
    targetStart.ArgumentList.Add("--mode");
    targetStart.ArgumentList.Add(manualMicrophone ? "manual-microphone" : "edit");
    targetStart.ArgumentList.Add("--hold-focus-ms");
    targetStart.ArgumentList.Add("30000");
    targetStart.ArgumentList.Add("--result");
    targetStart.ArgumentList.Add(targetResultPath);
    targetStart.ArgumentList.Add("--expected-substring");
    targetStart.ArgumentList.Add(expectedSubstring);
    if (forbiddenSubstring is not null)
    {
        targetStart.ArgumentList.Add("--forbidden-substring");
        targetStart.ArgumentList.Add(forbiddenSubstring);
    }
    target = StartOrExplain(targetStart) ?? throw new JourneyExpectationException(
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
    appStart.Environment["ENVIOUSWISPR_POLISH_PROVIDER"] = polishProvider.ToString();
    if (polishProvider == PolishProvider.EgOne)
    {
        appStart.Environment["ENVIOUSWISPR_EG1_SERVER_EXE"] = egOneServerExecutable!;
        appStart.Environment["ENVIOUSWISPR_EG1_MODEL_PATH"] = egOneModelFile!;
        appStart.Environment.Remove("ENVIOUSWISPR_EG1_GPU_LAYERS");
    }
    else if (polishProvider == PolishProvider.Ollama)
    {
        appStart.Environment["ENVIOUSWISPR_OLLAMA_ENDPOINT"] = ollamaEndpoint;
        appStart.Environment["ENVIOUSWISPR_OLLAMA_MODEL"] = ollamaModel!;
    }
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
        if (syntheticHotkey)
        {
            // START is never signalled in this mode; the harness ends the run through this event once
            // the log and the target have spoken, and the app's own exit-after-completion does the rest.
            appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_EXIT_EVENT"] = exitEventName;
        }
        if (failureMode == JourneyFailureMode.TargetUnavailable)
        {
            appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_HOLD_MILLISECONDS"] = "2000";
        }
        else if (headStart)
        {
            // FOUR SECONDS BUYS SEVEN POLLS AT THE 500 ms INTERVAL, and the planner needs more than
            // one because it refuses to commit the final segment - a segment at the end of the audio
            // so far is indistinguishable from the first half of a word still being said. One poll
            // would assert nothing about committing.
            appStart.Environment["ENVIOUSWISPR_UAT_JOURNEY_HOLD_MILLISECONDS"] = "4000";
        }
    }
    // THE WORKERS THAT WERE ALREADY THERE, PINNED BY HANDLE. The stray-worker check after the app
    // exits excludes exactly these and nothing else: a worker that predates this app and is still
    // the same process. A clock cannot say that - a creation time compared across a daylight-saving
    // change or a clock adjustment orders two processes wrongly - but a handle held from before the
    // launch can, and one whose process has since exited excludes nothing, so an id the system reused
    // for a child of this app is counted.
    preexistingWorkers = Process.GetProcessesByName("EnviousWispr.RuntimeWorker");
    foreach (var worker in preexistingWorkers)
    {
        try
        {
            _ = worker.SafeHandle;
            pinnedWorkers.Add(worker);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // GONE OR NOT OURS TO OPEN, AND THEREFORE NOT AN EXCLUSION. An object whose pin failed
            // holds no handle; asked later whether it is alive it would reopen its id, which by then
            // may belong to a child of this app, and answer for the wrong process. Only a pin that
            // succeeded can exclude; this one is kept for disposal and nothing else.
        }
    }

    app = StartOrExplain(appStart) ?? throw new JourneyExpectationException(
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
        throw new JourneyExpectationException(
            $"The production shell or final-ASR worker did not become ready " +
            $"(shellReady={shellReady}, runtimeReady={runtimeReady}, " +
            $"appExited={app.HasExited}, exitCode={(app.HasExited ? app.ExitCode : null)}, " +
            $"events={startupEvents}).");
    }

    ownedWorkerIds = ChildProcessIds(app.Id, "EnviousWispr.RuntimeWorker").ToArray();
    var expectedWorkerCount = failureMode == JourneyFailureMode.WorkerStartup ? 0 : 1;
    if (ownedWorkerIds.Length != expectedWorkerCount)
    {
        throw new JourneyExpectationException(
            $"The production journey started {ownedWorkerIds.Length} owned final-ASR workers; " +
            $"expected {expectedWorkerCount}.");
    }

    if (IsLocalPolishProvider(polishProvider))
    {
        var polishReady = WaitForPolishRuntimeReady(
            diagnosticPath,
            polishProvider,
            TimeSpan.FromSeconds(60));
        if (!polishReady)
        {
            throw new JourneyExpectationException(
                $"The {PolishProviderName(polishProvider)} runtime did not become ready; " +
                $"events={string.Join(',', ReadDiagnosticEvents(diagnosticPath))}.");
        }

        ownedPolishWorkerIds = polishProvider == PolishProvider.EgOne
            ? ChildProcessIds(app.Id, "llama-server").ToArray()
            : [];
        var expectedPolishWorkerCount = polishProvider == PolishProvider.EgOne ? 1 : 0;
        if (ownedPolishWorkerIds.Length != expectedPolishWorkerCount)
        {
            throw new JourneyExpectationException(
                $"The production journey started {ownedPolishWorkerIds.Length} owned local-polish workers; " +
                $"expected {expectedPolishWorkerCount}.");
        }
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
        else if (manualMicrophone)
        {
            Console.WriteLine(
                "MANUAL ACTION: Keep the controlled target focused, hold F8, say the displayed " +
                "fixed public phrase clearly into the microphone, then release F8.");
            targetObserved = WaitForExpectedTargetResult(
                targetResultPath,
                TimeSpan.FromSeconds(25));
            journeyCompleted = targetObserved;
        }
        else if (liveMicrophone)
        {
            SendKey(F8, keyDown: true);
            try
            {
                Thread.Sleep(500);
                await acousticStimulus();
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

            if (syntheticHotkey)
            {
                syntheticHotkeyEvidence = DriveSyntheticHotkey(
                    diagnosticPath,
                    targetResultPath,
                    target.MainWindowHandle,
                    profileDirectory,
                    quickTap);
                exitEvent.Set();
            }
            else
            {
                startEvent.Set();
            }

            if (failureMode == JourneyFailureMode.TargetUnavailable)
            {
                if (!WaitForDiagnosticEvent(
                        diagnosticPath,
                        "DictationRecordingStarted/",
                        TimeSpan.FromSeconds(5)))
                {
                    throw new JourneyExpectationException(
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
                throw new JourneyExpectationException("The production journey did not complete within 60 seconds.");
            }

            targetObserved = WaitForExpectedTargetResult(
                targetResultPath,
                escapeRecovery || failureMode == JourneyFailureMode.TargetUnavailable
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromSeconds(5));
        }
    }

    if (audioRoute is not null)
    {
        // BEFORE THE TARGET VERDICT. A take that fell back to the real microphone is staging whatever the
        // target saw: heard, it would be a silent pass that was not silent; unheard, a product failure
        // that was not the product's. The start line is on disk long before the app exits.
        RequireNoMicrophoneFallback(ReadDiagnosticEvents(diagnosticPath));
    }

    if (!targetObserved && !escapeRecovery && failureMode == JourneyFailureMode.None)
    {
        if (liveMicrophone)
        {
            _ = app.WaitForExit(35_000);
        }

        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            failedJourney = manualMicrophone
                ? "physical-hotkey-microphone"
                : liveMicrophone
                    ? "acoustic-playback"
                    : "reviewed-fixture",
            appVersion,
            appSha256,
            appSource,
            engine = engineName,
            language,
            acousticProbe,
            diagnosticEvents = ReadDiagnosticEvents(diagnosticPath),
            targetCharacterCount = ReadTargetCharacterCount(targetResultPath),
        }));

        throw new JourneyExpectationException(
            manualMicrophone
                ? "The controlled target did not observe the founder-spoken physical microphone phrase."
                : liveMicrophone
                    ? "The controlled target did not observe the expected public microphone phrase."
                    : "The controlled target did not observe the expected public-fixture text.");
    }

    appExitedCleanly = app.WaitForExit(liveMicrophone ? 35_000 : 15_000);
    if (!appExitedCleanly)
    {
        throw new JourneyExpectationException("The production app did not exit cleanly after journey completion.");
    }

    // EVERY WORKER THE APP EVER OWNED, NOT THE ONE COUNTED BEFORE THE RECORDING. The owned-worker
    // snapshot above is taken before the take begins, so a preview worker started during it - or one
    // left behind by a cancelled preview startup - was invisible to the cleanup check. Windows keeps a
    // process's parent id after the parent has exited, so the app's children can still be asked for.
    // A process id is reused, so a worker orphaned by an earlier app that once had this id is excluded
    // only if it was listed and pinned before this app was launched and is still that same process.
    strayWorkerCount = ChildProcessIds(app.Id, "EnviousWispr.RuntimeWorker")
        .Where(IsProcessRunning)
        .Count(processId => !pinnedWorkers.Any(worker => worker.Id == processId && StillAlive(worker)));
    if (strayWorkerCount != 0)
    {
        throw new JourneyExpectationException(
            $"The production app exited and left {strayWorkerCount} runtime worker(s) it had started still running.");
    }

    if (app.ExitCode != 0)
    {
        throw new JourneyExpectationException(
            $"The production app returned exit code {app.ExitCode}; " +
            $"events={string.Join(',', ReadDiagnosticEvents(diagnosticPath))}.");
    }

    var diagnosticEvents = ReadDiagnosticEvents(diagnosticPath);
    if (failureMode != JourneyFailureMode.None)
    {
        RequireFailureJourneyEvents(failureMode, diagnosticEvents);
        if (targetObserved)
        {
            throw new JourneyExpectationException("A failure journey unexpectedly delivered text to the target.");
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
    if (livePreview && syntheticHotkey && quickTap)
    {
        RequireQuickTapCancelledThePreviewStartup(diagnosticEvents);
    }
    else if (livePreview)
    {
        RequireLivePreviewJourneyEvents(diagnosticEvents);
    }
    if (headStart)
    {
        RequireHeadStartJourneyEvents(diagnosticEvents);
    }
    var polishEvidence = ReadPolishJourneyEvidence(diagnosticPath, polishProvider);
    if (polishProvider != PolishProvider.None)
    {
        RequirePolishJourneyEvidence(polishProvider, polishEvidence);
    }
    var productionStagesObserved = failureMode is JourneyFailureMode.None or JourneyFailureMode.TargetUnavailable;
    var recoveryHistoryObserved = escapeRecovery &&
        ReadEscapeRecoveryHistory(Path.Combine(profileDirectory, "history.json"));
    if (escapeRecovery && (!recoveryHistoryObserved || targetObserved))
    {
        throw new JourneyExpectationException(
            "Escape Recovery must save one 24-hour undelivered History entry and deliver nothing to the target.");
    }

    ownedWorkerCount = ownedWorkerIds.Count(IsProcessRunning);
    if (ownedWorkerCount != 0)
    {
        throw new JourneyExpectationException("The production journey left an owned runtime worker running.");
    }
    ownedPolishWorkerCount = ownedPolishWorkerIds.Count(IsProcessRunning);
    if (ownedPolishWorkerCount != 0)
    {
        throw new JourneyExpectationException("The production journey left an owned local-polish worker running.");
    }

    var hardware = await new WindowsHardwareDiscovery(CudaRuntimeDirectory.ForTooling()).ProbeAsync();
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
        // A BOOLEAN CANNOT ANSWER THE QUESTION THIS FEATURE IS JUDGED ON. "Did any update arrive" was
        // true throughout the period when Live Preview put ONE stale fragment on screen and then
        // froze for the rest of the sentence: the arithmetic allowed exactly one update per take and
        // the harness reported success. The count is the measurement; the flag above only says the
        // path is wired. Ref: #99.
        livePreviewUpdateCount = diagnosticEvents.Count(value => value.StartsWith(
            "LivePreviewUpdated/",
            StringComparison.Ordinal)),
        // The quick-tap-with-preview certificate, in the result rather than only in the verdict.
        livePreviewStartupCancelled = diagnosticEvents.Any(value => value.StartsWith(
            "LivePreviewStartupCancelled/",
            StringComparison.Ordinal)),
        // WHAT THE HEAD START DID, AS COUNTS. The journey only refuses an abandoned head start; whether
        // any stretch was committed and whether the release used it depends on the fixture having a
        // pause, and a reader deciding whether the feature paid needs the numbers, not the verdict.
        streamingSegmentsCommitted = diagnosticEvents.Count(value => value.StartsWith(
            "StreamingSegmentCommitted/",
            StringComparison.Ordinal)),
        streamingHeadStartUsed = diagnosticEvents.Any(value => value.StartsWith(
            "StreamingHeadStartUsed/",
            StringComparison.Ordinal)),
        streamingAbandoned = diagnosticEvents.Any(value => value.StartsWith(
            "StreamingAbandoned/",
            StringComparison.Ordinal)),
        appExitedCleanly,
        ownedWorkerStartedCount = ownedWorkerIds.Length,
        ownedWorkerCount,
        strayWorkerCount,
        ownedPolishWorkerStartedCount = ownedPolishWorkerIds.Length,
        ownedPolishWorkerCount,
        elapsedMilliseconds = timer.ElapsedMilliseconds,
        appVersion,
        appSha256,
        appSource,
        windowsVersion = Environment.OSVersion.Version.ToString(),
        architecture = hardware.Architecture.ToString(),
        engine = engineName,
        language,
        provider,
        modelPack,
        acousticProbe,
        audioRoute = audioRoute is null
            ? liveMicrophone ? "MachineDefaultEndpoints" : null
            : $"VirtualCable: {audioRoute.RenderName} -> {audioRoute.CaptureName}",
        syntheticHotkey = syntheticHotkeyEvidence,
        polish = PolishProviderName(polishProvider),
        polishCompleted = polishEvidence.Completed,
        polishDegraded = polishEvidence.Degraded,
        polishErrorCode = polishEvidence.ErrorCode,
        polishElapsedMilliseconds = polishEvidence.ElapsedMilliseconds,
        deterministicProfile = deterministicProfile == DeterministicJourneyProfile.None
            ? null
            : deterministicProfile.ToString(),
        deterministicFeaturesEnabled = deterministicProfile == DeterministicJourneyProfile.None
            ? (bool?)null
            : deterministicProfile == DeterministicJourneyProfile.Enabled,
        inputKind = failureMode switch
        {
            JourneyFailureMode.MicrophoneUnavailable => "SyntheticF8-AllowlistedAccessDeniedAudioFault",
            JourneyFailureMode.WorkerStartup => "StartupFault-MissingOwnedWorkerExecutable",
            _ when manualMicrophone => "PhysicalF8-FounderSpeech-ProductionWasapi",
            _ when synthesizedAcoustic => "SyntheticF8-WindowsSpeechPlayback-ProductionWasapi",
            _ when virtualCable => "SyntheticF8-ReviewedFixturePlayback-VirtualCable-ProductionWasapi",
            _ when liveMicrophone => "SyntheticF8-ReviewedFixturePlayback-ProductionWasapi",
            _ when syntheticHotkey && quickTap => "SyntheticHotkeyQuickTap-InstalledGlobalHook-ReviewedFixtureAudioCapture",
            _ when syntheticHotkey => "SyntheticHotkey-InstalledGlobalHook-ReviewedFixtureAudioCapture",
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
            : manualMicrophone
                ? fixtureIdentity
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
    // STOPPING A CHILD CAN THROW, AND FAILING TO STOP ONE IS NOT A FAILED JOURNEY. Kill races the
    // process exiting on its own and throws when it loses, which was escaping the report entirely.
    StopQuietly(app, closeFirst: false);
    StopQuietly(target, closeFirst: true);

    app?.Dispose();
    target?.Dispose();
    try
    {
        clipboardGuard?.Dispose();
    }
    catch (Exception cleanupFailure) when (cleanupFailure is IOException
        or UnauthorizedAccessException or COMException or InvalidOperationException)
    {
        // A FAILED TIDY-UP IS NOT A FAILED JOURNEY, AND MUST NOT BE ABLE TO SAY IT IS. Restoring a
        // clipboard and deleting a working directory both fail for reasons that have nothing to do
        // with what was being tested - a file still open, a virus scanner holding a handle - and
        // when they threw here they escaped past the report and replaced exit 2 with the crash this
        // whole change removes. Cleanup is also the part most likely to fail on a machine that is
        // already having a bad day, which is exactly when the verdict matters most.
        Console.Error.WriteLine($"  cleanup warning: {cleanupFailure.GetType().Name}: {cleanupFailure.Message}");
    }
    finally
    {
        foreach (var worker in preexistingWorkers)
        {
            worker.Dispose();
        }

        try
        {
            RemoveUatDirectory(uatDirectory);
        }
        catch (Exception removalFailure) when (removalFailure is IOException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"  cleanup warning: could not remove {uatDirectory}: {removalFailure.Message}");
        }
    }
}
}
catch (JourneyExpectationException instrument) when (instrument.InstrumentInvalid)
{
    // THE HARNESS COULD NOT DO ITS JOB, AND THAT IS NOT A VERDICT ABOUT THE APP. Its own exit code and
    // its own words, so a runner can never average it into a pass rate or read it as a product FAIL.
    // The class of defect this stops is written on JourneyExpectationException.Instrument.
    Console.Error.WriteLine();
    Console.Error.WriteLine("INSTRUMENT INVALID");
    Console.Error.WriteLine($"  {instrument.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("The harness refused or could not stage this run. Nothing above is evidence about");
    Console.Error.WriteLine("the product; fix the instrument and run again.");
    return 3;
}
catch (JourneyExpectationException expectation)
{
    // REPORTED AND RETURNED, NOT THROWN PAST THE END OF THE PROGRAM. A failed expectation used to
    // escape the top-level statements and terminate the process, and Windows records that in the
    // event log exactly as it records an application fault - so on 2026-08-28 eleven failing tests
    // from this harness were counted as evidence that the machine itself was unstable.
    //
    // Anything that is NOT an expectation is deliberately not caught here. A null reference or a
    // genuine fault in the app under test still propagates, still terminates, and still looks like
    // the crash it is, which is the distinction the whole change exists to restore.
    Console.Error.WriteLine();
    Console.Error.WriteLine("JOURNEY EXPECTATION NOT MET");
    Console.Error.WriteLine($"  {expectation.Message}");
    if (expectation.InnerException is { } cause)
    {
        // The message says what did not happen; this says what Windows said about it.
        Console.Error.WriteLine($"  caused by: {cause.GetType().Name}: {cause.Message}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("The harness stopped for a reason it can name, which is not a crash. The");
    Console.Error.WriteLine("artifacts written above say what the run observed; compare them with the");
    Console.Error.WriteLine("message. A genuine fault would have terminated the process instead.");
    return 2;
}

/// <summary>Ends a child process, and never lets the ending decide the journey's verdict.</summary>
/// <remarks>
/// `Kill` races the process exiting by itself and throws when it loses, and `CloseMainWindow` throws
/// on a process that has already gone. Neither says anything about what was being tested, and both
/// were able to replace the harness's own report with a crash.
/// </remarks>
/// <summary>Starts a process, and turns an ordinary launch failure into something readable.</summary>
/// <remarks>
/// A DIFFERENT CLASS FROM THE DELIBERATE THROWS, AND THE LAST ONE FOUND. Everything the harness
/// raises itself now carries one type and a gate refuses a stock one. This is the RUNTIME raising
/// `Win32Exception` because an executable is missing or will not run - an ordinary environment
/// failure, not a fault in anything, and it was still ending the run as a crash in the event log.
///
/// Wrapped HERE rather than by widening the reporter's catch, deliberately. Catching Win32Exception
/// at the top would also swallow one thrown from somewhere it really does mean a fault, and the
/// whole point of this work is that the two stay distinguishable.
/// </remarks>
static Process? StartOrExplain(ProcessStartInfo startInfo)
{
    try
    {
        return Process.Start(startInfo);
    }
    catch (Exception failure) when (failure is System.ComponentModel.Win32Exception
        or InvalidOperationException or PlatformNotSupportedException)
    {
        throw new JourneyExpectationException(
            $"Windows would not start {startInfo.FileName}.", failure);
    }
}

static void StopQuietly(Process? process, bool closeFirst)
{
    try
    {
        if (process is null || process.HasExited)
        {
            return;
        }

        if (closeFirst)
        {
            process.CloseMainWindow();
            if (process.WaitForExit(5_000))
            {
                return;
            }
        }

        process.Kill(entireProcessTree: true);
        process.WaitForExit(10_000);
    }
    catch (Exception failure) when (failure is InvalidOperationException
        or System.ComponentModel.Win32Exception or NotSupportedException)
    {
        Console.Error.WriteLine($"  cleanup warning: could not stop a child process: {failure.Message}");
    }
}

static void RequireFile(string path, string message)
{
    if (!File.Exists(path))
    {
        throw new JourneyExpectationException($"{message} Looked for: {path}");
    }
}

static void RequireAbsoluteFileArgument(
    string? path,
    string message,
    string? expectedFileName = null,
    string? expectedExtension = null)
{
    if (string.IsNullOrWhiteSpace(path) ||
        !Path.IsPathFullyQualified(path) ||
        !File.Exists(path) ||
        expectedFileName is not null && !string.Equals(
            Path.GetFileName(path),
            expectedFileName,
            StringComparison.OrdinalIgnoreCase) ||
        expectedExtension is not null && !string.Equals(
            Path.GetExtension(path),
            expectedExtension,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new JourneyExpectationException(message);
    }
}

static void RequireLoopbackEndpoint(string endpoint)
{
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
        !uri.IsLoopback ||
        uri.Scheme is not ("http" or "https") ||
        !string.IsNullOrEmpty(uri.UserInfo))
    {
        throw new JourneyExpectationException(
            "--ollama-endpoint must be a loopback HTTP or HTTPS address without credentials.");
    }
}

static string PolishProviderName(PolishProvider provider) => provider switch
{
    PolishProvider.None => "None",
    PolishProvider.EgOne => "EgOne",
    PolishProvider.Ollama => "Ollama",
    PolishProvider.OpenAI => "OpenAI",
    PolishProvider.Anthropic => "Anthropic",
    PolishProvider.Gemini => "Gemini",
    _ => throw new JourneyExpectationException(nameof(provider)),
};

static string DiagnosticProviderName(PolishProvider provider) => provider switch
{
    PolishProvider.OpenAI => "OpenAi",
    _ => PolishProviderName(provider),
};

static bool IsLocalPolishProvider(PolishProvider provider) =>
    provider is PolishProvider.EgOne or PolishProvider.Ollama;

static bool IsCloudPolishProvider(PolishProvider provider) =>
    provider is PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini;

static void RequireReviewedFixture(string path, string expectedHash)
{
    var file = new FileInfo(path);
    if (file.Length is <= 0 or > 1_000_000)
    {
        throw new JourneyExpectationException("The reviewed public fixture has an unexpected size.");
    }

    using var stream = file.OpenRead();
    var actualHash = Convert.ToHexString(SHA256.HashData(stream));
    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
    {
        throw new JourneyExpectationException("The reviewed public fixture hash does not match.");
    }
}

static string Sha256Hex(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
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
            throw new JourneyExpectationException(
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
            throw new JourneyExpectationException("The controlled delivery target exited before it was ready.");
        }

        process.Refresh();
        if (process.MainWindowHandle != 0)
        {
            return;
        }

        Thread.Sleep(100);
    }

    throw new JourneyExpectationException("The controlled delivery target did not show a window.");
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
                if (document.RootElement.GetProperty("containsExpected").GetBoolean() &&
                    (!document.RootElement.TryGetProperty("containsForbidden", out var forbidden) ||
                     !forbidden.GetBoolean()))
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

/// <summary>
/// The log's lines, read without locking the app out of its own file. `File.ReadLines` shares the
/// file for reading only, so an append the app makes while this reader holds it fails with a sharing
/// violation and that line is lost - the app's diagnostics are best-effort and swallow the failure.
/// One required stage went missing from a passing journey that way, once in about four runs.
/// </summary>
static IEnumerable<string> ReadSharedLines(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    while (reader.ReadLine() is { } line)
    {
        yield return line;
    }
}

static IReadOnlyList<string> ReadDiagnosticEvents(string path)
{
    if (!File.Exists(path))
    {
        return ["DiagnosticsUnavailable"];
    }

    var events = new List<string>();
    foreach (var line in ReadSharedLines(path))
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

static bool WaitForPolishRuntimeReady(
    string diagnosticPath,
    PolishProvider provider,
    TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        var evidence = ReadPolishJourneyEvidence(diagnosticPath, provider);
        if (evidence.RuntimeReady)
        {
            return true;
        }

        if (evidence.RuntimeDegraded)
        {
            return false;
        }

        Thread.Sleep(100);
    }

    return false;
}

static PolishJourneyEvidence ReadPolishJourneyEvidence(
    string path,
    PolishProvider provider)
{
    if (provider == PolishProvider.None || !File.Exists(path))
    {
        return new PolishJourneyEvidence(false, false, false, false, false, null, null);
    }

    var expectedProvider = DiagnosticProviderName(provider);
    var runtimeReady = false;
    var runtimeDegraded = false;
    var started = false;
    var completed = false;
    var degraded = false;
    string? errorCode = null;
    long? elapsedMilliseconds = null;
    try
    {
        foreach (var line in ReadSharedLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventName = root.TryGetProperty("event", out var eventElement)
                ? eventElement.GetString()
                : null;
            var diagnosticProvider = root.TryGetProperty("provider", out var providerElement)
                ? providerElement.GetString()
                : null;
            var providerMatches = string.Equals(
                diagnosticProvider,
                expectedProvider,
                StringComparison.Ordinal);
            switch (eventName)
            {
                case "PolishRuntimeReady":
                    runtimeReady |= providerMatches;
                    break;
                case "PolishRuntimeDegraded":
                    runtimeDegraded |= providerMatches;
                    break;
                case "PolishStarted":
                    started |= providerMatches;
                    break;
                case "PolishCompleted":
                    if (!providerMatches)
                    {
                        break;
                    }
                    completed = true;
                    if (root.TryGetProperty("elapsedMilliseconds", out var elapsedElement) &&
                        elapsedElement.ValueKind == JsonValueKind.Number &&
                        elapsedElement.TryGetInt64(out var parsedElapsed))
                    {
                        elapsedMilliseconds = parsedElapsed;
                    }
                    break;
                case "PolishDegraded":
                    if (!providerMatches)
                    {
                        break;
                    }
                    degraded = true;
                    if (root.TryGetProperty("errorCode", out var errorElement) &&
                        errorElement.ValueKind == JsonValueKind.String)
                    {
                        errorCode = errorElement.GetString();
                    }
                    if (root.TryGetProperty("elapsedMilliseconds", out var degradedElapsed) &&
                        degradedElapsed.ValueKind == JsonValueKind.Number &&
                        degradedElapsed.TryGetInt64(out var parsedDegradedElapsed))
                    {
                        elapsedMilliseconds = parsedDegradedElapsed;
                    }
                    break;
            }
        }
    }
    catch (Exception exception) when (exception is IOException or JsonException)
    {
        return new PolishJourneyEvidence(false, false, false, false, false, null, null);
    }

    return new PolishJourneyEvidence(
        runtimeReady,
        runtimeDegraded,
        started,
        completed,
        degraded,
        errorCode,
        elapsedMilliseconds);
}

static void RequirePolishJourneyEvidence(
    PolishProvider provider,
    PolishJourneyEvidence evidence)
{
    if (IsCloudPolishProvider(provider))
    {
        if (evidence.RuntimeReady ||
            evidence.RuntimeDegraded ||
            !evidence.Started ||
            evidence.Completed ||
            !evidence.Degraded ||
            !string.Equals(
                evidence.ErrorCode,
                "PolishCredentialMissing",
                StringComparison.Ordinal))
        {
            throw new JourneyExpectationException(
                $"The {PolishProviderName(provider)} journey did not preserve deterministic text " +
                $"through the expected isolated missing-credential fallback " +
                $"(runtimeReady={evidence.RuntimeReady}, runtimeDegraded={evidence.RuntimeDegraded}, " +
                $"started={evidence.Started}, completed={evidence.Completed}, degraded={evidence.Degraded}, " +
                $"errorCode={evidence.ErrorCode ?? "none"}).");
        }

        return;
    }

    if (!evidence.RuntimeReady ||
        evidence.RuntimeDegraded ||
        !evidence.Started ||
        !evidence.Completed ||
        evidence.Degraded)
    {
        throw new JourneyExpectationException(
            $"The {PolishProviderName(provider)} journey did not complete healthy local polish " +
            $"(runtimeReady={evidence.RuntimeReady}, runtimeDegraded={evidence.RuntimeDegraded}, " +
            $"started={evidence.Started}, completed={evidence.Completed}, degraded={evidence.Degraded}).");
    }
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
        throw new JourneyExpectationException(
            $"The production journey omitted required content-free stages: {string.Join(", ", missing)}.");
    }
}

/// <summary>
/// A quick tap with Live Preview on: the key-up runs while the preview worker is still starting, and
/// the app must record that it cancelled the startup rather than wait for the worker to answer. A run
/// where the worker answered before the queued release ran certifies nothing about that and is asked
/// to run again, the same way a quick tap that did not overlap the press is.
/// </summary>
static void RequireQuickTapCancelledThePreviewStartup(IReadOnlyList<string> events)
{
    static bool Has(IReadOnlyList<string> events, string name) =>
        events.Any(value => value.StartsWith(name + '/', StringComparison.Ordinal));
    if (Has(events, "LivePreviewStartupCancelled"))
    {
        return;
    }

    if (Has(events, "LivePreviewStarted"))
    {
        throw JourneyExpectationException.Instrument(
            "The quick tap delivered, but the preview worker had answered before the queued key-up ran, so "
                + "this run says nothing about a release during preview startup. Re-run; if the worker always "
                + "wins, the tap is not quick enough on this machine. "
                + $"events={string.Join(',', events)}.");
    }

    throw new JourneyExpectationException(
        "The quick tap with Live Preview on recorded neither a cancelled preview startup nor a started preview; "
            + $"events={string.Join(',', events)}.");
}

static void RequireLivePreviewJourneyEvents(IReadOnlyList<string> events)
{
    var required = new[] { "LivePreviewStarted", "LivePreviewUpdated", "LivePreviewStopped" };
    var missing = required.Where(eventName => !events.Any(value => value.StartsWith(
        eventName + '/',
        StringComparison.Ordinal))).ToArray();
    if (missing.Length > 0)
    {
        // THE EVENTS THAT WERE SEEN ARE NAMED, because "omitted" alone sends whoever reads it back to
        // rerun with a log copy. They are event names and failure categories, never content.
        throw new JourneyExpectationException(
            $"The live-preview journey omitted content-free stages: {string.Join(", ", missing)}; "
                + $"events={string.Join(',', events)}.");
    }
}

/// <summary>Requires a recording and rejects abandonment; reports commits and head-start use rather than requiring them.</summary>
/// <remarks>
/// THE SUMMARY USED TO SAY THE RELEASE USED A COMMIT, and the body below explains why it cannot ask
/// for one on these fixtures. What it asserts is a recording and no abandonment; what it reports, in
/// the result, is how many segments were committed and whether the release used them.
///
/// ABANDONING IS CORRECT BEHAVIOUR AND IS STILL A FAILURE HERE. The head start gives up on any error
/// rather than delivering half a dictation, and that design stays. This mode exists to assert the
/// happy path is reachable at all - on a clean fixture, on a machine with a working engine, there is
/// nothing to give up over.
/// </remarks>
static void RequireHeadStartJourneyEvents(IReadOnlyList<string> events)
{
    // AN EARLIER VERSION ALSO DEMANDED A COMMITTED SEGMENT, AND THAT WAS WRONG. It passed only
    // because the fixture capture was lying: it handed the whole WAV to the first snapshot, so the
    // planner saw a complete recording half a second in and committed nearly all of it at once. Once
    // the fixture began arriving at the sample rate, as a microphone does, the same run committed
    // NOTHING - and that is correct behaviour rather than a regression.
    //
    // THESE RUNS HAVE REPORTED ZERO COMMITS, AND THE CAUSE IS NOT ASSERTED HERE. A poll can commit
    // at least 1.5 s of speech followed by a qualifying silence - including a silence at the end of
    // the audio so far, since the planner commits through the silence that follows the speech it
    // visits. Whether these fixtures expose that opportunity depends on how they segment and on when
    // the polls land, which this journey does not measure. Requiring a commit would assert something
    // about the audio; the count is reported in the result instead.
    //
    // SO IT ASSERTS THE THING THAT WAS ACTUALLY BROKEN. Before the overflow fix the head start threw
    // on the FIRST poll of every recording ever made and was abandoned every time, so "did not give
    // up" is exactly the regression this catches - and it catches it on a fixture this short.
    //
    // WHAT IT DOES NOT PROVE is that the head start ever saves anybody time. That needs a fixture
    // long enough to contain a pause, which this repository does not have. Ref: #96, #85.
    if (!events.Any(value => value.StartsWith(
        "DictationRecordingStarted/",
        StringComparison.Ordinal)))
    {
        throw new JourneyExpectationException(
            "The head-start journey never recorded, so nothing about the head start was exercised.");
    }

    if (events.Any(value => value.StartsWith("StreamingAbandoned/", StringComparison.Ordinal)))
    {
        throw new JourneyExpectationException(
            "The streaming head start was abandoned on a clean fixture run. Giving up is correct on "
                + "a real failure, so read the abandoned record's error code: it names what failed.");
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
        throw new JourneyExpectationException(
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

/// <summary>
/// Whether a process pinned by handle before the launch is still that process; one that cannot say is
/// not. Only ever asked of a process whose handle was taken, so the answer is the handle's and not a
/// reopened id's.
/// </summary>
static bool StillAlive(Process pinned)
{
    try
    {
        return !pinned.HasExited;
    }
    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return false;
    }
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

static async Task PlayPublicFixtureAsync(
    string fixturePath,
    int gain,
    int repetitions,
    string? renderEndpointId = null)
{
    var (pcmBytes, sampleRate) = ReadReviewedMuLawFixture(
        fixturePath,
        gain);
    pcmBytes = RepeatPcm(pcmBytes, sampleRate, repetitions);
    using var stream = new MemoryStream(pcmBytes, writable: false);
    using var source = new RawSourceWaveStream(stream, new WaveFormat(sampleRate, 16, 1));
    // THE PLAYER KEEPS THE DEVICE. Disposing the device before the player left the player's volume
    // control on a released session manager and the first cable run crashed setting it; the player
    // does not dispose the device either. Declared before the player, so it is released after it.
    using var enumerator = new MMDeviceEnumerator();
    var playbackDuration = TimeSpan.FromSeconds(
        pcmBytes.Length / (sampleRate * sizeof(short) * 1d));
    Exception? failure;
    try
    {
        using var renderDevice = renderEndpointId is null ? null : enumerator.GetDevice(renderEndpointId);
        await using var output = renderDevice is null
            ? await new WasapiPlayerBuilder()
                .WithDefaultDeviceStreamRouting()
                .BuildAsync()
            : new WasapiPlayerBuilder()
                .WithDevice(renderDevice)
                .Build();
        var completed = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) => completed.TrySetResult(args.Exception);
        output.Init(source);
        output.Volume = 1f;
        output.Play();
        failure = await completed.Task.WaitAsync(playbackDuration + TimeSpan.FromSeconds(5));
    }
    catch (Exception exception) when (renderEndpointId is not null &&
        exception is COMException or CoreAudioException or InvalidOperationException or TimeoutException)
    {
        // ON THE CABLE, A PLAYBACK FAILURE IS STAGING. The cable's render endpoint could not be opened,
        // was taken exclusively, changed under the player, or never finished: the driver or another
        // program, never the product. Anything else that escapes is a programming fault and stays one.
        throw JourneyExpectationException.Instrument(
            $"--virtual-cable: the fixture could not be played into the cable ({exception.GetType().Name}: "
                + $"{exception.Message}). The cable, not the product, refused playback.",
            exception);
    }

    if (failure is not null)
    {
        if (renderEndpointId is not null)
        {
            throw JourneyExpectationException.Instrument(
                $"--virtual-cable: playback into the cable stopped with {failure.GetType().Name}: {failure.Message}. "
                    + "The cable, not the product, interrupted playback.",
                failure);
        }

        throw new JourneyExpectationException("The reviewed public fixture could not be played.", failure);
    }
}

static async Task<AcousticProbeMetrics> MeasureAcousticPlaybackAsync(
    Func<Task> playStimulus,
    string? captureEndpointId = null)
{
    ArgumentNullException.ThrowIfNull(playStimulus);
    await using var capture = new WasapiAudioCapture();
    var levelGate = new object();
    var levelEvents = 0;
    var observedPeak = 0f;
    double observedRmsSum = 0;
    capture.LevelChanged += (_, level) =>
    {
        lock (levelGate)
        {
            levelEvents++;
            observedPeak = Math.Max(observedPeak, level.Peak);
            observedRmsSum += level.RootMeanSquare;
        }
    };

    var started = await capture.StartAsync(new AudioCaptureRequest(
        DictationSessionId.Create(),
        captureEndpointId is null ? null : new AudioDeviceId(captureEndpointId)));
    if (!started.Succeeded)
    {
        return new AcousticProbeMetrics(
            Started: false,
            Outcome: "StartFailed",
            Error: started.Error?.Code.ToString(),
            DurationMilliseconds: 0,
            LevelEvents: 0,
            Peak: 0,
            AverageLevelRootMeanSquare: 0,
            CapturedRootMeanSquare: 0);
    }

    await Task.Delay(TimeSpan.FromMilliseconds(500));
    await playStimulus();
    await Task.Delay(TimeSpan.FromMilliseconds(500));
    var result = await capture.StopAsync();
    var samples = result.Samples.ToArray();
    var sumOfSquares = samples.Sum(sample => (double)sample * sample);
    double averageLevelRootMeanSquare;
    lock (levelGate)
    {
        averageLevelRootMeanSquare = levelEvents == 0 ? 0 : observedRmsSum / levelEvents;
    }

    return new AcousticProbeMetrics(
        Started: true,
        Outcome: result.Outcome.ToString(),
        Error: result.Error?.Code.ToString(),
        DurationMilliseconds: samples.Length * 1_000L / AudioSampleConverter.TargetSampleRate,
        LevelEvents: levelEvents,
        Peak: observedPeak,
        AverageLevelRootMeanSquare: averageLevelRootMeanSquare,
        CapturedRootMeanSquare: samples.Length == 0 ? 0 : Math.Sqrt(sumOfSquares / samples.Length));
}

/// <summary>
/// The two ends of VB-Audio's virtual cable, found by their own names. The machine's default playback
/// and recording devices are never consulted: the whole point of the cable is that a journey can run
/// without touching, or depending on, whatever the founder has selected. Either end missing is a
/// staging failure - the driver is not installed or not running - and says nothing about the product.
/// </summary>
static VirtualCableRoute FindVirtualCableEndpoints()
{
    const string RenderName = "CABLE Input (VB-Audio Virtual Cable)";
    const string CaptureName = "CABLE Output (VB-Audio Virtual Cable)";
    using var enumerator = new MMDeviceEnumerator();
    using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
    var renderIds = new List<string>();
    var captureIds = new List<string>();
    var monitoredCapture = false;
    foreach (var device in devices)
    {
        using (device)
        {
            if (device.DataFlow == DataFlow.Render && device.FriendlyName == RenderName)
            {
                renderIds.Add(device.ID);
            }
            else if (device.DataFlow == DataFlow.Capture && device.FriendlyName == CaptureName)
            {
                captureIds.Add(device.ID);
                monitoredCapture |= IsListenToThisDeviceEnabled(device);
            }
        }
    }

    if (renderIds.Count != 1 || captureIds.Count != 1)
    {
        // A NAME IS NOT AN IDENTITY. Windows allows two endpoints to share a friendly name, so one of each
        // is the only count this mode accepts; more is ambiguity and none is absence, and both are staging.
        throw JourneyExpectationException.Instrument(
            $"--virtual-cable needs exactly one active \"{RenderName}\" playback endpoint and exactly one "
                + $"active \"{CaptureName}\" recording endpoint; found playback={renderIds.Count}, "
                + $"recording={captureIds.Count}. Install VB-CABLE, or check the device is enabled.");
    }

    if (monitoredCapture)
    {
        // "LISTEN TO THIS DEVICE" TURNS THE CABLE BACK INTO SOUND. Windows can forward a recording endpoint
        // to a playback device, so a cable with that switch on plays the fixture through the speakers
        // after all. The endpoint choice cannot promise silence while that switch is on; refuse instead.
        throw JourneyExpectationException.Instrument(
            $"--virtual-cable: Windows \"Listen to this device\" is enabled on \"{CaptureName}\", which "
                + "forwards the cable to a playback device and would make this run audible. Turn it off in the "
                + "recording device's properties before running a silent journey.");
    }

    return new VirtualCableRoute(RenderName, renderIds[0], CaptureName, captureIds[0]);
}

/// <summary>
/// The endpoint property behind the "Listen to this device" checkbox. Windows answers an absent setting with
/// an empty value (off); a read that FAILS is not "off" - NAudio's `Contains` folds both into false, which
/// is how the first cut would have let a monitored cable play. The indexer throws on a failed read, and that
/// throw is a refusal.
/// </summary>
static bool IsListenToThisDeviceEnabled(MMDevice device)
{
    var listenEnabled = new PropertyKey(new Guid("24dbb0fc-9311-4b3d-9cf0-18ff155639d4"), 1);
    object? value;
    try
    {
        value = device.Properties[listenEnabled].Value;
    }
    catch (Exception exception) when (exception is COMException or CoreAudioException
        or NotImplementedException or NotSupportedException or ArgumentOutOfRangeException)
    {
        // The last three are NAudio's own decoder refusing a variant it does not understand - the
        // value never reaches the switch below, and an undecodable answer is still not "off".
        throw JourneyExpectationException.Instrument(
            "--virtual-cable: Windows would not say whether \"Listen to this device\" is on for the cable "
                + $"({exception.GetType().Name}: {exception.Message}); refusing rather than assuming it is off.",
            exception);
    }

    return value switch
    {
        null => false,
        bool enabled => enabled,
        _ => throw JourneyExpectationException.Instrument(
            $"--virtual-cable: the \"Listen to this device\" setting came back as {value.GetType().Name}, not a "
                + "yes or no; refusing rather than guessing."),
    };
}

/// <summary>
/// The cable must have carried the preflight stimulus, or nothing about the journey is staged. A probe that
/// did not start, did not finish, or heard nothing is the driver, an exclusive-mode holder or a muted cable -
/// staging, never the product - and the first cut only recorded it in the result while the journey ran on.
/// </summary>
static void RequireVirtualCableCarriedTheProbe(AcousticProbeMetrics probe)
{
    const float MinimumAudiblePeak = 0.05f;
    if (!probe.Started || probe.Outcome != "Completed" || probe.Peak < MinimumAudiblePeak)
    {
        throw JourneyExpectationException.Instrument(
            "--virtual-cable: the preflight fixture did not come back through the cable "
                + $"(started={probe.Started}, outcome={probe.Outcome}, error={probe.Error ?? "none"}, "
                + $"peak={probe.Peak:F3}, minimum={MinimumAudiblePeak:F2}). The cable, not the product, is not "
                + "carrying audio: check the driver is running, nothing holds the endpoint exclusively, and the "
                + "cable is not muted.");
    }
}

/// <summary>
/// The app keeps dictating on the machine's default microphone when its preferred one fails to open - the
/// right thing for a founder whose headset unplugged, and the wrong thing here, where the "default"
/// microphone is the founder's real one. The recording-start log line carries the fallback reason when
/// that happened, so the take's own record decides, after the fact, whether the cable was the microphone
/// for the whole take. A preflight cannot close this: the endpoint can vanish between it and the key.
/// </summary>
static void RequireNoMicrophoneFallback(IReadOnlyList<string> events)
{
    // A clean start is logged as "DictationRecordingStarted/None"; a fallback start carries the failure
    // category and the error code after the slash instead.
    var fallbacks = events
        .Where(value => value.StartsWith("DictationRecordingStarted/", StringComparison.Ordinal)
            && value != "DictationRecordingStarted/None")
        .ToList();
    if (fallbacks.Count > 0)
    {
        throw JourneyExpectationException.Instrument(
            "--virtual-cable: the app could not open the cable when the key went down and recorded from the "
                + $"machine's default microphone instead ({string.Join(", ", fallbacks)}). The result is not a "
                + "silent run and is not reported as one.");
    }
}

static async Task SpeakPublicPhraseAsync(string phrase)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
    var completion = new TaskCompletionSource<Exception?>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        object? voice = null;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice") ??
                throw new JourneyExpectationException("Windows speech synthesis is unavailable.");
            voice = Activator.CreateInstance(voiceType) ??
                throw new JourneyExpectationException("Windows speech synthesis could not start.");
            _ = voiceType.InvokeMember(
                "Volume",
                BindingFlags.SetProperty,
                binder: null,
                voice,
                [100],
                System.Globalization.CultureInfo.InvariantCulture);
            _ = voiceType.InvokeMember(
                "Rate",
                BindingFlags.SetProperty,
                binder: null,
                voice,
                [0],
                System.Globalization.CultureInfo.InvariantCulture);
            _ = voiceType.InvokeMember(
                "Speak",
                BindingFlags.InvokeMethod,
                binder: null,
                voice,
                [phrase, 0],
                System.Globalization.CultureInfo.InvariantCulture);
            completion.TrySetResult(null);
        }
        catch (Exception exception) when (exception is JourneyExpectationException
            or COMException or TargetInvocationException
            or NotSupportedException or UnauthorizedAccessException)
        {
            // ONLY THE FAILURES SPEECH SYNTHESIS ACTUALLY HAS. Catching every Exception here turned
            // a NullReferenceException or an OutOfMemoryException on this thread into an ordinary
            // "could not be synthesized" expectation - which is the reverse of the defect this whole
            // change exists to fix, and harder to see: a real fault reported as a tidy test failure
            // is a fault nobody goes looking for.
            //
            // JourneyExpectationException is FIRST in the list because the thread throws it itself,
            // for SAPI being unavailable or refusing to start. A first draft narrowed this filter
            // without adding the type it had just introduced, so the harness's own expected failure
            // terminated the process - the exact defect, reintroduced by the fix for it.
            //
            // TargetInvocationException because the voice is created by reflection, which wraps
            // whatever the constructor threw. InvalidOperationException is deliberately NOT here any
            // more: the conditions that used to raise it now raise the expectation type, so anything
            // still throwing it on this thread is an implementation fault and should read as one.
            //
            // Anything else is left to terminate the process, which is what a fault on a background
            // thread should do.
            completion.TrySetResult(exception);
        }
        finally
        {
            if (voice is not null && Marshal.IsComObject(voice))
            {
                _ = Marshal.FinalReleaseComObject(voice);
            }
        }
    })
    {
        IsBackground = true,
        Name = "EnviousWispr acoustic UAT speech",
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    var failure = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    if (failure is not null)
    {
        throw new JourneyExpectationException(
            "The fixed public phrase could not be synthesized through the default speakers.",
            failure);
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
        throw new JourneyExpectationException("The reviewed fixture is not a RIFF WAVE file.");
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
            throw new JourneyExpectationException("The reviewed fixture has an invalid chunk.");
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
                throw new JourneyExpectationException("The reviewed fixture is not mono 8-bit mu-law audio.");
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

    throw new JourneyExpectationException("The reviewed fixture has no supported audio data.");
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

/// <summary>Presses or releases one key, and says NOTHING about whether the app saw it.</summary>
/// <remarks>
/// THE PAYLOAD IS CHECKED BEFORE THE SYSCALL, BECAUSE NOTHING AFTER IT CAN. On 2026-09-04 and 05 a
/// probe built its INPUT struct by PowerShell nested assignment, which writes to a copy, and handed
/// SendInput a well-formed struct requesting nothing. SendInput inserted it and returned 1 - correctly.
/// Two sessions then concluded injected input was inert on this machine and built five explanations on
/// a request for zero movement. A 48-byte struct in a second helper was rejected outright for the same
/// lack of a check. Both are caught here, for someone who never read this.
///
/// IT RETURNS VOID ON PURPOSE. SendInput's count is about insertion, not arrival, and a value present
/// is a value somebody will read as "the OS said it worked". A count mismatch is an instrument failure
/// and is reported as one; success is only ever established from app.jsonl afterwards.
/// </remarks>
static void SendKey(byte virtualKey, bool keyDown)
{
    if (Marshal.SizeOf<Input>() != 40)
    {
        throw JourneyExpectationException.Instrument(
            "The synthetic keyboard input does not match the Win64 ABI (INPUT must be 40 bytes).");
    }

    if (virtualKey == 0)
    {
        throw JourneyExpectationException.Instrument(
            "Refusing to send an empty keyboard event: no virtual key was resolved.");
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
    if (input.Data.Keyboard.VirtualKey == 0 && input.Data.Keyboard.ScanCode == 0)
    {
        throw JourneyExpectationException.Instrument(
            "Refusing to send an empty keyboard event: the payload carries no key.");
    }

    if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
    {
        throw JourneyExpectationException.Instrument(
            "Synthetic keyboard input was not inserted into the input stream.");
    }
}

/// <summary>The take that starts the way a finger starts it, with nothing heard in the room.</summary>
/// <remarks>
/// EVERY VERDICT HERE IS READ FROM THE APP'S OWN LOG OR THE TARGET, NEVER FROM THE INJECTOR. This is
/// the method the macOS harness ("WisprEyes") arrived at after a summer of brute force: make something
/// happen, then read what the app said about itself. Its failures all came from trusting the harness's
/// own account instead, and this machine reproduced that failure verbatim on 2026-09-04.
///
/// THE CONTROL HAS BOTH HALVES. HotkeyReady proves the hook is installed before anything is pressed; a
/// quiet window proves DictationRecordingStarted is ABSENT when nothing is pressed; the press proves it
/// PRESENT; and the observed press-to-marker latency then has to fit comfortably inside the quiet
/// window, or the absence meant nothing and the run says so. That last check is also a drift alarm: the
/// day the app gets slower to start a take, this fails loudly instead of the control quietly weakening.
///
/// ONE ROUTE, VERIFIED BY READING. WindowsPushToTalkHook detects every press through its low-level
/// keyboard hook; RegisterHotKey there is a conflict probe that registers and unregisters at once. An
/// injected key therefore takes the path a finger takes, and the hook does not filter LLKHF_INJECTED.
///
/// SINGLE NON-MODIFIER KEYS ONLY. A modifier-only tap completes on a 40 ms poll, so an instant
/// synthetic press can fall between ticks and vanish - a harness artifact that reads as a flaky hotkey.
/// That is a declared boundary, refused by the resolver up front, not a scenario to discover at 2am.
/// </remarks>
static SyntheticHotkeyEvidence DriveSyntheticHotkey(
    string diagnosticPath,
    string targetResultPath,
    nint targetWindow,
    string profileDirectory,
    bool quickTap)
{
    var quietWindow = TimeSpan.FromSeconds(2);
    // The app's own default fixture hold (ResolveJourneyUatHoldDuration), so the two fixture-driven
    // modes differ in their trigger and nothing else.
    var holdAfterRecording = TimeSpan.FromMilliseconds(150);

    if (!WaitForDiagnosticEvent(diagnosticPath, "HotkeyReady/", TimeSpan.FromSeconds(10)))
    {
        throw new JourneyExpectationException(
            "The app never reported HotkeyReady, so there is no installed hook to press a key for.");
    }

    var virtualKey = ResolveRecordingVirtualKey(profileDirectory);

    // NEGATIVE HALF: nothing is pressed, so nothing may start.
    Thread.Sleep(quietWindow);
    if (ReadDiagnosticEvents(diagnosticPath).Any(value =>
            value.StartsWith("DictationRecordingStarted/", StringComparison.Ordinal)))
    {
        throw new JourneyExpectationException(
            "A recording started before any key was pressed, so DictationRecordingStarted is not "
                + "exclusive to the press and this control cannot certify anything.");
    }

    BringToForeground(targetWindow);
    Thread.Sleep(250);

    // POSITIVE HALF: the resolved key, held past the moment the app says it is recording - or, for a
    // quick tap, let go at once so the key-up arrives while the press is still opening the microphone.
    // The tap still has to produce a recording AND a delivery: the app owes the debounce, not the finger.
    var pressed = Stopwatch.StartNew();
    SendKey(virtualKey, keyDown: true);
    TimeSpan heldFor;
    bool recordingStarted;
    TimeSpan pressToRecording;
    if (quickTap)
    {
        SendKey(virtualKey, keyDown: false);
        heldFor = pressed.Elapsed;
        recordingStarted = WaitForDiagnosticEvent(
            diagnosticPath,
            "DictationRecordingStarted/",
            TimeSpan.FromSeconds(3));
        pressToRecording = pressed.Elapsed;
    }
    else
    {
        recordingStarted = WaitForDiagnosticEvent(
            diagnosticPath,
            "DictationRecordingStarted/",
            TimeSpan.FromSeconds(3));
        pressToRecording = pressed.Elapsed;
        Thread.Sleep(holdAfterRecording);
        SendKey(virtualKey, keyDown: false);
        heldFor = pressed.Elapsed;
    }
    if (!recordingStarted)
    {
        throw new JourneyExpectationException(
            "The injected recording key did not start a recording within 3 seconds: the installed hook "
                + "did not act on it.");
    }

    // THE QUIET WINDOW HAS TO HAVE MEANT SOMETHING. Absence for N certifies nothing until the same
    // marker has been watched arriving in well under N.
    if (pressToRecording * 3 > quietWindow)
    {
        throw JourneyExpectationException.Instrument(
            $"The quiet window ({quietWindow.TotalMilliseconds:F0} ms) is not comfortably longer than the "
                + $"observed press-to-recording latency ({pressToRecording.TotalMilliseconds:F0} ms), so its "
                + "absence check meant nothing. Lengthen the window or find out why the app got slower.");
    }

    // A DROPPED KEY-UP LOOKS LIKE A SLOW TRANSCRIPTION FROM HERE, so the wait is bounded and the
    // verdict is read off what the app wrote, not off the silence. A capture that was never finalised
    // is the lost release; a capture that was finalised and then nothing is a downstream failure, and
    // the two must not share a sentence.
    if (!WaitForDiagnosticEvent(diagnosticPath, "TextDeliveryCompleted/", TimeSpan.FromSeconds(45)))
    {
        var events = ReadDiagnosticEvents(diagnosticPath);
        var finalised = events.Any(value =>
            value.StartsWith("DictationCaptureFinalized/", StringComparison.Ordinal));
        throw new JourneyExpectationException(
            (quickTap && !finalised
                ? "The quick tap did not reach TextDeliveryCompleted and the capture was never finalised: "
                    + "the key-up that arrived during the press was not honoured; "
                : finalised
                    ? "The take finalised its capture but never reached TextDeliveryCompleted: transcription "
                        + "or delivery failed downstream of the hotkey; "
                    : "The synthetic-hotkey take did not reach TextDeliveryCompleted; ")
                + $"events={string.Join(',', events)}.");
    }

    // THE OVERLAP HAS TO HAVE HAPPENED, OR THE PASS MEANS NOTHING. A quick tap whose key-up landed
    // after the press had already finished starting took the ordinary path, and a build with the old
    // zero-timeout gate would have passed it too. The app writes DictationSignalQueued only when the
    // signal actually waited, so that line is the difference between a certified run and a lucky one.
    // WAITED FOR, NOT GLANCED AT. The app writes the queued line when the release's submitter resumes,
    // which can be after the delivery line the harness just saw; reading once would call a correct run
    // an instrument failure.
    if (quickTap && !WaitForDiagnosticEvent(diagnosticPath, "DictationSignalQueued/", TimeSpan.FromSeconds(5)))
    {
        throw JourneyExpectationException.Instrument(
            "The quick tap delivered, but the app never reported DictationSignalQueued, so the key-up did "
                + "not overlap the press and this run certifies nothing about the queue. Re-run; if it "
                + "never overlaps, the tap is not quick enough on this machine.");
    }

    var targetObserved = WaitForExpectedTargetResult(targetResultPath, TimeSpan.FromSeconds(5));
    return new SyntheticHotkeyEvidence(
        VirtualKey: $"0x{virtualKey:X2}",
        HeldMilliseconds: (int)heldFor.TotalMilliseconds,
        QuietWindowMilliseconds: (int)quietWindow.TotalMilliseconds,
        PressToRecordingMilliseconds: (int)pressToRecording.TotalMilliseconds,
        TargetObserved: targetObserved,
        QuickTap: quickTap);
}

/// <summary>The key the app is listening for, or a refusal - never a guess.</summary>
/// <remarks>
/// PORTED FROM THE macOS HARNESS'S ptt_binding CONTRACT. On 2026-08-10 that harness pressed a key
/// nothing was listening for and the FAIL was believed, on the one branch where a hotkey FAIL was most
/// likely to be believed. The defect class is an instrument failure reported as a product verdict, and
/// the design that stops it has THREE states, not two:
///   absent    - no settings file in the isolated profile - resolves through the SAME default the app
///               applies, DictationPreferences.Default, because a fresh profile really is running F8
///               with nothing on disk, and refusing there would reject a valid configuration;
///   valid     - a gesture the app's own parser accepts and its own key map can name - the binding;
///   malformed - anything else - REFUSED, because it says nothing about what the app is using.
/// The parser and the key map are the app's own (WindowsVirtualKeyMap, through InternalsVisibleTo), so
/// this cannot decay into a remembered table the way the macOS resolver once did.
///
/// CONFIGURATION ONLY. Whether the injector can drive the key is the caller's question. Chords and
/// modifier taps are refused here because this harness has declared them undrivable, not because they
/// are misconfigured; the message says which.
/// </remarks>
static byte ResolveRecordingVirtualKey(string profileDirectory)
{
    var settingsPath = Path.Combine(profileDirectory, "settings.json");
    string gestureText;
    if (!File.Exists(settingsPath))
    {
        gestureText = DictationPreferences.Default.PushToTalkGesture;
    }
    else
    {
        var loaded = new JsonSettingsStore(settingsPath).LoadAsync().GetAwaiter().GetResult();
        if (loaded.Status is not (SettingsLoadStatus.Loaded or SettingsLoadStatus.Migrated))
        {
            throw JourneyExpectationException.Instrument(
                $"The isolated profile's settings could not be read ({loaded.Status}), so the recording "
                    + "key cannot be established; refusing to guess.");
        }

        gestureText = loaded.Settings.Preferences.Dictation.PushToTalkGesture;
    }

    var parsed = HotkeyGestureParser.Parse(gestureText);
    if (!parsed.Succeeded || parsed.Gesture is null)
    {
        throw JourneyExpectationException.Instrument(
            "The configured recording gesture does not parse with the app's own parser, so nothing can "
                + "be pressed for it; refusing to guess.");
    }

    var gesture = parsed.Gesture.Value;
    if (!WindowsVirtualKeyMap.TryMap(gesture.Key, out var virtualKey))
    {
        throw JourneyExpectationException.Instrument(
            "The configured recording key has no entry in the app's own key map, so nothing can be "
                + "pressed for it; refusing to guess.");
    }

    if (gesture.Modifiers != HotkeyModifiers.None || HotkeyEdgeTracker.IsModifierKey(virtualKey))
    {
        throw JourneyExpectationException.Instrument(
            "The recording binding is a chord or a modifier tap. Those complete on a 40 ms poll that an "
                + "instant synthetic press can fall between, so this harness declares them undrivable "
                + "rather than report a flaky hotkey. Drive them with a finger.");
    }

    if (virtualKey is 0 or > 0xFF)
    {
        throw JourneyExpectationException.Instrument(
            "The resolved virtual key is outside the range SendInput takes.");
    }

    return (byte)virtualKey;
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
        throw new JourneyExpectationException("Refusing to remove an unexpected journey UAT directory.");
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

    return directory?.FullName ?? throw new JourneyExpectationException(
        "The repository root could not be located.");
}

static void BringToForeground(nint window)
{
    if (window == 0)
    {
        throw new JourneyExpectationException("The controlled delivery target has no window handle.");
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

/// <summary>Refuses an argument this harness does not know, before any journey is chosen.</summary>
/// <remarks>
/// A GREEN RUN OF THE WRONG JOURNEY IS THE MOST EXPENSIVE KIND OF PASS. On 2026-09-05 a wrapper joined
/// two flags into one token, `--english-parakeet,--synthetic-hotkey`. Every flag test here is an exact
/// match, so the token matched nothing, the harness ran its DEFAULT named-event journey, and that came
/// back green - a pass, for a journey nobody had asked for. It was caught only because the result's
/// `inputKind` label was read against the request. That is the macOS harness's failure mode (b): the
/// menu path proving the pipeline healthy while saying nothing about the hotkey.
///
/// So an unrecognised argument is INSTRUMENT INVALID, exit code 3, and no journey runs. The set below
/// is the harness's own flags; the `--mode`, `--result` and substring switches in this file belong to
/// the controlled target's command line and are never accepted here.
/// </remarks>
static void RequireKnownArguments(string[] arguments)
{
    string[] booleanFlags =
    [
        "--live-microphone", "--manual-microphone", "--english-parakeet", "--live-preview",
        "--head-start", "--escape-recovery", "--synthesized-acoustic", "--synthetic-hotkey",
        "--quick-tap", "--virtual-cable",
    ];
    string[] valuedFlags =
    [
        "--acoustic-gain", "--app-executable", "--deterministic-profile", "--eg1-model", "--eg1-server",
        "--failure", "--ollama-endpoint", "--ollama-model", "--polish",
    ];
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (booleanFlags.Contains(argument, StringComparer.OrdinalIgnoreCase))
        {
            continue;
        }

        if (valuedFlags.Contains(argument, StringComparer.OrdinalIgnoreCase))
        {
            var value = index + 1 < arguments.Length ? arguments[index + 1] : null;
            if (value is null || value.StartsWith("--", StringComparison.Ordinal))
            {
                throw JourneyExpectationException.Instrument(
                    $"{argument} needs a value and none followed it; refusing to run a journey other than "
                        + "the one asked for.");
            }

            index++;
            continue;
        }

        throw JourneyExpectationException.Instrument(
            $"Unrecognised argument '{argument}'. This harness matches flags exactly and would otherwise run "
                + "its default journey and report it green; refusing instead. Known flags: "
                + string.Join(' ', booleanFlags.Concat(valuedFlags)) + ".");
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

static int ParseBoundedIntArgument(
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

    if (!int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ||
        parsed < minimum ||
        parsed > maximum)
    {
        throw new JourneyExpectationException(
            $"{name} must be an integer from {minimum} through {maximum}.");
    }

    return parsed;
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
        _ => throw new JourneyExpectationException(nameof(failureMode)),
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
        throw new JourneyExpectationException(
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

/// <summary>Content-free evidence of the synthetic-hotkey take: which key, how long, how fast.</summary>
internal sealed record SyntheticHotkeyEvidence(
    string VirtualKey,
    int HeldMilliseconds,
    int QuietWindowMilliseconds,
    int PressToRecordingMilliseconds,
    bool TargetObserved,
    bool QuickTap);

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

internal sealed record PolishJourneyEvidence(
    bool RuntimeReady,
    bool RuntimeDegraded,
    bool Started,
    bool Completed,
    bool Degraded,
    string? ErrorCode,
    long? ElapsedMilliseconds);

internal sealed record VirtualCableRoute(
    string RenderName,
    string RenderId,
    string CaptureName,
    string CaptureId);

internal sealed record AcousticProbeMetrics(
    bool Started,
    string Outcome,
    string? Error,
    long DurationMilliseconds,
    int LevelEvents,
    float Peak,
    double AverageLevelRootMeanSquare,
    double CapturedRootMeanSquare);

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
        var snapshot = RunSta(CaptureOnSta) ?? throw new JourneyExpectationException(
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
            throw new JourneyExpectationException(
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
            throw new JourneyExpectationException("The clipboard guard operation failed.", failure);
        }

        return result!;
    }

    private sealed record ClipboardSnapshot(bool IsEmpty, DataObject? Data);
}

enum DeterministicJourneyProfile
{
    None,
    Enabled,
    Disabled,
}
