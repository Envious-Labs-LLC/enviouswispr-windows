using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.ASR;
using EnviousWispr.ModelDelivery;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;
using EnviousWispr.Services.Credentials;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.History;
using EnviousWispr.Services.Lifecycle;
using EnviousWispr.Services.Runtime;
using EnviousWispr.Services.Reliability;
using EnviousWispr.Services.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Reflection;
using System.Security;

namespace EnviousWispr.App;

public partial class App : Application, IAsyncDisposable
{
    private static readonly TimeSpan MaximumRecordingDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumFinalProcessingDuration = TimeSpan.FromMinutes(3);

    private readonly PrivacySafeObservabilityLogger _logger;
    private readonly ReleaseIdentity _releaseIdentity;
    private readonly VelopackUpdateService _updateService;
    private readonly JsonDiagnosticExportService _diagnosticExportService;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonPortableProfileService _profileService = new();
    private readonly JsonHistoryStore _historyStore;
    private readonly JsonApplicationRunStateStore _runStateStore;
    private readonly WindowsRecoveryTextStore _recoveryTextStore;
    private readonly WindowsSystemResourceProbe _resourceProbe;
    private readonly WindowsCredentialApiKeyStore _credentialStore;
    private readonly string _dataDirectory;
    private readonly string? _cudaRuntimeDirectory;
    private readonly RuntimeResourceArbiter _resourceArbiter = new();
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly SemaphoreSlim _sessionOperationGate = new(1, 1);
    private readonly DeterministicTextPipeline _deterministicTextPipeline = new();
    private SingleInstanceLock? _singleInstanceLock;
    private SingleInstanceActivationChannel? _activationChannel;
    private WindowsSystemLifecycleMonitor? _lifecycleMonitor;
    private WindowsPushToTalkHook? _pushToTalkHook;
    private PushToTalkSessionController? _sessionController;
    private IAudioCapture? _audioCapture;
    private RuntimeWorkerTranscriptionEngine? _transcriptionEngine;
    private RuntimeWorkerLivePreviewEngine? _previewEngine;
    private IPolishProvider? _polishProvider;
    private WindowsTextTargetAdapter? _textTargetAdapter;
    private ContextAwareTextDelivery? _textDelivery;
    private RuntimeResourceKind _polishResource = RuntimeResourceKind.Cpu;
    private bool _polishUsesLocalRuntime;
    private CloudPolishConsent? _cloudPolishConsent;
    private string? _localPolishNotice;
    private readonly CancellationTokenSource _polishLifetime = new();
    private Task? _polishWarmup;
    private CancellationTokenSource? _previewCancellation;
    private Task? _previewLoop;
    private long _previewSequence;
    private MainWindow? _window;
    private WindowsTrayIcon? _trayIcon;
    private IReadOnlyList<CustomWordEntry> _customWords = [];
    private AppSettings _settings = AppSettings.Default;
    private DeterministicTextOptions _deterministicTextOptions =
        DeterministicTextOptions.From(DictationPreferences.Default);
    private bool _disposed;
    private bool _exitRequested;
    private Task? _shutdownPreparation;
    private bool _backgroundNoticeShown;
    private Guid? _runId;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatLoop;
    private int _activationPending;
    private CancellationTokenSource? _recordingWatchdogCancellation;
    private Task? _recordingWatchdog;
    private CancellationTokenSource? _activeProcessingCancellation;
    private bool _canPersistRecoveryForSession = true;
    private bool _hasPendingRecovery;
    private RecoveryTextRecord? _pendingRecoveryRecord;
    private bool _escapeRecoveryForSession;

