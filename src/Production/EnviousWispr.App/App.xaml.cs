using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
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
    private bool _keybindCaptureActive;
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
    private CancellationTokenSource? _autoStopCancellation;
    private Task? _autoStopLoop;
    private CancellationTokenSource? _streamingCancellation;
    private Task? _streamingLoop;
    private readonly StreamingTranscriptAccumulator _streamed = new();
    private int _streamedThroughSample;

    private bool _streamingUsable;
    private long _previewSequence;
    private MainWindow? _window;
    private WindowsTrayIcon? _trayIcon;
    private IReadOnlyList<CustomWordEntry> _customWords = [];
    // VOLATILE BECAUSE THE HOTKEY THREAD READS IT AND THE UI THREAD REPLACES IT. The record itself
    // is immutable and cannot tear, but the REFERENCE can be read stale, and one of its readers is
    // the delivery-options closure that decides where a recording's words are about to go.
    private volatile AppSettings _settings = AppSettings.Default;
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
        _window.KeybindCaptureActiveChanged += OnKeybindCaptureActiveChanged;
        _window.SpeedCheckRequested += OnSpeedCheckRequested;
        _window.MishearingSuggestionsRequested += OnMishearingSuggestionsRequested;
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
        _window.SetSessionStatus(DictationStatus.Quiet("Preparing local transcription..."));
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
            window.KeybindCaptureActiveChanged -= OnKeybindCaptureActiveChanged;
            window.SpeedCheckRequested -= OnSpeedCheckRequested;
            window.MishearingSuggestionsRequested -= OnMishearingSuggestionsRequested;
            window.AppWindow.Closing -= OnAppWindowClosing;
            window.Closed -= OnWindowClosed;
        }

        // The window can close with a keybind field still focused, and unsubscribing above stops
        // the field's own LostFocus ever arriving. Clearing it here is what stops the hook being
        // left standing down with nothing on screen to explain why dictation no longer responds.
        OnKeybindCaptureActiveChanged(active: false);

        await PrepareForExitAsync().ConfigureAwait(true);
        _window = null;
    }

    /// <summary>How many times the speed check runs the pipeline.</summary>
    /// <remarks>
    /// Chosen so the 95th percentile means something. Below twenty samples it IS the maximum, and a
    /// tail figure that is secretly the worst single run reads as evidence while being an artefact
    /// of the sample size.
    /// </remarks>
    private const int SpeedCheckRuns = 50;

    /// <summary>
    /// A repeatable measurement of the text cleanup, with no microphone and no network.
    /// </summary>
    /// <remarks>
    /// WHAT IT MEASURES AND WHAT IT DOES NOT, said in the UI as well as here. It times the
    /// deterministic pipeline - the cleanup every dictation runs after transcription - which is the
    /// part of the wait that is entirely ours and entirely repeatable. It does NOT include
    /// recognition or AI polish: both need resources a speed check should not quietly consume, and
    /// a number silently including a cloud round trip would be measuring somebody's broadband.
    ///
    /// IT REFUSES WHILE A DICTATION IS RUNNING, and it uses the LIVE pipeline rather than a second
    /// one. Those two facts belong together: measuring a different object from the one every
    /// dictation uses is the one thing a speed check must not do, and sharing the live object means
    /// the two must not run at once.
    ///
    /// THE FIRST RUN IS DISCARDED and that is stated rather than silent. It pays for every lazy
    /// initialisation in the pipeline, which no dictation after the first one pays - but a
    /// benchmark that quietly drops its worst sample is precisely how a speed claim becomes untrue,
    /// so the reason sits beside the line that does it.
    /// </remarks>
    /// <summary>
    /// Asks the user's chosen polish model what a word is likely to be misheard as.
    /// </summary>
    /// <remarks>
    /// THE PROVIDER IS WHATEVER POLISH IS SET TO, AND THAT IS THE POINT. The user has already chosen
    /// a model and, for a cloud one, already provided a key. Asking them to configure a second thing
    /// for a convenience button would mean almost nobody ever sees it work.
    ///
    /// PRIVACY: THE WORD GOES WHERE THEIR POLISHED TEXT ALREADY GOES. It travels to the provider
    /// they chose, under their own key, with no Envious Labs endpoint in the path - the same
    /// boundary cloud polish already sits on. Nothing about this reaches us.
    ///
    /// A PROVIDER THAT CANNOT BE ASKED SAYS SO RATHER THAN FAILING QUIETLY. Every provider that
    /// ships today can answer, and a test enumerates them from the type system so a fourth cannot
    /// arrive without one. This branch is what the user would see if one ever did.
    /// </remarks>
    private async void OnMishearingSuggestionsRequested(string term, IReadOnlyList<string> existing)
    {
        if (_polishProvider is not IMishearingAdvisor advisor)
        {
            _window?.SetAliasSuggestions(
                term,
                MishearingAdvice.None(MishearingAdviceStatus.NotSupported));
            return;
        }

        MishearingAdvice advice;
        try
        {
            advice = await advisor.SuggestAsync(term, existing).ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is not (StackOverflowException or OutOfMemoryException))
        {
            advice = MishearingAdvice.None(MishearingAdviceStatus.Failed);
        }

        _window?.SetAliasSuggestions(term, advice);
    }

    private async void OnSpeedCheckRequested()
    {
        var pipeline = _deterministicTextPipeline;
        if (_sessionController?.CurrentSession is not null)
        {
            _window?.SetSpeedCheckResult(null);
            return;
        }

        var summary = await Task.Run(() => MeasureDeterministicPipeline(pipeline)).ConfigureAwait(true);
        _window?.SetSpeedCheckResult(summary);
    }

    private LatencySummary MeasureDeterministicPipeline(DeterministicTextPipeline pipeline)
    {
        // A realistic dictation rather than a word: the cleanup's cost scales with what it is given,
        // so timing "hello" would produce a number no real dictation resembles.
        const string spoken =
            "so i think we should ship the windows build this week comma and see what people say "
            + "about it period i counted 14 things left on the list and 3 of them need review";

        var request = new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), spoken, "speed-check", []),
            _customWords,
            _deterministicTextOptions);

        var timings = new List<double>(SpeedCheckRuns);
        for (var run = 0; run <= SpeedCheckRuns; run++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _ = pipeline.ProcessAsync(request).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                return LatencySummary.From([]);
            }

            timer.Stop();

            // Run zero pays for every lazy initialisation in the pipeline, and no dictation after
            // the first one pays it.
            if (run > 0)
            {
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }
        }

        return LatencySummary.From(timings);
    }

    private void OnKeybindCaptureActiveChanged(bool active)
    {
        _keybindCaptureActive = active;
        _pushToTalkHook?.SetCapturingKeybind(active);
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
        // WINDOWS LOCKING MID-DICTATION IS A FACT ABOUT THAT DICTATION. Written before the recovery
        // flow opens its own scope, so it opens one here too; otherwise the event that EXPLAINS the
        // recovery is the one line of it joined to nothing.
        using (_sessionController?.CurrentSession is { } interrupted
            ? DictationScope.Begin(interrupted.Id.Value)
            : NoScope.Instance)
        {
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, eventCode));
        }

        if (transition is SystemLifecycleTransition.Suspending or
            SystemLifecycleTransition.SessionLocked)
        {
            _ = RecoverFromSystemTransitionAsync(transition);
        }
        else
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus(DictationStatus.Quiet("Windows resumed. EnviousWispr is ready")));
        }
    }

    private void OnAudioDevicesChanged(AudioDeviceChange change)
    {
        // A MICROPHONE VANISHING MID-DICTATION IS A FACT ABOUT THAT DICTATION, and this arrives on
        // its own Windows device callback, so it inherits nothing. It is also the single most
        // useful line in the log when somebody asks why a recording went wrong, which is exactly
        // the line worth not leaving joined to nothing.
        using var dictation = _sessionController?.CurrentSession is { } recording
            ? DictationScope.Begin(recording.Id.Value)
            : NoScope.Instance;
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
        DictationStatus? status = requested?.Trim().ToLowerInvariant() switch
        {
            "recording" =>
                DictationStatus.Recording("Recording. Release to finish, Escape to cancel"),
            "processing" => DictationStatus.Processing("Transcribing locally..."),
            "success" => DictationStatus.Success("Inserted safely in the app you started in"),
            "warning" =>
                DictationStatus.Warning("Protected field: copied only. Paste manually if intended"),
            // THE TWO NEW SEVERITIES ARE DRIVABLE FROM HERE OR THEY ARE NOT TESTABLE AT ALL. An
            // advisory needs a provider to be misconfigured and a distress needs Windows to
            // interrupt a dictation, neither of which a person can arrange on demand. The advisory
            // carries its button, because the thing most worth looking at on a real screen is
            // whether a button on a window shown without activation can actually be pressed.
            "advisory" => DictationStatus.Advisory(
                "Ollama is offline. Cleaned text will still be preserved", OpenPolish),
            "distress" => DictationStatus.Distress(
                "Windows interrupted the active dictation; recovery is still pending"),
            "error" => DictationStatus.Error("Local transcription failed safely"),
            _ => null,
        };
        if (status.HasValue)
        {
            // Read through the declared local rather than a pattern-bound name. The gate that
            // checks every status names its pill can see what `status` was declared as; it cannot
            // see what a name introduced by a pattern is, and a gate that cannot tell has to
            // refuse. Saying it plainly here is cheaper than widening what the gate accepts.
            _window?.SetSessionStatus(status.Value);
        }
    }

    private void OnSessionStatusChanged(DictationStatus status)
    {
        try
        {
            _trayIcon?.SetStatus(status.Text);
            // The tray and the pill are driven from the same status, so the two surfaces cannot
            // disagree about whether a recording is live.
            _trayIcon?.SetState(TrayIconStates.For(status.State));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon = new WindowsTrayIcon(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Brand",
            "EnviousWispr.ico"));
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

            // A RESULT THAT ARRIVED WHILE THE WINDOW WAS HIDDEN IS STILL NEWS WHEN IT COMES BACK.
            // History finishing while the app sits in the notification area is the ordinary case,
            // and without this the announcement is simply never made.
            //
            // AFTER THE NAVIGATION, NOT BEFORE. Opening Settings from the tray runs through here too,
            // and announcing first spoke the history result to somebody who had asked for Settings.
            // Once the page has been switched away from, the ancestor check refuses it and the
            // pending result is kept for whenever History is actually opened.
            _window.AnnouncePendingHistoryState();
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
        // ONE SCOPE FOR THE WHOLE TEARDOWN, rather than one per line found. Quitting mid-recording
        // is a fact about that recording, and everything written on the way out - the shell closing,
        // the preview stopping, whatever a future teardown step logs - belongs to it. Five review
        // rounds each named one more unscoped line on this path; scoping the path is the answer that
        // does not need a sixth.
        using var dictation = _sessionController?.CurrentSession is { } recording
            ? DictationScope.Begin(recording.Id.Value)
            : NoScope.Instance;
        // THE SETTINGS WRITE FINISHES BEFORE THE WINDOW GOES. Teardown is synchronous and cannot
        // wait, so abandoning a save in flight let the process end mid-write - which is how a choice
        // somebody just made disappears. This is the one place on the exit path that can await it.
        if (_window is not null)
        {
            await _window.DrainSettingsAsync().ConfigureAwait(true);
        }

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

        cleanShutdown &= await TryCleanupAsync(StopStreamingTranscriptionAsync).ConfigureAwait(true);
        cleanShutdown &= await TryCleanupAsync(StopAutoStopWatchAsync).ConfigureAwait(true);
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
            deliveryOptions: () => TextDeliveryOptions.Default with
            {
                CopyInsteadOfPaste = _settings.Preferences.CopyInsteadOfPaste,
            },
            preferredAudioDevice: string.IsNullOrWhiteSpace(_settings.PreferredMicrophoneId)
                ? null
                : new EnviousWispr.Core.Audio.AudioDeviceId(_settings.PreferredMicrophoneId));
        _pushToTalkHook.Signalled += OnPushToTalkSignalled;
        // A saved keybind builds a NEW hook, which starts armed and knows nothing about a capture
        // field that is still focused. Carrying the state across is what stops the hook re-arming
        // underneath a field the user is still standing in.
        _pushToTalkHook.SetCapturingKeybind(_keybindCaptureActive);
        _window?.SetHotkeyReady(
            _pushToTalkHook.Gesture.ToString(),
            _pushToTalkHook.RecordingMode,
            _pushToTalkHook.CancelGesture.ToString(),
            _pushToTalkHook.QuickAddGesture.ToString());
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.HotkeyReady));
    }

    private void OnAudioLevelChanged(object? sender, AudioLevel level)
    {
        // STRAIGHT THROUGH, ON THE CAPTURE'S OWN THREAD. This arrives once per audio buffer, roughly
        // two hundred times a second, and SetAudioLevel only records a number - so posting each one
        // to the UI thread did that scheduling work for a value the meter's own timer would ask for
        // when it was ready. Every UI touch stays inside that tick.
        _window?.SetAudioLevel(level);
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
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.PolishRuntimeStarted,
            Provider: DiagnosticProviderIds.FromProviderId(provider.ProviderId)));
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
            DiagnosticProviderIds.FromProviderId(provider.ProviderId),
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
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.PolishRuntimeStarted,
            Provider: DiagnosticProviderIds.FromProviderId(provider.ProviderId)));
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
            timer.ElapsedMilliseconds,
            DiagnosticProviderIds.FromProviderId(provider.ProviderId)));
    }

    private async Task ConfigureTranscriptionAsync(FinalAsrEngine configuredEngine)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_DISABLE_LOCAL_RUNTIME"),
                "1",
                StringComparison.Ordinal))
        {
            _window?.SetSessionStatus(DictationStatus.Quiet("Local transcription disabled for performance UAT"));
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
            _window?.SetSessionStatus(
                DictationStatus.Advisory(
                    "Local transcription model is not installed", OpenTranscription));
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
            _window?.SetSessionStatus(
                DictationStatus.Advisory(
                    "Local transcription is unavailable on this machine", OpenTranscription));
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
                _window?.SetSessionStatus(
                    DictationStatus.Advisory(
                    "Local transcription could not start", OpenTranscription));
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.DictationTranscriptionFailed,
                    AppFailureCategory.RuntimeWorker));
                return;
            }

            ConfigureLivePreview(workerExecutable, hardware, whisperLanguage, forceCpu: true);
            _window?.SetSessionStatus(DictationStatus.Quiet("Local transcription ready on the processor"));
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionDegraded,
                AppFailureCategory.RuntimeProvider,
                ErrorCode: AppErrorCode.RuntimeProviderUnavailable));
            return;
        }

        ConfigureLivePreview(workerExecutable, hardware, whisperLanguage);
        _window?.SetSessionStatus(DictationStatus.Quiet("Local transcription ready"));
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
        var published = context.Status == TargetContextStatus.Available
            ? context.Context?.Selection.Trim()
            : null;

        // EVERY OUTCOME BELOW SAYS SOMETHING, INCLUDING THE REFUSALS. A refusal that is silent is
        // indistinguishable from nothing having happened at all - to the user, who is left looking
        // at an empty box, and to anyone testing it, who would be reporting the absence of an
        // effect as evidence of a decision.
        var acquisition = SelectionAcquisitionPolicy.Decide(
            hasValidTarget: true,
            published,
            isDictationRunning: _sessionController?.CurrentSession is not null,
            isDeliveryInFlight: _activeProcessingCancellation is not null);

        string? selection;
        string? message;
        switch (acquisition)
        {
            case SelectionAcquisition.UsePublished:
                selection = published;
                message = null;
                break;

            case SelectionAcquisition.SyntheticCopy:
                // Static because it holds no state - it borrows the clipboard and gives it back.
                selection = await WindowsTextTargetAdapter
                    .TryReadSelectionWithCopyAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                selection = selection?.Trim();
                message = string.IsNullOrWhiteSpace(selection)
                    ? "Nothing was selected in that app. Select a misheard word, then try the shortcut again."
                    : null;
                break;

            default:
                selection = null;
                message = "EnviousWispr was busy with a dictation, so it left your clipboard alone. Try the shortcut again in a moment.";
                break;
        }

        // THREE OUTCOMES, THREE EVENTS. The first version had two, so "the copy found nothing" and
        // "the user got their word" logged identically - and the log is where a support case looks
        // when nobody can reproduce the screen. The messages had been split from the start; the log
        // had not, which is the half that goes unnoticed until it is needed.
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            acquisition == SelectionAcquisition.Refuse
                ? AppEventCode.QuickAddRefused
                : string.IsNullOrWhiteSpace(selection)
                    ? AppEventCode.QuickAddSelectionEmpty
                    : AppEventCode.QuickAddPrepared));
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

        // Carried rather than inherited, because the catch blocks below run after the try's scope
        // has been disposed and are exactly the lines somebody reads first when a dictation went
        // wrong.
        //
        // SEEDED FROM THE CONTROLLER RATHER THAN LEFT NULL, because a release or a cancel can throw
        // BEFORE it returns a transition, and the recording those lines are about already exists.
        // Left null, the two catches lost the dictation on exactly the paths that create them.
        var interrupted = controller.CurrentSession?.Id.Value;
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
                        _window?.SetSessionStatus(DictationStatus.Quiet("Review recovered text before recording again"));
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
                        _window?.SetSessionStatus(DictationStatus.Distress(
                            "Recording paused because Windows memory is critically low"));
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

            // FROM HERE THE DICTATION IS KNOWN, AND EVERYTHING BELOW BELONGS TO IT. Above this line
            // the session either does not exist yet (a press) or is being read off the controller,
            // and lines written there honestly have no dictation. `Begin` restores rather than
            // clears, so this nesting inside another scope is safe.
            interrupted = result.Session?.Id.Value;
            using var dictation = interrupted is { } known
                ? DictationScope.Begin(known)
                : NoScope.Instance;
            WriteSessionEvent(result);
            if (result.Kind == SessionTransitionKind.Started && result.Session is not null)
            {
                _escapeRecoveryForSession = _settings.Preferences.Dictation.EscapeRecoveryEnabled;
                StartRecordingWatchdog(controller, result.Session.Id);
                await StartLivePreviewAsync(result.Session.Id).ConfigureAwait(false);
                StartAutoStopWatch(result.Session.Id);
                StartStreamingTranscription(result.Session.Id);
            }
            else if (result.Kind == SessionTransitionKind.FinalizeReady &&
                result.Session is not null &&
                result.Audio is not null)
            {
                processingCancellation = new CancellationTokenSource(
                    MaximumFinalProcessingDuration);
                _activeProcessingCancellation = processingCancellation;
                await StopStreamingTranscriptionAsync().ConfigureAwait(false);
                await StopAutoStopWatchAsync().ConfigureAwait(false);
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
                await StopStreamingTranscriptionAsync().ConfigureAwait(false);
                await StopAutoStopWatchAsync().ConfigureAwait(false);
                await StopLivePreviewAsync().ConfigureAwait(false);
                await controller.ResetAsync().ConfigureAwait(false);
            }

            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus(SessionStatus(result)));
        }
        catch (OperationCanceledException)
        {
            // THE CATCH RUNS AFTER THE TRY'S SCOPE HAS BEEN DISPOSED, so the id is carried in a
            // variable rather than inherited. Failure and recovery are the lines somebody reads
            // FIRST when a dictation went wrong, and they were the ones losing their dictation.
            using var failed = interrupted is { } timedOut
                ? DictationScope.Begin(timedOut)
                : NoScope.Instance;
            await RecoverFailedSessionAsync(
                controller,
                new AppError(
                    AppErrorCode.SessionTimedOut,
                    AppErrorStage.Session,
                    CanRetry: true),
                DictationStatus.Quiet("The dictation timed out and was recovered safely")).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            using var failed = interrupted is { } broken
                ? DictationScope.Begin(broken)
                : NoScope.Instance;
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
                DictationStatus.Error("Session failed and was reset safely")).ConfigureAwait(false);
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
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var dictation = DictationScope.Begin(sessionId.Value);
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
                    await StopStreamingTranscriptionAsync().ConfigureAwait(false);
                await StopAutoStopWatchAsync().ConfigureAwait(false);
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
                        _window?.SetSessionStatus(
                            DictationStatus.Warning("Recording timed out and was cancelled safely")));
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
        DictationStatus status)
    {
        await StopRecordingWatchdogAsync().ConfigureAwait(false);
        await StopStreamingTranscriptionAsync().ConfigureAwait(false);
                await StopAutoStopWatchAsync().ConfigureAwait(false);
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
        // WINDOWS LOCKING OR SUSPENDING ARRIVES ON ITS OWN CALLBACK, so this flow inherits nothing
        // and had no dictation at all - the capture transition, the preview stop, the failure and
        // the recovery lines all landed joined to nothing, on the path where a user most wants to
        // know what happened to their words.
        using var dictation = _sessionController?.CurrentSession is { } interrupted
            ? DictationScope.Begin(interrupted.Id.Value)
            : NoScope.Instance;
        _activeProcessingCancellation?.Cancel();
        if (!await _sessionOperationGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
                _window?.SetSessionStatus(DictationStatus.Distress(
                    "Windows interrupted the active dictation; recovery is still pending")));
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
                    await StopStreamingTranscriptionAsync().ConfigureAwait(false);
                await StopAutoStopWatchAsync().ConfigureAwait(false);
                await StopLivePreviewAsync().ConfigureAwait(false);
                    _window?.DispatcherQueue.TryEnqueue(() =>
                        _window?.SetSessionStatus(DictationStatus.Quiet(
                            transition == SystemLifecycleTransition.Suspending
                                ? "Windows is suspending. Captured audio is being preserved"
                                : "Windows locked. Captured audio is being preserved")));
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
                DictationStatus.Quiet(
                    "Windows interrupted the session; it was reset safely")).ConfigureAwait(false);
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
                    DictationStatus.Quiet("Windows interrupted the session; recovery timed out safely")).ConfigureAwait(false);
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
                    DictationStatus.Quiet(
                    "Windows interrupted the session; it was reset safely")).ConfigureAwait(false);
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

    /// <summary>How often the watcher asks whether the speaker has finished.</summary>
    /// <remarks>
    /// Far more often than the threshold it is testing, so the recording ends close to when the
    /// user expects rather than up to a poll late. Cheap: it reads a buffer already being written.
    /// </remarks>
    private static readonly TimeSpan AutoStopPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Watches a running recording and ends it when the speaker has stopped, if the user asked.
    /// </summary>
    /// <remarks>
    /// IT ENDS THE RECORDING THROUGH THE SAME DOOR A KEY RELEASE USES. Calling
    /// HandlePushToTalkAsync with a Released signal means the session state machine, the hook's
    /// own recording flag, transcription, delivery and history all run exactly as they would have.
    /// A parallel finish path here would be a second implementation of ending a dictation, and the
    /// two would drift.
    ///
    /// HAS-HEARD-SPEECH IS STICKY AND LIVES HERE, not in the snapshot. The buffer only holds a
    /// window, so a speaker who says one word and then pauses past that window would look to a
    /// single snapshot like someone who never spoke - and the policy would stop protecting them at
    /// the exact moment it should fire. Once speech is heard in this recording, it stays heard.
    ///
    /// THE SNAPSHOT WINDOW IS LONGER THAN ANY THRESHOLD IT COULD BE ASKED ABOUT. A window shorter
    /// than the threshold can never contain enough silence to satisfy it, so the feature would
    /// simply never fire - silently, and looking exactly like a user who had not turned it on.
    /// </remarks>
    /// <summary>How often the streaming loop looks for a stretch it can commit.</summary>
    private static readonly TimeSpan StreamingPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Transcribes finished parts of a recording while the user is still speaking.
    /// </summary>
    /// <remarks>
    /// ITS WORST CASE IS TODAY'S BEHAVIOUR, and that is the design rather than a safety net bolted
    /// on. The full audio is kept regardless; the streamed text is only USED if every commit
    /// succeeded. Any failure - a dead worker, a cancelled request, an exception - clears
    /// <see cref="_streamingUsable"/> and the release transcribes the whole take exactly as it does
    /// now. Dictation working is the first rule this product has, and a speed feature must not be
    /// able to break it.
    ///
    /// SO THERE IS NO SETTING. A change that cannot make things worse does not need one, and every
    /// switch added is a thing a user has to understand before they benefit.
    ///
    /// IT DOES NOT RUN WITH LIVE PREVIEW ON. Both transcribe during the recording and both use the
    /// same worker, so together they would queue behind each other and make the release SLOWER than
    /// doing nothing. Live Preview is the user's explicit choice and is display-only; this is
    /// invisible and makes the real text faster. Turning off the thing they chose would be wrong,
    /// so the invisible one stands down.
    /// </remarks>
    private void StartStreamingTranscription(DictationSessionId sessionId)
    {
        _streamed.Clear();
        _streamedThroughSample = 0;
        _streamingUsable = false;

        if (_settings.Preferences.LivePreviewEnabled ||
            _transcriptionEngine is not { } engine ||
            _audioCapture is not IAudioSnapshotSource snapshots)
        {
            return;
        }

        _streamingUsable = true;
        var cancellation = new CancellationTokenSource();
        _streamingCancellation = cancellation;
        _streamingLoop = RunStreamingTranscriptionAsync(
            snapshots,
            engine,
            sessionId,
            cancellation.Token);
    }

    private async Task RunStreamingTranscriptionAsync(
        IAudioSnapshotSource snapshots,
        RuntimeWorkerTranscriptionEngine engine,
        DictationSessionId sessionId,
        CancellationToken cancellationToken)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var dictation = DictationScope.Begin(sessionId.Value);
        var segmenter = new SpeechSegmenter(
            AudioSampleConverter.TargetSampleRate,
            TimeSpan.FromMilliseconds(400));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(StreamingPollInterval, cancellationToken).ConfigureAwait(false);

                // The WHOLE recording so far, not a window: a commit is a range measured from the
                // start, and a rolling window would make those indices mean something different on
                // every poll.
                var snapshot = snapshots.GetSnapshot(TimeSpan.MaxValue);
                if (snapshot is null || snapshot.SessionId != sessionId)
                {
                    continue;
                }

                var commit = StreamingCommitPlanner.NextCommit(
                    snapshot.Samples.Span,
                    snapshot.SampleRate,
                    _streamedThroughSample,
                    segmenter);
                if (commit is not { } range)
                {
                    continue;
                }

                var slice = snapshot.Samples.Slice(
                    range.StartSample,
                    range.EndSample - range.StartSample);
                var transcript = await engine.TranscribeAsync(
                    new CapturedAudio(sessionId, slice, snapshot.SampleRate, snapshot.Channels),
                    cancellationToken).ConfigureAwait(false);

                _streamed.Append(transcript.Text);
                _streamedThroughSample = range.EndSample;
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.StreamingSegmentCommitted));
            }
        }
        catch (OperationCanceledException)
        {
            // The recording ended, which is the ordinary case. What has been committed so far
            // stays usable - the ranges already transcribed are still correct.
        }
        catch (Exception exception) when (
            exception is not (StackOverflowException or OutOfMemoryException))
        {
            // ANY failure gives up on the head start entirely rather than delivering a partial
            // transcript. Half a dictation is worse than a slow one.
            _streamingUsable = false;
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.StreamingAbandoned,
                AppFailureCategory.AsrUnavailable));
        }
    }

    private async Task StopStreamingTranscriptionAsync()
    {
        var cancellation = _streamingCancellation;
        var loop = _streamingLoop;
        _streamingCancellation = null;
        _streamingLoop = null;
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation.Dispose();
    }

    private void StartAutoStopWatch(DictationSessionId sessionId)
    {
        var dictation = _settings.Preferences.Dictation;
        if (!dictation.AutoStopEnabled ||
            dictation.RecordingMode != DictationRecordingMode.Toggle ||
            _audioCapture is not IAudioSnapshotSource snapshots)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _autoStopCancellation = cancellation;
        _autoStopLoop = RunAutoStopWatchAsync(snapshots, sessionId, dictation, cancellation.Token);
    }

    private async Task RunAutoStopWatchAsync(
        IAudioSnapshotSource snapshots,
        DictationSessionId sessionId,
        DictationPreferences dictation,
        CancellationToken cancellationToken)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var scope = DictationScope.Begin(sessionId.Value);
        var required = TimeSpan.FromSeconds(dictation.AutoStopSilenceSeconds);
        if (required < AutoStopPolicy.MinimumSilence)
        {
            required = AutoStopPolicy.MinimumSilence;
        }

        // Comfortably more than the threshold, so the window can always hold enough silence to
        // answer the question being asked of it.
        var window = required + required;
        var heardSpeech = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AutoStopPollInterval, cancellationToken).ConfigureAwait(false);

                var snapshot = snapshots.GetSnapshot(window);
                if (snapshot is null || snapshot.SessionId != sessionId)
                {
                    continue;
                }

                var segmenter = new SpeechSegmenter(snapshot.SampleRate, TimeSpan.FromMilliseconds(400));
                var samples = snapshot.Samples.Span;
                heardSpeech |= segmenter.Segment(samples).Any(segment => segment.IsSpeech);

                var decision = AutoStopPolicy.Decide(
                    dictation.AutoStopEnabled,
                    dictation.RecordingMode == DictationRecordingMode.Toggle,
                    heardSpeech,
                    segmenter.TrailingSilence(samples),
                    required);
                if (decision != AutoStopDecision.Stop)
                {
                    continue;
                }

                _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.AutoStopTriggered));
                // Fire and return. Awaiting here would hold this loop open across the whole
                // transcription, and the loop is cancelled as part of ending the recording.
                _ = HandlePushToTalkAsync(PushToTalkSignal.Released);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The recording ended some other way, which is the ordinary case.
        }
    }

    private async Task StopAutoStopWatchAsync()
    {
        var cancellation = _autoStopCancellation;
        var loop = _autoStopLoop;
        _autoStopCancellation = null;
        _autoStopLoop = null;
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation.Dispose();
    }

    private async Task StartLivePreviewAsync(DictationSessionId sessionId)
    {
        // Every flow that serves a dictation opens the scope for itself. Inheriting one would in
        // fact work here - a child async flow keeps the AsyncLocal value it captured even after the
        // caller disposes its own scope - and that is exactly why this does not rely on it: the
        // join would then be a property of who happened to call whom, invisible at this method and
        // unprovable by anything. Opening it here makes it a property of this flow, which a gate
        // can check. One line per flow, and the flows are the methods that take a session id.
        using var scope = DictationScope.Begin(sessionId.Value);
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
        // STOPPING IS REACHED FROM MORE PLACES THAN STARTING, and one of them is quitting the app
        // from the tray mid-recording - a shutdown path that inherits nothing, where the line saying
        // the preview stopped was the last thing written about that dictation and was joined to
        // nothing. Read off the controller rather than taken as a parameter, because the callers
        // that lose the join are exactly the ones with no id to pass.
        using var dictation = _sessionController?.CurrentSession is { } recording
            ? DictationScope.Begin(recording.Id.Value)
            : NoScope.Instance;
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
        // EVERY LINE WRITTEN FROM HERE ON SAYS WHICH DICTATION IT BELONGED TO. The scope is ambient,
        // so helpers that have never heard of it - polish, delivery, the recovery write - are joined
        // without being handed anything, and one added next month is joined on arrival.
        using var dictation = DictationScope.Begin(sessionId.Value);
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
                _window?.SetSessionStatus(DictationStatus.Advisory(
                    "Audio captured, but local transcription is unavailable", OpenTranscription)));
            return;
        }

        _window?.DispatcherQueue.TryEnqueue(() =>
            _window?.SetSessionStatus(DictationStatus.Processing("Transcribing locally...")));
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationTranscriptionStarted));
        // The whole wait, not the sum of the stages. A sum reports zero for every await and
        // dispatcher hop BETWEEN them, which is exactly where an unexplained delay would hide.
        // In a finally, so a path that throws still reports what the user waited before it did.
        var waitTimer = System.Diagnostics.Stopwatch.StartNew();
        ArchiveDictationAudio(audio);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var transcript = await TranscribeUsingAnyHeadStartAsync(engine, audio, cancellationToken)
                .ConfigureAwait(false);
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
            // A model that comes off the rails returns a CONFIDENT string rather than an error, so
            // polishResult.UsedFallback is false and every check above says the call succeeded.
            // This is the only place that asks whether what came back is worth showing anyone.
            //
            // Polish is a limb and the transcript is the heart: a refusal leaves the user with the
            // cleaned text they already had, which is the same outcome as any other limb failure.
            var polishReview = polishResult is null || polishResult.UsedFallback
                ? new PolishOutputReview(PolishOutputVerdict.Accepted, string.Empty)
                : PolishOutputGuard.Review(processed.Output.Text, polishResult.Output.Text);
            var polishVerdict = polishReview.Verdict;
            if (polishVerdict != PolishOutputVerdict.Accepted)
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.PolishOutputRefused,
                    // InvalidData rather than LocalPolish or CloudPolish: the refusal is about
                    // what came BACK, and either provider can produce it. Attributing it to one
                    // would make the log claim a cause it does not know.
                    AppFailureCategory.InvalidData));
            }

            if (polishResult is not null && !polishResult.UsedFallback &&
                polishVerdict == PolishOutputVerdict.Accepted)
            {
                // THE REVIEWED TEXT, NOT WHAT THE PROVIDER SENT. The guard strips what the model
                // wrote ABOUT the text - "Sure, here is the cleaned transcript:" - and using the
                // raw string here would put that chatter in somebody's document with their words.
                processed = await _deterministicTextPipeline.ApplyPolishedTextAsync(
                    deterministicRequest,
                    processed,
                    polishReview.Text,
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
                        _window?.SetSessionStatus(
                            DictationStatus.Processing("Delivering to the app you started in...")));
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
                        polishResult is { Status: PolishAttemptStatus.Polished } &&
                            polishVerdict == PolishOutputVerdict.Accepted,
                        delivery.Delivered).ConfigureAwait(false);
                    await controller.CompleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    await controller.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                    _window?.DispatcherQueue.TryEnqueue(() =>
                        _window?.ReportDeliveryAndMaybeOfferLanguage(
                            DeliveryStatusReport.For(delivery),
                            DeliveryLanguage(transcript)));
                    return;
                }
            }

            await SaveHistoryAsync(
                transcript,
                processed.Output.Text,
                polishResult is { Status: PolishAttemptStatus.Polished } &&
                    polishVerdict == PolishOutputVerdict.Accepted,
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
                    ? DictationStatus.Quiet("No speech detected")
                    : recoveryOnly
                        ? DictationStatus.Quiet("Escape Recovery finished. Text is ready to copy")
                    : processed.IsDegraded
                    ? DictationStatus.Success("Transcribed and cleaned locally with a safe fallback")
                    : polishResult is { UsedFallback: true }
                        ? DictationStatus.Success(PolishFallbackStatus(polishResult))
                    : polishResult is { UsedFallback: false }
                        ? _cloudPolishConsent is null
                            ? DictationStatus.Success("Transcribed and polished locally")
                            : DictationStatus.Success(
                                $"Transcribed and polished directly with {_cloudPolishConsent.ProviderName}")
                    : transcript.UsedFallback
                        ? DictationStatus.Success("Transcribed and cleaned locally with CPU fallback")
                        : DictationStatus.Success("Transcribed and cleaned locally");
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
                _window?.SetSessionStatus(DictationStatus.Error("Local transcription failed safely")));
        }
        finally
        {
            // In a finally rather than beside each return, because this method leaves by several
            // paths - delivered, held for recovery, and a transcription failure - and a wait the
            // user sat through is worth the same whichever one it took. Beside the returns, the
            // failure path is the one that would have been missed, and it is the slowest.
            waitTimer.Stop();
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationCompleted,
                AppFailureCategory.None,
                waitTimer.ElapsedMilliseconds));
        }
    }

    /// <summary>
    /// Keeps the audio of a dictation so a bad transcript can be replayed. DEBUG builds only.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS, "the app heard that wrong" IS UNREPRODUCIBLE. The audio is gone the moment
    /// the dictation finishes, so the only evidence is the wrong text and somebody's memory of
    /// what they said - which is the least reliable input available and the one every report is
    /// currently built on.
    ///
    /// DEBUG ONLY, compiled out entirely rather than gated at runtime. Audio is the most sensitive
    /// thing this app touches, and a runtime flag is a thing that can be turned on; a conditional
    /// compile is not present in the binary a user runs at all. It never leaves the machine either
    /// way - the network boundary is untouched - but "cannot be enabled" is a stronger claim than
    /// "is not enabled" and it costs nothing here.
    ///
    /// FAILURES ARE SWALLOWED, and that is right for this one specifically. A debugging aid that
    /// can break a dictation is worse than no debugging aid: the archive exists to help diagnose
    /// the pipeline, so it must never be the reason the pipeline failed.
    /// </remarks>
    [System.Diagnostics.Conditional("DEBUG")]
    private void ArchiveDictationAudio(CapturedAudio audio)
    {
        try
        {
            var directory = Path.Combine(_dataDirectory, "audio-archive");
            Directory.CreateDirectory(directory);

            var existing = Directory
                .EnumerateFiles(directory, "*.wav")
                .Select(path => (Path: path, Written: (DateTimeOffset)File.GetLastWriteTimeUtc(path)))
                .ToArray();
            foreach (var stale in AudioArchiveRetention.ToDelete(existing))
            {
                File.Delete(stale);
            }

            // The session id rather than a timestamp, so the file can be matched to the log lines
            // for the same dictation. A timestamp would collide with itself on a fast machine and
            // would have to be matched by eye against a clock.
            File.WriteAllBytes(
                Path.Combine(directory, $"{audio.SessionId.Value:N}.wav"),
                WaveFile.EncodeMono(audio.Samples.Span, audio.SampleRate));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    /// <summary>
    /// Transcribes only what streaming did not already cover, and joins the two.
    /// </summary>
    /// <remarks>
    /// THIS IS WHERE STREAMING PAYS. Everything committed while the user was speaking is already
    /// text, so the release only has to recognise the tail - which is why a long dictation stops
    /// costing a long wait.
    ///
    /// IT FALLS BACK TO THE WHOLE RECORDING ON ANY DOUBT, and the conditions are checked here
    /// rather than trusted from the loop. No head start, a failure flag, or a tail that would be
    /// longer than the audio all mean transcribe everything, exactly as before streaming existed.
    /// Half a dictation is worse than a slow one, and this is the last place to refuse.
    ///
    /// THE TAIL'S ENGINE ID AND LANGUAGE ARE THE ONES REPORTED, because they came from the same
    /// engine on the same audio and the committed pieces cannot disagree about them. The token
    /// timings are the tail's alone and are already only used for diagnostics.
    /// </remarks>
    private async Task<Transcript> TranscribeUsingAnyHeadStartAsync(
        RuntimeWorkerTranscriptionEngine engine,
        CapturedAudio audio,
        CancellationToken cancellationToken)
    {
        var headStart = _streamed.ToString();
        var usable = _streamingUsable &&
            _streamedThroughSample > 0 &&
            _streamedThroughSample < audio.Samples.Length &&
            !string.IsNullOrWhiteSpace(headStart);

        if (!usable)
        {
            return await engine.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
        }

        var tailAudio = audio with
        {
            Samples = audio.Samples[_streamedThroughSample..],
        };
        var tail = await engine.TranscribeAsync(tailAudio, cancellationToken).ConfigureAwait(false);

        var joined = new StreamingTranscriptAccumulator();
        joined.Append(headStart);
        joined.Append(tail.Text);

        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.StreamingHeadStartUsed));

        return tail with { Text = joined.ToString() };
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
            Provider: DiagnosticProviderIds.FromProviderId(provider.ProviderId)));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        PolishResult result;
        if (!_polishUsesLocalRuntime)
        {
            result = await provider.TryPolishAsync(
                new PolishRequest(input, detectedLanguage, PolishVocabulary.Eligible(
                    input.Text,
                    _settings.UserData.CustomWords)),
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
                        new PolishRequest(input, detectedLanguage, PolishVocabulary.Eligible(
                    input.Text,
                    _settings.UserData.CustomWords)),
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
            DiagnosticProviderIds.FromProviderId(provider.ProviderId),
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
        if (result.Kind == SessionTransitionKind.Started &&
            _audioCapture is ICaptureStartTimings timings &&
            timings.LastDeviceOpenMilliseconds is { } openMs)
        {
            // THE NUMBER THAT DECIDES A FEATURE. Warming the capture engine removes the OPEN half
            // and nothing else, so if open is cheap the whole idea is worth nothing and the privacy
            // question behind it never needs asking. Logged rather than reasoned about, because the
            // one thing nobody has done is look.
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.CaptureDeviceOpened,
                ElapsedMilliseconds: openMs));
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.CaptureStreamStarted,
                ElapsedMilliseconds: timings.LastStreamStartMilliseconds ?? -1));
        }

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

    private static string? DeliveryLanguage(Transcript transcript) =>
        transcript.EngineId.StartsWith(
            ParakeetTranscriptionEngine.ModelId,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : transcript.DetectedLanguage;

    private static DictationStatus SessionStatus(SessionTransitionResult result) => result.Kind switch
    {
        SessionTransitionKind.Started =>
            DictationStatus.Recording("Recording. Release to finish, Escape to cancel"),
        SessionTransitionKind.FinalizeReady when result.Error is not null =>
            DictationStatus.Quiet("Capture preserved after a microphone interruption"),
        SessionTransitionKind.FinalizeReady =>
            DictationStatus.Quiet("Capture complete. Transcribing locally"),
        SessionTransitionKind.Cancelled => DictationStatus.Quiet("Cancelled. Nothing will be delivered"),
        SessionTransitionKind.Failed => DictationStatus.Error("Session failed safely"),
        _ => DictationStatus.Quiet("Idle"),
    };

    private static string HotkeyFailureStatus(AppError? error) => error?.Code switch
    {
        AppErrorCode.HotkeyConflict => "Configured shortcut is already in use",
        AppErrorCode.HotkeyInvalid => "Configured shortcut is invalid",
        _ => "Global shortcut is unavailable",
    };

    /// <summary>The button an advisory about the cleanup provider carries.</summary>
    /// <remarks>
    /// ONE INSTANCE RATHER THAN ONE PER ROW. Four sentences send the user to the same page, and
    /// four copies of the same two words is how one of them ends up saying something else.
    /// </remarks>
    private static readonly PillAction OpenPolish =
        new("Open settings", PillActionKind.OpenPolishSettings, "Open AI polish settings");

    /// <summary>The button an advisory about the speech engine carries.</summary>
    private static readonly PillAction OpenTranscription =
        new("Open settings", PillActionKind.OpenTranscriptionSettings, "Open transcription settings");

    private static DictationStatus OllamaHealthStatus(OllamaHealth health) => health switch
    {
        // EVERY UNHEALTHY OLLAMA ROW IS A SETUP PROBLEM THE USER CAN FIX, so it is an advisory
        // rather than an error or, as it was, nothing at all. A user whose polish provider is
        // switched off currently gets a silently plainer result and no pill saying why.
        OllamaHealth.EndpointInvalid =>
            DictationStatus.Advisory("Ollama endpoint must point to this PC", OpenPolish),
        OllamaHealth.ServerUnavailable =>
            DictationStatus.Advisory(
                "Ollama is offline. Cleaned text will still be preserved", OpenPolish),
        OllamaHealth.ServerUnhealthy =>
            DictationStatus.Advisory("Ollama did not return a usable health response", OpenPolish),
        OllamaHealth.NoLocalModels =>
            DictationStatus.Advisory(
                "Ollama is running, but no local model is installed", OpenPolish),
        _ => DictationStatus.Quiet("Ollama is ready"),
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
