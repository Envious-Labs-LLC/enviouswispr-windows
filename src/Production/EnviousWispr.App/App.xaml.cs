using System.Reflection;
using System.Security;
using EnviousWispr.App.Composition;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Credentials;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;
using EnviousWispr.Services.Feedback;
using EnviousWispr.Services.History;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Lifecycle;
using EnviousWispr.Services.Reliability;
using EnviousWispr.Services.Runtime;
using EnviousWispr.Services.Settings;
using Microsoft.UI.Xaml;

namespace EnviousWispr.App;

public partial class App : Application, IAsyncDisposable
{
    private readonly PrivacySafeObservabilityLogger _logger;
    private WindowsObservabilityService? _observabilityService;
    private FeedbackCoordinator? _feedbackCoordinator;
    private SentryFeedbackSender? _feedbackSender;
    private FeedbackOutbox? _feedbackOutbox;
    private HardwareSnapshot? _lastHardwareSnapshot;
    private readonly ReleaseIdentity _releaseIdentity;
    private readonly StoreUpdateService _updateService;
    private readonly JsonDiagnosticExportService _diagnosticExportService;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonPortableProfileService _profileService = new();
    private readonly SqliteHistoryStore _historyStore;
    private readonly LocalDictationAudioArchive _dictationAudioArchive;
    private readonly JsonApplicationRunStateStore _runStateStore;
    private readonly WindowsRecoveryTextStore _recoveryTextStore;
    private readonly SessionPersistence _sessionPersistence;
    // THE SEAM THE SESSION IS COMPOSED THROUGH, HELD AS THE SEAM (plan-2 step 15). The executor asks
    // ISystemResourceProbe whether a recording may start; the shell constructs the Windows probe and
    // hands it over as the contract, not the class, so nothing here can reach for what the session
    // does not see.
    private readonly ISystemResourceProbe _resourceProbe;
    private readonly WindowsCredentialApiKeyStore _credentialStore;
    private readonly string _dataDirectory;
    private readonly RuntimeResourceArbiter _resourceArbiter = new();
    private readonly LivePreviewController _livePreview;
    private DictationSessionCoordinator? _sessionCoordinator;
    private Task? _startup;
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly DeterministicTextPipeline _deterministicTextPipeline = new();
    private readonly SessionRuntime _runtime;
    private PushToTalkSessionController? _sessionController;
    private readonly AutoStopMonitor _autoStop;
    private readonly RecordingWatchdog _watchdog;
    private readonly StreamingTranscriptionController _streaming;
    private MainWindow? _window;
    private IReadOnlyList<CustomWordEntry> _customWords = [];
    private IReadOnlyList<string> _enabledVocabularyPackIds = [];
    private SnippetVocabulary _snippetVocabulary = SnippetVocabulary.Empty;
    // VOLATILE BECAUSE THE HOTKEY THREAD READS IT AND THE UI THREAD REPLACES IT. The record itself
    // is immutable and cannot tear, but the REFERENCE can be read stale, and one of its readers is
    // the delivery-options closure that decides where a recording's words are about to go.
    private volatile AppSettings _settings = AppSettings.Default;
    private DeterministicTextOptions _deterministicTextOptions =
        DeterministicTextOptions.From(DictationPreferences.Default);
    private bool _disposed;
    private bool _exitRequested;
    private OllamaModelHost? _ollamaModelHost;
    private WindowPresentationSession? _presentation;
    private Guid? _runId;

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
        var diagnosticPath = Path.Combine(_dataDirectory, "diagnostics", "app.jsonl");
        IPrivacySafeTelemetryTransport? telemetryTransport = null;
        if (TelemetryEndpointPolicy.TryResolveConfiguredEndpoint(out var telemetryEndpoint))
        {
            telemetryTransport = new HttpPrivacySafeTelemetryTransport(telemetryEndpoint!);
        }

