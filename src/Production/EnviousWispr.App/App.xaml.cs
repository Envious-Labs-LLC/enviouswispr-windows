using EnviousWispr.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Lifecycle;
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

            if (result.Kind == SessionTransitionKind.FinalizeReady && result.Session is not null)
            {
                await controller.CompleteAsync(result.Session.Id).ConfigureAwait(false);
                await controller.ResetAsync().ConfigureAwait(false);
            }
            else if (result.Kind is SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
            {
                await controller.ResetAsync().ConfigureAwait(false);
            }

            WriteSessionEvent(result);
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
        SessionTransitionKind.FinalizeReady => "Capture complete — final ASR is the next production phase",
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