    public App()
    {
        InitializeComponent();

        _releaseIdentity = ResolveReleaseIdentity();

        var uatCredentialSuffix = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX");
        _credentialStore = string.IsNullOrWhiteSpace(uatCredentialSuffix)
            ? new WindowsCredentialApiKeyStore()
            : WindowsCredentialApiKeyStore.CreateForIsolatedUat(uatCredentialSuffix);

        var dataDirectory = Environment.GetEnvironmentVariable("ENVIOUSWISPR_DATA_DIRECTORY");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Envious Labs",
                _releaseIdentity.DataDirectoryName);
        }

        _dataDirectory = Path.GetFullPath(dataDirectory);
        _cudaRuntimeDirectory = ResolveCudaRuntimeDirectory(_dataDirectory);
        var diagnosticPath = Path.Combine(_dataDirectory, "diagnostics", "app.jsonl");
        IPrivacySafeTelemetryTransport? telemetryTransport = null;
        var allowLoopbackTelemetry = string.Equals(
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_ALLOW_LOOPBACK_TELEMETRY"),
            "1",
            StringComparison.Ordinal);
        if (TelemetryEndpointPolicy.TryNormalize(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_TELEMETRY_ENDPOINT"),
                allowLoopbackTelemetry,
                out var telemetryEndpoint))
        {
            telemetryTransport = new HttpPrivacySafeTelemetryTransport(telemetryEndpoint!);
        }

        _logger = new PrivacySafeObservabilityLogger(
            new JsonLineFileLogger(diagnosticPath, enabled: false),
            telemetryTransport);
        _diagnosticExportService = new JsonDiagnosticExportService(diagnosticPath);
        _settingsStore = new JsonSettingsStore(Path.Combine(_dataDirectory, "settings.json"));
        _historyStore = new JsonHistoryStore(Path.Combine(_dataDirectory, "history.json"));
        _runStateStore = new JsonApplicationRunStateStore(Path.Combine(_dataDirectory, "run-state.json"));
        _recoveryTextStore = new WindowsRecoveryTextStore(Path.Combine(_dataDirectory, "recovery.json"));
        _resourceProbe = new WindowsSystemResourceProbe(_dataDirectory);

        var allowLoopbackUpdates = string.Equals(
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_ALLOW_LOOPBACK_UPDATES"),
            "1",
            StringComparison.Ordinal);
        _ = UpdateEndpointPolicy.TryNormalize(
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_UPDATE_ENDPOINT"),
            allowLoopbackUpdates,
            out var updateEndpoint);
        _updateService = new VelopackUpdateService(
            _releaseIdentity,
            updateEndpoint,
            new WindowsUpdateArtifactValidator());

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
        if (!SingleInstanceLock.TryAcquire(_releaseIdentity.SingleInstanceKey, out _singleInstanceLock))
        {
            var activated = await SingleInstanceActivationChannel.RequestActivationAsync(
                _releaseIdentity.SingleInstanceKey,
                TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                activated
                    ? AppEventCode.DuplicateInstanceActivated
                    : AppEventCode.DuplicateInstanceRejected));
            Exit();
            return;
        }

        _activationChannel = new SingleInstanceActivationChannel(_releaseIdentity.SingleInstanceKey);
        _activationChannel.ActivationRequested += OnDuplicateActivationRequested;
        _activationChannel.Start();

        var runStart = await _runStateStore.BeginRunAsync(DateTimeOffset.UtcNow).ConfigureAwait(true);
        _runId = runStart.Status == RunStateLoadStatus.Unavailable ? null : runStart.RunId;
        if (_runId is { } activeRunId)
        {
            StartHeartbeat(activeRunId);
        }

        var loadResult = await _settingsStore.LoadAsync().ConfigureAwait(true);
        var observability = loadResult.Settings.Observability ?? ObservabilityPreferences.Default;
        _logger.Configure(observability, DateTimeOffset.UtcNow);
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ApplicationStarting));
        if (runStart.RecoveredInterruptedRun)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.ApplicationRunRecovered,
                AppFailureCategory.Recovery,
                ErrorCode: AppErrorCode.PreviousRunInterrupted));
        }

        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            EventFor(loadResult.Status),
            FailureFor(loadResult.Status)));

        var settings = loadResult.Settings with
        {
            LaunchCount = checked(loadResult.Settings.LaunchCount + 1),
        };
        _settings = settings;
        _customWords = settings.UserData.CustomWords;
        _deterministicTextOptions = DeterministicTextOptions.From(settings.Preferences.Dictation);
        ConfigurePolish(settings.Preferences.Polish);

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

        _window = new MainWindow(
            settings,
            loadResult.Status,
            _settingsStore,
            _profileService,
            _historyStore,
            _credentialStore,
            _recoveryTextStore,
            _diagnosticExportService,
            _logger.TelemetryAvailable,
            _releaseIdentity,
            _updateService.IsConfigured,
            _updateService.CurrentVersion);
        _window.SettingsChanged += OnSettingsChanged;
        _window.SessionStatusChanged += OnSessionStatusChanged;
        _window.AudioDevicesChanged += OnAudioDevicesChanged;
        _window.RecoveryCleared += OnRecoveryCleared;
        _window.DiagnosticsExportCompleted += OnDiagnosticsExportCompleted;
        _window.UpdateCheckRequested += OnUpdateCheckRequested;
        _window.UpdateApplyRequested += OnUpdateApplyRequested;
        _window.AppWindow.Closing += OnAppWindowClosing;
        _window.Closed += OnWindowClosed;
        _window.Activate();
        if (Interlocked.Exchange(ref _activationPending, 0) == 1)
        {
            ShowMainWindow(openSettings: false);
        }

        ConfigureTrayIcon();
        await _window.InitializeProductDataAsync().ConfigureAwait(true);
        var recovery = await LoadStartupRecoveryAsync().ConfigureAwait(true);
        _hasPendingRecovery = recovery.Status == RecoveryTextLoadStatus.Found;
        _pendingRecoveryRecord = recovery.Record;
        _window.SetRecoveredText(recovery);
        if (runStart.RecoveredInterruptedRun && recovery.Status != RecoveryTextLoadStatus.Found)
        {
            _window.SetRunRecoveryNotice(runStart.ConsecutiveInterruptedRuns);
        }

        ConfigureSystemLifecycleMonitor();
        _window.FocusInitialControl();
        _window.SetCloudPolishNotice(_cloudPolishConsent?.Notice);
        _window.SetOllamaPolishNotice(_localPolishNotice);
        _window.SetSessionStatus("Preparing local transcription...");
        await ConfigureTranscriptionAsync(settings.Preferences.Dictation.FinalEngine).ConfigureAwait(true);
        ConfigurePushToTalk(settings.Preferences.Dictation);
        if (_polishProvider is EgOnePolishProvider polishProvider)
        {
            _polishWarmup = WarmPolishRuntimeAsync(polishProvider, _polishLifetime.Token);
        }
        else if (_polishProvider is OllamaPolishProvider ollamaProvider)
        {
            _polishWarmup = ProbeOllamaRuntimeAsync(ollamaProvider, _polishLifetime.Token);
        }

        ApplyOverlayUatState();
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellShown));
        SignalPerformanceUatReady();
        StartPublicFixtureJourneyUat();
        ApplyReliabilityUatExit();
    }

    private void SignalPerformanceUatReady()
    {
        SignalPerformanceUatEvent("ENVIOUSWISPR_UAT_READY_EVENT");
        if (_transcriptionEngine is not null)
        {
            SignalPerformanceUatEvent("ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT");
        }
    }

    private static void SignalPerformanceUatEvent(string environmentVariable)
    {
        const string allowedPrefix = @"Local\EnviousLabs.EnviousWispr.PerformanceUat.";
        var eventName = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(eventName) ||
            eventName.Length > 200 ||
            !eventName.StartsWith(allowedPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(eventName);
            readyEvent.Set();
        }
        catch (Exception exception) when (exception is
                                          ArgumentException or
                                          WaitHandleCannotBeOpenedException or
                                          UnauthorizedAccessException or
                                          IOException)
        {
            // UAT instrumentation must never make normal startup fail.
        }
    }

    private async Task<RecoveryTextLoadResult> LoadStartupRecoveryAsync()
    {
        var recovery = await _recoveryTextStore.LoadAsync().ConfigureAwait(true);
        if (recovery.Status != RecoveryTextLoadStatus.Missing ||
            !string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_RECOVERY_STATE"),
                "synthetic",
                StringComparison.Ordinal))
        {
            return recovery;
        }

        var record = new RecoveryTextRecord(
            DictationSessionId.Create(),
            DateTimeOffset.UtcNow,
            "Synthetic unfinished dictation for Windows recovery UAT.");
        return await _recoveryTextStore.SaveAsync(record).ConfigureAwait(true)
            ? new RecoveryTextLoadResult(RecoveryTextLoadStatus.Found, record)
            : new RecoveryTextLoadResult(
                RecoveryTextLoadStatus.Unavailable,
                Error: new AppError(
                    AppErrorCode.StorageUnavailable,
                    AppErrorStage.RecoveryText,
                    CanRetry: true));
    }

    private void ApplyReliabilityUatExit()
    {
        var requested = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_EXIT_AFTER_MILLISECONDS");
        if (!int.TryParse(requested, out var milliseconds) ||
            milliseconds is < 500 or > 30_000)
        {
            return;
        }

        _ = ExitAfterUatDelayAsync(TimeSpan.FromMilliseconds(milliseconds));
    }

    private async Task ExitAfterUatDelayAsync(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        _window?.DispatcherQueue.TryEnqueue(ExitFromTray);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        var window = _window;
        if (window is not null)
        {
            window.SettingsChanged -= OnSettingsChanged;
            window.SessionStatusChanged -= OnSessionStatusChanged;
            window.AudioDevicesChanged -= OnAudioDevicesChanged;
            window.RecoveryCleared -= OnRecoveryCleared;
            window.DiagnosticsExportCompleted -= OnDiagnosticsExportCompleted;
            window.UpdateCheckRequested -= OnUpdateCheckRequested;
            window.UpdateApplyRequested -= OnUpdateApplyRequested;
            window.AppWindow.Closing -= OnAppWindowClosing;
            window.Closed -= OnWindowClosed;
        }

        await PrepareForExitAsync().ConfigureAwait(true);
        _window = null;
    }

    private void OnDuplicateActivationRequested(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref _activationPending, 1);
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DuplicateInstanceActivated));
        var window = _window;
        if (window is not null)
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _activationPending, 0);
                ShowMainWindow(openSettings: false);
            });
        }
    }

    private void StartHeartbeat(Guid runId)
    {
        _heartbeatCancellation = new CancellationTokenSource();
        _heartbeatLoop = RunHeartbeatAsync(runId, _heartbeatCancellation.Token);
    }

    private async Task RunHeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await _runStateStore.HeartbeatAsync(
                        runId,
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false))
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.ApplicationHeartbeatFailed,
                        AppFailureCategory.StorageUnavailable));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ConfigureSystemLifecycleMonitor()
    {
        try
        {
            _lifecycleMonitor = new WindowsSystemLifecycleMonitor();
            _lifecycleMonitor.Transitioned += OnSystemLifecycleTransitioned;
        }
        catch (InvalidOperationException)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.SystemLifecycle));
        }
    }

    private void OnSystemLifecycleTransitioned(
        object? sender,
        SystemLifecycleTransition transition)
    {
        var eventCode = transition switch
        {
            SystemLifecycleTransition.Suspending => AppEventCode.SystemSuspending,
            SystemLifecycleTransition.Resumed => AppEventCode.SystemResumed,
            SystemLifecycleTransition.SessionLocked => AppEventCode.SessionLocked,
            SystemLifecycleTransition.SessionUnlocked => AppEventCode.SessionUnlocked,
            _ => AppEventCode.UnhandledFailure,
        };
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, eventCode));
        if (transition is SystemLifecycleTransition.Suspending or
            SystemLifecycleTransition.SessionLocked)
        {
            _ = RecoverFromSystemTransitionAsync(transition);
        }
        else
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus("Windows resumed — EnviousWispr is ready"));
        }
    }

    private void OnAudioDevicesChanged(AudioDeviceChange change)
    {
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.AudioDevicesChanged,
            change.AffectsCapture
                ? AppFailureCategory.AudioUnavailable
                : AppFailureCategory.None));
    }

    private void OnRecoveryCleared()
    {
        _hasPendingRecovery = false;
        _pendingRecoveryRecord = null;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var previousSharing = _settings.Observability?.ShareAnonymousTelemetry == true;
        _settings = settings;
        _customWords = settings.UserData.CustomWords;
        _deterministicTextOptions = DeterministicTextOptions.From(settings.Preferences.Dictation);
        var observability = settings.Observability ?? ObservabilityPreferences.Default;
        _logger.Configure(observability, DateTimeOffset.UtcNow);
        if (previousSharing != observability.ShareAnonymousTelemetry)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                observability.ShareAnonymousTelemetry
                    ? AppEventCode.TelemetryConsentEnabled
                    : AppEventCode.TelemetryConsentDisabled));
        }
    }

    private void OnDiagnosticsExportCompleted(bool succeeded, int recordCount)
    {
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            succeeded ? AppEventCode.DiagnosticsExported : AppEventCode.DiagnosticsExportFailed,
            succeeded ? AppFailureCategory.None : AppFailureCategory.Observability,
            ElapsedMilliseconds: null));
    }

    private async void OnUpdateCheckRequested()
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        if (!await _sessionOperationGate.WaitAsync(0).ConfigureAwait(true))
        {
            _window?.SetUpdateStatus(new UpdateOperationResult(UpdateOperationStatus.BusyDictating));
            return;
        }

        try
        {
            _window?.SetUpdateCheckInProgress();
            var result = await _updateService.CheckDownloadAndVerifyAsync().ConfigureAwait(true);
            _window?.SetUpdateStatus(result);
        }
        finally
        {
            _sessionOperationGate.Release();
        }
    }

    private async void OnUpdateApplyRequested()
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        if (!await _sessionOperationGate.WaitAsync(0).ConfigureAwait(true))
        {
            _window?.SetUpdateStatus(new UpdateOperationResult(UpdateOperationStatus.BusyDictating));
            return;
        }

        _exitRequested = true;
        _sessionOperationGate.Release();
        try
        {
            if (!_updateService.TryApplyPendingAndRestart())
            {
                _exitRequested = false;
                _window?.SetUpdateStatus(new UpdateOperationResult(UpdateOperationStatus.Failed));
                return;
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            _exitRequested = false;
            _window?.SetUpdateStatus(new UpdateOperationResult(UpdateOperationStatus.Failed));
            return;
        }

        await PrepareForExitAsync().ConfigureAwait(true);
        Exit();
    }

    private void ApplyOverlayUatState()
    {
        var requested = Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_OVERLAY_STATE");
        var status = requested?.Trim().ToLowerInvariant() switch
        {
            "recording" => "Recording — release to finish, Escape to cancel",
            "processing" => "Transcribing locally...",
            "success" => "Inserted safely in the app you started in",
            "warning" => "Protected field — copied only; paste manually if intended",
            "error" => "Local transcription failed safely",
            _ => null,
        };
        if (status is not null)
        {
            _window?.SetSessionStatus(status);
        }
    }

    private void OnSessionStatusChanged(string status)
    {
        try
        {
            _trayIcon?.SetStatus(status);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon = new WindowsTrayIcon();
        _trayIcon.ShowWindowRequested += () => ShowMainWindow(openSettings: false);
        _trayIcon.OpenSettingsRequested += () => ShowMainWindow(openSettings: true);
        _trayIcon.ExitRequested += ExitFromTray;
        _trayIcon.SetStatus("ready");
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
        if (!_backgroundNoticeShown)
        {
            _backgroundNoticeShown = true;
            _trayIcon?.ShowBackgroundNotice();
        }
    }

    private void ShowMainWindow(bool openSettings)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is null)
            {
                return;
            }

            _window.AppWindow.Show();
            _window.Activate();
            if (openSettings)
            {
                _window.OpenSettings();
            }
        });
    }

    private void ExitFromTray()
    {
        _window?.DispatcherQueue.TryEnqueue(() => _ = ExitFromTrayAsync());
    }

    private async Task ExitFromTrayAsync()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        await PrepareForExitAsync().ConfigureAwait(true);
        _window?.Close();
    }

    private Task PrepareForExitAsync() =>
        _shutdownPreparation ??= PrepareForExitCoreAsync();

    private async Task PrepareForExitCoreAsync()
    {
        _window?.ShutdownProductWindows();
        (_polishProvider as EgOnePolishProvider)?.TerminateRuntimeImmediately();
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellClosed));
        await DisposeAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cleanShutdown = true;
        _activeProcessingCancellation?.Cancel();
        cleanShutdown &= await TryCleanupAsync(StopRecordingWatchdogAsync).ConfigureAwait(true);

        var sessionGateHeld = false;
        try
        {
            sessionGateHeld = await _sessionOperationGate
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
            cleanShutdown &= sessionGateHeld;
        }
        catch (ObjectDisposedException)
        {
            cleanShutdown = false;
        }

        if (_lifecycleMonitor is not null)
        {
            _lifecycleMonitor.Transitioned -= OnSystemLifecycleTransitioned;
            cleanShutdown &= TryCleanup(_lifecycleMonitor.Dispose);
            _lifecycleMonitor = null;
        }

        if (_pushToTalkHook is not null)
        {
            _pushToTalkHook.Signalled -= OnPushToTalkSignalled;
            cleanShutdown &= await TryCleanupAsync(
                async () => await _pushToTalkHook.DisposeAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _pushToTalkHook = null;
        }

        cleanShutdown &= await TryCleanupAsync(StopLivePreviewAsync).ConfigureAwait(true);

        if (_audioCapture is not null)
        {
            _audioCapture.LevelChanged -= OnAudioLevelChanged;
        }

        if (_sessionController is not null)
        {
            cleanShutdown &= await TryCleanupAsync(
                async () => await _sessionController.DisposeAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _sessionController = null;
            _audioCapture = null;
        }

        if (_textTargetAdapter is not null)
        {
            cleanShutdown &= TryCleanup(_textTargetAdapter.Dispose);
        }

        _textTargetAdapter = null;
        _textDelivery = null;

        if (_previewEngine is not null)
        {
            cleanShutdown &= await TryCleanupAsync(
                async () => await _previewEngine.DisposeAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _previewEngine = null;
        }

        if (_transcriptionEngine is not null)
        {
            cleanShutdown &= await TryCleanupAsync(
                async () => await _transcriptionEngine.DisposeAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _transcriptionEngine = null;
        }

        if (_polishProvider is not null)
        {
            _polishLifetime.Cancel();
            if (_polishWarmup is not null)
            {
                cleanShutdown &= await TryCleanupAsync(async () =>
                {
                    try
                    {
                        await _polishWarmup.ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        // App shutdown cancels an in-flight fixed semantic readiness probe.
                    }
                }).ConfigureAwait(true);

                _polishWarmup = null;
            }

            cleanShutdown &= await TryCleanupAsync(
                async () => await _polishProvider.DisposeAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _polishProvider = null;
        }

        if (_activationChannel is not null)
        {
            _activationChannel.ActivationRequested -= OnDuplicateActivationRequested;
            cleanShutdown &= await TryCleanupAsync(
                async () => await _activationChannel.DisposeAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _activationChannel = null;
        }

        var heartbeatCancellation = Interlocked.Exchange(ref _heartbeatCancellation, null);
        var heartbeatLoop = Interlocked.Exchange(ref _heartbeatLoop, null);
        heartbeatCancellation?.Cancel();
        if (heartbeatLoop is not null)
        {
            cleanShutdown &= await TryCleanupAsync(async () =>
            {
                try
                {
                    await heartbeatLoop.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                }
            }).ConfigureAwait(true);
        }

        heartbeatCancellation?.Dispose();

        cleanShutdown &= TryCleanup(_resourceArbiter.Dispose);
        cleanShutdown &= TryCleanup(_polishLifetime.Dispose);
        cleanShutdown &= TryCleanup(_previewGate.Dispose);
        if (_trayIcon is not null)
        {
            cleanShutdown &= TryCleanup(_trayIcon.Dispose);
        }

        _trayIcon = null;
        cleanShutdown &= TryCleanup(_historyStore.Dispose);
        cleanShutdown &= TryCleanup(_recoveryTextStore.Dispose);
        if (sessionGateHeld)
        {
            _sessionOperationGate.Release();
            cleanShutdown &= TryCleanup(_sessionOperationGate.Dispose);
        }

        if (_runId is { } runId)
        {
            var completed = cleanShutdown &&
                await _runStateStore.CompleteRunAsync(runId, DateTimeOffset.UtcNow)
                    .ConfigureAwait(true);
            cleanShutdown &= completed;
            if (completed)
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.ApplicationCleanShutdown));
            }
        }

        if (_singleInstanceLock is not null)
        {
            cleanShutdown &= TryCleanup(_singleInstanceLock.Dispose);
        }

        _singleInstanceLock = null;
        cleanShutdown &= TryCleanup(_runStateStore.Dispose);

        if (!cleanShutdown)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Recovery));
        }

        try
        {
            await _logger.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Telemetry disposal is best-effort and cannot make the product shutdown unclean.
        }

        GC.SuppressFinalize(this);
    }

    private async Task<bool> TryCleanupAsync(Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Recovery));
            return false;
        }
    }

    private bool TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
            return true;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Recovery));
            return false;
        }
    }

    private void ConfigurePushToTalk(DictationPreferences preferences)
    {
        if (!WindowsPushToTalkHook.TryCreate(
                preferences.PushToTalkGesture,
                preferences.RecordingMode,
                preferences.CancelGesture,
                preferences.QuickAddGesture,
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

        _audioCapture = TryCreateJourneyAudioFailure(out var failureCapture) &&
            failureCapture is not null
            ? failureCapture
            : TryCreatePublicFixtureAudioCapture(out var fixtureCapture) && fixtureCapture is not null
                ? fixtureCapture
                : new WasapiAudioCapture();
        var audioCapture = _audioCapture;
        audioCapture.LevelChanged += OnAudioLevelChanged;
        _textTargetAdapter = new WindowsTextTargetAdapter();
        _textDelivery = new ContextAwareTextDelivery(_textTargetAdapter);
        _sessionController = new PushToTalkSessionController(
            audioCapture,
            new WindowsForegroundTargetProvider(),
            preferredAudioDevice: string.IsNullOrWhiteSpace(_settings.PreferredMicrophoneId)
                ? null
                : new EnviousWispr.Core.Audio.AudioDeviceId(_settings.PreferredMicrophoneId));
        _pushToTalkHook.Signalled += OnPushToTalkSignalled;
        _window?.SetHotkeyReady(
            _pushToTalkHook.Gesture.ToString(),
            _pushToTalkHook.RecordingMode,
            _pushToTalkHook.CancelGesture.ToString(),
            _pushToTalkHook.QuickAddGesture.ToString());
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.HotkeyReady));
    }

    private void OnAudioLevelChanged(object? sender, AudioLevel level)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _window?.SetAudioLevel(level));
    }

    private void ConfigurePolish(PolishPreferences preferences)
    {
        var provider = preferences.Provider;
        var environmentProvider = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_POLISH_PROVIDER");
        if (Enum.TryParse<PolishProvider>(environmentProvider, ignoreCase: true, out var parsedProvider))
        {
            provider = parsedProvider;
        }

        if (provider is PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini)
        {
            var configuredModel = CloudPolishOptions.ModelIdLooksLikeProvider(
                preferences.ModelId,
                provider)
                ? preferences.ModelId!
                : CloudPolishOptions.DefaultModel(provider);
            _polishProvider = provider switch
            {
                PolishProvider.OpenAI => new OpenAiPolishProvider(_credentialStore, configuredModel),
                PolishProvider.Anthropic => new AnthropicPolishProvider(_credentialStore, configuredModel),
                PolishProvider.Gemini => new GeminiPolishProvider(_credentialStore, configuredModel),
                _ => null,
            };
            _cloudPolishConsent = CloudPolishConsent.For(provider);
            return;
        }

        if (provider == PolishProvider.Ollama)
        {
            var endpoint = Environment.GetEnvironmentVariable("ENVIOUSWISPR_OLLAMA_ENDPOINT");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = preferences.OllamaEndpoint;
            }

            var model = Environment.GetEnvironmentVariable("ENVIOUSWISPR_OLLAMA_MODEL");
            if (string.IsNullOrWhiteSpace(model))
            {
                model = preferences.ModelId ?? string.Empty;
            }

            _polishProvider = new OllamaPolishProvider(new OllamaPolishOptions(endpoint, model));
            _polishUsesLocalRuntime = true;
            _localPolishNotice = OllamaEndpointPolicy.TryNormalize(endpoint, out var normalized)
                ? $"Ollama polish uses {normalized}. Dictated text stays on this PC; hosted Ollama models are refused."
                : "Ollama polish is disabled until its endpoint is a loopback HTTP or HTTPS address.";
            return;
        }

        if (provider != PolishProvider.EgOne)
        {
            return;
        }

        var serverExecutable = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_EG1_SERVER_EXE");
        if (string.IsNullOrWhiteSpace(serverExecutable))
        {
            var provisionedServer = Path.Combine(
                _dataDirectory,
                "runtime",
                "llama.cpp",
                "llama-server.exe");
            serverExecutable = File.Exists(provisionedServer)
                ? provisionedServer
                : Path.Combine(AppContext.BaseDirectory, "runtime", "llama-server.exe");
        }

        var modelFile = Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_MODEL_PATH");
        if (string.IsNullOrWhiteSpace(modelFile))
        {
            var modelDirectory = Path.Combine(_dataDirectory, "models", "eg-1");
            var shippingModel = Path.Combine(
                modelDirectory,
                "eg-1-v2-q5_k_m-00001-of-00008.gguf");
            var founderModel = Path.Combine(modelDirectory, "active.gguf");
            modelFile = File.Exists(shippingModel) ? shippingModel : founderModel;
        }

        int? gpuLayers = null;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_GPU_LAYERS"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedGpuLayers))
        {
            gpuLayers = parsedGpuLayers;
            _polishResource = RuntimeResourceKind.Accelerator;
        }

        _polishProvider = new EgOnePolishProvider(new EgOnePolishOptions(
            new EgOneServerOptions(serverExecutable, modelFile, GpuLayers: gpuLayers),
            preferences.ModelId ?? "eg-1"));
        _polishUsesLocalRuntime = true;
    }

    private async Task ProbeOllamaRuntimeAsync(
        OllamaPolishProvider provider,
        CancellationToken cancellationToken)
    {
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.PolishRuntimeStarted));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var health = await provider.ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
        timer.Stop();
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            health.Health == OllamaHealth.Ready
                ? AppEventCode.PolishRuntimeReady
                : AppEventCode.PolishRuntimeDegraded,
            health.Health == OllamaHealth.Ready
                ? AppFailureCategory.None
                : AppFailureCategory.LocalPolish,
            timer.ElapsedMilliseconds,
            DiagnosticProviderFor(provider.ProviderId),
            health.Error?.Code));
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (health.Health != OllamaHealth.Ready)
            {
                _window?.SetSessionStatus(OllamaHealthStatus(health.Health));
            }
        });
    }

    private async Task WarmPolishRuntimeAsync(
        EgOnePolishProvider provider,
        CancellationToken cancellationToken)
    {
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.PolishRuntimeStarted));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var health = await provider.ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
        timer.Stop();
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            health.Health == EgOneHealth.Green
                ? AppEventCode.PolishRuntimeReady
                : AppEventCode.PolishRuntimeDegraded,
            health.Health == EgOneHealth.Green
                ? AppFailureCategory.None
                : AppFailureCategory.LocalPolish,
            timer.ElapsedMilliseconds));
    }

    private async Task ConfigureTranscriptionAsync(FinalAsrEngine configuredEngine)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_DISABLE_LOCAL_RUNTIME"),
                "1",
                StringComparison.Ordinal))
        {
            _window?.SetSessionStatus("Local transcription disabled for performance UAT");
            return;
        }

        var environmentEngine = Environment.GetEnvironmentVariable("ENVIOUSWISPR_ASR_ENGINE");
        if (Enum.TryParse<FinalAsrEngine>(environmentEngine, ignoreCase: true, out var parsedEngine))
        {
            configuredEngine = parsedEngine;
        }

        var engine = configuredEngine == FinalAsrEngine.Automatic
            ? FinalAsrEngine.Parakeet
            : configuredEngine;
        var whisperLanguage = WhisperLanguageCodes.For(
            _settings.Preferences.Dictation.WhisperLanguage);
        if (WhisperLanguageCodes.TryNormalize(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_ASR_LANGUAGE"),
                out var environmentLanguage))
        {
            whisperLanguage = environmentLanguage;
        }

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

        var hardware = await new WindowsHardwareDiscovery(_cudaRuntimeDirectory)
            .ProbeAsync()
            .ConfigureAwait(true);
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.RuntimeSelectionObserved,
            Engine: engine == FinalAsrEngine.Whisper
                ? DiagnosticEngineChoice.Whisper
                : DiagnosticEngineChoice.Parakeet,
            HardwareClass: DiagnosticHardwareClassFor(hardware)));
        var workerExecutable = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        _transcriptionEngine = engine == FinalAsrEngine.Whisper
            ? CreateWhisperEngine(workerExecutable, modelDirectory, hardware, whisperLanguage)
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
            _transcriptionEngine = engine == FinalAsrEngine.Whisper
                ? CreateCpuWhisperEngine(workerExecutable, modelDirectory, hardware, whisperLanguage)
                : CreateCpuParakeetEngine(workerExecutable, modelDirectory, hardware);
            started = _transcriptionEngine is null
                ? new RuntimeWorkerResult(false, RuntimeWorkerState.Faulted)
                : await _transcriptionEngine.StartAsync().ConfigureAwait(true);
            if (!started.Succeeded)
            {
                if (_transcriptionEngine is not null)
                {
                    await _transcriptionEngine.DisposeAsync().ConfigureAwait(true);
                }

                _transcriptionEngine = null;
                _window?.SetSessionStatus("Local transcription worker could not start");
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.DictationTranscriptionFailed,
                    AppFailureCategory.RuntimeWorker));
                return;
            }

            ConfigureLivePreview(workerExecutable, hardware, whisperLanguage, forceCpu: true);
            _window?.SetSessionStatus("Local transcription ready with CPU recovery");
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionDegraded,
                AppFailureCategory.RuntimeProvider,
                ErrorCode: AppErrorCode.RuntimeProviderUnavailable));
            return;
        }

        ConfigureLivePreview(workerExecutable, hardware, whisperLanguage);
        _window?.SetSessionStatus("Local transcription ready");
    }

    private void ConfigureLivePreview(
        string workerExecutable,
        HardwareSnapshot hardware,
        string language,
        bool forceCpu = false)
    {
        var modelDirectory = ResolveModelDirectory(
            WhisperTranscriptionEngine.PreviewModelId,
            "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY");
        if (modelDirectory is null ||
            !new LocalWhisperModelProbe().Probe(modelDirectory).PreviewSmallComplete)
        {
            return;
        }

        var useCuda = !forceCpu &&
            hardware.Architecture == ProcessorArchitectureKind.X64 &&
            hardware.Cuda.IsDriverAvailable &&
            hardware.Cuda.DeviceCount > 0 &&
            hardware.IsOnnxRuntimeCudaDependencySetAvailable;
        var provider = useCuda ? RuntimeProviderKind.Cuda : RuntimeProviderKind.Cpu;
        var threads = Math.Clamp(
            hardware.PhysicalCoreCount > 0
                ? hardware.PhysicalCoreCount
                : Math.Max(1, hardware.LogicalProcessorCount / 2),
            2,
            8);
        _previewEngine = new RuntimeWorkerLivePreviewEngine(
            new RuntimeWorkerTranscriptionOptions(
                workerExecutable,
                modelDirectory,
                provider,
                ParakeetModelPack.Quantized,
                threads,
                CpuFallbackThreads: threads,
                StartupTimeout: TimeSpan.FromSeconds(15),
                TranscriptionTimeout: TimeSpan.FromSeconds(15),
                Engine: FinalAsrEngine.Whisper,
                WhisperPack: WhisperModelPack.PreviewSmall,
                Language: language,
                CudaRuntimeDirectory: _cudaRuntimeDirectory),
            _resourceArbiter);
    }

    private RuntimeWorkerTranscriptionEngine? CreateParakeetEngine(
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
                CudaRuntimeDirectory: _cudaRuntimeDirectory));
    }

    private static RuntimeWorkerTranscriptionEngine? CreateCpuParakeetEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware)
    {
        var models = new LocalParakeetModelProbe().Probe(modelDirectory);
        var selection = ParakeetRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        if (!selection.Succeeded ||
            selection.Provider is not RuntimeProviderKind.Cpu ||
            selection.ModelPack is null)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(
            new RuntimeWorkerTranscriptionOptions(
                workerExecutable,
                modelDirectory,
                RuntimeProviderKind.Cpu,
                selection.ModelPack.Value,
                selection.IntraOpThreads,
                selection.InterOpThreads,
                CpuFallbackThreads: selection.IntraOpThreads));
    }

    private RuntimeWorkerTranscriptionEngine? CreateWhisperEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware,
        string language)
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
            Language: language,
            CudaRuntimeDirectory: _cudaRuntimeDirectory));
    }

    private static RuntimeWorkerTranscriptionEngine? CreateCpuWhisperEngine(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware,
        string language)
    {
        var models = new LocalWhisperModelProbe().Probe(modelDirectory);
        var selection = WhisperRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        if (!selection.Succeeded ||
            selection.Provider is not RuntimeProviderKind.Cpu ||
            selection.ModelPack is null)
        {
            return null;
        }

        return new RuntimeWorkerTranscriptionEngine(new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            selection.ThreadCount,
            CpuFallbackThreads: selection.ThreadCount,
            Engine: FinalAsrEngine.Whisper,
            WhisperPack: selection.ModelPack.Value,
            Language: language));
    }

    private static string? ResolveCudaRuntimeDirectory(string dataDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("ENVIOUSWISPR_CUDA_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var installed = Path.Combine(dataDirectory, "runtime", "cuda");
        return Directory.Exists(installed) ? installed : null;
    }

    private string? ResolveModelDirectory(
        string modelId,
        string environmentVariable = "ENVIOUSWISPR_MODEL_DIRECTORY")
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var installed = Path.Combine(_dataDirectory, "models", modelId);
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

    private void OnPushToTalkSignalled(object? sender, PushToTalkSignalEvent args)
    {
        if (args.Signal == PushToTalkSignal.QuickAdd)
        {
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.QuickAddRequested));
            _ = HandleQuickAddAsync();
            return;
        }

        _ = HandlePushToTalkAsync(args.Signal);
    }

    private async Task HandleQuickAddAsync()
    {
        if (_exitRequested || _disposed || _textTargetAdapter is null ||
            _sessionController?.CurrentSession is not null)
        {
            return;
        }

        var target = new WindowsForegroundTargetProvider().CaptureForegroundTarget();
        if (target is null || !target.Value.IsValid)
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                ShowMainWindow(openSettings: false);
                _window?.OpenQuickAdd(null, "Select a word in another app, then press the Add-a-word shortcut again.");
            });
            return;
        }

        var context = await _textTargetAdapter.CaptureContextAsync(
            target.Value,
            TextDeliveryOptions.Default).ConfigureAwait(false);
        var selection = context.Status == TargetContextStatus.Available &&
            context.Context?.TargetKind != TextTargetKind.Terminal
                ? context.Context?.Selection.Trim()
                : null;
        var message = !string.IsNullOrWhiteSpace(selection)
            ? null
            : context.Context?.TargetKind == TextTargetKind.Terminal
                ? "Terminal windows do not share their selection. Add the word here by hand."
                : "No readable selection was found. Select a misheard word, then try the shortcut again.";
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.QuickAddPrepared));
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            ShowMainWindow(openSettings: false);
            _window?.OpenQuickAdd(selection, message);
        });
    }

    private static bool TryCreatePublicFixtureAudioCapture(
        out PublicFixtureAudioCapture? capture)
    {
        capture = null;
        return HasValidPublicFixtureJourneyUatConfiguration() &&
            PublicFixtureAudioCapture.TryCreate(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_AUDIO_FIXTURE"),
                out capture);
    }

    private static bool TryCreateJourneyAudioFailure(out IAudioCapture? capture)
    {
        capture = null;
        if (!HasValidFailureJourneyUatConfiguration() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_AUDIO_FAILURE"),
                "access-denied",
                StringComparison.Ordinal))
        {
            return false;
        }

        capture = new AccessDeniedAudioCapture();
        return true;
    }

    private void StartPublicFixtureJourneyUat()
    {
        if (!HasValidPublicFixtureJourneyUatConfiguration())
        {
            return;
        }

        _ = RunPublicFixtureJourneyUatAsync();
    }

    private async Task RunPublicFixtureJourneyUatAsync()
    {
        const string startVariable = "ENVIOUSWISPR_UAT_JOURNEY_START_EVENT";
        const string completeVariable = "ENVIOUSWISPR_UAT_JOURNEY_COMPLETE_EVENT";
        try
        {
            if (_audioCapture is not PublicFixtureAudioCapture ||
                _sessionController is null ||
                !TryOpenJourneyUatEvent(startVariable, out var startEvent) ||
                startEvent is null)
            {
                return;
            }

            using (startEvent)
            {
                var started = await Task.Run(
                        () => startEvent.WaitOne(TimeSpan.FromSeconds(30)))
                    .ConfigureAwait(false);
                if (!started || _exitRequested || _disposed)
                {
                    return;
                }
            }

            await HandlePushToTalkAsync(PushToTalkSignal.Pressed).ConfigureAwait(false);
            if (_sessionController.CurrentSession?.State !=
                EnviousWispr.Core.Sessions.DictationSessionState.Recording)
            {
                return;
            }

            var fixtureHold = ResolveJourneyUatHoldDuration();
            await Task.Delay(fixtureHold).ConfigureAwait(false);
            var stopSignal = string.Equals(
                    Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_JOURNEY_CANCEL"),
                    "1",
                    StringComparison.Ordinal)
                ? PushToTalkSignal.Cancelled
                : PushToTalkSignal.Released;
            await HandlePushToTalkAsync(stopSignal).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Unknown));
        }
        finally
        {
            SignalJourneyUatEvent(completeVariable);
            if (string.Equals(
                    Environment.GetEnvironmentVariable(
                        "ENVIOUSWISPR_UAT_JOURNEY_EXIT_AFTER_COMPLETION"),
                    "1",
                    StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                _window?.DispatcherQueue.TryEnqueue(ExitFromTray);
            }
        }
    }

    private static bool TryOpenJourneyUatEvent(
        string environmentVariable,
        out EventWaitHandle? journeyEvent)
    {
        journeyEvent = null;
        var eventName = Environment.GetEnvironmentVariable(environmentVariable);
        if (!IsJourneyUatEventName(eventName))
        {
            return false;
        }

        try
        {
            journeyEvent = EventWaitHandle.OpenExisting(eventName!);
            return true;
        }
        catch (Exception exception) when (exception is
                                          ArgumentException or
                                          WaitHandleCannotBeOpenedException or
                                          UnauthorizedAccessException or
                                          IOException)
        {
            return false;
        }
    }

    private static bool HasValidPublicFixtureJourneyUatConfiguration()
    {
        return string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_JOURNEY"),
                "public-fixture-v1",
                StringComparison.Ordinal) &&
            HasValidJourneyUatCredentialSuffix() &&
            IsJourneyUatEventName(Environment.GetEnvironmentVariable(
                "ENVIOUSWISPR_UAT_JOURNEY_START_EVENT")) &&
            IsJourneyUatEventName(Environment.GetEnvironmentVariable(
                "ENVIOUSWISPR_UAT_JOURNEY_COMPLETE_EVENT"));
    }

    private static bool HasValidFailureJourneyUatConfiguration() =>
        string.Equals(
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_JOURNEY"),
            "failure-v1",
            StringComparison.Ordinal) &&
        HasValidJourneyUatCredentialSuffix() &&
        IsPerformanceUatEventName(Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_READY_EVENT")) &&
        IsPerformanceUatEventName(Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_RUNTIME_READY_EVENT"));

    private static bool HasValidJourneyUatCredentialSuffix()
    {
        var credentialSuffix = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX");
        return credentialSuffix is { Length: 40 } &&
            credentialSuffix.StartsWith("journey-", StringComparison.Ordinal) &&
            Guid.TryParseExact(credentialSuffix[8..], "N", out _);
    }

    private static bool IsPerformanceUatEventName(string? eventName)
    {
        const string allowedPrefix = @"Local\EnviousLabs.EnviousWispr.PerformanceUat.";
        return !string.IsNullOrWhiteSpace(eventName) &&
            eventName.Length <= 200 &&
            eventName.StartsWith(allowedPrefix, StringComparison.Ordinal);
    }

    private static TimeSpan ResolveJourneyUatHoldDuration()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_LIVE_PREVIEW"),
                "1",
                StringComparison.Ordinal))
        {
            return TimeSpan.FromSeconds(5);
        }

        var requested = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_JOURNEY_HOLD_MILLISECONDS");
        return int.TryParse(requested, out var milliseconds) &&
            milliseconds is >= 100 and <= 5_000
                ? TimeSpan.FromMilliseconds(milliseconds)
                : TimeSpan.FromMilliseconds(150);
    }

    private static bool IsJourneyUatEventName(string? eventName)
    {
        const string allowedPrefix = @"Local\EnviousLabs.EnviousWispr.JourneyUat.";
        return !string.IsNullOrWhiteSpace(eventName) &&
            eventName.Length <= 200 &&
            eventName.StartsWith(allowedPrefix, StringComparison.Ordinal);
    }

    private static void SignalJourneyUatEvent(string environmentVariable)
    {
        if (!TryOpenJourneyUatEvent(environmentVariable, out var journeyEvent) ||
            journeyEvent is null)
        {
            return;
        }

        using (journeyEvent)
        {
            journeyEvent.Set();
        }
    }

    private async Task HandlePushToTalkAsync(PushToTalkSignal signal)
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        var controller = _sessionController;
        if (controller is null)
        {
            return;
        }

        if (!await _sessionOperationGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        CancellationTokenSource? processingCancellation = null;
        try
        {
            if (signal == PushToTalkSignal.Pressed)
            {
                if (_hasPendingRecovery)
                {
                    _window?.DispatcherQueue.TryEnqueue(() =>
                    {
                        ShowMainWindow(openSettings: false);
                        _window?.SetReliabilityNotice(
                            "Recovered text is waiting",
                            "Copy or delete the unfinished dictation on Home before starting another recording.");
                        _window?.SetSessionStatus("Review recovered text before recording again");
                    });
                    return;
                }

                var admission = SystemResourceAdmissionPolicy.Evaluate(_resourceProbe.Probe());
                _canPersistRecoveryForSession = admission.CanPersistRecovery;
                if (admission.Status != DictationAdmissionStatus.Ready)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.ResourcePressureDetected,
                        AppFailureCategory.ResourcePressure,
                        ErrorCode: admission.Error?.Code));
                }

                if (!admission.CanStart)
                {
                    _window?.DispatcherQueue.TryEnqueue(() =>
                    {
                        _window?.SetReliabilityNotice(
                            "Windows memory is critically low",
                            "Close another memory-heavy app, then try dictation again. No recording was started.",
                            isError: true);
                        _window?.SetSessionStatus("Recording paused because Windows memory is critically low");
                    });
                    return;
                }

                if (!admission.CanPersistRecovery)
                {
                    _window?.DispatcherQueue.TryEnqueue(() =>
                        _window?.SetReliabilityNotice(
                            "Disk space is critically low",
                            "Dictation can continue, but EnviousWispr may be unable to save an encrypted crash-recovery copy."));
                }
            }
            else
            {
                await StopRecordingWatchdogAsync().ConfigureAwait(false);
            }

            var recoverCancelledRecording =
                signal == PushToTalkSignal.Cancelled && _escapeRecoveryForSession;
            var result = signal switch
            {
                PushToTalkSignal.Pressed => await controller.PressAsync().ConfigureAwait(false),
                PushToTalkSignal.Released => await controller.ReleaseAsync().ConfigureAwait(false),
                PushToTalkSignal.Cancelled when recoverCancelledRecording =>
                    await controller.ReleaseAsync().ConfigureAwait(false),
                PushToTalkSignal.Cancelled => await controller.CancelAsync().ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported push-to-talk signal."),
            };

            WriteSessionEvent(result);
            if (result.Kind == SessionTransitionKind.Started && result.Session is not null)
            {
                _escapeRecoveryForSession = _settings.Preferences.Dictation.EscapeRecoveryEnabled;
                StartRecordingWatchdog(controller, result.Session.Id);
                await StartLivePreviewAsync().ConfigureAwait(false);
            }
            else if (result.Kind == SessionTransitionKind.FinalizeReady &&
                result.Session is not null &&
                result.Audio is not null)
            {
                processingCancellation = new CancellationTokenSource(
                    MaximumFinalProcessingDuration);
                _activeProcessingCancellation = processingCancellation;
                await StopLivePreviewAsync().ConfigureAwait(false);
                await TranscribeFinalAsync(
                        controller,
                        result.Session.Id,
                        result.Audio,
                        processingCancellation.Token,
                        recoveryOnly: recoverCancelledRecording)
                    .ConfigureAwait(false);
                return;
            }
            else if (result.Kind is SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
            {
                _escapeRecoveryForSession = false;
                await StopLivePreviewAsync().ConfigureAwait(false);
                await controller.ResetAsync().ConfigureAwait(false);
            }

            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus(SessionStatus(result)));
        }
        catch (OperationCanceledException)
        {
            await RecoverFailedSessionAsync(
                controller,
                new AppError(
                    AppErrorCode.SessionTimedOut,
                    AppErrorStage.Session,
                    CanRetry: true),
                "The dictation timed out and was recovered safely").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationSessionFailed,
                AppFailureCategory.Unknown));
            await RecoverFailedSessionAsync(
                controller,
                new AppError(
                    AppErrorCode.InvalidTransition,
                    AppErrorStage.Session,
                    CanRetry: true),
                "Session failed and was reset safely").ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_activeProcessingCancellation, processingCancellation))
            {
                _activeProcessingCancellation = null;
            }

            processingCancellation?.Dispose();
            _sessionOperationGate.Release();
        }
    }

    private void StartRecordingWatchdog(
        PushToTalkSessionController controller,
        DictationSessionId sessionId)
    {
        _recordingWatchdogCancellation?.Cancel();
        _recordingWatchdogCancellation?.Dispose();
        _recordingWatchdogCancellation = new CancellationTokenSource();
        _recordingWatchdog = WatchRecordingAsync(
            controller,
            sessionId,
            _recordingWatchdogCancellation.Token);
    }

    private async Task WatchRecordingAsync(
        PushToTalkSessionController controller,
        DictationSessionId sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RecordingWatchdogDuration(), cancellationToken).ConfigureAwait(false);
            await _sessionOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (controller.CurrentSession is
                    {
                        Id: var currentId,
                        State: EnviousWispr.Core.Sessions.DictationSessionState.Recording,
                    } && currentId == sessionId)
                {
                    await StopLivePreviewAsync().ConfigureAwait(false);
                    var error = new AppError(
                        AppErrorCode.SessionTimedOut,
                        AppErrorStage.Session,
                        CanRetry: true);
                    await controller.AbortAsync(error, cancellationToken).ConfigureAwait(false);
                    await controller.ResetAsync(cancellationToken).ConfigureAwait(false);
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.DictationSessionRecovered,
                        AppFailureCategory.Recovery,
                        ErrorCode: error.Code));
                    _window?.DispatcherQueue.TryEnqueue(() =>
                        _window?.SetSessionStatus("Recording timed out and was cancelled safely"));
                }
            }
            finally
            {
                _sessionOperationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan RecordingWatchdogDuration()
    {
        var requested = Environment.GetEnvironmentVariable(
            "ENVIOUSWISPR_UAT_RECORDING_TIMEOUT_MILLISECONDS");
        return int.TryParse(requested, out var milliseconds) &&
            milliseconds is >= 500 and <= 30_000
                ? TimeSpan.FromMilliseconds(milliseconds)
                : MaximumRecordingDuration;
    }

    private async Task StopRecordingWatchdogAsync()
    {
        var cancellation = Interlocked.Exchange(ref _recordingWatchdogCancellation, null);
        var watchdog = Interlocked.Exchange(ref _recordingWatchdog, null);
        cancellation?.Cancel();
        if (watchdog is not null)
        {
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation?.Dispose();
    }

    private async Task RecoverFailedSessionAsync(
        PushToTalkSessionController controller,
        AppError error,
        string status)
    {
        await StopRecordingWatchdogAsync().ConfigureAwait(false);
        await StopLivePreviewAsync().ConfigureAwait(false);
        if (controller.CurrentSession is not null)
        {
            await controller.AbortAsync(error).ConfigureAwait(false);
            await controller.ResetAsync().ConfigureAwait(false);
        }

        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationSessionRecovered,
            AppFailureCategory.Recovery,
            ErrorCode: error.Code));
        ShowPendingRecovery();
        _window?.DispatcherQueue.TryEnqueue(() => _window?.SetSessionStatus(status));
    }

    private async Task RecoverFromSystemTransitionAsync(SystemLifecycleTransition transition)
    {
        _activeProcessingCancellation?.Cancel();
        if (!await _sessionOperationGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus("Windows interrupted the active dictation; recovery is still pending"));
            return;
        }

        CancellationTokenSource? processingCancellation = null;
        try
        {
            var controller = _sessionController;
            if (controller?.CurrentSession is null)
            {
                return;
            }

            if (controller.CurrentSession.State ==
                EnviousWispr.Core.Sessions.DictationSessionState.Recording)
            {
                await StopRecordingWatchdogAsync().ConfigureAwait(false);
                var result = await controller.ReleaseAsync().ConfigureAwait(false);
                WriteSessionEvent(result);
                if (result.Kind == SessionTransitionKind.FinalizeReady &&
                    result.Session is not null &&
                    result.Audio is not null)
                {
                    processingCancellation = new CancellationTokenSource(
                        MaximumFinalProcessingDuration);
                    _activeProcessingCancellation = processingCancellation;
                    await StopLivePreviewAsync().ConfigureAwait(false);
                    _window?.DispatcherQueue.TryEnqueue(() =>
                        _window?.SetSessionStatus(
                            transition == SystemLifecycleTransition.Suspending
                                ? "Windows is suspending — captured audio is being preserved"
                                : "Windows locked — captured audio is being preserved"));
                    await TranscribeFinalAsync(
                            controller,
                            result.Session.Id,
                            result.Audio,
                            processingCancellation.Token)
                        .ConfigureAwait(false);
                    return;
                }
            }

            await RecoverFailedSessionAsync(
                controller,
                new AppError(
                    AppErrorCode.Cancelled,
                    AppErrorStage.SystemLifecycle,
                    CanRetry: true),
                "Windows interrupted the session; it was reset safely").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_sessionController is { } controller)
            {
                await RecoverFailedSessionAsync(
                    controller,
                    new AppError(
                        AppErrorCode.SessionTimedOut,
                        AppErrorStage.SystemLifecycle,
                        CanRetry: true),
                    "Windows interrupted the session; recovery timed out safely").ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationSessionFailed,
                AppFailureCategory.SystemLifecycle));
            if (_sessionController is { } controller)
            {
                await RecoverFailedSessionAsync(
                    controller,
                    new AppError(
                        AppErrorCode.InvalidTransition,
                        AppErrorStage.SystemLifecycle,
                        CanRetry: true),
                    "Windows interrupted the session; it was reset safely").ConfigureAwait(false);
            }
        }
        finally
        {
            if (ReferenceEquals(_activeProcessingCancellation, processingCancellation))
            {
                _activeProcessingCancellation = null;
            }

            processingCancellation?.Dispose();
            _sessionOperationGate.Release();
        }
    }

    private async Task StartLivePreviewAsync()
    {
        if (!_settings.Preferences.LivePreviewEnabled)
        {
            return;
        }

        var engine = _previewEngine;
        var audioCapture = _audioCapture as IAudioSnapshotSource;
        if (engine is null || audioCapture is null)
        {
            return;
        }

        await _previewGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_previewLoop is not null)
            {
                return;
            }

            var started = await engine.StartAsync().ConfigureAwait(false);
            if (!started.Succeeded)
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.LivePreviewFailed,
                    FailureFor(started.Error)));
                return;
            }

            _previewSequence = 0;
            _previewCancellation = new CancellationTokenSource();
            _previewLoop = RunLivePreviewAsync(
                engine,
                audioCapture,
                _previewCancellation.Token);
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.LivePreviewStarted));
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private async Task RunLivePreviewAsync(
        RuntimeWorkerLivePreviewEngine engine,
        IAudioSnapshotSource audioCapture,
        CancellationToken cancellationToken)
    {
        var cadence = TimeSpan.FromMilliseconds(2_500);
        var maximumWindow = TimeSpan.FromSeconds(20);
        try
        {
            while (true)
            {
                await Task.Delay(cadence, cancellationToken).ConfigureAwait(false);
                var snapshot = audioCapture.GetSnapshot(maximumWindow);
                if (snapshot is null || snapshot.Samples.Length < 8_000)
                {
                    continue;
                }

                var timer = System.Diagnostics.Stopwatch.StartNew();
                var update = await engine.PreviewAsync(
                    snapshot,
                    Interlocked.Increment(ref _previewSequence),
                    cancellationToken).ConfigureAwait(false);
                timer.Stop();
                if (!update.Succeeded)
                {
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.LivePreviewFailed,
                        FailureFor(update.Error),
                        timer.ElapsedMilliseconds));
                    return;
                }

                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.LivePreviewUpdated,
                    ElapsedMilliseconds: timer.ElapsedMilliseconds));
                _window?.DispatcherQueue.TryEnqueue(() =>
                    _window?.SetLivePreview(update.Text));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Release or cancellation intentionally stops preview without affecting final ASR.
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.LivePreviewFailed,
                AppFailureCategory.RuntimeWorker));
        }
    }

    private async Task StopLivePreviewAsync()
    {
        await _previewGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cancellation = _previewCancellation;
            var loop = _previewLoop;
            _previewCancellation = null;
            _previewLoop = null;
            cancellation?.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The preview task observes cancellation as its normal stop path.
                }
            }

            cancellation?.Dispose();
            if (_previewEngine is not null)
            {
                await _previewEngine.StopAsync().ConfigureAwait(false);
            }

            _window?.DispatcherQueue.TryEnqueue(() => _window?.SetLivePreview(text: null));
            if (loop is not null)
            {
                _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.LivePreviewStopped));
            }
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private async Task TranscribeFinalAsync(
        PushToTalkSessionController controller,
        DictationSessionId sessionId,
        CapturedAudio audio,
        CancellationToken cancellationToken,
        bool recoveryOnly = false)
    {
        _escapeRecoveryForSession = false;
        var engine = _transcriptionEngine;
        if (engine is null)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.AsrUnavailable));
            await controller.CompleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            await controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
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
            var transcript = await engine.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
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
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DeterministicProcessingStarted));
            var processingTimer = System.Diagnostics.Stopwatch.StartNew();
            var deterministicRequest = new DeterministicTextRequest(
                transcript,
                _customWords,
                _deterministicTextOptions);
            var processed = await _deterministicTextPipeline.ProcessAsync(
                deterministicRequest,
                cancellationToken).ConfigureAwait(false);
            processingTimer.Stop();
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                processed.IsDegraded
                    ? AppEventCode.DeterministicProcessingDegraded
                    : AppEventCode.DeterministicProcessingCompleted,
                processed.IsDegraded
                    ? AppFailureCategory.PostProcessing
                    : AppFailureCategory.None,
                processingTimer.ElapsedMilliseconds));
            await SaveRecoveryTextAsync(processed.Output, cancellationToken).ConfigureAwait(false);
            var polishResult = await TryPolishAsync(
                    processed.Output,
                    transcript.DetectedLanguage,
                    cancellationToken)
                .ConfigureAwait(false);
            if (polishResult is not null && !polishResult.UsedFallback)
            {
                processed = await _deterministicTextPipeline.ApplyPolishedTextAsync(
                    deterministicRequest,
                    processed,
                    polishResult.Output.Text,
                    cancellationToken).ConfigureAwait(false);
                await SaveRecoveryTextAsync(processed.Output, cancellationToken).ConfigureAwait(false);
            }

            if (!recoveryOnly &&
                !string.IsNullOrWhiteSpace(processed.Output.Text) &&
                _textDelivery is not null &&
                controller.CurrentSession is { } pendingSession)
            {
                var deliveryTransition = await controller
                    .BeginDeliveryAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (deliveryTransition.Kind == SessionTransitionKind.Delivering)
                {
                    _window?.DispatcherQueue.TryEnqueue(() =>
                        _window?.SetSessionStatus("Delivering to the app you started in..."));
                    _logger.Write(new AppLogEntry(
                        DateTimeOffset.UtcNow,
                        AppEventCode.TextDeliveryStarted));
                    var deliveryTimer = System.Diagnostics.Stopwatch.StartNew();
                    var delivery = await _textDelivery.DeliverAsync(
                        new TextDeliveryRequest(
                            processed.Output,
                            pendingSession.Target,
                            DeliveryLanguage(transcript),
                            pendingSession.DeliveryOptions),
                        cancellationToken).ConfigureAwait(false);
                    deliveryTimer.Stop();
                    WriteDeliveryEvent(delivery, deliveryTimer.ElapsedMilliseconds);
                    if (delivery.Delivered || delivery.ClipboardFallback)
                    {
                        await ClearRecoveryTextAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        ShowPendingRecovery();
                    }
                    await SaveHistoryAsync(
                        transcript,
                        processed.Output.Text,
                        polishResult is { Status: PolishAttemptStatus.Polished },
                        delivery.Delivered).ConfigureAwait(false);
                    await controller.CompleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    await controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                    _window?.DispatcherQueue.TryEnqueue(() =>
                        _window?.SetSessionStatus(DeliveryStatus(delivery)));
                    return;
                }
            }

            await SaveHistoryAsync(
                transcript,
                processed.Output.Text,
                polishResult is { Status: PolishAttemptStatus.Polished },
                wasDelivered: false,
                expiresAt: recoveryOnly ? DateTimeOffset.UtcNow.AddHours(24) : null,
                forceSave: recoveryOnly)
                .ConfigureAwait(false);
            await controller.CompleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            await controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            ShowPendingRecovery();
            if (recoveryOnly && !string.IsNullOrWhiteSpace(processed.Output.Text))
            {
                _window?.DispatcherQueue.TryEnqueue(() =>
                    _window?.SetReliabilityNotice(
                        "Escape Recovery finished",
                        "The dictation is ready to copy on Home and stays in History for 24 hours unless you Keep it."));
            }
            var status = string.IsNullOrWhiteSpace(processed.Output.Text)
                    ? "No speech detected"
                    : recoveryOnly
                        ? "Escape Recovery finished — text is ready to copy"
                    : processed.IsDegraded
                    ? "Transcribed and cleaned locally with a safe fallback"
                    : polishResult is { UsedFallback: true }
                        ? PolishFallbackStatus(polishResult)
                    : polishResult is { UsedFallback: false }
                        ? _cloudPolishConsent is null
                            ? "Transcribed and polished locally"
                            : $"Transcribed and polished directly with {_cloudPolishConsent.ProviderName}"
                    : transcript.UsedFallback
                        ? "Transcribed and cleaned locally with CPU fallback"
                        : "Transcribed and cleaned locally";
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
            await controller.CompleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            await controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus("Local transcription failed safely"));
        }
    }

    private async Task SaveRecoveryTextAsync(
        ProcessedText text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            return;
        }

        var record = new RecoveryTextRecord(text.SessionId, DateTimeOffset.UtcNow, text.Text);
        _pendingRecoveryRecord = record;
        _hasPendingRecovery = true;
        if (!_canPersistRecoveryForSession)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.RecoveryTextUnavailable,
                AppFailureCategory.ResourcePressure,
                ErrorCode: AppErrorCode.LowDiskSpace));
            return;
        }

        var saved = await _recoveryTextStore.SaveAsync(record, cancellationToken)
            .ConfigureAwait(false);
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            saved ? AppEventCode.RecoveryTextSaved : AppEventCode.RecoveryTextUnavailable,
            saved ? AppFailureCategory.None : AppFailureCategory.Recovery,
            ErrorCode: saved ? null : AppErrorCode.StorageUnavailable));
    }

    private async Task ClearRecoveryTextAsync()
    {
        if (!await _recoveryTextStore.ClearAsync().ConfigureAwait(false))
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.RecoveryTextUnavailable,
                AppFailureCategory.Recovery,
                ErrorCode: AppErrorCode.StorageUnavailable));
            return;
        }

        _pendingRecoveryRecord = null;
        _hasPendingRecovery = false;
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.RecoveryTextCleared));
        _window?.DispatcherQueue.TryEnqueue(() => _window?.ClearRecoveredText());
    }

    private void ShowPendingRecovery()
    {
        var record = _pendingRecoveryRecord;
        if (record is null)
        {
            return;
        }

        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            ShowMainWindow(openSettings: false);
            _window?.SetRecoveredText(new RecoveryTextLoadResult(
                RecoveryTextLoadStatus.Found,
                record));
        });
    }

    private async Task SaveHistoryAsync(
        Transcript transcript,
        string text,
        bool wasPolished,
        bool wasDelivered,
        DateTimeOffset? expiresAt = null,
        bool forceSave = false)
    {
        var historyPreferences = _settings.Preferences.History;
        if ((!historyPreferences.IsEnabled && !forceSave) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var result = await _historyStore.AddAsync(
            DictationHistoryEntry.Create(
                DateTimeOffset.UtcNow,
                text,
                transcript.EngineId,
                wasPolished,
                wasDelivered,
                expiresAt),
            historyPreferences.RetentionDays,
            DateTimeOffset.UtcNow).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                if (_window is not null)
                {
                    _ = _window.NotifyHistoryChangedAsync();
                }
            });
        }
    }

    private async Task<PolishResult?> TryPolishAsync(
        ProcessedText input,
        string? detectedLanguage,
        CancellationToken cancellationToken)
    {
        var provider = _polishProvider;
        if (provider is null || string.IsNullOrWhiteSpace(input.Text))
        {
            return null;
        }

        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.PolishStarted,
            Provider: DiagnosticProviderFor(provider.ProviderId)));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        PolishResult result;
        if (!_polishUsesLocalRuntime)
        {
            result = await provider.TryPolishAsync(
                new PolishRequest(input, detectedLanguage),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var acquired = await _resourceArbiter.AcquireAsync(
                _polishResource,
                RuntimeWorkloadKind.LocalPolish,
                TimeSpan.FromSeconds(2),
                cancellationToken).ConfigureAwait(false);
            if (!acquired.Succeeded || acquired.Lease is null)
            {
                timer.Stop();
                result = new PolishResult(
                    input,
                    PolishAttemptStatus.Unavailable,
                    acquired.Error ?? new AppError(
                        AppErrorCode.RuntimeResourceBusy,
                        AppErrorStage.RuntimeResource,
                        CanRetry: true),
                    timer.ElapsedMilliseconds);
            }
            else
            {
                await using (acquired.Lease.ConfigureAwait(false))
                {
                    result = await provider.TryPolishAsync(
                        new PolishRequest(input, detectedLanguage),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        timer.Stop();
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            result.UsedFallback ? AppEventCode.PolishDegraded : AppEventCode.PolishCompleted,
            result.UsedFallback
                ? _polishUsesLocalRuntime
                    ? AppFailureCategory.LocalPolish
                    : AppFailureCategory.CloudPolish
                : AppFailureCategory.None,
            timer.ElapsedMilliseconds,
            DiagnosticProviderFor(provider.ProviderId),
            result.Error?.Code));
        return result;
    }

    private void WriteSessionEvent(SessionTransitionResult result)
    {
        if (result.Kind == SessionTransitionKind.Started)
        {
            _pushToTalkHook?.SetRecordingActive(active: true);
        }
        else if (result.Kind is SessionTransitionKind.FinalizeReady or
                 SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
        {
            _pushToTalkHook?.SetRecordingActive(active: false);
            if (result.Kind is SessionTransitionKind.Cancelled or SessionTransitionKind.Failed)
            {
                _escapeRecoveryForSession = false;
            }
        }

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
                FailureFor(result.Error),
                ErrorCode: result.Error?.Code));
        }
    }

    private void WriteDeliveryEvent(DeliveryResult result, long elapsedMilliseconds)
    {
        var eventCode = result switch
        {
            { Delivered: true } => AppEventCode.TextDeliveryCompleted,
            { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.None } =>
                AppEventCode.TextDeliveryClipboardFallback,
            { ClipboardFallback: true } => AppEventCode.TextDeliveryRefused,
            _ => AppEventCode.TextDeliveryFailed,
        };
        var errorCode = result.RefusalReason switch
        {
            TextDeliveryRefusalReason.None => (AppErrorCode?)null,
            TextDeliveryRefusalReason.TargetUnavailable or
                TextDeliveryRefusalReason.TargetChanged => AppErrorCode.DeliveryTargetChanged,
            TextDeliveryRefusalReason.ProtectedField => AppErrorCode.DeliveryProtectedField,
            TextDeliveryRefusalReason.ElevatedTarget => AppErrorCode.DeliveryElevatedTarget,
            TextDeliveryRefusalReason.ClipboardUnavailable => AppErrorCode.DeliveryClipboardUnavailable,
            TextDeliveryRefusalReason.InputStateUnsafe or
                TextDeliveryRefusalReason.InputBlocked => AppErrorCode.DeliveryInputBlocked,
            _ => AppErrorCode.DeliveryUnsupportedTarget,
        };
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            eventCode,
            result.Delivered ? AppFailureCategory.None : AppFailureCategory.TextDelivery,
            elapsedMilliseconds,
            ErrorCode: errorCode));
    }

    private static string DeliveryStatus(DeliveryResult result) => result switch
    {
        { Delivered: true, Route: TextDeliveryRoute.UiAutomationValue } =>
            "Inserted safely in the app you started in",
        { Delivered: true, ClipboardRestored: true } =>
            "Pasted safely and restored your clipboard",
        { Delivered: true } => "Pasted safely",
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.ProtectedField } =>
            "Protected field — copied only; paste manually if intended",
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.ElevatedTarget } =>
            "Windows blocked the elevated app — copied only",
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.TargetChanged } =>
            "The target changed — copied only to protect your text",
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.UnsafeMultilineTarget } =>
            "Terminal line break refused — copied only",
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.UnsupportedTarget } =>
            "Automatic paste is unsafe here — copied only",
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.InputStateUnsafe } =>
            "A key was held — copied only; paste manually",
        { ClipboardFallback: true } => "Copied — press Ctrl+V",
        { RefusalReason: TextDeliveryRefusalReason.ClipboardUnavailable } =>
            "Clipboard unavailable — text is held safely in memory",
        { RefusalReason: TextDeliveryRefusalReason.DirectWriteUnverified } =>
            "Insertion could not be verified — text is held safely in memory",
        _ => "Text delivery stopped safely",
    };

    private static string? DeliveryLanguage(Transcript transcript) =>
        transcript.EngineId.StartsWith(
            ParakeetTranscriptionEngine.ModelId,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : transcript.DetectedLanguage;

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

    private static string OllamaHealthStatus(OllamaHealth health) => health switch
    {
        OllamaHealth.EndpointInvalid => "Ollama endpoint must point to this PC",
        OllamaHealth.ServerUnavailable => "Ollama is offline — cleaned text will still be preserved",
        OllamaHealth.ServerUnhealthy => "Ollama did not return a usable health response",
        OllamaHealth.NoLocalModels => "Ollama is running, but no local model is installed",
        _ => "Ollama is ready",
    };

    private static string PolishFallbackStatus(PolishResult result) => result.Error?.Code switch
    {
        AppErrorCode.PolishEndpointInvalid =>
            "Cleaned locally; Ollama endpoint must point to this PC",
        AppErrorCode.PolishRemoteModelDisallowed =>
            "Cleaned locally; hosted Ollama models are disabled",
        AppErrorCode.PolishModelUnavailable =>
            "Cleaned locally; the selected Ollama model is not installed",
        AppErrorCode.PolishTimedOut =>
            "Cleaned locally; Ollama timed out",
        AppErrorCode.PolishProviderUnavailable =>
            "Cleaned locally; Ollama is offline",
        AppErrorCode.PolishOutputTruncated =>
            "Cleaned locally; Ollama returned incomplete text",
        _ => "Cleaned locally; AI polish failed safely",
    };

    private static AppFailureCategory FailureFor(AppError? error) => error?.Code switch
    {
        AppErrorCode.HotkeyConflict => AppFailureCategory.HotkeyConflict,
        AppErrorCode.HotkeyInvalid or AppErrorCode.HotkeyUnavailable =>
            AppFailureCategory.HotkeyUnavailable,
        AppErrorCode.TargetUnavailable => AppFailureCategory.TargetUnavailable,
        AppErrorCode.AccessDenied when error?.Stage == AppErrorStage.AudioCapture =>
            AppFailureCategory.AudioUnavailable,
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

    private static DiagnosticProvider? DiagnosticProviderFor(string providerId) =>
        providerId.Trim().ToLowerInvariant() switch
        {
            "eg-1" => DiagnosticProvider.EgOne,
            "ollama" => DiagnosticProvider.Ollama,
            "openai" => DiagnosticProvider.OpenAi,
            "anthropic" => DiagnosticProvider.Anthropic,
            "gemini" => DiagnosticProvider.Gemini,
            _ => null,
        };

    private static DiagnosticHardwareClass DiagnosticHardwareClassFor(HardwareSnapshot hardware)
    {
        if (hardware.Cuda.IsDriverAvailable && hardware.Cuda.DeviceCount > 0)
        {
            return DiagnosticHardwareClass.NvidiaCuda;
        }

        if (hardware.GraphicsAdapters.Any(adapter => adapter.IsActive))
        {
            return DiagnosticHardwareClass.GpuPresent;
        }

        return hardware.Status == HardwareProbeStatus.Complete
            ? DiagnosticHardwareClass.CpuOnly
            : DiagnosticHardwareClass.Unknown;
    }

    private static ReleaseIdentity ResolveReleaseIdentity()
    {
        var configured = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(
                attribute.Key,
                "EnviousWisprReleaseChannel",
                StringComparison.Ordinal))
            ?.Value;
        if (!ReleaseIdentity.TryParse(configured, out var identity))
        {
            throw new InvalidOperationException("The embedded release channel is invalid.");
        }

        return identity;
    }

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

internal sealed class AccessDeniedAudioCapture : IAudioCapture
{
    public event EventHandler<AudioLevel>? LevelChanged
    {
        add { }
        remove { }
    }

    public bool IsCapturing => false;

    public Task<AudioOperationResult> StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AudioOperationResult(
            Succeeded: false,
            new AppError(
                AppErrorCode.AccessDenied,
                AppErrorStage.AudioCapture,
                CanRetry: true)));
    }

    public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CapturedAudio(
            new DictationSessionId(Guid.Empty),
            ReadOnlyMemory<float>.Empty,
            SampleRate: 16_000,
            Channels: 1,
            Outcome: AudioCaptureOutcome.Interrupted,
            Error: new AppError(
                AppErrorCode.InvalidTransition,
                AppErrorStage.AudioCapture,
                CanRetry: false)));
    }

    public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AudioOperationResult(Succeeded: true));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