        _logger = new PrivacySafeObservabilityLogger(
            new JsonLineFileLogger(diagnosticPath, enabled: false),
            telemetryTransport);
        _diagnosticExportService = new JsonDiagnosticExportService(diagnosticPath);
        _settingsStore = new JsonSettingsStore(
            Path.Combine(_dataDirectory, "settings.json"),
            Path.Combine(_dataDirectory, "user-data.json"));
        var dictationRecordingsDirectory = Path.Combine(_dataDirectory, "recordings", "dictations");
        _dictationAudioArchive = new LocalDictationAudioArchive(dictationRecordingsDirectory);
        _historyStore = new SqliteHistoryStore(
            Path.Combine(_dataDirectory, "history.db"),
            Path.Combine(_dataDirectory, "history.json"),
            dictationRecordingsDirectory);
        _runStateStore = new JsonApplicationRunStateStore(Path.Combine(_dataDirectory, "run-state.json"));
        _recoveryTextStore = new WindowsRecoveryTextStore(Path.Combine(_dataDirectory, "recovery.json"));
        // THE LONG-LIVED SESSION OWNERS ARE ONE COMPOSITION'S, SHARED WITH THE TESTS. The shell chooses
        // the stores and the pipeline and supplies leaf reads of its own state; RuntimeComposition
        // builds the persistence owner, the finaliser and the four background owners the same way
        // for the app and for a test. The shell keeps the references it stops and disposes.
        _runtime = RuntimeComposition.Compose(new RuntimeCompositionParts(
            _recoveryTextStore,
            _historyStore,
            _deterministicTextPipeline,
            _resourceArbiter,
            _logger,
            new RuntimeShell(
                new WindowRuntimeView(this),
                LivePreviewEnabled: () => _settings.Preferences.LivePreviewEnabled,
                History: () => _settings.Preferences.History,
                CustomWords: () => _settings.UserData.CustomWords,
                EnabledVocabularyPackIds: () => _settings.UserData.EnabledVocabularyPackIds,
                Audio: () => _audioCapture as IAudioSnapshotSource,
                Engine: () => _transcriptionEngine,
                PreviewEngine: () => _previewEngine,
                PreviewUnavailableReason: () => _previewUnavailableReason,
                RecordingSessionId: () => _sessionController?.CurrentSession?.Id,
                Coordinator: () => _sessionCoordinator,
                Leaving: () => _exitRequested || _disposed,
                ClipboardText: WindowsClipboardText.TryReadAsync,
                Dictation: () => _settings.Preferences.Dictation),
            TimeProvider.System,
            _dictationAudioArchive,
            EnsureLearnedWordCheckStep()));
        _sessionPersistence = _runtime.Persistence;
        _livePreview = _runtime.Preview;
        _streaming = _runtime.Streaming;
        _watchdog = _runtime.Watchdog;
        _autoStop = _runtime.AutoStop;
        _resourceProbe = new WindowsSystemResourceProbe(_dataDirectory);

        // THE WINDOW IS READ WHEN THE STORE IS FIRST ASKED, not now: it does not exist yet, and the
        // Store needs it as the owner of its install prompt.
        _updateService = new StoreUpdateService(
            () => _window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0);

