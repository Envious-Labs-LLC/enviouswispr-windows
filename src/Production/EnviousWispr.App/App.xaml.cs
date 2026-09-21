using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.ASR;
using EnviousWispr.ModelDelivery;
using EnviousWispr.LLM;
using EnviousWispr.App.Composition;
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

    private readonly PrivacySafeObservabilityLogger _logger;
    private readonly ReleaseIdentity _releaseIdentity;
    private readonly VelopackUpdateService _updateService;
    private readonly JsonDiagnosticExportService _diagnosticExportService;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonPortableProfileService _profileService = new();
    private readonly JsonHistoryStore _historyStore;
    private readonly JsonApplicationRunStateStore _runStateStore;
    private readonly WindowsRecoveryTextStore _recoveryTextStore;
    private readonly SessionPersistence _sessionPersistence;
    private readonly WindowsSystemResourceProbe _resourceProbe;
    private readonly WindowsCredentialApiKeyStore _credentialStore;
    private readonly string _dataDirectory;
    private readonly string? _cudaRuntimeDirectory;
    private readonly RuntimeResourceArbiter _resourceArbiter = new();
    private readonly LivePreviewController _livePreview;
    private DictationSessionCoordinator? _sessionCoordinator;
    private readonly DeterministicTextPipeline _deterministicTextPipeline = new();
    private readonly SessionRuntime _runtime;
    private SingleInstanceLock? _singleInstanceLock;
    private SingleInstanceActivationChannel? _activationChannel;
    private WindowsSystemLifecycleMonitor? _lifecycleMonitor;
    private WindowsPushToTalkHook? _pushToTalkHook;
    private bool _keybindCaptureActive;
    private PushToTalkSessionController? _sessionController;
    private IAudioCapture? _audioCapture;
    private RuntimeWorkerTranscriptionEngine? _transcriptionEngine;
    private RuntimeWorkerLivePreviewEngine? _previewEngine;
    /// <summary>Why <see cref="_previewEngine"/> could not be built, or null when it was.</summary>
    /// <remarks>
    /// HELD RATHER THAN LOGGED WHERE IT IS DISCOVERED. Configuration runs on every launch,
    /// including the overwhelming majority where Live Preview is switched off, so writing the
    /// failure there would file a complaint on behalf of a user who never asked for the
    /// feature - once per launch, on every machine without the optional pack. The reason is
    /// kept here and reported at the moment somebody actually turns Live Preview on.
    /// </remarks>
    private AppErrorCode? _previewUnavailableReason;
    private IPolishProvider? _polishProvider;
    private WindowsTextTargetAdapter? _textTargetAdapter;
    private ContextAwareTextDelivery? _textDelivery;
    private RuntimeResourceKind _polishResource = RuntimeResourceKind.Cpu;
    private bool _polishUsesLocalRuntime;
    private CloudPolishConsent? _cloudPolishConsent;
    private string? _localPolishNotice;
    private readonly CancellationTokenSource _polishLifetime = new();
    private Task? _polishWarmup;
    private readonly AutoStopMonitor _autoStop;
    private readonly RecordingWatchdog _watchdog;
    private readonly StreamingTranscriptionController _streaming;
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
    private bool _sessionTornDownCleanly = true;
    private bool _exitRequested;
    private ApplicationLifetime? _lifetime;
    private bool _backgroundNoticeShown;
    private Guid? _runId;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatLoop;
    private int _activationPending;

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
                Audio: () => _audioCapture as IAudioSnapshotSource,
                Engine: () => _transcriptionEngine,
                PreviewEngine: () => _previewEngine,
                PreviewUnavailableReason: () => _previewUnavailableReason,
                RecordingSessionId: () => _sessionController?.CurrentSession?.Id,
                Coordinator: () => _sessionCoordinator,
                Leaving: () => _exitRequested || _disposed),
            TimeProvider.System));
        _sessionPersistence = _runtime.Persistence;
        _livePreview = _runtime.Preview;
        _streaming = _runtime.Streaming;
        _watchdog = _runtime.Watchdog;
        _autoStop = _runtime.AutoStop;
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
        _window.ModelDownloadRequested += OnModelDownloadRequested;
        _window.ModelDownloadCancelRequested += OnModelDownloadCancelRequested;
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
        _sessionPersistence.AdoptStartupRecovery(recovery);
        _window.SetRecoveredText(recovery);
        if (StartupNoticeDecision.For(
                runStart.RecoveredInterruptedRun,
                runStart.PreviousRunWasDictating,
                recovery.Status) == StartupNotice.DictationMayBeLost)
        {
            _window.SetPossiblyLostDictationNotice();
        }

        ConfigureSystemLifecycleMonitor();
        _window.FocusInitialControl();
        _window.SetCloudPolishNotice(_cloudPolishConsent?.Notice);
        _window.SetOllamaPolishNotice(_localPolishNotice);
        _window.SetSessionStatus(DictationStatus.Quiet("Preparing local transcription..."));
        await ConfigureTranscriptionAsync(settings.Preferences.Dictation.FinalEngine).ConfigureAwait(true);
        await PresentModelDeliveryAsync().ConfigureAwait(true);
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
        StartSyntheticLevelRampIfRequested();
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
            window.ModelDownloadRequested -= OnModelDownloadRequested;
            window.ModelDownloadCancelRequested -= OnModelDownloadCancelRequested;
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
            SystemLifecycleTransition.SessionEnding => AppEventCode.SystemSessionEnding,
            _ => AppEventCode.UnhandledFailure,
        };
        if (transition == SystemLifecycleTransition.SessionEnding && _runId is { } endingRunId)
        {
            // RECORDED HERE, NOT AT TEARDOWN, BECAUSE TEARDOWN MAY NEVER COME. Windows gives the
            // process a short and unguaranteed window after this notification and can kill it at any
            // point, so the one chance to write down that the ending was EXPECTED is now. Without it
            // a deliberate restart is stored as an interruption, which is the same trace a crash
            // leaves. Fire and forget for the same reason: waiting on a disk write inside a shutdown
            // notification is how an app becomes the thing that delays somebody's shutdown. Ref: #93.
            _ = _runStateStore.NoteSystemEndingAsync(endingRunId, DateTimeOffset.UtcNow);
        }

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
            // THE FINALISATION IN FLIGHT IS CANCELLED WHETHER OR NOT THE INTERRUPTION IS QUEUED. A lock
            // that lands while the exit is draining settings must still stop a transcription the exit
            // is about to tear down under; the interruption itself is refused once leaving has begun.
            _sessionCoordinator?.CancelProcessing();
            if (!_exitRequested && !_disposed && _sessionCoordinator is { } coordinator)
            {
                _ = coordinator.InterruptAsync(transition);
            }
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

    private void OnRecoveryCleared() => _sessionPersistence.ForgetPendingRecovery();

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

        // THE SESSION IS HELD FOR THE WHOLE CHECK, so a press during the download is Busy rather than
        // a recording under an update; before the coordinator exists there is nothing to hold.
        var hold = _sessionCoordinator is { } coordinator ? coordinator.TryHold() : NoScope.Instance;
        if (hold is null)
        {
            _window?.SetUpdateStatus(new UpdateOperationResult(UpdateOperationStatus.BusyDictating));
            return;
        }

        using (hold)
        {
            _window?.SetUpdateCheckInProgress();
            var result = await _updateService.CheckDownloadAndVerifyAsync().ConfigureAwait(true);
            // THE APP MAY HAVE LEFT WHILE THE DOWNLOAD RAN; the window is not told anything then.
            if (!_exitRequested && !_disposed)
            {
                _window?.SetUpdateStatus(result);
            }
        }
    }

    private async void OnUpdateApplyRequested()
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        // THE SESSION IS HELD THROUGH THE ATTEMPT, and the leaving flag is set before it is given
        // back: a press admitted between the two would have opened a microphone under a restart. If the
        // restart does not happen the flag comes off and the hold goes back, and dictation resumes.
        var hold = _sessionCoordinator is { } coordinator ? coordinator.TryHold() : NoScope.Instance;
        if (hold is null)
        {
            _window?.SetUpdateStatus(new UpdateOperationResult(UpdateOperationStatus.BusyDictating));
            return;
        }

        using (hold)
        {
            _exitRequested = true;
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

            // ADMISSION CLOSES INSIDE THE HOLD. The restart is going to happen; a key that lands between
            // the hold going back and the exit path closing admission would otherwise be admitted.
            _sessionCoordinator?.Close();
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

    /// <summary>
    /// Leaving, shared by every path out - the tray, the window, an update, a system ending: admission
    /// closes before the first await and the exit budget starts; the settings write finishes; the shell
    /// closes its windows; then the exit under the one budget - the session shut down by its owner,
    /// what it used disposed only once nothing uses it, the run completed only when everything
    /// finished, and the host ended by force when something would not. A second caller shares the
    /// first's task; the window closing behind the first is one such caller.
    /// </summary>
    private Task<ExitReport> PrepareForExitAsync()
    {
        // ONE SCOPE FOR THE WHOLE TEARDOWN, rather than one per line found. Quitting mid-recording
        // is a fact about that recording, and everything written on the way out - the shell closing,
        // the preview stopping, whatever a teardown step logs - belongs to it. The lifetime's flow
        // captures the scope at the call and keeps it after this method lets it go.
        using var dictation = _sessionController?.CurrentSession is { } recording
            ? DictationScope.Begin(recording.Id.Value)
            : NoScope.Instance;
        _disposed = true;
        return Lifetime().ExitAsync();
    }

    /// <summary>The exit, reached as a disposal: the same shared task the exit paths await.</summary>
    public async ValueTask DisposeAsync()
    {
        await PrepareForExitAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The lifetime, built once; its parts read the shell's fields at the step, not at the build.</summary>
    private ApplicationLifetime Lifetime() =>
        _lifetime ??= new ApplicationLifetime(LifetimeParts(), _logger, TimeProvider.System, new HostTerminator());

    /// <summary>
    /// The shell's steps of leaving, in the order the lifetime runs them. What each touches is the
    /// shell's; the order, the budget and the retention are the lifetime's.
    /// </summary>
    /// <remarks>
    /// WHAT THE SESSION USES IS LISTED UNDER DisposeSessionDependencies AND DisposeLast, AND NOWHERE
    /// ELSE. A command or a background loop the session's shutdown reported still running is inside
    /// the engines, the polish provider, the stores; the lifetime runs those two lists only behind a
    /// quiescent session with nothing outstanding - a gate reads this method for that. The run-state
    /// store is last because the heartbeat, joined under Quiesce, writes to it too.
    /// </remarks>
    private LifetimeParts LifetimeParts() => new(
        CloseAdmission: () => _sessionCoordinator?.Close(),
        DrainSettings: () => _window?.DrainSettingsAsync() ?? Task.CompletedTask,
        ShellClosing: () =>
        {
            _window?.ShutdownProductWindows();
            (_polishProvider as EgOnePolishProvider)?.TerminateRuntimeImmediately();
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ShellClosed));
        },
        // THE EXIT POLICY: a transcription in flight is cut short rather than waited for, and so is the
        // polish warm-up; the session's shutdown then waits for the cut to land. Cancellation is not
        // quiescence - the shutdown's report says whether it landed.
        CancelProcessing: () =>
        {
            _sessionCoordinator?.CancelProcessing();
            _polishLifetime.Cancel();
        },
        ReleaseInputs:
        [
            LifetimeStep.Of("lifecycle monitor", () =>
            {
                if (_lifecycleMonitor is { } monitor)
                {
                    monitor.Transitioned -= OnSystemLifecycleTransitioned;
                    _lifecycleMonitor = null;
                    monitor.Dispose();
                }
            }),
            new LifetimeStep("push-to-talk hook", async () =>
            {
                if (_pushToTalkHook is { } hook)
                {
                    hook.Signalled -= OnPushToTalkSignalled;
                    _pushToTalkHook = null;
                    await hook.DisposeAsync().ConfigureAwait(true);
                }
            }),
        ],
        ShutDownSession: _sessionCoordinator is { } coordinator ? budget => coordinator.ShutdownAsync(budget) : null,
        Quiesce:
        [
            new LifetimeStep("polish warm-up", async () =>
            {
                if (_polishWarmup is { } warmup)
                {
                    _polishWarmup = null;
                    try
                    {
                        await warmup.ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        // The exit cancels an in-flight readiness probe; a cancelled probe is over.
                    }
                }
            }),
            new LifetimeStep("heartbeat", async () =>
            {
                var cancellation = Interlocked.Exchange(ref _heartbeatCancellation, null);
                var loop = Interlocked.Exchange(ref _heartbeatLoop, null);
                cancellation?.Cancel();
                if (loop is not null)
                {
                    try
                    {
                        await loop.ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                cancellation?.Dispose();
            }),
        ],
        DisposeSessionDependencies:
        [
            // A shell that never composed a session tears its controller down here, in the session's
            // place; one that did has had the coordinator do it under the session.
            new LifetimeStep("session teardown", async () =>
            {
                if (_sessionCoordinator is null)
                {
                    await TearDownSessionAsync().ConfigureAwait(true);
                }

                if (!_sessionTornDownCleanly)
                {
                    throw new InvalidOperationException("The session teardown reported a failure.");
                }
            }),
            new LifetimeStep("preview engine", async () =>
            {
                if (_previewEngine is { } engine)
                {
                    _previewEngine = null;
                    await engine.DisposeAsync().ConfigureAwait(true);
                }
            }),
            new LifetimeStep("transcription engine", async () =>
            {
                if (_transcriptionEngine is { } engine)
                {
                    _transcriptionEngine = null;
                    await engine.DisposeAsync().ConfigureAwait(true);
                }
            }),
            new LifetimeStep("polish provider", async () =>
            {
                if (_polishProvider is { } provider)
                {
                    _polishProvider = null;
                    await provider.DisposeAsync().ConfigureAwait(true);
                }
            }),
            LifetimeStep.Of("worker arbiter", _resourceArbiter.Dispose),
            LifetimeStep.Of("polish lifetime", _polishLifetime.Dispose),
            new LifetimeStep("live preview", () => _livePreview.DisposeAsync().AsTask()),
            new LifetimeStep("watchdog", () => _watchdog.DisposeAsync().AsTask()),
            new LifetimeStep("auto-stop", () => _autoStop.DisposeAsync().AsTask()),
            LifetimeStep.Of("history store", _historyStore.Dispose),
            LifetimeStep.Of("recovery store", _recoveryTextStore.Dispose),
        ],
        DisposeShell:
        [
            new LifetimeStep("activation channel", async () =>
            {
                if (_activationChannel is { } channel)
                {
                    channel.ActivationRequested -= OnDuplicateActivationRequested;
                    _activationChannel = null;
                    await channel.DisposeAsync().ConfigureAwait(true);
                }
            }),
            LifetimeStep.Of("tray icon", () =>
            {
                var tray = _trayIcon;
                _trayIcon = null;
                tray?.Dispose();
            }),
            new LifetimeStep("session coordinator", async () =>
            {
                if (_sessionCoordinator is { } stopped)
                {
                    _sessionCoordinator = null;
                    await stopped.DisposeAsync().ConfigureAwait(true);
                }
            }),
        ],
        CompleteRun: () => _runId is { } runId
            ? _runStateStore.CompleteRunAsync(runId, DateTimeOffset.UtcNow)
            : Task.FromResult(true),
        DisposeLast:
        [
            LifetimeStep.Of("single-instance lock", () =>
            {
                var singleInstance = _singleInstanceLock;
                _singleInstanceLock = null;
                singleInstance?.Dispose();
            }),
            LifetimeStep.Of("run-state store", _runStateStore.Dispose),
        ],
        DisposeLogger: () => _logger.DisposeAsync().AsTask());

    /// <summary>
    /// Ends the process when the exit could not: something would not finish inside the budget and is
    /// still running, holding whatever it holds; the log has said so and been closed; the next launch
    /// reads an interrupted run. The exit code is the escalation's own, so a harness can tell it apart.
    /// </summary>
    private sealed class HostTerminator : IHostTerminator
    {
        public const int ExitCode = 70;

        public void Terminate(ExitReport report) => Environment.Exit(ExitCode);
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
        // THE COORDINATOR OWNS THE SESSION. The watchdog's timeout and Windows locking or suspending
        // are commands on its queue; the update check holds the session through it; shutdown stops it
        // and needs no gate of its own. Built beside the controller so a press captures its target and
        // delivery choice from the same provider the controller would have asked, at the instant of
        // the key, before the queue's first hop.
        // THE JOINS ARE ONE FILE'S, SHARED WITH THE TESTS. The shell chooses the concrete things and
        // supplies leaf reads and sinks; SessionComposition builds the runner, the executor and the
        // coordinator the same way for the app and for a test that drives them without a window.
        _sessionCoordinator = SessionComposition.Compose(new SessionCompositionParts(
            _sessionController,
            audioCapture,
            _runtime,
            _resourceProbe,
            _runStateStore,
            _logger,
            new SessionShell(
                new WindowSessionView(this),
                AttachedSession: () => _sessionController?.CurrentSession,
                Dictation: () => _settings.Preferences.Dictation,
                Engine: () => _transcriptionEngine,
                Delivery: () => _textDelivery,
                Options: () => new FinalizationOptions(_customWords, _deterministicTextOptions, CurrentPolishSetup()),
                CloudPolishProviderName: () => _cloudPolishConsent?.ProviderName,
                RunId: () => _runId,
                RecordingActive: active => _pushToTalkHook?.SetRecordingActive(active),
                ArchiveAudio: audio => ArchiveDictationAudio(audio),
                TearDownSession: TearDownSessionAsync),
            TimeProvider.System));
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

    /// <summary>Drives the meters from a synthetic ramp, with no microphone in the loop.</summary>
    /// <remarks>
    /// EVERY MEASUREMENT OF A FLAT METER SO FAR IS ENTANGLED WITH AN ACOUSTIC PATH NOBODY CONTROLS.
    /// A person speaks, a room absorbs, a device gains, a driver converts, and only then does a
    /// number reach the code - so a flat meter could be any link in that chain. This replaces the
    /// whole chain with a number that is known, which separates "the meters cannot draw" from "the
    /// capture cannot hear" in one run rather than by argument.
    ///
    /// A RAMP RATHER THAN A CONSTANT, because a constant proves only that one value renders. A ramp
    /// climbing from silence to full and back proves the scale, the direction, and on the recording
    /// rail the scroll as well: the shape has to appear as a shape.
    ///
    /// IT GOES THROUGH THE SAME DOOR AS A REAL LEVEL, which is the entire point. Anything that drew
    /// bars directly would prove the bars and nothing between here and them.
    ///
    /// UAT ONLY, and it says so by living behind the same environment variable convention as every
    /// other harness switch in this app. It writes nothing, records nothing, and touches no setting.
    /// </remarks>
    private void StartSyntheticLevelRampIfRequested()
    {
        if (Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_LEVEL_RAMP") is not "1")
        {
            return;
        }

        var window = _window;
        if (window is null)
        {
            return;
        }

        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        var step = 0;
        timer.Tick += (_, _) =>
        {
            // Two seconds up, two seconds down, forever. Eighty ticks a cycle at fifty milliseconds.
            var phase = step++ % 80;
            var climb = phase < 40 ? phase / 40f : (80 - phase) / 40f;

            // FROM THE FLOOR TO FULL SCALE IN THE UNIT THE METER SPEAKS, so the ramp exercises the
            // real curve rather than sidestepping it.
            var level = climb <= 0f ? 0f : MathF.Pow(10f, (RecordingLevelHistory.FloorDecibels * (1f - climb)) / 20f);
            window.SetAudioLevel(new AudioLevel(level, level));
        };
        timer.Start();
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
            _window?.SetSessionStatus(DictationStatus
                .Quiet("Local transcription disabled for performance UAT")
                .AboutTheTranscriptionEngine());
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

        var modelDirectory = await ResolveModelDirectoryAsync(engine == FinalAsrEngine.Whisper
            ? WhisperTranscriptionEngine.ModelId
            : ParakeetTranscriptionEngine.ModelId).ConfigureAwait(true);
        if (modelDirectory is null)
        {
            _window?.SetSessionStatus(
                DictationStatus.Advisory(
                        "Local transcription model is not installed", StatusActions.OpenTranscription)
                    .AboutTheTranscriptionEngine());
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionFailed,
                AppFailureCategory.AsrUnavailable));
            return;
        }

        var hardware = await new WindowsHardwareDiscovery(_cudaRuntimeDirectory)
            .ProbeAsync()
            .ConfigureAwait(true);
        var workerExecutable = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        var previewModelDirectory = await ResolveModelDirectoryAsync(
            WhisperTranscriptionEngine.PreviewModelId,
            "ENVIOUSWISPR_PREVIEW_MODEL_DIRECTORY").ConfigureAwait(true);
        // THE SELECTION IS MADE BEFORE IT IS OBSERVED. This line used to be written above the two
        // Create calls, so it could only ever report the hardware and never what the hardware was
        // used FOR - which is why a machine transcribing on the processor beside an idle graphics
        // card had nothing anywhere saying whether the card was absent, refused, or chosen and
        // broken. Ref: #102.
        _transcriptionEngine = engine == FinalAsrEngine.Whisper
            ? CreateWhisperEngine(
                workerExecutable, modelDirectory, hardware, whisperLanguage, out var selectionReason)
            : CreateParakeetEngine(workerExecutable, modelDirectory, hardware, out selectionReason);
        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.RuntimeSelectionObserved,
            Engine: engine == FinalAsrEngine.Whisper
                ? DiagnosticEngineChoice.Whisper
                : DiagnosticEngineChoice.Parakeet,
            HardwareClass: DiagnosticHardwareClassFor(hardware),
            RuntimeSelection: selectionReason));
        if (_transcriptionEngine is null)
        {
            _window?.SetSessionStatus(
                DictationStatus.Advisory(
                        "Local transcription is unavailable on this machine", StatusActions.OpenTranscription)
                    .AboutTheTranscriptionEngine());
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
                            "Local transcription could not start", StatusActions.OpenTranscription)
                        .AboutTheTranscriptionEngine());
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.DictationTranscriptionFailed,
                    AppFailureCategory.RuntimeWorker));
                return;
            }

            ConfigureLivePreview(workerExecutable, hardware, whisperLanguage, previewModelDirectory, forceCpu: true);
            // THE MEMBER NO SELECTOR CAN PRODUCE. Getting here means the selection SUCCEEDED and the
            // runtime then refused to start, which the selector cannot see and therefore cannot
            // report. Measured on the development machine: the graphics libraries were missing, the
            // card was chosen, the start failed, the processor took over, and dictation ran about a
            // hundred times slower for days with nothing saying so. Ref: #102.
            var degradedReason =
                selectionReason == DiagnosticRuntimeSelectionReason.GpuSelected
                    ? DiagnosticRuntimeSelectionReason.ProcessorSelectedAfterGpuFailedToStart
                    : selectionReason;
            // ADVISORY, NOT QUIET, AND ONLY WHERE THE CARD IS ACTUALLY THE STORY. This is the user's
            // setup needing attention rather than the app breaking, so it takes the advisory pill and
            // carries the consequence rather than the mechanism - a person who reads "ready on the
            // processor" has been told a fact about our plumbing and nothing they can act on. Where
            // the run was never going to be on a card, there is no consequence to report and the
            // quiet line stands.
            _window?.SetSessionStatus(
                (degradedReason == DiagnosticRuntimeSelectionReason.ProcessorSelectedAfterGpuFailedToStart
                    ? DictationStatus.Advisory(
                        "Your graphics card did not start, so dictation is slower", StatusActions.OpenTranscription)
                    : DictationStatus.Quiet("Local transcription ready on the processor"))
                    .AboutTheTranscriptionEngine());
            _logger.Write(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationTranscriptionDegraded,
                AppFailureCategory.RuntimeProvider,
                ErrorCode: AppErrorCode.RuntimeProviderUnavailable,
                RuntimeSelection: degradedReason));
            return;
        }

        ConfigureLivePreview(workerExecutable, hardware, whisperLanguage, previewModelDirectory);
        _window?.SetSessionStatus(
            DictationStatus.Quiet("Local transcription ready").AboutTheTranscriptionEngine());
    }

    private void ConfigureLivePreview(
        string workerExecutable,
        HardwareSnapshot hardware,
        string language,
        string? modelDirectory,
        bool forceCpu = false)
    {
        if (modelDirectory is null ||
            !new LocalWhisperModelProbe().Probe(modelDirectory).PreviewSmallComplete)
        {
            _previewUnavailableReason = AppErrorCode.ModelPackUnavailable;
            return;
        }

        // ASKS ABOUT whisper.cpp NOW, WHICH IS WHAT LIVE PREVIEW ACTUALLY RUNS. This used to require
        // onnxruntime's CUDA dependency set as well - a probe about the library PARAKEET uses - so a
        // machine whose card works for whisper.cpp was put on the processor because a different
        // library's files were absent. The rule lives beside the engine it describes and is tested
        // there. Ref: #99.
        var provider = WhisperPreviewRuntime.Select(hardware, forceCpu);
        var threads = Math.Clamp(
            hardware.PhysicalCoreCount > 0
                ? hardware.PhysicalCoreCount
                : Math.Max(1, hardware.LogicalProcessorCount / 2),
            2,
            8);
        _previewUnavailableReason = null;
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
        HardwareSnapshot hardware,
        out DiagnosticRuntimeSelectionReason reason)
    {
        var models = new LocalParakeetModelProbe().Probe(modelDirectory);
        var selection = ParakeetRuntimeSelector.Select(hardware, models);
        var cpuSelection = ParakeetRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        // REPORTED ON BOTH EXITS, WHICH IS THE POINT OF THE OUT PARAMETER. The failing path is the
        // one nobody could read: returning null told the caller only that there was no engine.
        reason = DiagnosticRuntimeSelectionReasons.From(selection);
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
        string language,
        out DiagnosticRuntimeSelectionReason reason)
    {
        var models = new LocalWhisperModelProbe().Probe(modelDirectory);
        var selection = WhisperRuntimeSelector.Select(hardware, models);
        var cpuSelection = WhisperRuntimeSelector.Select(
            hardware,
            models,
            RuntimeProviderPreference.Cpu);
        reason = DiagnosticRuntimeSelectionReasons.From(selection);
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

    // MOVED TO CORE, UNCHANGED IN BEHAVIOUR, so the harnesses that judge this app can ask the same
    // question and get the same answer. They used to read the environment variable alone, so on a
    // machine with the runtime provisioned and the variable unset the app ran on the card while the
    // gate reported that CUDA could not load. Ref: #129.
    private static string? ResolveCudaRuntimeDirectory(string dataDirectory) =>
        CudaRuntimeDirectory.ForApplication(dataDirectory);

    private void OnPushToTalkSignalled(object? sender, PushToTalkSignalEvent args)
    {
        if (args.Signal == PushToTalkSignal.QuickAdd)
        {
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.QuickAddRequested));
            _ = HandleQuickAddAsync();
            return;
        }

        _ = _runtime.SubmitAsync(args.Signal);
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
            isDeliveryInFlight: _sessionCoordinator?.IsProcessing == true);

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
                // A HARNESS THAT DRIVES THE REAL HOTKEY NEVER SIGNALS START. It presses the key itself
                // and asks this method for one thing only: a clean exit it can trigger once the log and
                // the target have said the take landed. Without that, the only exit left is this wait
                // timing out, and a slow transcription would then end the app mid-journey and read as
                // omitted stages - a harness limit reported as a product defect. The exit event is
                // optional and lives in the same allowlisted scheme as START; when it is configured the
                // window is long enough that the harness, not this constant, decides when the run ends.
                TryOpenJourneyUatEvent("ENVIOUSWISPR_UAT_JOURNEY_EXIT_EVENT", out var exitEvent);
                using (exitEvent)
                {
                    var started = await Task.Run(() => exitEvent is null
                            ? startEvent.WaitOne(TimeSpan.FromSeconds(30))
                            : WaitHandle.WaitAny(
                                [startEvent, exitEvent],
                                TimeSpan.FromSeconds(120)) == 0)
                        .ConfigureAwait(false);
                    if (!started || _exitRequested || _disposed)
                    {
                        return;
                    }
                }
            }

            await _runtime.SubmitAsync(PushToTalkSignal.Pressed).ConfigureAwait(false);
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
            await _runtime.SubmitAsync(stopSignal).ConfigureAwait(false);
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

    /// <summary>The window as the long-lived session owners see it: each sink one dispatch to the window, and nothing decided here.</summary>
    private sealed class WindowRuntimeView(App app) : IRuntimeView
    {
        public void ShowPreview(LivePreviewFrame? frame) =>
            app._window?.DispatcherQueue.TryEnqueue(() =>
            {
                // ASKED AT THE DRAW. A frame queued before the preview closed is drawn after; the
                // frame knows its screen is gone and the clear that closed it is left standing.
                if (frame is null || frame.IsCurrent())
                {
                    app._window?.SetLivePreview(frame?.Text);
                }
            });

        public void ShowRecoveredText(RecoveryTextLoadResult result) =>
            app._window?.DispatcherQueue.TryEnqueue(() => app._window?.SetRecoveredText(result));

        public void ClearRecoveredText() =>
            app._window?.DispatcherQueue.TryEnqueue(() => app._window?.ClearRecoveredText());

        public void NotifyHistoryChanged() =>
            app._window?.DispatcherQueue.TryEnqueue(() =>
            {
                if (app._window is not null)
                {
                    _ = app._window.NotifyHistoryChangedAsync();
                }
            });

        public void ShowMainWindow() => app.ShowMainWindow(openSettings: false);
    }

    /// <summary>The window as the session sees it: each sink one dispatch to the window, and nothing decided here.</summary>
    private sealed class WindowSessionView(App app) : ISessionView
    {
        public void ShowStatus(DictationStatus status) =>
            app._window?.DispatcherQueue.TryEnqueue(() => app._window?.SetSessionStatus(status));

        public void ShowNotice(string title, string message, bool isError = false) =>
            app._window?.DispatcherQueue.TryEnqueue(() => app._window?.SetReliabilityNotice(title, message, isError));

        public void ShowMainWindow() => app.ShowMainWindow(openSettings: false);

        public void ReportDelivery(DictationStatus delivered, string? detectedLanguage) =>
            app._window?.DispatcherQueue.TryEnqueue(() =>
                app._window?.ReportDeliveryAndMaybeOfferLanguage(delivered, detectedLanguage));
    }

    /// <summary>
    /// The session-specific disposal, run by the coordinator after its last command: the timers,
    /// streaming and the preview stopped; the capture let go of; the session controller and the
    /// delivery route disposed. The engines and the rest of the shell follow in the shell.
    /// </summary>
    private async Task TearDownSessionAsync()
    {
        // THE BACKGROUND WORK IS THE EXECUTOR'S TO STOP, under the shutdown's deadline, before this is
        // reached; what is left here is the shell's own: the capture's event, the controller, the
        // delivery route.
        var clean = true;
        if (_audioCapture is not null)
        {
            _audioCapture.LevelChanged -= OnAudioLevelChanged;
        }

        if (_sessionController is not null)
        {
            clean &= await TryCleanupAsync(
                async () => await _sessionController.DisposeAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            _sessionController = null;
            _audioCapture = null;
        }

        if (_textTargetAdapter is not null)
        {
            clean &= TryCleanup(_textTargetAdapter.Dispose);
        }

        _textTargetAdapter = null;
        _textDelivery = null;
        _sessionTornDownCleanly = clean;
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

    /// <summary>The polish provider in force, and how it is hosted, or null when there is none.</summary>
    private PolishSetup? CurrentPolishSetup() =>
        _polishProvider is { } provider
            ? new PolishSetup(provider, _polishUsesLocalRuntime, _polishResource)
            : null;

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

    private static AppFailureCategory FailureFor(AppError? error) => AppFailureCategories.For(error);

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
