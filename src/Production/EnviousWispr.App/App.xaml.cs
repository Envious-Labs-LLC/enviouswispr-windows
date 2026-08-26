using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Lifecycle;
using EnviousWispr.Services.Settings;
using Microsoft.UI.Xaml;
using System.Security;

namespace EnviousWispr.App;

public partial class App : Application
{
    private const string SingleInstanceKey = "EnviousLabs.EnviousWispr.Production";

    private readonly JsonLineFileLogger _logger;
    private readonly JsonSettingsStore _settingsStore;
    private SingleInstanceLock? _singleInstanceLock;
    private MainWindow? _window;

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
            await _settingsStore.SaveAsync(settings).ConfigureAwait(true);
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
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellShown));
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellClosed));
        _singleInstanceLock?.Dispose();
        _singleInstanceLock = null;
        _window = null;
    }

    private static AppEventCode EventFor(SettingsLoadStatus status) => status switch
    {
        SettingsLoadStatus.Loaded => AppEventCode.SettingsLoaded,
        SettingsLoadStatus.Missing => AppEventCode.SettingsCreated,
        SettingsLoadStatus.Invalid or SettingsLoadStatus.Unavailable => AppEventCode.SettingsRecovered,
        _ => AppEventCode.SettingsRecovered,
    };

    private static AppFailureCategory FailureFor(SettingsLoadStatus status) => status switch
    {
        SettingsLoadStatus.Invalid => AppFailureCategory.InvalidData,
        SettingsLoadStatus.Unavailable => AppFailureCategory.StorageUnavailable,
        _ => AppFailureCategory.None,
    };
}