        UnhandledException += (_, eventArgs) =>
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.UnhandledFailure,
                AppFailureCategory.Unknown));
            if (eventArgs.Exception is { } ex)
            {
                _ = _observabilityService?.CaptureUnhandledExceptionBlocking(
                    ex,
                    mechanismType: "winui_unhandled_exception",
                    handled: false,
                    level: "fatal");
            }

            eventArgs.Handled = false;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var instanceKey = SingleInstanceLock.ResolveKey(
            _releaseIdentity.SingleInstanceKey,
            Environment.GetEnvironmentVariable("ENVIOUSWISPR_DATA_DIRECTORY"));
        if (!SingleInstanceLock.TryAcquire(instanceKey, out _singleInstanceLock))
        {
            var activated = await SingleInstanceActivationChannel.RequestActivationAsync(
                instanceKey,
                TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                activated
                    ? AppEventCode.DuplicateInstanceActivated
                    : AppEventCode.DuplicateInstanceRejected));
            Exit();
            return;
        }

        _activationChannel = new SingleInstanceActivationChannel(instanceKey);
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
        var appVersion = CurrentAppVersion();
        var observabilityIdentity = WindowsObservabilityIdentity.CreateDefault(
            _releaseIdentity,
            isPackagedMsix: _updateService.IsStoreInstalled,
            appVersion: appVersion);
        _observabilityService = WindowsObservabilityService.CreateFromEnvironment(
            _dataDirectory,
            observabilityIdentity,
            observability);
        _observabilityService.ApplySettings(loadResult.Settings, emitSettingsChangedEvent: false);
        _observabilityService.AttachProcessExceptionHandlers();
        _feedbackSender = new SentryFeedbackSender(observabilityIdentity);
        _feedbackOutbox = new FeedbackOutbox(_dataDirectory);
        _feedbackCoordinator = new FeedbackCoordinator(
            new FeedbackDraftStore(_dataDirectory),
            _feedbackOutbox,
            _feedbackSender,
            observabilityIdentity);
        _logger.AttachObserver(record =>
        {
            _observabilityService.ObserveDiagnosticRecord(record);
            _feedbackCoordinator?.ObserveDiagnosticRecord(record);
        });
        RuntimeWorkerSupervisor.AbnormalWorkerExitObserved += OnAbnormalWorkerExitObserved;

        _logger.Configure(observability, DateTimeOffset.UtcNow);
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ApplicationStarting));
        if (runStart.RecoveredInterruptedRun)
        {
            // THE LOG IS WHERE THIS BELONGS AND IT IS THE ONLY PLACE IT NOW GOES. A previous run
            // that did not record a clean exit used to raise a banner on Home saying so, with a
            // running count beside it - and the app cannot tell WHY a run ended. Closing a laptop,
            // choosing Restart from the Start menu, logging off, or Task Manager all leave exactly
            // the same trace as a fault. Counted together, they told one machine it had failed
            // nineteen times in a row when most of those were a build script releasing a file lock.
            //
            // AND THE PERSON READING IT COULD DO NOTHING WITH IT. This branch only runs when there
            // was no unfinished dictation to restore, so nothing was lost; the case where something
            // WAS lost is handled by the recovery card, which is untouched. A first-screen banner
            // that accuses the product of failing, on a number it cannot justify, about an event
            // that cost the reader nothing, is worse than silence. Support still has the fact here.
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.ApplicationRunRecovered,
                AppFailureCategory.Recovery,
                ErrorCode: AppErrorCode.PreviousRunInterrupted));
            _ = _observabilityService.CaptureInterruptedDictationNoteAsync(runStart.PreviousRunWasDictating);
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
        _enabledVocabularyPackIds = settings.UserData.EnabledVocabularyPackIds;
        _snippetVocabulary = SnippetVocabulary.From(settings.UserData);
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

        // THE WINDOW'S PRESENTATION IS COMPOSED HERE AND OWNED BY THE SHELL (plan-2 step 12): the one
        // settings writer, the presenters over it, the microphone test and the device catalogue with
        // their production factories. The window takes the session and the facts of this launch; the
        // shell's lifetime closes the session - drain, then disposal - among its shell services.
        _polishModelSource = new PolishModelSource(_credentialStore);
        var historyRepolishService = new HistoryRepolishService(
            _historyStore,
            _runtime.PolishExecutor,
            _deterministicTextPipeline,
            CurrentPolishSetup,
            () => _deterministicTextOptions,
            () => _customWords,
            targetAdapter: null,
            logger: _logger,
            clock: TimeProvider.System,
            enabledVocabularyPackIds: () => _enabledVocabularyPackIds,
            targetAdapterProvider: () => _textTargetAdapter,
            beginExecution: BeginHistoryRepolishExecution);
        _ollamaModelHost = new OllamaModelHost(
            _logger,
            () => _ollamaActiveModel,
            resourceAdmission: _resourceArbiter,
            isDictationBusy: () => IsSpeechEngineBusy);
        _presentation = WindowComposition.Compose(
            _settingsStore,
            settings,
            _historyStore,
            _recoveryTextStore,
            _credentialStore,
            _polishModelSource,
            _profileService,
            _diagnosticExportService,
            _ollamaModelHost,
            historyRepolishService);
        _window = new MainWindow(
            settings,
            _presentation,
            new WindowLaunch(
                loadResult.Status,
                _logger.TelemetryAvailable || (_observabilityService?.PostHog.IsConfigured ?? false),
                _releaseIdentity,
                _updateService.IsStoreInstalled,
                _updateService.InstalledVersion,
                _dataDirectory,
                observability.SendCrashReports));
        _window.SettingsChanged += OnSettingsChanged;
        _window.SessionStatusChanged += OnSessionStatusChanged;
        _window.RecordingSoundPlaybackFailed += OnRecordingSoundPlaybackFailed;
        _window.AudioDevicesChanged += OnAudioDevicesChanged;
        _window.RecoveryCleared += OnRecoveryCleared;
        _window.RecoveryUndoRequested += OnRecoveryUndoRequested;
        _window.HistoryPasteRequested += OnHistoryPasteRequested;
        _window.DiagnosticsExportCompleted += OnDiagnosticsExportCompleted;
        _window.SnippetImportReported += OnSnippetImportReported;
        _window.WordImportReported += OnWordImportReported;
        _window.UpdateCheckRequested += OnUpdateCheckRequested;
        _window.UpdateApplyRequested += OnUpdateApplyRequested;
        _window.StartupToggleRequested += OnStartupToggleRequested;
        _window.LoadStartupRegistration = () => _startupRegistration.GetStatusAsync(_startupCancellation.Token);
        _window.ModelDownloadRequested += OnModelDownloadRequested;
        _window.ModelDownloadCancelRequested += OnModelDownloadCancelRequested;
        _window.OnboardingDualDownloadRequested += OnOnboardingDualDownloadRequested;
        _window.OnboardingDualDownloadCancelRequested += OnOnboardingDualDownloadCancelRequested;
        _window.OnboardingDualDownloadRetryRequested += OnOnboardingDualDownloadRetryRequested;
        _window.EgOneModelDownloadRequested += OnEgOneModelDownloadRequested;
        _window.EgOneModelDownloadCancelRequested += OnModelDownloadCancelRequested;
        _window.EgOneModelRemoveRequested += OnEgOneModelRemoveRequested;
        _window.SpeechEngineDownloadRequested += OnSpeechEngineDownloadRequested;
        _window.SpeechEngineRemoveRequested += OnSpeechEngineRemoveRequested;
        _window.IsSpeechEngineBusy = () => IsSpeechEngineBusy;
        _window.PreviewModelDownloadRequested += OnPreviewModelDownloadRequested;
        _window.PreviewModelRemoveRequested += OnPreviewModelRemoveRequested;
        _window.DictationEnded += OnDictationEnded;
        _window.TranscriptionRetryRequested += OnTranscriptionRetryRequested;
        _window.GraphicsRuntimeDownloadRequested += OnGraphicsRuntimeDownloadRequested;
        _window.GraphicsRuntimeDownloadCancelRequested += OnGraphicsRuntimeDownloadCancelRequested;
        _window.ManualDictationToggleRequested += OnManualDictationToggleRequested;
        _window.KeybindCaptureActiveChanged += OnKeybindCaptureActiveChanged;
        _window.TranscribeFile = TranscribeFileAsync;
        _window.ExportUserData = ExportUserDataAsync;
        _window.DeleteAllData = DeleteAllData;
        ReportDataDeletionLeftover(_window);
        _window.SpeedCheckRequested += OnSpeedCheckRequested;
        _window.MishearingSuggestionsRequested += OnMishearingSuggestionsRequested;
        _window.FeedbackDataDirectory = _dataDirectory;
        _window.DictationRecordingsDirectory = _dictationAudioArchive.RecordingsDirectory;
        _window.FeedbackDraftStore = _feedbackCoordinator.DraftStore;
        _window.BuildFeedbackDiagnosticsSnapshot = () =>
            _feedbackCoordinator.BuildDiagnosticsSnapshot(_settings, _lastHardwareSnapshot);
        _window.SubmitFeedback = (submission, cancellationToken) =>
            _feedbackCoordinator.SubmitAsync(submission, cancellationToken);
        _window.FlushFeedbackOutbox = cancellationToken =>
            _feedbackCoordinator.FlushOutboxAsync(cancellationToken);
        _window.CheckUndeliverableFeedback = cancellationToken =>
            _feedbackCoordinator.HasUndeliverableReportsAsync(cancellationToken);
        InitializeSelfLearning();
        AttachSelfLearningToWindow(_window);
        _window.AppWindow.Closing += OnAppWindowClosing;
        _window.Closed += OnWindowClosed;
        _window.Activate();
        if (Interlocked.Exchange(ref _activationPending, 0) == 1)
        {
            ShowMainWindow(openSettings: false);
        }

        ConfigureTrayIcon();
        // FROM THE TRAY ON, AN EXIT CAN BEGIN. The rest of the launch - the recovery read, the engines,
        // the session's construction - is one tracked task the lifetime cancels under its exit policy
        // and joins under Quiesce before it disposes anything the launch borrows; and after each of its
        // awaits it reads Leaving, so nothing is constructed once the exit has begun.
        _startup = CompleteStartupAsync(settings, runStart, _window);
        await _startup.ConfigureAwait(true);
    }

    private async Task CompleteStartupAsync(AppSettings settings, ApplicationRunStartResult runStart, MainWindow window)
    {
        // THE CLEANUP STAGES ARE WARMED BESIDE THE LAUNCH, OFF THE UI THREAD (#239). A stage's first call
        // pays compilation and first-use construction, and on a slow or throttled machine that once crossed
        // the stage's deadline, so the first dictation after launch lost its spoken emoji. Joined in the
        // finally below, so it belongs to the launch's one tracked task, which the exit already joins before
        // it disposes the logger this writes to. It takes milliseconds and never throws.
        var cleanupWarmUp = Task.Run(WarmDeterministicStages);

        // A PRIVATE COPY OF ANOTHER APP'S WORDS NEVER OUTLIVES ONE LAUNCH. A read removes its copy at once, but a
        // scanner holding the file can stop that; the next launch sweeps the one folder those copies live in, off the
        // UI thread, and says only THAT something was left, never what.
        var importScratchSweep = Task.Run(SweepAppImportCopies);
        try
        {
            await CompleteStartupCoreAsync(settings, runStart, window).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            // THE EXIT'S OWN CANCELLATION IS AN ENDING, NOT A FAULT. The exit policy cancelled the
            // launch's token while the recovery read was out; the read ends cancelled, the launch is
            // over, and nothing of it escapes to the launch entry point - the lifetime finishes its
            // teardown and reports.
        }
        finally
        {
            await cleanupWarmUp.ConfigureAwait(true);
            await importScratchSweep.ConfigureAwait(true);
        }
    }

    /// <summary>Warms the deterministic cleanup stages once; a failure is logged by stage name only and swallowed.</summary>
    private void WarmDeterministicStages()
    {
        try
        {
            foreach (var stage in _deterministicTextPipeline.WarmStages())
            {
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.DeterministicStageWarmUpFailed,
                    AppFailureCategory.PostProcessing,
                    Stage: stage,
                    StageStatus: DeterministicStageStatus.Failed));
            }
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            // A WARM-UP IS AN OPTIMISATION, NEVER A LAUNCH FAILURE. Each stage still runs on every dictation
            // under its own deadline; all this costs is that the first one pays the first-call cost.
        }
    }

    private async Task CompleteStartupCoreAsync(AppSettings settings, ApplicationRunStartResult runStart, MainWindow window)
    {
        await window.InitializeProductDataAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        var recovery = await LoadStartupRecoveryAsync(_startupCancellation.Token).ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        _sessionPersistence.AdoptStartupRecovery(recovery);
        window.SetRecoveredText(recovery);
        if (StartupNoticeDecision.For(
                runStart.RecoveredInterruptedRun,
                runStart.PreviousRunWasDictating,
                recovery.Status) == StartupNotice.DictationMayBeLost)
        {
            window.SetPossiblyLostDictationNotice();
        }

        ConfigureSystemLifecycleMonitor();
        window.FocusInitialControl();
        window.SetCloudPolishNotice(_cloudPolishConsent?.Notice);
        window.SetOllamaPolishNotice(_localPolishNotice);
        window.SetSessionStatus(DictationStatus.Quiet("Preparing local transcription..."));
        await ConfigureTranscriptionAsync(settings.Preferences.Dictation.FinalEngine).ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        PresentGraphicsRuntime();
        await PresentModelDeliveryAsync().ConfigureAwait(true);
        if (Leaving)
        {
            return;
        }

        ConfigurePushToTalk(settings.Preferences.Dictation);
        StartPolishWarmup();

        ApplyOverlayUatState();
        StartSyntheticLevelRampIfRequested();
        _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellShown));
        _ = FlushFeedbackOutboxSafelyAsync(_startupCancellation.Token);
        SignalPerformanceUatReady();
        StartPublicFixtureJourneyUat();
        ApplyReliabilityUatExit();
    }

    private async Task<RecoveryTextLoadResult> LoadStartupRecoveryAsync(CancellationToken cancellationToken)
    {
        var recovery = await _recoveryTextStore.LoadAsync(cancellationToken).ConfigureAwait(true);
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
        return await _recoveryTextStore.SaveAsync(record, cancellationToken).ConfigureAwait(true)
            ? new RecoveryTextLoadResult(RecoveryTextLoadStatus.Found, record)
            : new RecoveryTextLoadResult(
                RecoveryTextLoadStatus.Unavailable,
                Error: new AppError(
                    AppErrorCode.StorageUnavailable,
                    AppErrorStage.RecoveryText,
                    CanRetry: true));
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
            window.RecoveryUndoRequested -= OnRecoveryUndoRequested;
            window.HistoryPasteRequested -= OnHistoryPasteRequested;
            window.DiagnosticsExportCompleted -= OnDiagnosticsExportCompleted;
            window.SnippetImportReported -= OnSnippetImportReported;
            window.WordImportReported -= OnWordImportReported;
            window.UpdateCheckRequested -= OnUpdateCheckRequested;
            window.UpdateApplyRequested -= OnUpdateApplyRequested;
            window.StartupToggleRequested -= OnStartupToggleRequested;
            window.LoadStartupRegistration = null;
            window.ModelDownloadRequested -= OnModelDownloadRequested;
            window.ModelDownloadCancelRequested -= OnModelDownloadCancelRequested;
            window.OnboardingDualDownloadRequested -= OnOnboardingDualDownloadRequested;
            window.OnboardingDualDownloadCancelRequested -= OnOnboardingDualDownloadCancelRequested;
            window.OnboardingDualDownloadRetryRequested -= OnOnboardingDualDownloadRetryRequested;
            window.EgOneModelDownloadRequested -= OnEgOneModelDownloadRequested;
            window.EgOneModelDownloadCancelRequested -= OnModelDownloadCancelRequested;
            window.EgOneModelRemoveRequested -= OnEgOneModelRemoveRequested;
            window.SpeechEngineDownloadRequested -= OnSpeechEngineDownloadRequested;
            window.SpeechEngineRemoveRequested -= OnSpeechEngineRemoveRequested;
            window.IsSpeechEngineBusy = null;
            window.PreviewModelDownloadRequested -= OnPreviewModelDownloadRequested;
            window.PreviewModelRemoveRequested -= OnPreviewModelRemoveRequested;
            window.DictationEnded -= OnDictationEnded;
            window.TranscriptionRetryRequested -= OnTranscriptionRetryRequested;
            window.GraphicsRuntimeDownloadRequested -= OnGraphicsRuntimeDownloadRequested;
            window.GraphicsRuntimeDownloadCancelRequested -= OnGraphicsRuntimeDownloadCancelRequested;
            window.ManualDictationToggleRequested -= OnManualDictationToggleRequested;
            window.KeybindCaptureActiveChanged -= OnKeybindCaptureActiveChanged;
            window.SpeedCheckRequested -= OnSpeedCheckRequested;
            window.MishearingSuggestionsRequested -= OnMishearingSuggestionsRequested;
            DetachSelfLearningFromWindow(window);
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

    private void OnSettingsChanged(AppSettings settings)
    {
        var previousSharing = _settings.Observability?.ShareAnonymousTelemetry == true;
        var previousFinalEngine = _settings.Preferences.Dictation.FinalEngine;
        var previousPreviewEnabled = _settings.Preferences.LivePreviewEnabled;
        var previousPreviewEngine = _settings.Preferences.LivePreviewEngine;
        var previousPolish = _settings.Preferences.Polish;
        var previousLearnFromEdits = _settings.Preferences.Dictation.LearnFromEditsEnabled;
        _settings = settings;
        _customWords = settings.UserData.CustomWords;
        _enabledVocabularyPackIds = settings.UserData.EnabledVocabularyPackIds;
        _snippetVocabulary = SnippetVocabulary.From(settings.UserData);
        _deterministicTextOptions = DeterministicTextOptions.From(settings.Preferences.Dictation);
        try
        {
            _trayIcon?.SetEngineAndPolishSummary(
                settings.Preferences.Dictation.FinalEngine,
                settings.Preferences.Polish.Provider);
            _trayIcon?.SetTheme(settings.Preferences.Theme);
        }
        catch (ObjectDisposedException)
        {
        }

        var observability = settings.Observability ?? ObservabilityPreferences.Default;
        _logger.Configure(observability, DateTimeOffset.UtcNow);
        _observabilityService?.ApplySettings(settings, emitSettingsChangedEvent: true);
        if (previousSharing != observability.ShareAnonymousTelemetry)
        {
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                observability.ShareAnonymousTelemetry
                    ? AppEventCode.TelemetryConsentEnabled
                    : AppEventCode.TelemetryConsentDisabled));
        }

        if (previousFinalEngine != settings.Preferences.Dictation.FinalEngine)
        {
            _transcriptionReconfigurePending = true;
            TrySchedulePendingModelRefresh();
        }

        if (previousPreviewEnabled != settings.Preferences.LivePreviewEnabled ||
            previousPreviewEngine != settings.Preferences.LivePreviewEngine)
        {
            _livePreviewReconfigurePending = true;
            TrySchedulePendingModelRefresh();
        }

        if (previousPolish != settings.Preferences.Polish)
        {
            _polishReconfigurePending = true;
            TrySchedulePendingModelRefresh();
        }

        if (previousLearnFromEdits != settings.Preferences.Dictation.LearnFromEditsEnabled)
        {
            OnSelfLearningSettingChanged(settings.Preferences.Dictation.LearnFromEditsEnabled);
        }
    }

    /// <summary>Whether the exit has begun, as a queued callback should read it: the flags, or the presentation's closure.</summary>
    private bool Leaving => _exitRequested || _disposed || _presentation is { Closing: true };

    /// <summary>Posts window effects only while this window's presentation is still admitted.</summary>
    private void PostToLiveWindow(Action<MainWindow> update)
    {
        if (Leaving || _window is not { } window || _presentation is not { } presentation)
        {
            return;
        }

        // A TERMINAL SESSION STATUS CAN ARRIVE AFTER THE OVERLAY CLOSED. Queue acceptance says
        // nothing about that lifetime: take the existing presentation lease at the draw, so a late
        // callback is refused and a running one finishes before ShutdownProductWindows closes it.
        presentation.TryPost(
            callback => window.DispatcherQueue.TryEnqueue(() => callback()),
            () =>
            {
                if (!Leaving && ReferenceEquals(_window, window))
                {
                    update(window);
                }
            });
    }

    /// <summary>The window as the long-lived session owners see it: each sink one dispatch to the window, and nothing decided here.</summary>
    private sealed class WindowRuntimeView(App app) : IRuntimeView
    {
        public void ShowPreview(LivePreviewFrame? frame) =>
            app.PostToLiveWindow(window =>
            {
                // ASKED AT THE DRAW. A frame queued before the preview closed is drawn after; the
                // frame knows its screen is gone and the clear that closed it is left standing.
                if (frame is null || frame.IsCurrent())
                {
                    window.SetLivePreview(frame?.Text);
                }
            });

        public void ShowRecoveredText(RecoveryTextLoadResult result, bool undoOffered) =>
            app.PostToLiveWindow(window => window.SetRecoveredText(result, undoOffered));

        public void ClearRecoveredText() =>
            app.PostToLiveWindow(window => window.ClearRecoveredText());

        public void NotifyHistoryChanged() =>
            app.PostToLiveWindow(window =>
            {
                app.RefreshLastDictationPreview();
                _ = window.NotifyHistoryChangedAsync();
            });

        public void ShowMainWindow() => app.ShowMainWindow(openSettings: false);

        public void ShowSessionBusy(SessionHolder holder) => app.ShowSessionBusy(holder);
    }

    /// <summary>The window as the session sees it: each sink one dispatch to the window, and nothing decided here.</summary>
    private sealed class WindowSessionView(App app) : ISessionView
    {
        public void ShowStatus(DictationStatus status) =>
            app.PostToLiveWindow(window => window.SetSessionStatus(status));

        public void ShowNotice(string title, string message, bool isError = false) =>
            app.PostToLiveWindow(window => window.SetReliabilityNotice(title, message, isError));

        public void ShowMainWindow() => app.ShowMainWindow(openSettings: false);

        public void ReportDelivery(DeliveryReport delivered, string? detectedLanguage) =>
            app.PostToLiveWindow(window =>
            {
                window.ReportDeliveryAndMaybeOfferLanguage(delivered, detectedLanguage);
                app.MaybeShowPolishSetupCard(delivered, window);
            });

        public void CancelRecordingCue() =>
            app.PostToLiveWindow(window => window.CancelRecordingSoundCue());
    }

    // THE SHELL'S THREE PARTS OF THE SESSION'S DISPOSAL, one operation each. The executor's teardown
    // calls them in its order, around its own disposal of the controller, once the session is
    // quiescent and the background work has stopped; a part that throws is recorded by the executor
    // and the next still runs. Nothing here waits, orders or decides.

    /// <summary>The level meter comes off the capture, so a closing window is not told about the stop.</summary>
    private void DetachCaptureObservers()
    {
        if (_audioCapture is { } capture)
        {
            capture.LevelChanged -= OnAudioLevelChanged;
        }
    }

    /// <summary>The controller and its capture have been disposed by the executor: the references go.</summary>
    private void ReleaseSession()
    {
        _sessionController = null;
        _audioCapture = null;
    }

    /// <summary>The delivery route is disposed and let go of.</summary>
    private void DisposeDeliveryRoute()
    {
        var adapter = _textTargetAdapter;
        _textTargetAdapter = null;
        _textDelivery = null;
        adapter?.Dispose();
    }

    private static AppFailureCategory FailureFor(AppError? error) => AppFailureCategories.For(error);

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
