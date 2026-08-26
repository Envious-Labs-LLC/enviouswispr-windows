using EnviousWispr.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ASR;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Lifecycle;
using EnviousWispr.Services.Runtime;
using EnviousWispr.Services.Settings;
using Microsoft.UI.Xaml;
using System.Security;

namespace EnviousWispr.App;

public partial class App : Application, IAsyncDisposable
{
    private const string SingleInstanceKey = "EnviousLabs.EnviousWispr.Production";

    private readonly JsonLineFileLogger _logger;
    private readonly JsonSettingsStore _settingsStore;
    private SingleInstanceLock? _singleInstanceLock;
    private WindowsPushToTalkHook? _pushToTalkHook;
    private PushToTalkSessionController? _sessionController;
    private RuntimeWorkerTranscriptionEngine? _transcriptionEngine;
    private MainWindow? _window;
    private bool _disposed;

    public App()
    {
        InitializeComponent();

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Envious Labs",
            "EnviousWispr");
        _logger = new JsonLineFileLogger(Path.Combine(dataDirectory, "diagnostics", "app.jsonl"));
        _settingsStore = new JsonSettingsStore(Path.Combine(dataDirectory, "settings.json"));

        UnhandledException += (_, eventArgs) =>
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Unknown));
            eventArgs.Handled = false;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ApplicationStarting));

        if (!SingleInstanceLock.TryAcquire(SingleInstanceKey, out _singleInstanceLock))
        {
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.DuplicateInstanceRejected));
            Exit();
            return;
        }

        var loadResult = await _settingsStore.LoadAsync().ConfigureAwait(true);
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            EventFor(loadResult.Status),
            FailureFor(loadResult.Status)));

        var settings = loadResult.Settings with
        {
            LaunchCount = checked(loadResult.Settings.LaunchCount + 1),
        };

        try
        {
            if (loadResult.Status is SettingsLoadStatus.Invalid or SettingsLoadStatus.Migrated)
            {
                await _settingsStore.ResetAsync(settings).ConfigureAwait(true);
                if (loadResult.Status == SettingsLoadStatus.Invalid)
                {
                    _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.SettingsReset));
                }
            }
            else if (loadResult.Status is not (SettingsLoadStatus.NewerVersion or SettingsLoadStatus.Unavailable))
            {
                await _settingsStore.SaveAsync(settings).ConfigureAwait(true);
            }
        }
        catch (IOException)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.SettingsRecovered,
                AppFailureCategory.StorageUnavailable));
        }
        catch (UnauthorizedAccessException)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.SettingsRecovered,
                AppFailureCategory.AccessDenied));
        }
        catch (SecurityException)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.SettingsRecovered,
                AppFailureCategory.AccessDenied));
        }

        _window = new MainWindow(settings, loadResult.Status);
        _window.Closed += OnWindowClosed;
        _window.Activate();
        _window.SetSessionStatus("Preparing local transcription...");
        await ConfigureTranscriptionAsync(settings.Preferences.Dictation.FinalEngine).ConfigureAwait(true);
        ConfigurePushToTalk(settings.Preferences.Dictation.PushToTalkGesture);
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellShown));
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellClosed));
        await DisposeAsync().ConfigureAwait(true);
        _window = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pushToTalkHook is not null)
        {
            _pushToTalkHook.Signalled -= OnPushToTalkSignalled;
            await _pushToTalkHook.DisposeAsync().ConfigureAwait(true);
            _pushToTalkHook = null;
        }

        if (_sessionController is not null)
        {
            await _sessionController.DisposeAsync().ConfigureAwait(true);
            _sessionController = null;
        }

        if (_transcriptionEngine is not null)
        {
            await _transcriptionEngine.DisposeAsync().ConfigureAwait(true);
            _transcriptionEngine = null;
        }

        _singleInstanceLock?.Dispose();
        _singleInstanceLock = null;
        GC.SuppressFinalize(this);
    }

    private void ConfigurePushToTalk(string configuredGesture)
    {
        if (!WindowsPushToTalkHook.TryCreate(
                configuredGesture,
                out _pushToTalkHook,
                out var error) ||
            _pushToTalkHook is null)
        {
            _window?.SetHotkeyUnavailable(HotkeyFailureStatus(error));
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.HotkeyFailed,
                FailureFor(error)));
            return;
        }

        _sessionController = new PushToTalkSessionController(
            new WasapiAudioCapture(),
            new WindowsForegroundTargetProvider());
        _pushToTalkHook.Signalled += OnPushToTalkSignalled;
        _window?.SetHotkeyReady(_pushToTalkHook.Gesture.ToString());
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.HotkeyReady));
    }

    private async Task ConfigureTranscriptionAsync(FinalAsrEngine configuredEngine)
    {
        var environmentEngine = Environment.GetEnvironmentVariable("ENVIOUSWISPR_ASR_ENGINE");
        if (Enum.TryParse<FinalAsrEngine>(environmentEngine, ignoreCase: true, out var parsedEngine))
        {
            configuredEngine = parsedEngine;
        }

        var engine = configuredEngine == FinalAsrEngine.Automatic
            ? FinalAsrEngine.Parakeet
            : configuredEngine;
        var modelDirectory = ResolveModelDirectory(engine == FinalAsrEngine.Whisper
            ? WhisperTranscriptionEngine.ModelId
            : ParakeetTranscriptionEngine.ModelId);
        if (modelDirectory is null)
        {
            _window?.SetSessionStatus("Local transcription model is not installed");
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.AsrUnavailable));
            return;
        }

        var hardware = await new WindowsHardwareDiscovery().ProbeAsync().ConfigureAwait(true);
        var workerExecutable = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        _transcriptionEngine = engine == FinalAsrEngine.Whisper
            ? CreateWhisperEngine(workerExecutable, modelDirectory, hardware)
            : CreateParakeetEngine(workerExecutable, modelDirectory, hardware);
        if (_transcriptionEngine is null)
        {
            _window?.SetSessionStatus("Local transcription is unavailable on this machine");
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.AsrUnavailable));
            return;
        }

        var started = await _transcriptionEngine.StartAsync().ConfigureAwait(true);
        if (!started.Succeeded)
        {
            await _transcriptionEngine.DisposeAsync().ConfigureAwait(true);
            _transcriptionEngine = null;
            _window?.SetSessionStatus("Local transcription worker could not start");
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.RuntimeWorker));
            return;
        }

        _window?.SetSessionStatus("Local transcription ready");
    }

    private static RuntimeWorkerTranscriptionEngine? CreateParakeetEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware)
    {
        var models = new LocalParakeetModelProbe().Probe(modelDirectory);
        var selection = ParakeetRuntimeSelector.Select(hardware, models);
        var cpuSelection = ParakeetRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        if (!selection.Succeeded ||
            selection.Provider is null ||
            selection.ModelPack is null ||
            !cpuSelection.Succeeded)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(
            new RuntimeWorkerTranscriptionOptions(
                workerExecutable,
                modelDirectory,
                selection.Provider.Value,
                selection.ModelPack.Value,
                selection.IntraOpThreads,
                selection.InterOpThreads,
                CpuFallbackThreads: cpuSelection.IntraOpThreads,
                CudaRuntimeDirectory: Environment.GetEnvironmentVariable(
                    "ENVIOUSWISPR_CUDA_RUNTIME_DIR")));
    }

    private static RuntimeWorkerTranscriptionEngine? CreateWhisperEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware)
    {
        var models = new LocalWhisperModelProbe().Probe(modelDirectory);
        var selection = WhisperRuntimeSelector.Select(hardware, models);
        var cpuSelection = WhisperRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        if (!selection.Succeeded ||
            selection.Provider is null ||
            selection.ModelPack is null ||
            !cpuSelection.Succeeded)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            selection.Provider.Value,
            ParakeetModelPack.Quantized,
            selection.ThreadCount,
            CpuFallbackThreads: cpuSelection.ThreadCount,
            Engine: FinalAsrEngine.Whisper,
            WhisperPack: selection.ModelPack.Value,
            Language: "auto",
            CudaRuntimeDirectory: Environment.GetEnvironmentVariable(
                "ENVIOUSWISPR_CUDA_RUNTIME_DIR")));
    }

    private static string? ResolveModelDirectory(string modelId)
    {
        var configured = Environment.GetEnvironmentVariable("ENVIOUSWISPR_MODEL_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Envious Labs",
            "EnviousWispr",
            "models",
            modelId);
        if (Directory.Exists(installed))
        {
            return installed;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        var developmentModel = directory is null
            ? null
            : Path.Combine(directory.FullName, "models", modelId);
        return developmentModel is not null && Directory.Exists(developmentModel)
            ? developmentModel
            : null;
    }

    private void OnPushToTalkSignalled(object? sender, PushToTalkSignalEvent args) =>
        _ = HandlePushToTalkAsync(args.Signal);

    private async Task HandlePushToTalkAsync(PushToTalkSignal signal)
    {
        var controller = _sessionController;
        if (controller is null)
        {
            return;
        }

        try
        {
            var result = signal switch
            {
                PushToTalkSignal.Pressed => await controller.PressAsync().ConfigureAwait(false),
                PushToTalkSignal.Released => await controller.ReleaseAsync().ConfigureAwait(false),
                PushToTalkSignal.Cancelled => await controller.CancelAsync().ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported push-to-talk signal."),
            };

            WriteSessionEvent(result);
            if (result.Kind == SessionTransitionKind.FinalizeReady &&
                result.Session is not null &&
                result.Audio is not null)
            {
                await TranscribeFinalAsync(controller, result.Session.Id, result.Audio)
                    .ConfigureAwait(false);
                return;
            }
            else if (result.Kind is SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
            {
                await controller.ResetAsync().ConfigureAwait(false);
            }

            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus(SessionStatus(result)));
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationSessionFailed,
                AppFailureCategory.Unknown));
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus("Session failed safely"));
        }
    }

    private async Task TranscribeFinalAsync(
        PushToTalkSessionController controller,
        DictationSessionId sessionId,
        CapturedAudio audio)
    {
        var engine = _transcriptionEngine;
        if (engine is null)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.AsrUnavailable));
            await controller.CompleteAsync(sessionId).ConfigureAwait(false);
            await controller.ResetAsync().ConfigureAwait(false);
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus("Audio captured, but local transcription is unavailable"));
            return;
        }

        _window?.DispatcherQueue.TryEnqueue(() =>
            _window?.SetSessionStatus("Transcribing locally..."));
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationTranscriptionStarted));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var transcript = await engine.TranscribeAsync(audio).ConfigureAwait(false);
            timer.Stop();
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                transcript.UsedFallback
                    ? AppEventCode.DictationTranscriptionDegraded
                    : AppEventCode.DictationTranscriptionCompleted,
                transcript.UsedFallback
                    ? FailureFor(transcript.DegradedError)
                    : AppFailureCategory.None,
                timer.ElapsedMilliseconds));
            await controller.CompleteAsync(sessionId).ConfigureAwait(false);
            await controller.ResetAsync().ConfigureAwait(false);
            var status = string.IsNullOrWhiteSpace(transcript.Text)
                ? "No speech detected"
                : transcript.UsedFallback
                    ? "Transcribed locally with CPU fallback — delivery comes next"
                    : "Transcribed locally — delivery comes next";
            _window?.DispatcherQueue.TryEnqueue(() => _window?.SetSessionStatus(status));
        }
        catch (TranscriptionEngineException exception)
        {
            timer.Stop();
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                FailureFor(exception.Error),
                timer.ElapsedMilliseconds));
            await controller.CompleteAsync(sessionId).ConfigureAwait(false);
            await controller.ResetAsync().ConfigureAwait(false);
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus("Local transcription failed safely"));
        }
    }

    private void WriteSessionEvent(SessionTransitionResult result)
    {
        var eventCode = result.Kind switch
        {
            SessionTransitionKind.Started => AppEventCode.DictationRecordingStarted,
            SessionTransitionKind.FinalizeReady => AppEventCode.DictationCaptureFinalized,
            SessionTransitionKind.Cancelled => AppEventCode.DictationCancelled,
            SessionTransitionKind.Failed => AppEventCode.DictationSessionFailed,
            _ => (AppEventCode?)null,
        };
        if (eventCode is not null)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                eventCode.Value,
                FailureFor(result.Error)));
        }
    }

    private static string SessionStatus(SessionTransitionResult result) => result.Kind switch
    {
        SessionTransitionKind.Started => "Recording — release to finish, Escape to cancel",
        SessionTransitionKind.FinalizeReady when result.Error is not null =>
            "Capture preserved after a microphone interruption",
        SessionTransitionKind.FinalizeReady => "Capture complete — transcribing locally",
        SessionTransitionKind.Cancelled => "Cancelled — nothing will be delivered",
        SessionTransitionKind.Failed => "Session failed safely",
        _ => "Idle",
    };

    private static string HotkeyFailureStatus(AppError? error) => error?.Code switch
    {
        AppErrorCode.HotkeyConflict => "Configured shortcut is already in use",
        AppErrorCode.HotkeyInvalid => "Configured shortcut is invalid",
        _ => "Global shortcut is unavailable",
    };

    private static AppFailureCategory FailureFor(AppError? error) => error?.Code switch
    {
        AppErrorCode.HotkeyConflict => AppFailureCategory.HotkeyConflict,
        AppErrorCode.HotkeyInvalid or AppErrorCode.HotkeyUnavailable =>
            AppFailureCategory.HotkeyUnavailable,
        AppErrorCode.TargetUnavailable => AppFailureCategory.TargetUnavailable,
        AppErrorCode.AudioDeviceUnavailable or AppErrorCode.AudioDeviceLost =>
            AppFailureCategory.AudioUnavailable,
        AppErrorCode.RuntimeProviderUnavailable or AppErrorCode.RuntimeProviderIncompatible =>
            AppFailureCategory.RuntimeProvider,
        AppErrorCode.RuntimeWorkerFailed => AppFailureCategory.RuntimeWorker,
        AppErrorCode.ModelPackUnavailable or AppErrorCode.TranscriptionFailed =>
            AppFailureCategory.AsrUnavailable,
        null => AppFailureCategory.None,
        _ => AppFailureCategory.Unknown,
    };

    private static AppEventCode EventFor(SettingsLoadStatus status) => status switch
    {
        SettingsLoadStatus.Loaded => AppEventCode.SettingsLoaded,
        SettingsLoadStatus.Missing => AppEventCode.SettingsCreated,
        SettingsLoadStatus.Migrated => AppEventCode.SettingsMigrated,
        SettingsLoadStatus.Invalid or SettingsLoadStatus.Unavailable => AppEventCode.SettingsRecovered,
        SettingsLoadStatus.NewerVersion => AppEventCode.SettingsNewerVersionPreserved,
        _ => AppEventCode.SettingsRecovered,
    };

    private static AppFailureCategory FailureFor(SettingsLoadStatus status) => status switch
    {
        SettingsLoadStatus.Invalid => AppFailureCategory.InvalidData,
        SettingsLoadStatus.NewerVersion => AppFailureCategory.InvalidData,
        SettingsLoadStatus.Unavailable => AppFailureCategory.StorageUnavailable,
        _ => AppFailureCategory.None,
    };
}
