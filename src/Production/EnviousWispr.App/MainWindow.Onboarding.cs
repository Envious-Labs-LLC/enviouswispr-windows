using System.Globalization;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>13-screen Fluent 2 onboarding walkthrough, checkpoint resume, key rehearsal, and dual model delivery.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan MicrophonePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RehearsalHoldThreshold = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan RehearsalDoubleTapWindow = TimeSpan.FromMilliseconds(480);

    private readonly OnboardingFlow _onboarding = new();
    private readonly DispatcherTimer _onboardingBeat = new();
    private readonly DispatcherTimer _onboardingMicrophonePoll = new() { Interval = MicrophonePollInterval };
    private readonly DispatcherTimer _onboardingKeyHoldTimer = new() { Interval = RehearsalHoldThreshold };

    private OnboardingCheckpointStore? _onboardingCheckpointStore;
    private Task _checkpointWriteQueue = Task.CompletedTask;
    private bool _onboardingStateLoaded;
    private bool _onboardingCheckpointSuppress;

    private string _onboardingEngineSentence = OnboardingCopy.StartingEngine;
    private SpeechEngineReadiness _latestEngineReadiness = SpeechEngineReadiness.Preparing;
    private FinalAsrEngine? _latestEngineReadinessEngine;
    private bool _finishingOnboarding;
    private ModelDeliveryPresentation? _onboardingModel;
    private MicrophoneReadiness? _onboardingMicrophone;
    private MicrophoneConsent _onboardingMicConsent = MicrophoneConsent.Unknown;
    private WindowsSetupRecommendation? _onboardingRecommendation;
    private OnboardingDownloadSnapshot? _onboardingDownloadSnapshot;
    private bool _cudaRuntimeAvailable;

    private OnboardingLanguageRow[] _onboardingAllLanguageRows = [];
    private Border[] _onboardingProgressSegments = [];
    private Button[] _onboardingClosingDotButtons = [];
    private string? _suggestedSystemLanguageCode;

    private OnboardingStep? _drawnStep;
    private OnboardingPracticeState? _drawnPractice;
    private OnboardingGateDecision? _drawnGate;
    private bool _onboardingFocusOrphaned;

    private PolishProvider _onboardingCloudProvider = PolishProvider.OpenAI;
    private int _onboardingCloudCheckGeneration;
    private (string SpeechModelId, string? PolishModelId)? _activeRequestedDownloadPair;
    private bool _onboardingSeeAllCloudTab;
    private bool _onboardingIsUpdatingLanguageUi;
    private bool _onboardingIsUpdatingCards;
    private bool _onboardingIsUpdatingCloudModels;

    private WindowsKeybindCaptureSession? _onboardingKeyCaptureSession;
    private bool _onboardingRehearsalKeyDown;
    private bool _onboardingHoldRehearsed;
    private bool _onboardingDoubleTapRehearsed;
    private DateTimeOffset? _onboardingLastTapUpUtc;
    private bool _onboardingClosingPaused;
    private bool _onboardingLipsAnimating;
    private bool _dictationActive;
    private string? _lastLanguageQuery;
    private string? _lastSelectedLanguageCode;

    /// <summary>Raised when setup asks the speech engine to re-initialize after failure.</summary>
    public event Action? TranscriptionRetryRequested;

    /// <summary>Raised when a live dictation finishes on the window so pending model switches can drain.</summary>
    public event Action? DictationEnded;

    /// <summary>Raised when onboarding asks to provision Speech and optional EG-1 models concurrently or sequentially.</summary>
    public event Action<string, string?, bool>? OnboardingDualDownloadRequested;

    /// <summary>Raised when onboarding cancels active dual downloads.</summary>
    public event Action? OnboardingDualDownloadCancelRequested;

    /// <summary>Raised when onboarding drops EG-1 so speech downloads can continue unaffected.</summary>
    public event Action? OnboardingDualDownloadSkipPolishRequested;

    /// <summary>Raised when onboarding retries failed/cancelled dual downloads.</summary>
    public event Action? OnboardingDualDownloadRetryRequested;

    /// <summary>Raised true while onboarding Screen 9 is actively listening for key rehearsal gestures.</summary>
    public event Action<bool>? OnboardingKeyRehearsalActiveChanged;

    /// <summary>Validates, persists, and rebinds the global recording key immediately during setup.</summary>
    public Func<string, DictationRecordingMode, Task<(bool Applied, string? Error)>>? ApplyOnboardingRecordingKey { get; set; }

    /// <summary>Applies selected speech/polish engines in the background when skipping the Downloads screen.</summary>
    public Func<FinalAsrEngine, OnboardingPolishChoiceKind, string?, string?, Task>? ApplyOnboardingEnginesChoice { get; set; }

    /// <summary>Refreshes on-disk model probe state before deciding whether the Downloads screen can be skipped.</summary>
    public Func<FinalAsrEngine, string?, Task<(bool SpeechInstalled, bool PolishInstalled)>>? ProbeOnboardingModelsInstalled { get; set; }

    internal static SpeechModelDelivery ToSpeechModelDelivery(OnboardingDownloadStage stage) => stage switch
    {
        OnboardingDownloadStage.Pending => SpeechModelDelivery.Missing,
        OnboardingDownloadStage.Downloading => SpeechModelDelivery.Downloading,
        OnboardingDownloadStage.Activating => SpeechModelDelivery.Activating,
        OnboardingDownloadStage.Installed => SpeechModelDelivery.Installed,
        OnboardingDownloadStage.Cancelled => SpeechModelDelivery.Cancelled,
        OnboardingDownloadStage.Failed => SpeechModelDelivery.Failed,
        OnboardingDownloadStage.CannotDownload => SpeechModelDelivery.CannotDownload,
        _ => SpeechModelDelivery.Unknown,
    };

    internal void RequestOnboardingDualDownload(string speechModelId, string? polishModelId, bool parallelDownloads) =>
        OnboardingDualDownloadRequested?.Invoke(speechModelId, polishModelId, parallelDownloads);

    internal void CancelOnboardingDualDownload()
    {
        _activeRequestedDownloadPair = null;
        OnboardingDualDownloadCancelRequested?.Invoke();
    }

    internal void SkipOnboardingPolishDownload()
    {
        if (_activeRequestedDownloadPair is { } pair)
        {
            _activeRequestedDownloadPair = (pair.SpeechModelId, null);
        }

        if (_onboardingDownloadSnapshot is { Polish: not null } snap)
        {
            var updatedPlan = snap.Plan with
            {
                PolishModelId = null,
                PolishTotalBytes = 0,
                PolishAlreadyInstalled = true,
                PolishDiskBytes = 0,
            };
            var speechReady = snap.Speech.Stage == OnboardingDownloadStage.Installed;
            var speechTotal = Math.Max(snap.Speech.TotalBytes, snap.Speech.CompletedBytes);
            var speechDone = speechReady ? speechTotal : snap.Speech.CompletedBytes;
            var percent = speechTotal > 0 ? Math.Clamp(100.0 * speechDone / speechTotal, 0d, 100d) : (speechReady ? 100d : 0d);
            var provisional = snap with
            {
                Plan = updatedPlan,
                Polish = null,
                CombinedPercent = percent,
                IsPolishReady = true,
                FailureMessage = snap.Speech.Stage is OnboardingDownloadStage.Failed
                    or OnboardingDownloadStage.CannotDownload
                    or OnboardingDownloadStage.Cancelled
                    ? snap.Speech.StatusDetail
                    : null,
            };
            _onboardingDownloadSnapshot = provisional with
            {
                BackgroundPillSummary = OnboardingDownloadCoordinator.FormatBackgroundPillSummary(provisional),
            };
        }

        OnboardingDualDownloadSkipPolishRequested?.Invoke();
    }

    internal void RetryOnboardingDualDownload() =>
        OnboardingDualDownloadRetryRequested?.Invoke();

    internal void ObserveOnboardingDownloadSnapshot(OnboardingDownloadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _onboardingDownloadSnapshot = snapshot;
        _onboarding.ReportModel(ToSpeechModelDelivery(snapshot.Speech.Stage));
        if (snapshot.Polish is { } polish)
        {
            _onboarding.ReportPolishModel(ToSpeechModelDelivery(polish.Stage));
        }

        if (_onboarding.Step == OnboardingStep.GettingReady && _onboarding.TryAdvanceFromGate())
        {
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    public void SetTranscriptionReadiness(SpeechEngineReadiness readiness, FinalAsrEngine? engine = null)
    {
        _latestEngineReadiness = readiness;
        if (engine is { } knownEngine)
        {
            _latestEngineReadinessEngine = knownEngine;
        }

        SyncLiveInventoryToOnboarding();
        if (_onboarding.Step == OnboardingStep.GettingReady && _onboarding.TryAdvanceFromGate())
        {
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    public void SetDictationActive(bool active)
    {
        var ended = _dictationActive && !active;
        _dictationActive = active;
        if (active)
        {
            _audioPlayback.Stop();
        }

        if (RunSetupAgainButton is not null)
        {
            RunSetupAgainButton.IsEnabled = !active;
        }

        var practiceText = OnboardingPracticeBox?.Text ?? string.Empty;
        if (ended)
        {
            DictationEnded?.Invoke();
            _onboarding.TakeEnded(practiceText);
            if (_onboarding.PracticeState == OnboardingPracticeState.MissedTheBox
                && OnboardingView is { Visibility: Visibility.Visible }
                && OnboardingPracticeBox is not null)
            {
                OnboardingPracticeBox.Focus(FocusState.Programmatic);
            }
        }

        if (active)
        {
            _onboarding.TakeStarted(practiceText);
        }

        RenderOnboarding();
    }

    private void ObserveStatusForOnboarding(DictationStatus status)
    {
        if (OnboardingView is not { Visibility: Visibility.Visible })
        {
            return;
        }

        if (_onboarding.Step == OnboardingStep.Practice && OnboardingPracticeBox is not null)
        {
            if (status.State == DictationOverlayState.Recording && !_onboarding.TakeInFlight)
            {
                _onboarding.TakeStarted(OnboardingPracticeBox.Text);
            }
            else if (status.State is DictationOverlayState.Error or DictationOverlayState.Distress
                or DictationOverlayState.Advisory)
            {
                _onboarding.TakeFailed();
            }
        }

        RenderOnboarding();
    }

    private void ObserveDeliveryForOnboarding(DeliveryLanding landing)
    {
        _onboarding.TakeDelivered(OnboardingTakeDeliveries.For(landing));
        RenderOnboarding();
    }

    private void ObserveModelForOnboarding(ModelDeliveryPresentation presentation)
    {
        if (presentation.Stage == SpeechModelDelivery.Unknown)
        {
            return;
        }

        _onboardingModel = presentation;
        SyncLiveInventoryToOnboarding();
        if (_onboarding.Step == OnboardingStep.GettingReady && _onboarding.TryAdvanceFromGate())
        {
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    private void ObserveMicrophoneForOnboarding(MicrophoneReadiness readiness, MicrophoneConsent consent)
    {
        _onboardingMicrophone = readiness;
        _onboardingMicConsent = consent;
        _onboarding.ReportMicrophone(consent);
        if (_onboarding.Step == OnboardingStep.GettingReady && _onboarding.TryAdvanceFromGate())
        {
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    private void ObserveRecordingKeyForOnboarding(string gesture, DictationRecordingMode mode)
    {
        _onboarding.ReportRecordingKey(gesture, mode);
        RenderOnboarding();
    }

    private void ObserveRecordingKeyProblemForOnboarding(string problem)
    {
        // Secondary isolated UAT windows hold no global hook when a daily-driver owns it.
        // Keep the saved shortcut visible so onboarding key selection and focused rehearsal work cleanly.
        if (string.IsNullOrWhiteSpace(_onboarding.RecordingKey))
        {
            _onboarding.ReportRecordingKey(
                _settings.Preferences.Dictation.PushToTalkGesture,
                _settings.Preferences.Dictation.RecordingMode);
        }

        RenderOnboarding();
    }

    internal void ObserveOnboardingKeyRehearsalSignal(PushToTalkSignalEvent args)
    {
        if (OnboardingView is not { Visibility: Visibility.Visible } ||
            _onboarding.Step != OnboardingStep.Key ||
            _onboardingKeyCaptureSession is not null)
        {
            return;
        }

        if (args.Signal == PushToTalkSignal.Pressed)
        {
            if (args.IsHandsFree)
            {
                _onboardingRehearsalKeyDown = false;
                _onboardingKeyHoldTimer.Stop();
                _onboardingDoubleTapRehearsed = true;
                _onboarding.MarkKeyRehearsalCompleted();
            }
            else
            {
                _onboardingRehearsalKeyDown = true;
                _onboardingHoldRehearsed = true;
                _onboarding.MarkKeyRehearsalCompleted();
            }
        }
        else if (args.Signal == PushToTalkSignal.Released)
        {
            _onboardingRehearsalKeyDown = false;
            _onboardingKeyHoldTimer.Stop();
            _onboardingHoldRehearsed = true;
            _onboarding.MarkKeyRehearsalCompleted();
        }

        RenderOnboarding();
    }

    internal async Task InitializeOnboardingStateAsync()
    {
        EnsureOnboardingChromeBuilt();
        _onboardingCheckpointStore ??= OnboardingCheckpointStore.ForDataDirectory(_dataDirectory);
        _hardwareProfile = HardwareProfileSummary.GetCurrent();
        _suggestedSystemLanguageCode = SetupLanguageMetadata.ResolveSystemLanguageCode(CultureInfo.CurrentUICulture);
        _onboardingMicConsent = WindowsMicrophoneConsent.Read();
        _onboarding.ReportMicrophone(_onboardingMicConsent);

        var initialKey = string.IsNullOrWhiteSpace(_settings.Preferences.Dictation.PushToTalkGesture)
            ? DictationPreferences.Default.PushToTalkGesture
            : _settings.Preferences.Dictation.PushToTalkGesture;
        _onboarding.ReportRecordingKey(initialKey, _settings.Preferences.Dictation.RecordingMode);

        if (!_settings.HasCompletedOnboarding)
        {
            _onboardingCheckpointSuppress = true;
            try
            {
                var checkpointResult = await _onboardingCheckpointStore.LoadAsync().ConfigureAwait(true);
                var decision = _onboarding.Restore(checkpointResult.Checkpoint, hasCompletedOnboarding: false);
                var rec = ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
                _onboarding.ApplyRecommendation(rec, applyPrimaryChoices: !decision.ShouldResume);
                _onboardingRecommendation = rec;

                if (decision.ShouldResume && TryGetCloudProvider(_onboarding.Choices.PolishChoice, out var savedCloud))
                {
                    _onboardingCloudProvider = savedCloud;
                    _onboardingSeeAllCloudTab = true;
                }
            }
            finally
            {
                _onboardingCheckpointSuppress = false;
            }
        }

        _onboardingStateLoaded = true;
        SyncLiveInventoryToOnboarding();
        if (_onboarding.Step >= OnboardingStep.Downloads && _onboarding.Step < OnboardingStep.Closing)
        {
            StartOnboardingDownloadsIfNeeded();
            if (_onboarding.Model == SpeechModelDelivery.Installed &&
                _onboarding.Engine != SpeechEngineReadiness.Ready &&
                _onboarding.PolishModel == SpeechModelDelivery.Installed &&
                _settings.Preferences.Dictation.FinalEngine != _onboarding.Choices.SpeechEngine &&
                _onboardingDownloadSnapshot?.IsRunning != true &&
                ApplyOnboardingEnginesChoice is { } applyEngines)
            {
                _ = applyEngines(
                    _onboarding.Choices.SpeechEngine,
                    _onboarding.Choices.PolishChoice,
                    _onboarding.Choices.EgOneModelId,
                    _onboarding.Choices.CloudModelId);
            }
        }

        RenderOnboarding();
    }

    private void BeginOnboardingVisit()
    {
        EnsureOnboardingChromeBuilt();
        _hardwareProfile ??= HardwareProfileSummary.GetCurrent();
        _suggestedSystemLanguageCode ??= SetupLanguageMetadata.ResolveSystemLanguageCode(CultureInfo.CurrentUICulture);
        _onboardingMicConsent = WindowsMicrophoneConsent.Read();

        CancelOnboardingKeyCapture();
        _onboardingDownloadSnapshot = null;
        _activeRequestedDownloadPair = null;
        _onboardingCloudCheckGeneration++;
        _onboardingHoldRehearsed = false;
        _onboardingDoubleTapRehearsed = false;
        _onboardingRehearsalKeyDown = false;
        _onboardingLastTapUpUtc = null;
        _onboardingClosingPaused = false;
        _onboardingSeeAllCloudTab = false;
        _drawnStep = null;
        _drawnPractice = null;
        _drawnGate = null;

        _onboarding.Begin();
        _onboarding.ReportMicrophone(_onboardingMicConsent);
        var activeKey = string.IsNullOrWhiteSpace(_settings.Preferences.Dictation.PushToTalkGesture)
            ? DictationPreferences.Default.PushToTalkGesture
            : _settings.Preferences.Dictation.PushToTalkGesture;
        _onboarding.ReportRecordingKey(activeKey, _settings.Preferences.Dictation.RecordingMode);

        var rec = ComputeOnboardingRecommendation(LanguageCatalog.AutomaticCode);
        _onboardingRecommendation = rec;
        _onboarding.ApplyRecommendation(rec, applyPrimaryChoices: true);
        SyncLiveInventoryToOnboarding();

        if (OnboardingPracticeBox is not null)
        {
            OnboardingPracticeBox.Text = string.Empty;
        }

        if (OnboardingLanguageSearchBox is not null)
        {
            OnboardingLanguageSearchBox.Text = string.Empty;
        }
        _lastLanguageQuery = null;
        _lastSelectedLanguageCode = null;
        RenderOnboarding();
    }

    private void EnsureOnboardingChromeBuilt()
    {
        if (_onboardingProgressSegments.Length == 0 && OnboardingProgressGrid is not null)
        {
            OnboardingProgressGrid.ColumnDefinitions.Clear();
            OnboardingProgressGrid.Children.Clear();
            var height = (double)Application.Current.Resources["BrandOnboardingProgressSegmentHeight"];
            var radius = (CornerRadius)Application.Current.Resources["BrandOnboardingProgressSegmentRadius"];
            var segments = new Border[OnboardingFlow.PrimaryStepCount];
            for (var i = 0; i < OnboardingFlow.PrimaryStepCount; i++)
            {
                OnboardingProgressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var segment = new Border
                {
                    Height = height,
                    CornerRadius = radius,
                };
                Grid.SetColumn(segment, i);
                OnboardingProgressGrid.Children.Add(segment);
                segments[i] = segment;
            }

            _onboardingProgressSegments = segments;
        }

        if (_onboardingClosingDotButtons.Length == 0 && OnboardingClosingDotsPanel is not null)
        {
            OnboardingClosingDotsPanel.Children.Clear();
            var dotSize = (double)Application.Current.Resources["BrandOnboardingClosingDotSize"];
            var dotRadius = (CornerRadius)Application.Current.Resources["BrandOnboardingClosingDotRadius"];
            var quietStyle = Application.Current.Resources["BrandQuietButtonStyle"] as Style;
            var dots = new Button[OnboardingCopy.ClosingBeats.Count];
            for (var i = 0; i < OnboardingCopy.ClosingBeats.Count; i++)
            {
                var beatIndex = i;
                var button = new Button
                {
                    Tag = beatIndex,
                    Width = dotSize + 14,
                    Height = dotSize + 10,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    CornerRadius = dotRadius,
                    Style = quietStyle,
                    Content = new Border
                    {
                        Width = dotSize,
                        Height = dotSize,
                        CornerRadius = dotRadius,
                    },
                };
                AutomationProperties.SetName(button, $"Step {beatIndex + 1} of {OnboardingCopy.ClosingBeats.Count}");
                button.Click += OnboardingClosingDot_Click;
                OnboardingClosingDotsPanel.Children.Add(button);
                dots[i] = button;
            }

            _onboardingClosingDotButtons = dots;
        }

        if (_onboardingAllLanguageRows.Length == 0)
        {
            _onboardingAllLanguageRows = LanguageCatalog.PickerEntries
                .Select(entry => new OnboardingLanguageRow(
                    entry.Code,
                    entry.DisplayName,
                    entry.PickerSubtitle,
                    LanguageCatalog.IsSupported(FinalAsrEngine.Parakeet, entry.Code)
                        ? "Parakeet \u00B7 Whisper"
                        : "Whisper"))
                .ToArray();
        }
    }

    private WindowsSetupRecommendation ComputeOnboardingRecommendation(string? selectedLanguageCode) =>
        WindowsSetupRecommender.Recommend(BuildSetupHardwareSnapshot(selectedLanguageCode));

    private void SyncLiveInventoryToOnboarding()
    {
        var speechState = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper
            ? _whisperModelState
            : _parakeetModelState;

        if (speechState.IsInstalled)
        {
            _onboarding.ReportModel(SpeechModelDelivery.Installed);
            var selectedEngine = _onboarding.Choices.SpeechEngine;
            var readinessMatchesSelection = _settings.Preferences.Dictation.FinalEngine == selectedEngine &&
                _latestEngineReadinessEngine == selectedEngine;
            _onboarding.ReportEngine(readinessMatchesSelection
                ? _latestEngineReadiness
                : SpeechEngineReadiness.Preparing);
        }
        else if (speechState.DeliveryStage != SpeechModelDelivery.Unknown)
        {
            _onboarding.ReportModel(speechState.DeliveryStage);
            _onboarding.ReportEngine(speechState.DeliveryStage switch
            {
                SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating => SpeechEngineReadiness.Preparing,
                SpeechModelDelivery.Failed or SpeechModelDelivery.CannotDownload => SpeechEngineReadiness.Unavailable,
                _ => SpeechEngineReadiness.NotInstalled,
            });
        }
        else
        {
            _onboarding.ReportModel(SpeechModelDelivery.Missing);
            _onboarding.ReportEngine(SpeechEngineReadiness.NotInstalled);
        }

        if (_onboarding.Choices.PolishChoice == OnboardingPolishChoiceKind.EgOne)
        {
            var egOneId = EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId);
            var egState = ResolveEgOneDisplayState(egOneId);
            if (egState.IsInstalled)
            {
                _onboarding.ReportPolishModel(SpeechModelDelivery.Installed);
            }
            else if (egState.DeliveryStage != SpeechModelDelivery.Unknown)
            {
                _onboarding.ReportPolishModel(egState.DeliveryStage);
            }
            else
            {
                _onboarding.ReportPolishModel(SpeechModelDelivery.Missing);
            }
        }
        else
        {
            _onboarding.ReportPolishModel(SpeechModelDelivery.Installed);
        }
    }

    internal void OnboardingBackButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOnboardingKeyCapture();
        if (_onboarding.GoBack())
        {
            QueueOnboardingCheckpointSave();
            RenderOnboarding();
        }
    }

    internal void OnboardingDownloadPillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingDownloadSnapshot is null)
        {
            return;
        }

        var summary = OnboardingDownloadCoordinator.FormatBackgroundPillSummary(_onboardingDownloadSnapshot)
            ?? "Downloads complete";
        Announce(summary);
    }

    internal async void FinishOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOnboardingKeyCapture();
        switch (_onboarding.Step)
        {
            case OnboardingStep.Welcome:
                _onboarding.AdvanceFromWelcome();
                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;

            case OnboardingStep.Language:
                _onboarding.ContinueFromLanguage();
                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;

            case OnboardingStep.SpeechPick:
            case OnboardingStep.SpeechCompare:
                _onboarding.ContinueFromSpeechPick();
                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;

            case OnboardingStep.PolishIntro:
                _onboarding.ContinueFromPolishIntro();
                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;

            case OnboardingStep.PolishPick:
            case OnboardingStep.PolishSeeAll:
                await AdvanceFromPolishChoiceAsync().ConfigureAwait(true);
                break;

            case OnboardingStep.Downloads:
                _onboarding.ContinueFromDownloads();
                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;

            case OnboardingStep.Permissions:
                var consent = WindowsMicrophoneConsent.Read();
                _onboardingMicConsent = consent;
                if (_onboarding.ContinueFromPermissions(consent))
                {
                    QueueOnboardingCheckpointSave();
                }

                RenderOnboarding();
                break;

            case OnboardingStep.Key:
                SyncLiveInventoryToOnboarding();
                _onboarding.ContinueFromKey();
                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;

            case OnboardingStep.GettingReady:
                if (_onboarding.TryAdvanceFromGate())
                {
                    QueueOnboardingCheckpointSave();
                }
                else if (TrySkipPolishFromGettingReady())
                {
                    QueueOnboardingCheckpointSave();
                }
                else if (_onboarding.Model == SpeechModelDelivery.Installed &&
                         _settings.Preferences.Dictation.FinalEngine != _onboarding.Choices.SpeechEngine &&
                         _onboardingDownloadSnapshot?.IsRunning != true &&
                         ApplyOnboardingEnginesChoice is { } applySelected)
                {
                    _onboarding.RetrySetup();
                    _ = applySelected(
                        _onboarding.Choices.SpeechEngine,
                        _onboarding.Choices.PolishChoice,
                        _onboarding.Choices.EgOneModelId,
                        _onboarding.Choices.CloudModelId);
                }
                else
                {
                    _onboarding.RetrySetup();
                    RetryOnboardingDualDownload();
                }

                RenderOnboarding();
                break;

            case OnboardingStep.Practice:
                if (_onboarding.ContinueFromPractice())
                {
                    QueueOnboardingCheckpointSave();
                    RenderOnboarding();
                }
                else if (_onboarding.MayFinish(OnboardingExit.SkipPractice))
                {
                    _onboarding.SkipPractice();
                    QueueOnboardingCheckpointSave();
                    RenderOnboarding();
                }

                break;

            case OnboardingStep.Closing:
                await FinishOnboardingAsync().ConfigureAwait(true);
                break;
        }
    }

    internal async void OnboardingSkipButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOnboardingKeyCapture();
        switch (_onboarding.Step)
        {
            case OnboardingStep.PolishIntro:
                _onboarding.ContinueFromPolishIntro();
                _onboarding.SelectPolish(OnboardingPolishChoiceKind.None);
                await AdvanceFromPolishChoiceAsync().ConfigureAwait(true);
                break;

            case OnboardingStep.Practice:
                if (_onboarding.SkipPractice())
                {
                    QueueOnboardingCheckpointSave();
                    RenderOnboarding();
                }

                break;

            case OnboardingStep.GettingReady:
                _ = TrySkipPolishFromGettingReady();
                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;
        }
    }

    internal async void RunSetupAgainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dictationActive)
        {
            return;
        }

        if (!await UpdateSettingsAsync(OnboardingFlow.Restarted).ConfigureAwait(true))
        {
            ShowMessage(
                "Setup could not be restarted",
                "Your settings could not be saved just now. Nothing was changed; try again in a moment.",
                InfoBarSeverity.Warning);
            return;
        }

        await ClearOnboardingCheckpointAsync().ConfigureAwait(true);
        ShowOnboarding(show: true);
    }

    private async Task FinishOnboardingAsync()
    {
        if (_finishingOnboarding)
        {
            return;
        }

        _finishingOnboarding = true;
        FinishOnboardingButton.IsEnabled = false;
        try
        {
            StopOnboardingTimers();
            CancelOnboardingKeyCapture();
            OnboardingKeyRehearsalActiveChanged?.Invoke(false);

            var choices = _onboarding.Choices;
            var (whisperLanguage, englishSpelling) = LanguageCatalog.ApplyPickerSelection(
                choices.LanguageCode,
                _settings.Preferences.Dictation.EnglishSpelling);
            var polishProvider = choices.PolishChoice switch
            {
                OnboardingPolishChoiceKind.EgOne => PolishProvider.EgOne,
                OnboardingPolishChoiceKind.Ollama => PolishProvider.Ollama,
                OnboardingPolishChoiceKind.OpenAI => PolishProvider.OpenAI,
                OnboardingPolishChoiceKind.Claude => PolishProvider.Anthropic,
                OnboardingPolishChoiceKind.Gemini => PolishProvider.Gemini,
                _ => PolishProvider.None,
            };
            var egOneId = polishProvider == PolishProvider.EgOne
                ? EgOneModelCatalog.NormalizeOrDefault(choices.EgOneModelId)
                : _settings.Preferences.Polish.EgOneModelId;
            var polishModelId = polishProvider switch
            {
                PolishProvider.EgOne => egOneId,
                PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini => string.IsNullOrWhiteSpace(choices.CloudModelId)
                    ? (_session.Provider.RecommendedModel(polishProvider) ?? GetDefaultCloudModelFallback(polishProvider))
                    : choices.CloudModelId,
                _ => null,
            };
            var shortcut = !string.IsNullOrWhiteSpace(choices.HotkeyGestureText)
                ? choices.HotkeyGestureText!
                : _settings.Preferences.Dictation.PushToTalkGesture;

            var saved = await TrySaveAsync(
                current =>
                {
                    var completed = OnboardingFlow.Completed(current);
                    var recents = string.Equals(choices.LanguageCode, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase)
                        ? completed.RecentLanguages
                        : LanguageCatalog.RecordRecentLanguage(completed.RecentLanguages, choices.LanguageCode);
                    return completed with
                    {
                        RecentLanguages = recents,
                        Preferences = completed.Preferences with
                        {
                            Dictation = completed.Preferences.Dictation with
                            {
                                FinalEngine = choices.SpeechEngine,
                                WhisperLanguage = whisperLanguage,
                                EnglishSpelling = englishSpelling,
                                PushToTalkGesture = shortcut,
                                RecordingMode = choices.RecordingMode,
                            },
                            Polish = completed.Preferences.Polish with
                            {
                                Provider = polishProvider,
                                EgOneModelId = egOneId,
                                ModelId = polishModelId,
                            },
                        },
                    };
                },
                "Setup complete",
                "Your choices were saved on this PC.").ConfigureAwait(true);
            if (!saved)
            {
                ArmClosingBeatTimer();
                return;
            }

            if (_onboarding.Step == OnboardingStep.Closing)
            {
                _onboarding.CompleteClosing();
            }

            await ClearOnboardingCheckpointAsync().ConfigureAwait(true);
            ApplySettingsToControls();
            ShowOnboarding(show: false);
            ProductNavigation.SelectedItem = HistoryNavItem;
            HistoryNavItem.Focus(FocusState.Programmatic);
        }
        finally
        {
            _finishingOnboarding = false;
            if (OnboardingView is { Visibility: Visibility.Visible })
            {
                DrawOnboardingFooter(_onboarding.Step);
            }
        }
    }

    private void QueueOnboardingCheckpointSave()
    {
        if (!_onboardingStateLoaded ||
            _onboardingCheckpointSuppress ||
            _settings.HasCompletedOnboarding ||
            _onboarding.Step is OnboardingStep.Done)
        {
            return;
        }

        _onboardingCheckpointStore ??= OnboardingCheckpointStore.ForDataDirectory(_dataDirectory);
        var store = _onboardingCheckpointStore;
        var snapshot = _onboarding.ToCheckpoint();
        _checkpointWriteQueue = _checkpointWriteQueue.ContinueWith(
            async _ =>
            {
                try
                {
                    await store.SaveAsync(snapshot).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                }
            },
            TaskScheduler.Default).Unwrap();
    }

    private async Task ClearOnboardingCheckpointAsync()
    {
        _onboardingCheckpointStore ??= OnboardingCheckpointStore.ForDataDirectory(_dataDirectory);
        try
        {
            await _checkpointWriteQueue.ConfigureAwait(true);
            await _onboardingCheckpointStore.ClearAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
        }
    }
}
