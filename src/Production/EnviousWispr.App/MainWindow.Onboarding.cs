using System.Globalization;
using System.Runtime.InteropServices;
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
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.ViewManagement;

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

    internal void CancelOnboardingDualDownload() =>
        OnboardingDualDownloadCancelRequested?.Invoke();

    internal void SkipOnboardingPolishDownload()
    {
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

        RunSetupAgainButton.IsEnabled = !active;
        if (ended)
        {
            DictationEnded?.Invoke();
            _onboarding.TakeEnded(OnboardingPracticeBox.Text);
            if (_onboarding.PracticeState == OnboardingPracticeState.MissedTheBox && OnboardingView.Visibility == Visibility.Visible)
            {
                OnboardingPracticeBox.Focus(FocusState.Programmatic);
            }
        }

        if (active)
        {
            _onboarding.TakeStarted(OnboardingPracticeBox.Text);
        }

        RenderOnboarding();
    }

    private void ObserveStatusForOnboarding(DictationStatus status)
    {
        if (OnboardingView.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_onboarding.Step == OnboardingStep.Practice)
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
        if (OnboardingView.Visibility != Visibility.Visible ||
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
        if (_onboarding.Step == OnboardingStep.Downloads && _onboarding.NeedsDownloadStart)
        {
            StartOnboardingDownloadsIfNeeded();
        }
        else if (_onboarding.Step >= OnboardingStep.Downloads &&
                 !_onboarding.NeedsDownloadStart &&
                 _onboarding.Model == SpeechModelDelivery.Installed &&
                 _onboarding.Engine != SpeechEngineReadiness.Ready &&
                 _onboarding.PolishModel == SpeechModelDelivery.Installed &&
                 _settings.Preferences.Dictation.FinalEngine != _onboarding.Choices.SpeechEngine &&
                 ApplyOnboardingEnginesChoice is { } applyEngines)
        {
            _ = applyEngines(
                _onboarding.Choices.SpeechEngine,
                _onboarding.Choices.PolishChoice,
                _onboarding.Choices.EgOneModelId,
                _onboarding.Choices.CloudModelId);
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

        OnboardingPracticeBox.Text = string.Empty;
        OnboardingLanguageSearchBox.Text = string.Empty;
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

    private WindowsSetupRecommendation ComputeOnboardingRecommendation(string? selectedLanguageCode)
    {
        var profile = _hardwareProfile ?? HardwareProfileSummary.GetCurrent();
        _hardwareProfile = profile;
        var snapshot = new SetupHardwareSnapshot(
            TotalRamGb: profile.TotalRamGb,
            GpuKind: profile.GpuKind,
            DedicatedVramGb: profile.DedicatedVramGb,
            PhysicalCoreCount: profile.PhysicalCoreCount,
            PerformanceCoreCount: profile.PerformanceCoreCount,
            Architecture: profile.Architecture,
            AvailableDiskGb: profile.AvailableDiskGb,
            SelectedLanguageCode: selectedLanguageCode,
            SystemLanguageCode: CultureInfo.CurrentUICulture.Name,
            CudaRuntimeInstalled: _cudaRuntimeAvailable);
        return WindowsSetupRecommender.Recommend(snapshot);
    }

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

    private void OnboardingBackButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOnboardingKeyCapture();
        if (_onboarding.GoBack())
        {
            QueueOnboardingCheckpointSave();
            RenderOnboarding();
        }
    }

    private void OnboardingDownloadPillButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingDownloadSnapshot is null)
        {
            return;
        }

        var summary = OnboardingDownloadCoordinator.FormatBackgroundPillSummary(_onboardingDownloadSnapshot)
            ?? "Downloads complete";
        Announce(summary);
    }

    private async void FinishOnboardingButton_Click(object sender, RoutedEventArgs e)
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
                else if (_onboarding.MayFinish(OnboardingExit.SkipAheadWithoutPolish))
                {
                    SkipOnboardingPolishDownload();
                    _onboarding.SkipAheadWithoutPolish();
                    QueueOnboardingCheckpointSave();
                }
                else if (_onboarding.MayFinish(OnboardingExit.ContinueWithoutPolish))
                {
                    SkipOnboardingPolishDownload();
                    _onboarding.ContinueWithoutAiPolish();
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

    private async void OnboardingSkipButton_Click(object sender, RoutedEventArgs e)
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
                if (_onboarding.MayFinish(OnboardingExit.SkipAheadWithoutPolish))
                {
                    SkipOnboardingPolishDownload();
                    _onboarding.SkipAheadWithoutPolish();
                }
                else if (_onboarding.MayFinish(OnboardingExit.ContinueWithoutPolish))
                {
                    SkipOnboardingPolishDownload();
                    _onboarding.ContinueWithoutAiPolish();
                }

                QueueOnboardingCheckpointSave();
                RenderOnboarding();
                break;
        }
    }

    private void OnboardingSuggestedLanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_suggestedSystemLanguageCode))
        {
            return;
        }

        SelectOnboardingLanguage(_suggestedSystemLanguageCode);
    }

    private void OnboardingDetectAutoCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingLanguageUi)
        {
            return;
        }

        SelectOnboardingLanguage(LanguageCatalog.AutomaticCode);
    }

    private void OnboardingLanguageSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshOnboardingLanguageList();

    private void OnboardingLanguageSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        if (OnboardingLanguageListView.ItemsSource is IReadOnlyList<OnboardingLanguageRow> visible && visible.Count == 1)
        {
            SelectOnboardingLanguage(visible[0].Code);
            e.Handled = true;
        }
    }

    private void OnboardingLanguageListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OnboardingLanguageRow row)
        {
            SelectOnboardingLanguage(row.Code);
        }
    }

    private void SelectOnboardingLanguage(string? languageCode)
    {
        var rec = ComputeOnboardingRecommendation(languageCode);
        _onboardingRecommendation = rec;
        _onboarding.SelectLanguage(languageCode, rec);
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingOpenSpeechCompareButton_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.OpenSpeechCompare();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingSpeechCompareCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingCards || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        var engine = string.Equals(tag, "Whisper", StringComparison.Ordinal)
            ? FinalAsrEngine.Whisper
            : FinalAsrEngine.Parakeet;
        var modelId = engine == FinalAsrEngine.Whisper ? WhisperModelIds.Final : ParakeetModelIds.Final;
        _onboarding.SelectSpeech(engine, modelId);
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingSpeechCompareGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveTwoColumnGrid(
            OnboardingSpeechCompareGrid,
            e.NewSize.Width,
            600,
            OnboardingSpeechCompareCol1,
            OnboardingWhisperCompareCard);

    private void OnboardingPolishPickGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveTwoColumnGrid(
            OnboardingPolishPickGrid,
            e.NewSize.Width,
            600,
            OnboardingPolishPickCol1,
            OnboardingPolishSecondaryCard);

    private void OnboardingSeeAllEgOneGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveThreeCardGridLayout(
            OnboardingSeeAllEgOneGrid,
            e.NewSize.Width,
            stackThreshold: 640,
            [OnboardingSeeAllEgOneFullCard, OnboardingSeeAllEgOneMiniCard, OnboardingSeeAllEgOneMicroCard]);

    private static void ApplyResponsiveTwoColumnGrid(
        Grid grid,
        double width,
        double stackThreshold,
        ColumnDefinition secondColumn,
        FrameworkElement secondCard)
    {
        if (grid is null || secondColumn is null || secondCard is null)
        {
            return;
        }

        if (width > 0 && width < stackThreshold)
        {
            secondColumn.Width = new GridLength(0);
            Grid.SetRow(secondCard, 1);
            Grid.SetColumn(secondCard, 0);
        }
        else
        {
            secondColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(secondCard, 0);
            Grid.SetColumn(secondCard, 1);
        }
    }

    private void OnboardingPolishPrimaryCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingCards || sender is not RadioButton { Tag: string modelId })
        {
            return;
        }

        _onboarding.SelectPolish(OnboardingPolishChoiceKind.EgOne, EgOneModelCatalog.NormalizeOrDefault(modelId));
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingOpenPolishSeeAllButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingSeeAllCloudTab = TryGetCloudProvider(_onboarding.Choices.PolishChoice, out _);
        _onboarding.OpenPolishSeeAll();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private async void OnboardingSkipPolish_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.SelectPolish(OnboardingPolishChoiceKind.None);
        SyncLiveInventoryToOnboarding();
        await AdvanceFromPolishChoiceAsync().ConfigureAwait(true);
    }

    private void OnboardingPolishLowSpecCloudButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingSeeAllCloudTab = true;
        _onboarding.OpenPolishSeeAll();
        SelectOnboardingCloudProvider(_onboardingCloudProvider);
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingPolishTabLocalButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingSeeAllCloudTab = false;
        if (_onboarding.Choices.PolishChoice != OnboardingPolishChoiceKind.EgOne)
        {
            var defaultEgOne = _onboardingRecommendation?.Polish.PrimaryModelId ?? EgOneModelCatalog.MiniId;
            _onboarding.SelectPolish(OnboardingPolishChoiceKind.EgOne, defaultEgOne);
            SyncLiveInventoryToOnboarding();
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    private void OnboardingPolishTabCloudButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingSeeAllCloudTab = true;
        SelectOnboardingCloudProvider(_onboardingCloudProvider);
        RenderOnboarding();
    }

    private void OnboardingSeeAllEgOneCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_onboardingIsUpdatingCards || sender is not RadioButton { Tag: string modelId })
        {
            return;
        }

        _onboardingSeeAllCloudTab = false;
        _onboarding.SelectPolish(OnboardingPolishChoiceKind.EgOne, EgOneModelCatalog.NormalizeOrDefault(modelId));
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingCloudProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var provider = tag switch
        {
            "Gemini" => PolishProvider.Gemini,
            "Claude" => PolishProvider.Anthropic,
            _ => PolishProvider.OpenAI,
        };
        SelectOnboardingCloudProvider(provider);
        RenderOnboarding();
    }

    private void SelectOnboardingCloudProvider(PolishProvider provider)
    {
        _onboardingCloudProvider = provider;
        var kind = ToOnboardingPolishKind(provider);
        var defaultModel = _session.Provider.RecommendedModel(provider) ?? GetDefaultCloudModelFallback(provider);
        var currentModel = _onboarding.Choices.PolishChoice == kind &&
            !string.IsNullOrWhiteSpace(_onboarding.Choices.CloudModelId)
            ? _onboarding.Choices.CloudModelId
            : defaultModel;
        _onboarding.SelectPolish(kind, cloudModelId: currentModel);
        SyncLiveInventoryToOnboarding();
        QueueOnboardingCheckpointSave();
        PopulateOnboardingCloudModels(provider, [defaultModel], currentModel);
        RefreshOnboardingCloudKeyStatusText(provider);
    }

    private static OnboardingPolishChoiceKind ToOnboardingPolishKind(PolishProvider provider) => provider switch
    {
        PolishProvider.Gemini => OnboardingPolishChoiceKind.Gemini,
        PolishProvider.Anthropic => OnboardingPolishChoiceKind.Claude,
        _ => OnboardingPolishChoiceKind.OpenAI,
    };

    private static bool TryGetCloudProvider(OnboardingPolishChoiceKind kind, out PolishProvider provider)
    {
        switch (kind)
        {
            case OnboardingPolishChoiceKind.OpenAI:
                provider = PolishProvider.OpenAI;
                return true;
            case OnboardingPolishChoiceKind.Claude:
                provider = PolishProvider.Anthropic;
                return true;
            case OnboardingPolishChoiceKind.Gemini:
                provider = PolishProvider.Gemini;
                return true;
            default:
                provider = PolishProvider.None;
                return false;
        }
    }

    private static string GetDefaultCloudModelFallback(PolishProvider provider) => provider switch
    {
        PolishProvider.Anthropic => "claude-haiku-4-5",
        PolishProvider.Gemini => "gemini-2.5-flash-lite",
        _ => "gpt-4o-mini",
    };

    private void PopulateOnboardingCloudModels(
        PolishProvider provider,
        IReadOnlyList<string> discoveredModels,
        string? preferredModelId)
    {
        var items = new List<string>();
        foreach (var candidate in discoveredModels)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !items.Contains(candidate, StringComparer.Ordinal))
            {
                items.Add(candidate);
            }
        }

        var recommended = _session.Provider.RecommendedModel(provider) ?? GetDefaultCloudModelFallback(provider);
        if (!items.Contains(recommended, StringComparer.Ordinal))
        {
            items.Add(recommended);
        }

        if (!string.IsNullOrWhiteSpace(preferredModelId) && !items.Contains(preferredModelId, StringComparer.Ordinal))
        {
            items.Insert(0, preferredModelId);
        }

        _onboardingIsUpdatingCloudModels = true;
        try
        {
            OnboardingCloudModelComboBox.ItemsSource = items;
            var target = !string.IsNullOrWhiteSpace(preferredModelId) ? preferredModelId : recommended;
            OnboardingCloudModelComboBox.SelectedIndex = Math.Max(0, items.IndexOf(target));
        }
        finally
        {
            _onboardingIsUpdatingCloudModels = false;
        }
    }

    private void RefreshOnboardingCloudKeyStatusText(PolishProvider provider)
    {
        var status = _session.Provider.KeyStatus(provider);
        var message = status switch
        {
            ApiKeyReadStatus.Found => $"{ProviderDisplayName(provider)} API key is saved in Windows Credential Manager.",
            ApiKeyReadStatus.Unavailable => "Windows Credential Manager is unavailable right now.",
            _ => $"Paste your {ProviderDisplayName(provider)} API key and click Check my key, or choose a model below.",
        };
        SetLiveText(OnboardingCloudKeyStatusText, message);
    }

    private void OnboardingCloudApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var hasText = !string.IsNullOrWhiteSpace(OnboardingCloudApiKeyBox.Password);
        var hasStored = _session.Provider.KeyStatus(_onboardingCloudProvider) == ApiKeyReadStatus.Found;
        OnboardingCloudCheckKeyButton.IsEnabled = hasText || hasStored;
    }

    private async void OnboardingCloudCheckKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = _onboardingCloudProvider;
        var enteredKey = OnboardingCloudApiKeyBox.Password;
        if (!string.IsNullOrWhiteSpace(enteredKey))
        {
            var saved = _session.Provider.SaveKey(provider, enteredKey);
            if (saved == ApiKeySaveOutcome.Saved)
            {
                OnboardingCloudApiKeyBox.Password = string.Empty;
            }
            else if (saved == ApiKeySaveOutcome.StorageUnavailable)
            {
                SetLiveText(OnboardingCloudKeyStatusText, "Windows Credential Manager could not store the key.");
                return;
            }
        }

        if (_session.Provider.KeyStatus(provider) != ApiKeyReadStatus.Found)
        {
            SetLiveText(OnboardingCloudKeyStatusText, $"Enter a {ProviderDisplayName(provider)} API key first.");
            OnboardingCloudApiKeyBox.Focus(FocusState.Programmatic);
            return;
        }

        SetLiveText(OnboardingCloudKeyStatusText, $"Checking {ProviderDisplayName(provider)} key...");
        OnboardingCloudCheckKeyButton.IsEnabled = false;
        try
        {
            var listing = await _session.Provider.ListModelsAsync(provider, null).ConfigureAwait(true);
            if (listing is null || _session.Closing)
            {
                return;
            }

            if (listing.Models.Count > 0)
            {
                var selected = OnboardingCloudModelComboBox.SelectedItem as string
                    ?? _onboarding.Choices.CloudModelId
                    ?? listing.Models[0];
                PopulateOnboardingCloudModels(provider, listing.Models, selected);
                _onboarding.SelectPolish(ToOnboardingPolishKind(provider), cloudModelId: selected);
                QueueOnboardingCheckpointSave();
            }

            if (listing.Discovery is { } discovery)
            {
                SetLiveText(OnboardingCloudKeyStatusText, ModelDiscoveryNotice(provider, discovery));
            }
        }
        finally
        {
            var canCheck = !string.IsNullOrWhiteSpace(OnboardingCloudApiKeyBox.Password) ||
                _session.Provider.KeyStatus(_onboardingCloudProvider) == ApiKeyReadStatus.Found;
            OnboardingCloudCheckKeyButton.IsEnabled = canCheck;
        }
    }

    private void OnboardingCloudClearKeyButton_Click(object sender, RoutedEventArgs e)
    {
        OnboardingCloudApiKeyBox.Password = string.Empty;
        if (_session.Provider.CheckKeyRemoval(_onboardingCloudProvider) == ApiKeyRemovalCheck.Confirm)
        {
            _ = _session.Provider.RemoveKey(_onboardingCloudProvider);
        }

        RefreshOnboardingCloudKeyStatusText(_onboardingCloudProvider);
    }

    private void OnboardingCloudModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_onboardingIsUpdatingCloudModels || OnboardingCloudModelComboBox.SelectedItem is not string modelId)
        {
            return;
        }

        _onboarding.SelectPolish(ToOnboardingPolishKind(_onboardingCloudProvider), cloudModelId: modelId);
        QueueOnboardingCheckpointSave();
    }

    private async Task AdvanceFromPolishChoiceAsync()
    {
        SyncLiveInventoryToOnboarding();
        var speechInstalled = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper
            ? _whisperModelState.IsInstalled
            : _parakeetModelState.IsInstalled;
        var polishInstalled = !_onboarding.RequiresLocalEgOne ||
            ResolveEgOneDisplayState(EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).IsInstalled;
        var needsDownload = !speechInstalled || !polishInstalled;

        _onboarding.ContinueFromPolishPick(downloadsNeededOverride: needsDownload);
        QueueOnboardingCheckpointSave();

        if (_onboarding.Step == OnboardingStep.Downloads)
        {
            StartOnboardingDownloadsIfNeeded();
        }
        else if (ApplyOnboardingEnginesChoice is { } applyEngines)
        {
            await applyEngines(
                _onboarding.Choices.SpeechEngine,
                _onboarding.Choices.PolishChoice,
                _onboarding.Choices.EgOneModelId,
                _onboarding.Choices.CloudModelId).ConfigureAwait(true);
        }

        RenderOnboarding();
    }

    private void StartOnboardingDownloadsIfNeeded()
    {
        if (!_onboarding.NeedsDownloadStart)
        {
            return;
        }

        _onboarding.DownloadStarted();
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        var speechModelId = _onboarding.Choices.SpeechModelId;
        var polishModelId = _onboarding.RequiresLocalEgOne
            ? EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)
            : null;
        if (OnboardingDualDownloadRequested is not null)
        {
            RequestOnboardingDualDownload(speechModelId, polishModelId, rec.Polish.ParallelDownloads);
        }
        else
        {
            ModelDownloadRequested?.Invoke();
        }
    }

    private void OnboardingDownloadCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.RequestCancel();
        CancelOnboardingDualDownload();
        RenderOnboarding();
    }

    private void OnboardingDownloadRetryButton_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.RetrySetup();
        if (_onboarding.Model == SpeechModelDelivery.Installed &&
            _onboarding.Engine != SpeechEngineReadiness.Ready &&
            _settings.Preferences.Dictation.FinalEngine != _onboarding.Choices.SpeechEngine &&
            _onboardingDownloadSnapshot?.IsRunning != true &&
            ApplyOnboardingEnginesChoice is { } applySelected)
        {
            _ = applySelected(
                _onboarding.Choices.SpeechEngine,
                _onboarding.Choices.PolishChoice,
                _onboarding.Choices.EgOneModelId,
                _onboarding.Choices.CloudModelId);
        }
        else if (_onboarding.Engine == SpeechEngineReadiness.Unavailable && _onboarding.Model == SpeechModelDelivery.Installed)
        {
            TranscriptionRetryRequested?.Invoke();
        }
        else if (_onboardingDownloadSnapshot is null)
        {
            StartOnboardingDownloadsIfNeeded();
        }
        else
        {
            RetryOnboardingDualDownload();
        }

        RenderOnboarding();
    }

    private void OnboardingSkipPolishAndAdvance_Click(object sender, RoutedEventArgs e)
    {
        SkipOnboardingPolishDownload();
        _onboarding.SelectPolish(OnboardingPolishChoiceKind.None);
        SyncLiveInventoryToOnboarding();
        if (_onboarding.Step == OnboardingStep.Downloads)
        {
            _onboarding.ContinueFromDownloads();
        }
        else if (_onboarding.Step == OnboardingStep.GettingReady)
        {
            _ = _onboarding.TryAdvanceFromGate();
        }

        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingGateSkipPolishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboarding.MayFinish(OnboardingExit.SkipAheadWithoutPolish))
        {
            SkipOnboardingPolishDownload();
            _ = _onboarding.SkipAheadWithoutPolish();
        }
        else if (_onboarding.MayFinish(OnboardingExit.ContinueWithoutPolish))
        {
            SkipOnboardingPolishDownload();
            _ = _onboarding.ContinueWithoutAiPolish();
        }
        else
        {
            OnboardingSkipPolishAndAdvance_Click(sender, e);
            return;
        }

        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private async void OnboardingRefreshMicButton_Click(object sender, RoutedEventArgs e) =>
        await LoadMicrophonesAsync().ConfigureAwait(true);

    private void OnboardingKeycapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingKeyCaptureSession is not null)
        {
            CancelOnboardingKeyCapture();
            RenderOnboarding();
            return;
        }

        StartOnboardingKeyCapture();
    }

    private async void OnboardingPresetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset })
        {
            return;
        }

        CancelOnboardingKeyCapture();
        await ApplyOnboardingHotkeySelectionAsync(preset).ConfigureAwait(true);
    }

    private void StartOnboardingKeyCapture()
    {
        CancelOnboardingKeyCapture();
        KeybindCaptureActiveChanged?.Invoke(true);
        OnboardingKeyRehearsalActiveChanged?.Invoke(false);
        SetLiveText(OnboardingKeyStatusText, "Press your new dictation shortcut (press Esc to cancel)...");

        try
        {
            _onboardingKeyCaptureSession = new WindowsKeybindCaptureSession(
                allowModifierOnly: true,
                allowEscapeAsBinding: false,
                allowClear: false,
                onCommitted: async gestureText =>
                {
                    StopOnboardingKeyCaptureSession();
                    KeybindCaptureActiveChanged?.Invoke(false);
                    await ApplyOnboardingHotkeySelectionAsync(gestureText).ConfigureAwait(true);
                },
                onCancelled: () =>
                {
                    CancelOnboardingKeyCapture();
                    SetLiveText(OnboardingKeyStatusText, string.Empty);
                    RenderOnboarding();
                },
                onInvalidAttempt: hint => SetLiveText(OnboardingKeyStatusText, hint),
                dispatchToUi: action => DispatcherQueue.TryEnqueue(() => action()));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            CancelOnboardingKeyCapture();
            SetLiveText(OnboardingKeyStatusText, "Could not start shortcut capture. Pick a preset or try again.");
        }

        RenderOnboarding();
    }

    private void CancelOnboardingKeyCapture()
    {
        if (_onboardingKeyCaptureSession is null)
        {
            return;
        }

        StopOnboardingKeyCaptureSession();
        KeybindCaptureActiveChanged?.Invoke(false);
    }

    private void StopOnboardingKeyCaptureSession()
    {
        _onboardingKeyCaptureSession?.Dispose();
        _onboardingKeyCaptureSession = null;
    }

    private async Task ApplyOnboardingHotkeySelectionAsync(string gestureText)
    {
        var parsed = HotkeyGestureParser.Parse(gestureText);
        if (!parsed.Succeeded || parsed.Gesture is not { } parsedGesture)
        {
            SetLiveText(OnboardingKeyStatusText, "Use a supported shortcut such as Ctrl+Win, RightCtrl, or F8.");
            return;
        }

        var canonical = parsedGesture.ToString()!;
        var mode = DictationRecordingMode.PushToTalk;

        if (ApplyOnboardingRecordingKey is not null)
        {
            var (applied, error) = await ApplyOnboardingRecordingKey(canonical, mode).ConfigureAwait(true);
            if (!applied)
            {
                SetLiveText(OnboardingKeyStatusText, error ?? "Could not switch shortcut right now.");
                return;
            }
        }

        _onboardingHoldRehearsed = false;
        _onboardingDoubleTapRehearsed = false;
        _onboardingRehearsalKeyDown = false;
        _onboardingLastTapUpUtc = null;
        _onboarding.ReportRecordingKey(canonical, mode);
        SetLiveText(OnboardingKeyStatusText, $"Shortcut set to {canonical}. Test holding or double-pressing it below.");
        QueueOnboardingCheckpointSave();
        RenderOnboarding();
    }

    private void OnboardingView_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (OnboardingView.Visibility != Visibility.Visible ||
            _onboarding.Step != OnboardingStep.Key ||
            _onboardingKeyCaptureSession is not null)
        {
            return;
        }

        if (!MatchesCurrentOnboardingHotkey(e.Key))
        {
            return;
        }

        if (!_onboardingRehearsalKeyDown)
        {
            _onboardingRehearsalKeyDown = true;
            _onboardingHoldRehearsed = true;
            _onboardingKeyHoldTimer.Tick -= OnOnboardingKeyHoldTimerTick;
            _onboardingKeyHoldTimer.Tick += OnOnboardingKeyHoldTimerTick;
            _onboardingKeyHoldTimer.Start();

            if (_onboardingLastTapUpUtc is { } lastTap &&
                DateTimeOffset.UtcNow - lastTap <= RehearsalDoubleTapWindow)
            {
                _onboardingDoubleTapRehearsed = true;
            }

            _onboarding.MarkKeyRehearsalCompleted();
            RenderOnboarding();
        }
    }

    private void OnboardingView_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (OnboardingView.Visibility != Visibility.Visible ||
            _onboarding.Step != OnboardingStep.Key ||
            _onboardingKeyCaptureSession is not null ||
            !_onboardingRehearsalKeyDown)
        {
            return;
        }

        if (MatchesCurrentOnboardingHotkeyKeyUp(e.Key))
        {
            _onboardingRehearsalKeyDown = false;
            _onboardingKeyHoldTimer.Stop();
            _onboardingLastTapUpUtc = DateTimeOffset.UtcNow;
            RenderOnboarding();
        }
    }

    private void OnOnboardingKeyHoldTimerTick(object? sender, object e)
    {
        _onboardingKeyHoldTimer.Stop();
        if (_onboardingRehearsalKeyDown && _onboarding.Step == OnboardingStep.Key)
        {
            _onboardingHoldRehearsed = true;
            _onboarding.MarkKeyRehearsalCompleted();
            RenderOnboarding();
        }
    }

    private bool MatchesCurrentOnboardingHotkey(VirtualKey key)
    {
        var text = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? DictationPreferences.Default.PushToTalkGesture;
        var parsed = HotkeyGestureParser.Parse(text);
        if (!parsed.Succeeded || parsed.Gesture is not { } gesture)
        {
            return false;
        }

        var currentModifiers = HeldModifierSet() | ModifierFlagOf(key);
        if (string.IsNullOrEmpty(gesture.Key))
        {
            return currentModifiers.HasFlag(gesture.Modifiers);
        }

        if (gesture.Modifiers == HotkeyModifiers.None)
        {
            return MatchesNamedGestureKey(key, gesture.Key);
        }

        return (currentModifiers & gesture.Modifiers) == gesture.Modifiers &&
            MatchesNamedGestureKey(key, gesture.Key);
    }

    private bool MatchesCurrentOnboardingHotkeyKeyUp(VirtualKey key)
    {
        var text = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? DictationPreferences.Default.PushToTalkGesture;
        var parsed = HotkeyGestureParser.Parse(text);
        if (!parsed.Succeeded || parsed.Gesture is not { } gesture)
        {
            return true;
        }

        if (string.IsNullOrEmpty(gesture.Key))
        {
            return ModifierFlagOf(key) != HotkeyModifiers.None;
        }

        return MatchesNamedGestureKey(key, gesture.Key);
    }

    private static bool MatchesNamedGestureKey(VirtualKey key, string gestureKey) => gestureKey switch
    {
        "LeftCtrl" or "RightCtrl" => key == VirtualKey.Control || key == VirtualKey.LeftControl || key == VirtualKey.RightControl,
        "LeftShift" or "RightShift" => key == VirtualKey.Shift || key == VirtualKey.LeftShift || key == VirtualKey.RightShift,
        "LeftAlt" or "RightAlt" => key == VirtualKey.Menu || key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu,
        "LeftWin" => key == VirtualKey.LeftWindows,
        "RightWin" => key == VirtualKey.RightWindows,
        "Space" => key == VirtualKey.Space,
        "Escape" => key == VirtualKey.Escape,
        _ => Enum.TryParse<VirtualKey>(gestureKey, ignoreCase: true, out var mapped) && mapped == key,
    };

    private void OnboardingPracticeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _onboarding.PracticeTextChanged(OnboardingPracticeBox.Text);
        if (_onboarding.PracticeSucceeded)
        {
            QueueOnboardingCheckpointSave();
        }

        RenderOnboarding();
    }

    private void OnboardingClosingCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _onboardingClosingPaused = true;
        _onboardingBeat.Stop();
    }

    private void OnboardingClosingCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _onboardingClosingPaused = false;
        ArmClosingBeatTimer();
    }

    private void OnboardingClosingDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int beatIndex })
        {
            return;
        }

        _onboardingClosingPaused = true;
        _onboardingBeat.Stop();
        _onboarding.SetClosingBeat(beatIndex);
        RenderOnboarding();
    }

    private void OnboardingClosingWatchAgainButton_Click(object sender, RoutedEventArgs e)
    {
        _onboardingClosingPaused = false;
        _onboarding.RestartClosing();
        RenderOnboarding();
        ArmClosingBeatTimer();
    }

    private async void RunSetupAgainButton_Click(object sender, RoutedEventArgs e)
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
            if (OnboardingView.Visibility == Visibility.Visible)
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

    private void RenderOnboarding()
    {
        if (OnboardingView.Visibility != Visibility.Visible)
        {
            StopOnboardingTimers();
            OnboardingKeyRehearsalActiveChanged?.Invoke(false);
            return;
        }

        EnsureOnboardingChromeBuilt();
        var step = _onboarding.Step;
        var screenChanged = _drawnStep != step;
        if (screenChanged && _drawnStep == OnboardingStep.Key)
        {
            CancelOnboardingKeyCapture();
            _onboardingRehearsalKeyDown = false;
            _onboardingKeyHoldTimer.Stop();
        }

        OnboardingKeyRehearsalActiveChanged?.Invoke(
            step == OnboardingStep.Key && _onboardingKeyCaptureSession is null);

        if (screenChanged && step == OnboardingStep.Practice && _dictationActive && !_onboarding.TakeInFlight)
        {
            _onboarding.TakeStarted(OnboardingPracticeBox.Text);
        }

        if (screenChanged && step == OnboardingStep.Closing)
        {
            ArmClosingBeatTimer();
        }
        else if (step != OnboardingStep.Closing)
        {
            _onboardingBeat.Stop();
        }

        ArmMicrophonePoll();
        DrawOnboardingChrome(step);
        DrawOnboardingStepHeader(step);
        SetVisibleOnboardingPanels(step);

        var focusedBefore = FocusedOnboardingControl();
        switch (step)
        {
            case OnboardingStep.Welcome:
                UpdateOnboardingLipsMotion();
                break;
            case OnboardingStep.Language:
                DrawOnboardingLanguage();
                break;
            case OnboardingStep.SpeechPick:
                DrawOnboardingSpeechPick();
                break;
            case OnboardingStep.SpeechCompare:
                DrawOnboardingSpeechCompare();
                break;
            case OnboardingStep.PolishPick:
                DrawOnboardingPolishPick();
                break;
            case OnboardingStep.PolishSeeAll:
                DrawOnboardingPolishSeeAll();
                break;
            case OnboardingStep.Downloads:
                DrawOnboardingDownloads();
                break;
            case OnboardingStep.Permissions:
                DrawOnboardingPermissions();
                break;
            case OnboardingStep.Key:
                DrawOnboardingKey();
                break;
            case OnboardingStep.GettingReady:
                DrawOnboardingGettingReady();
                break;
            case OnboardingStep.Practice:
                DrawOnboardingPractice();
                break;
            case OnboardingStep.Closing:
                DrawOnboardingClosing();
                break;
        }

        DrawOnboardingFooter(step);

        if (!screenChanged)
        {
            RescueOnboardingFocus(focusedBefore);
        }

        if (screenChanged)
        {
            _drawnStep = step;
            _drawnPractice = _onboarding.PracticeState;
            _drawnGate = _onboarding.EvaluateGate();
            OnboardingScroll.ChangeView(null, 0, null, disableAnimation: true);
            FocusOnboardingScreen(step);
        }
        else if (step == OnboardingStep.Practice && _drawnPractice != _onboarding.PracticeState)
        {
            _drawnPractice = _onboarding.PracticeState;
            Announce($"{OnboardingTitle.Text}. {OnboardingSubtitle.Text}");
        }
        else if (step == OnboardingStep.GettingReady && _drawnGate != _onboarding.EvaluateGate())
        {
            _drawnGate = _onboarding.EvaluateGate();
        }
    }

    private void DrawOnboardingChrome(OnboardingStep step)
    {
        var showBack = _onboarding.CanGoBack;
        var showProgress = step is not (OnboardingStep.Welcome or OnboardingStep.Closing or OnboardingStep.Done);
        var pillSummary = _onboardingDownloadSnapshot is null
            ? null
            : OnboardingDownloadCoordinator.FormatBackgroundPillSummary(_onboardingDownloadSnapshot);
        var showDownloadPill = !string.IsNullOrWhiteSpace(pillSummary) &&
            step is not (OnboardingStep.Welcome or OnboardingStep.Downloads);

        OnboardingBackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        OnboardingProgressGrid.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadPillButton.Visibility = showDownloadPill ? Visibility.Visible : Visibility.Collapsed;
        OnboardingChromeBar.Visibility = showBack || showProgress || showDownloadPill
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showProgress)
        {
            var currentNum = OnboardingFlow.PrimaryStepNumberFor(step);
            var activeBrush = Application.Current.Resources["BrandAccentSolidBrush"] as Brush;
            var idleBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
            for (var i = 0; i < _onboardingProgressSegments.Length; i++)
            {
                _onboardingProgressSegments[i].Background = i < currentNum ? activeBrush : idleBrush;
            }
        }

        if (showDownloadPill && pillSummary is not null)
        {
            OnboardingDownloadPillText.Text = pillSummary;
            ToolTipService.SetToolTip(OnboardingDownloadPillButton, pillSummary);
            var isDone = _onboardingDownloadSnapshot is { IsSpeechReady: true, IsPolishReady: true } snap &&
                string.IsNullOrWhiteSpace(snap.FailureMessage);
            OnboardingDownloadPillDot.Fill = Application.Current.Resources[
                isDone ? "BrandSuccessBrush" : "BrandAccentBrush"] as Brush;
        }
    }

    private void DrawOnboardingStepHeader(OnboardingStep step)
    {
        if (step is OnboardingStep.Welcome or OnboardingStep.Closing or OnboardingStep.Done)
        {
            OnboardingStepHeader.Visibility = Visibility.Collapsed;
            return;
        }

        OnboardingStepHeader.Visibility = Visibility.Visible;
        OnboardingStepText.Text = OnboardingCopy.StepOf(step).ToUpperInvariant();
        var (title, subtitle) = step switch
        {
            OnboardingStep.Language => (
                "Which language do you dictate in?",
                "Pick automatic detection or lock a language for higher recognition accuracy."),
            OnboardingStep.SpeechPick => (
                "Choose your speech engine",
                "EnviousWispr tuned this recommendation to your language and PC hardware."),
            OnboardingStep.SpeechCompare => (
                "Compare speech models",
                "Both models run privately on your PC with zero audio leaving this device."),
            OnboardingStep.PolishIntro => (
                "Cleaner writing that still sounds like you",
                "Optional AI polish cleans grammar, filler words, and formatting automatically."),
            OnboardingStep.PolishPick => (
                "Meet EG-1",
                "Our local AI polish model, picked to match your PC memory and graphics."),
            OnboardingStep.PolishSeeAll => (
                "Choose your AI polish option",
                "Pick any local EG-1 size or connect your own OpenAI, Gemini, or Claude key."),
            OnboardingStep.Downloads => (
                "Downloading your models",
                "Your downloads run in the background so you can keep moving through setup."),
            OnboardingStep.Permissions => (
                "Allow microphone access",
                "Windows only asks for your microphone so dictation stays private and ready."),
            OnboardingStep.Key => (
                "Meet your dictation key",
                "Pick the shortcut you will use anywhere on Windows and test it below."),
            OnboardingStep.GettingReady => (
                OnboardingCopy.GateTitle,
                "Waiting for your chosen downloads or microphone check to finish."),
            OnboardingStep.Practice => (
                OnboardingCopy.PracticeHeadline(_onboarding.PracticeState, _onboarding.PracticeSucceeded),
                OnboardingCopy.PracticeSubhead(
                    _onboarding.PracticeState,
                    _onboarding.PracticeSucceeded,
                    _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win",
                    _onboarding.Choices.RecordingMode,
                    _onboarding.MissedTo)),
            _ => ("Setup", string.Empty),
        };
        OnboardingTitle.Text = title;
        OnboardingSubtitle.Text = subtitle;
    }

    private void SetVisibleOnboardingPanels(OnboardingStep step)
    {
        OnboardingWelcomePanel.Visibility = step == OnboardingStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        OnboardingLanguagePanel.Visibility = step == OnboardingStep.Language ? Visibility.Visible : Visibility.Collapsed;
        OnboardingSpeechPickPanel.Visibility = step == OnboardingStep.SpeechPick ? Visibility.Visible : Visibility.Collapsed;
        OnboardingSpeechComparePanel.Visibility = step == OnboardingStep.SpeechCompare ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPolishIntroPanel.Visibility = step == OnboardingStep.PolishIntro ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPolishPickPanel.Visibility = step == OnboardingStep.PolishPick ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPolishSeeAllPanel.Visibility = step == OnboardingStep.PolishSeeAll ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadsPanel.Visibility = step == OnboardingStep.Downloads ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPermissionsPanel.Visibility = step == OnboardingStep.Permissions ? Visibility.Visible : Visibility.Collapsed;
        OnboardingKeyPanel.Visibility = step == OnboardingStep.Key ? Visibility.Visible : Visibility.Collapsed;
        OnboardingGettingReadyPanel.Visibility = step == OnboardingStep.GettingReady ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPracticePanel.Visibility = step == OnboardingStep.Practice ? Visibility.Visible : Visibility.Collapsed;
        OnboardingClosingPanel.Visibility = step == OnboardingStep.Closing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateOnboardingLipsMotion()
    {
        var animate = ShouldAnimateMotion();
        if (animate && !_onboardingLipsAnimating)
        {
            _onboardingLipsAnimating = true;
            OnboardingLipsBreathStoryboard.Begin();
        }
        else if (!animate && _onboardingLipsAnimating)
        {
            _onboardingLipsAnimating = false;
            OnboardingLipsBreathStoryboard.Stop();
            OnboardingRainbowLipsContainer.Opacity = 1.0;
        }
    }

    private void DrawOnboardingLanguage()
    {
        var code = _onboarding.Choices.LanguageCode;
        var isAuto = string.IsNullOrWhiteSpace(code) ||
            string.Equals(code, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase);

        _onboardingIsUpdatingLanguageUi = true;
        try
        {
            OnboardingDetectAutoCard.IsChecked = isAuto;
        }
        finally
        {
            _onboardingIsUpdatingLanguageUi = false;
        }

        OnboardingLanguageGreetingText.Text = SetupLanguageMetadata.GetGreeting(code, CultureInfo.CurrentUICulture);
        var displayName = isAuto
            ? LanguageCatalog.AutomaticTitle
            : LanguageCatalog.TryGetEntry(code, out var selectedEntry)
                ? selectedEntry.DisplayName
                : code;
        OnboardingLanguageSelectedTitleText.Text = displayName;

        var parakeetSupports = isAuto || LanguageCatalog.IsSupported(FinalAsrEngine.Parakeet, code);
        OnboardingLanguageEngineBadgeText.Text = parakeetSupports
            ? "Parakeet \u00B7 25 languages"
            : "Whisper \u00B7 99+ languages";
        if (Application.Current.Resources[parakeetSupports ? "BrandAccentChipStyle" : "BrandNeutralChipStyle"] is Style chipStyle)
        {
            OnboardingLanguageEngineBadge.Style = chipStyle;
        }
        if (Application.Current.Resources[parakeetSupports ? "BrandAccentChipTextStyle" : "BrandNeutralChipTextStyle"] is Style chipTextStyle)
        {
            OnboardingLanguageEngineBadgeText.Style = chipTextStyle;
        }

        OnboardingLanguageEngineDot.Fill = Application.Current.Resources[
            parakeetSupports ? "BrandAccentBrush" : "BrandTextSecondaryBrush"] as Brush;

        if (!string.IsNullOrWhiteSpace(_suggestedSystemLanguageCode) &&
            LanguageCatalog.TryGetEntry(_suggestedSystemLanguageCode, out var suggestedEntry) &&
            !string.Equals(code, suggestedEntry.Code, StringComparison.OrdinalIgnoreCase))
        {
            OnboardingSuggestedLanguageButton.Visibility = Visibility.Visible;
            OnboardingSuggestedLanguageText.Text = $"Suggested from Windows: {suggestedEntry.DisplayName}";
        }
        else
        {
            OnboardingSuggestedLanguageButton.Visibility = Visibility.Collapsed;
        }

        RefreshOnboardingLanguageList();
    }

    private void RefreshOnboardingLanguageList()
    {
        EnsureOnboardingChromeBuilt();
        var query = OnboardingLanguageSearchBox.Text ?? string.Empty;
        var selectedCode = _onboarding.Choices.LanguageCode;
        if (OnboardingLanguageListView.ItemsSource is not null &&
            string.Equals(_lastLanguageQuery, query, StringComparison.Ordinal) &&
            string.Equals(_lastSelectedLanguageCode, selectedCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLanguageQuery = query;
        _lastSelectedLanguageCode = selectedCode;
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _onboardingAllLanguageRows
            : SetupLanguageMetadata.SearchPickerEntries(query)
                .Select(entry => new OnboardingLanguageRow(
                    entry.Code,
                    entry.DisplayName,
                    entry.PickerSubtitle,
                    LanguageCatalog.IsSupported(FinalAsrEngine.Parakeet, entry.Code)
                        ? "Parakeet \u00B7 Whisper"
                        : "Whisper"))
                .ToArray();

        for (var i = 0; i < filtered.Length; i++)
        {
            filtered[i] = filtered[i] with
            {
                IsSelected = string.Equals(filtered[i].Code, selectedCode, StringComparison.OrdinalIgnoreCase),
            };
        }

        OnboardingLanguageListView.ItemsSource = filtered;
        OnboardingLanguageEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawOnboardingSpeechPick()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var selectedEngine = _onboarding.Choices.SpeechEngine;
        var isWhisper = selectedEngine == FinalAsrEngine.Whisper;
        var isInstalled = (isWhisper ? _whisperModelState : _parakeetModelState).IsInstalled;

        OnboardingSpeechPickTitle.Text = isWhisper ? "Meet Whisper" : "Meet Parakeet";
        OnboardingSpeechPickDescription.Text = isWhisper
            ? "Multilingual accuracy covering 99+ languages and mixed-script dictation."
            : "Ultra-fast local speech recognition tuned for 25 European languages.";
        var sizeLabel = isWhisper
            ? EnginePolishPresentation.WhisperBundleSizeLabel
            : EnginePolishPresentation.ParakeetBundleSizeLabel;
        OnboardingSpeechInstallBadgeText.Text = isInstalled ? "Already installed" : $"{sizeLabel} download";
        if (Application.Current.Resources[isInstalled ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle"] is Style chipStyle)
        {
            OnboardingSpeechInstallBadge.Style = chipStyle;
        }
        if (Application.Current.Resources[isInstalled ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle"] is Style chipTextStyle)
        {
            OnboardingSpeechInstallBadgeText.Style = chipTextStyle;
        }

        OnboardingSpeechPickSizeText.Text = isInstalled
            ? "Already downloaded on this PC (0 MB extra needed)."
            : $"One-time download: {sizeLabel}.";
        OnboardingSpeechPickSpeedText.Text = isWhisper ? rec.Speech.WhisperSpeedNote : rec.Speech.ParakeetSpeedNote;
        OnboardingSpeechPickLanguageNoteText.Text = rec.EffectiveLanguageEntry is { } entry
            ? $"Tailored for {entry.DisplayName}."
            : "Configured for Automatic language detection.";
    }

    private void DrawOnboardingSpeechCompare()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var selectedEngine = _onboarding.Choices.SpeechEngine;
        var parakeetAllowed = rec.Speech.IsParakeetAvailable;

        _onboardingIsUpdatingCards = true;
        try
        {
            OnboardingParakeetCompareCard.IsEnabled = parakeetAllowed;
            OnboardingWhisperCompareCard.IsEnabled = true;
            OnboardingParakeetCompareCard.IsChecked = selectedEngine == FinalAsrEngine.Parakeet && parakeetAllowed;
            OnboardingWhisperCompareCard.IsChecked = selectedEngine == FinalAsrEngine.Whisper || !parakeetAllowed;
        }
        finally
        {
            _onboardingIsUpdatingCards = false;
        }

        OnboardingParakeetCompareRecommendedBadge.Visibility = rec.Speech.PrimaryEngine == FinalAsrEngine.Parakeet
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnboardingWhisperCompareRecommendedBadge.Visibility = rec.Speech.PrimaryEngine == FinalAsrEngine.Whisper
            ? Visibility.Visible
            : Visibility.Collapsed;

        var parakeetStatusText = !parakeetAllowed
            ? (rec.Speech.ParakeetUnavailableReason ?? $"Doesn't support {rec.EffectiveLanguageEntry?.DisplayName ?? "this language"}")
            : _parakeetModelState.IsInstalled
                ? "Installed · 25 European languages"
                : "25 European languages";
        OnboardingParakeetCompareStatusBadgeText.Text = parakeetStatusText;
        if (Application.Current.Resources[!parakeetAllowed
            ? "BrandWarningChipStyle"
            : _parakeetModelState.IsInstalled
                ? "BrandSuccessChipStyle"
                : "BrandNeutralChipStyle"] is Style parakeetBadgeStyle)
        {
            OnboardingParakeetCompareStatusBadge.Style = parakeetBadgeStyle;
        }
        if (Application.Current.Resources[!parakeetAllowed
            ? "BrandWarningChipTextStyle"
            : _parakeetModelState.IsInstalled
                ? "BrandSuccessChipTextStyle"
                : "BrandNeutralChipTextStyle"] is Style parakeetTextStyle)
        {
            OnboardingParakeetCompareStatusBadgeText.Style = parakeetTextStyle;
        }

        OnboardingParakeetCompareSizeText.Text = _parakeetModelState.IsInstalled
            ? "Ready on this PC"
            : $"Download: {EnginePolishPresentation.ParakeetBundleSizeLabel}";
        OnboardingParakeetCompareDetailText.Text = rec.Speech.ParakeetSpeedNote;

        OnboardingWhisperCompareStatusBadgeText.Text = _whisperModelState.IsInstalled
            ? "Installed · 99+ languages"
            : "99+ languages";
        if (Application.Current.Resources[_whisperModelState.IsInstalled
            ? "BrandSuccessChipStyle"
            : "BrandAccentChipStyle"] is Style whisperBadgeStyle)
        {
            OnboardingWhisperCompareStatusBadge.Style = whisperBadgeStyle;
        }
        if (Application.Current.Resources[_whisperModelState.IsInstalled
            ? "BrandSuccessChipTextStyle"
            : "BrandAccentChipTextStyle"] is Style whisperTextStyle)
        {
            OnboardingWhisperCompareStatusBadgeText.Style = whisperTextStyle;
        }

        OnboardingWhisperCompareSizeText.Text = _whisperModelState.IsInstalled
            ? "Ready on this PC"
            : $"Download: {EnginePolishPresentation.WhisperBundleSizeLabel}";
        OnboardingWhisperCompareDetailText.Text = rec.Speech.WhisperSpeedNote;
    }

    private void DrawOnboardingPolishPick()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var profile = _hardwareProfile ?? HardwareProfileSummary.GetCurrent();
        OnboardingPolishHardwareBadgeText.Text = $"This PC: {profile.SummaryText}";

        OnboardingPolishLowSpecCard.Visibility = rec.Polish.LowSpecPolishAdvisory
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (rec.Polish.LowSpecPolishAdvisory && !string.IsNullOrWhiteSpace(rec.Polish.SecondaryAdvisory))
        {
            OnboardingPolishLowSpecDetailText.Text = rec.Polish.SecondaryAdvisory;
        }

        var primaryId = EgOneModelCatalog.NormalizeOrDefault(rec.Polish.PrimaryModelId);
        var secondaryId = EgOneModelCatalog.NormalizeOrDefault(
            rec.Polish.SecondaryModelId ??
            (primaryId == EgOneModelCatalog.MiniId ? EgOneModelCatalog.MicroId : EgOneModelCatalog.MiniId));

        var selectedEgOne = _onboarding.Choices.PolishChoice == OnboardingPolishChoiceKind.EgOne
            ? EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)
            : null;
        if (selectedEgOne is null || (selectedEgOne != primaryId && selectedEgOne != secondaryId))
        {
            selectedEgOne = primaryId;
        }

        _onboardingIsUpdatingCards = true;
        try
        {
            OnboardingPolishPrimaryCard.Tag = primaryId;
            OnboardingPolishSecondaryCard.Tag = secondaryId;
            OnboardingPolishPrimaryCard.IsChecked = selectedEgOne == primaryId;
            OnboardingPolishSecondaryCard.IsChecked = selectedEgOne == secondaryId;
        }
        finally
        {
            _onboardingIsUpdatingCards = false;
        }

        ApplyOnboardingEgOneSummary(
            primaryId,
            rec.Polish.PrimaryTag,
            isAccentTag: true,
            OnboardingPolishPrimaryTitleText,
            OnboardingPolishPrimaryTagBadge,
            OnboardingPolishPrimaryTagText,
            OnboardingPolishPrimaryStatusBadge,
            OnboardingPolishPrimaryStatusText,
            OnboardingPolishPrimaryDescText,
            OnboardingPolishPrimarySpecsText);

        ApplyOnboardingEgOneSummary(
            secondaryId,
            string.IsNullOrWhiteSpace(rec.Polish.SecondaryTag) ? "Smaller download" : rec.Polish.SecondaryTag,
            isAccentTag: rec.Polish.SecondaryPromoted,
            OnboardingPolishSecondaryTitleText,
            OnboardingPolishSecondaryTagBadge,
            OnboardingPolishSecondaryTagText,
            OnboardingPolishSecondaryStatusBadge,
            OnboardingPolishSecondaryStatusText,
            OnboardingPolishSecondaryDescText,
            OnboardingPolishSecondarySpecsText);
    }

    private void ApplyOnboardingEgOneSummary(
        string modelId,
        string tagLabel,
        bool isAccentTag,
        TextBlock titleText,
        Border tagBadge,
        TextBlock tagText,
        Border statusBadge,
        TextBlock statusText,
        TextBlock descText,
        TextBlock specsText)
    {
        var facts = EnginePolishPresentation.DescribeEgOneTier(modelId);
        var state = ResolveEgOneDisplayState(modelId);
        var installed = state.IsInstalled;
        var diskLabel = string.IsNullOrWhiteSpace(state.BundleSizeText)
            ? facts.DefaultDiskSizeText
            : state.BundleSizeText;

        titleText.Text = facts.Title;
        tagText.Text = tagLabel;
        if (Application.Current.Resources[isAccentTag ? "BrandAccentChipStyle" : "BrandNeutralChipStyle"] is Style tagStyle)
        {
            tagBadge.Style = tagStyle;
        }
        if (Application.Current.Resources[isAccentTag ? "BrandAccentChipTextStyle" : "BrandNeutralChipTextStyle"] is Style tagTextStyle)
        {
            tagText.Style = tagTextStyle;
        }

        statusText.Text = installed ? "Installed" : $"{diskLabel} download";
        if (Application.Current.Resources[installed ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle"] is Style statusStyle)
        {
            statusBadge.Style = statusStyle;
        }
        if (Application.Current.Resources[installed ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle"] is Style statusTextStyle)
        {
            statusText.Style = statusTextStyle;
        }

        descText.Text = facts.Description;
        specsText.Text = $"{facts.QualityText} \u00B7 {facts.SpeedText} \u00B7 {facts.MemoryText}";
    }

    private void DrawOnboardingPolishSeeAll()
    {
        var rec = _onboardingRecommendation ?? ComputeOnboardingRecommendation(_onboarding.Choices.LanguageCode);
        _onboardingRecommendation = rec;
        var profile = _hardwareProfile ?? HardwareProfileSummary.GetCurrent();
        OnboardingSeeAllHardwareBadgeText.Text = $"This PC: {profile.SummaryText}";

        var isCloudTab = _onboardingSeeAllCloudTab || TryGetCloudProvider(_onboarding.Choices.PolishChoice, out _);
        ApplySegmentButtonState(OnboardingPolishTabLocalButton, !isCloudTab);
        ApplySegmentButtonState(OnboardingPolishTabCloudButton, isCloudTab);
        OnboardingSeeAllLocalPanel.Visibility = isCloudTab ? Visibility.Collapsed : Visibility.Visible;
        OnboardingSeeAllCloudPanel.Visibility = isCloudTab ? Visibility.Visible : Visibility.Collapsed;

        var recId = EgOneModelCatalog.NormalizeOrDefault(rec.Polish.PrimaryModelId);
        var selectedEgOne = EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId ?? recId);

        _onboardingIsUpdatingCards = true;
        try
        {
            OnboardingSeeAllEgOneFullCard.IsChecked = !isCloudTab && selectedEgOne == EnginePolishPresentation.EgOneFullModelId;
            OnboardingSeeAllEgOneMiniCard.IsChecked = !isCloudTab && selectedEgOne == EnginePolishPresentation.EgOneMiniModelId;
            OnboardingSeeAllEgOneMicroCard.IsChecked = !isCloudTab && selectedEgOne == EnginePolishPresentation.EgOneMicroModelId;
        }
        finally
        {
            _onboardingIsUpdatingCards = false;
        }

        DrawOnboardingSeeAllEgOneCard(
            EnginePolishPresentation.EgOneFullModelId,
            recId,
            OnboardingSeeAllFullRecBadge,
            OnboardingSeeAllFullStatusBadge,
            OnboardingSeeAllFullStatusText);
        DrawOnboardingSeeAllEgOneCard(
            EnginePolishPresentation.EgOneMiniModelId,
            recId,
            OnboardingSeeAllMiniRecBadge,
            OnboardingSeeAllMiniStatusBadge,
            OnboardingSeeAllMiniStatusText);
        DrawOnboardingSeeAllEgOneCard(
            EnginePolishPresentation.EgOneMicroModelId,
            recId,
            OnboardingSeeAllMicroRecBadge,
            OnboardingSeeAllMicroStatusBadge,
            OnboardingSeeAllMicroStatusText);

        if (isCloudTab)
        {
            ApplySegmentButtonState(OnboardingCloudOpenAiButton, _onboardingCloudProvider == PolishProvider.OpenAI);
            ApplySegmentButtonState(OnboardingCloudGeminiButton, _onboardingCloudProvider == PolishProvider.Gemini);
            ApplySegmentButtonState(OnboardingCloudClaudeButton, _onboardingCloudProvider == PolishProvider.Anthropic);
            var canCheck = !string.IsNullOrWhiteSpace(OnboardingCloudApiKeyBox.Password) ||
                _session.Provider.KeyStatus(_onboardingCloudProvider) == ApiKeyReadStatus.Found;
            OnboardingCloudCheckKeyButton.IsEnabled = canCheck;
            if (OnboardingCloudModelComboBox.ItemsSource is null)
            {
                var defaultModel = _session.Provider.RecommendedModel(_onboardingCloudProvider)
                    ?? GetDefaultCloudModelFallback(_onboardingCloudProvider);
                PopulateOnboardingCloudModels(_onboardingCloudProvider, [defaultModel], _onboarding.Choices.CloudModelId ?? defaultModel);
                RefreshOnboardingCloudKeyStatusText(_onboardingCloudProvider);
            }
        }
    }

    private void DrawOnboardingSeeAllEgOneCard(
        string modelId,
        string recommendedId,
        Border recBadge,
        Border statusBadge,
        TextBlock statusText)
    {
        var facts = EnginePolishPresentation.DescribeEgOneTier(modelId);
        var state = ResolveEgOneDisplayState(modelId);
        var diskLabel = string.IsNullOrWhiteSpace(state.BundleSizeText)
            ? facts.DefaultDiskSizeText
            : state.BundleSizeText;
        recBadge.Visibility = string.Equals(modelId, recommendedId, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
        statusText.Text = state.IsInstalled ? "Installed" : diskLabel;
        if (Application.Current.Resources[state.IsInstalled ? "BrandSuccessChipStyle" : "BrandNeutralChipStyle"] is Style badgeStyle)
        {
            statusBadge.Style = badgeStyle;
        }
        if (Application.Current.Resources[state.IsInstalled ? "BrandSuccessChipTextStyle" : "BrandNeutralChipTextStyle"] is Style textStyle)
        {
            statusText.Style = textStyle;
        }
    }

    private void DrawOnboardingDownloads()
    {
        var snapshot = _onboardingDownloadSnapshot;
        var speechName = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper
            ? "Whisper speech model"
            : "Parakeet speech model";
        OnboardingSpeechDownloadNameText.Text = speechName;

        var speechStage = snapshot?.Speech.Stage ?? (_onboarding.Model == SpeechModelDelivery.Installed
            ? OnboardingDownloadStage.Installed
            : OnboardingDownloadStage.Pending);
        var speechPercent = snapshot?.Speech.Percent ?? (speechStage == OnboardingDownloadStage.Installed ? 100 : 0);
        OnboardingSpeechDownloadProgress.Value = Math.Clamp(speechPercent, 0, 100);
        OnboardingSpeechDownloadProgress.IsIndeterminate = speechStage == OnboardingDownloadStage.Activating;
        OnboardingSpeechDownloadBadgeText.Text = FormatDownloadStageBadge(speechStage, speechPercent);
        SetLiveText(
            OnboardingSpeechDownloadDetailText,
            snapshot?.Speech.StatusDetail ?? (speechStage == OnboardingDownloadStage.Installed
                ? "Verified and ready on this PC."
                : "Preparing speech model download..."));

        var showPolishCard = _onboarding.RequiresLocalEgOne;
        OnboardingPolishDownloadCard.Visibility = showPolishCard ? Visibility.Visible : Visibility.Collapsed;
        if (showPolishCard)
        {
            var polishName = EnginePolishPresentation.DescribeEgOneTier(
                EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).Title;
            OnboardingPolishDownloadNameText.Text = $"{polishName} AI polish";

            var polishSnapshot = snapshot?.Polish;
            var polishStage = polishSnapshot?.Stage ?? (_onboarding.PolishModel == SpeechModelDelivery.Installed
                ? OnboardingDownloadStage.Installed
                : OnboardingDownloadStage.Pending);
            var polishPercent = polishSnapshot?.Percent ?? (polishStage == OnboardingDownloadStage.Installed ? 100 : 0);
            OnboardingPolishDownloadProgress.Value = Math.Clamp(polishPercent, 0, 100);
            OnboardingPolishDownloadProgress.IsIndeterminate = polishStage == OnboardingDownloadStage.Activating;
            OnboardingPolishDownloadBadgeText.Text = FormatDownloadStageBadge(polishStage, polishPercent);
            SetLiveText(
                OnboardingPolishDownloadDetailText,
                polishSnapshot?.StatusDetail ?? (polishStage == OnboardingDownloadStage.Installed
                    ? "Verified and ready on this PC."
                    : "Waiting to download local AI polish..."));
        }

        var canCancel = snapshot?.Speech.CanCancel == true || snapshot?.Polish?.CanCancel == true;
        var canRetry = snapshot?.Speech.CanRetry == true || snapshot?.Polish?.CanRetry == true;
        var polishFailed = snapshot?.Polish is { Stage: OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled or OnboardingDownloadStage.CannotDownload };
        OnboardingDownloadCancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        OnboardingDownloadSkipPolishButton.Visibility = showPolishCard && polishFailed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatDownloadStageBadge(OnboardingDownloadStage stage, double percent) => stage switch
    {
        OnboardingDownloadStage.Installed => "Ready",
        OnboardingDownloadStage.Downloading => $"{Math.Round(percent):0}%",
        OnboardingDownloadStage.Activating => "Starting",
        OnboardingDownloadStage.Cancelled => "Paused",
        OnboardingDownloadStage.Failed => "Failed",
        OnboardingDownloadStage.CannotDownload => "Unavailable",
        _ => "Queued",
    };

    private void DrawOnboardingPermissions()
    {
        var consent = WindowsMicrophoneConsent.Read();
        _onboardingMicConsent = consent;
        var allowed = consent != MicrophoneConsent.Blocked;
        OnboardingMicAllowedBadge.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        OnboardingMicNeedsAttentionBadge.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        var detail = _onboardingMicrophone?.Sentence ?? (allowed
            ? "Windows microphone access is allowed and ready for dictation."
            : "Microphone access is turned off for desktop apps in Windows Privacy Settings.");
        SetLiveText(OnboardingMicStatusText, detail);
    }

    private void DrawOnboardingKey()
    {
        var currentKey = _onboardingKeyCaptureSession is not null
            ? "Press key..."
            : (_onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? DictationPreferences.Default.PushToTalkGesture);
        OnboardingKeycapText.Text = currentKey;
        OnboardingKeycapHintText.Text = _onboardingKeyCaptureSession is not null
            ? "Listening for your shortcut. Press Escape to cancel."
            : "Click the keycap to capture a custom key, or pick a preset below.";

        ApplyPresetButtonStyle(OnboardingPresetCtrlWinButton, currentKey);
        ApplyPresetButtonStyle(OnboardingPresetRightCtrlButton, currentKey);
        ApplyPresetButtonStyle(OnboardingPresetF8Button, currentKey);
        ApplyPresetButtonStyle(OnboardingPresetAltSpaceButton, currentKey);

        var activeBrush = Application.Current.Resources["BrandAccentBrush"] as Brush;
        var dividerBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
        var successStyle = Application.Current.Resources["BrandSuccessChipStyle"] as Style;
        var successTextStyle = Application.Current.Resources["BrandSuccessChipTextStyle"] as Style;
        var neutralStyle = Application.Current.Resources["BrandNeutralChipStyle"] as Style;
        var neutralTextStyle = Application.Current.Resources["BrandNeutralChipTextStyle"] as Style;

        OnboardingRehearsalHoldCard.BorderBrush = _onboardingRehearsalKeyDown || _onboardingHoldRehearsed
            ? activeBrush
            : dividerBrush;
        OnboardingRehearsalHoldBadge.Style = _onboardingHoldRehearsed ? successStyle : neutralStyle;
        OnboardingRehearsalHoldBadgeText.Style = _onboardingHoldRehearsed ? successTextStyle : neutralTextStyle;
        OnboardingRehearsalHoldBadgeText.Text = _onboardingHoldRehearsed ? "Tried" : "Try holding";

        OnboardingRehearsalDoubleTapCard.BorderBrush = _onboardingDoubleTapRehearsed
            ? activeBrush
            : dividerBrush;
        OnboardingRehearsalDoubleTapBadge.Style = _onboardingDoubleTapRehearsed ? successStyle : neutralStyle;
        OnboardingRehearsalDoubleTapBadgeText.Style = _onboardingDoubleTapRehearsed ? successTextStyle : neutralTextStyle;
        OnboardingRehearsalDoubleTapBadgeText.Text = _onboardingDoubleTapRehearsed ? "Tried" : "Tap twice";
    }

    private static void ApplyPresetButtonStyle(Button button, string activeKey)
    {
        var isMatch = button.Tag is string tag && string.Equals(tag, activeKey, StringComparison.OrdinalIgnoreCase);
        if (Application.Current.Resources[isMatch ? "BrandQuietActiveButtonStyle" : "BrandQuietButtonStyle"] is Style style)
        {
            button.Style = style;
        }
    }

    private void DrawOnboardingGettingReady()
    {
        var speechReady = _onboarding.IsSpeechReady;
        var speechStage = _onboardingDownloadSnapshot?.Speech.Stage;
        var speechPercent = _onboardingDownloadSnapshot?.Speech.Percent ?? 0;
        ApplyGateRowStyle(
            OnboardingGateSpeechBadge,
            OnboardingGateSpeechBadgeText,
            speechReady ? "Ready" : speechStage == OnboardingDownloadStage.Failed ? "Failed" : "Setting up",
            speechReady ? "BrandSuccessChipStyle" : speechStage == OnboardingDownloadStage.Failed ? "BrandErrorChipStyle" : "BrandAccentChipStyle",
            speechReady ? "BrandSuccessChipTextStyle" : speechStage == OnboardingDownloadStage.Failed ? "BrandErrorChipTextStyle" : "BrandAccentChipTextStyle");
        OnboardingGateSpeechDetailText.Text = speechReady
            ? $"{_onboarding.Choices.SpeechEngine} is ready for dictation."
            : (_onboardingDownloadSnapshot?.Speech.StatusDetail ?? "Preparing speech model...");
        OnboardingGateSpeechProgress.Visibility = !speechReady && speechStage == OnboardingDownloadStage.Downloading
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnboardingGateSpeechProgress.Value = Math.Clamp(speechPercent, 0, 100);

        OnboardingGatePolishRow.Visibility = _onboarding.RequiresLocalEgOne ? Visibility.Visible : Visibility.Collapsed;
        if (_onboarding.RequiresLocalEgOne)
        {
            var polishReady = _onboarding.IsPolishReadyForGate;
            var polishStage = _onboardingDownloadSnapshot?.Polish?.Stage;
            var polishPercent = _onboardingDownloadSnapshot?.Polish?.Percent ?? 0;
            ApplyGateRowStyle(
                OnboardingGatePolishBadge,
                OnboardingGatePolishBadgeText,
                polishReady ? "Ready" : polishStage is OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled ? "Needs attention" : "Downloading",
                polishReady ? "BrandSuccessChipStyle" : polishStage is OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled ? "BrandWarningChipStyle" : "BrandAccentChipStyle",
                polishReady ? "BrandSuccessChipTextStyle" : polishStage is OnboardingDownloadStage.Failed or OnboardingDownloadStage.Cancelled ? "BrandWarningChipTextStyle" : "BrandAccentChipTextStyle");
            OnboardingGatePolishDetailText.Text = polishReady
                ? "Local AI polish is ready."
                : (_onboardingDownloadSnapshot?.Polish?.StatusDetail ?? "Finishing EG-1 download...");
            OnboardingGatePolishProgress.Visibility = !polishReady && polishStage == OnboardingDownloadStage.Downloading
                ? Visibility.Visible
                : Visibility.Collapsed;
            OnboardingGatePolishProgress.Value = Math.Clamp(polishPercent, 0, 100);
        }

        var micAllowed = _onboarding.MicrophoneAllowed;
        ApplyGateRowStyle(
            OnboardingGateMicBadge,
            OnboardingGateMicBadgeText,
            micAllowed ? "Allowed" : "Needs attention",
            micAllowed ? "BrandSuccessChipStyle" : "BrandWarningChipStyle",
            micAllowed ? "BrandSuccessChipTextStyle" : "BrandWarningChipTextStyle");
        OnboardingGateMicDetailText.Text = micAllowed
            ? "Microphone access verified."
            : "Turn on desktop microphone access in Windows Privacy Settings.";
        OnboardingGateMicSettingsButton.Visibility = micAllowed ? Visibility.Collapsed : Visibility.Visible;

        var decision = _onboarding.EvaluateGate();
        OnboardingGateRetryButton.Visibility = decision == OnboardingGateDecision.PolishFailedContinueWithout ||
            _onboarding.Model is SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled
                ? Visibility.Visible
                : Visibility.Collapsed;
        OnboardingGateSkipPolishButton.Visibility = decision is OnboardingGateDecision.WaitingWithPolishDownloading
            or OnboardingGateDecision.PolishFailedContinueWithout
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static void ApplyGateRowStyle(
        Border badge,
        TextBlock text,
        string label,
        string badgeStyleKey,
        string textStyleKey)
    {
        text.Text = label;
        if (Application.Current.Resources[badgeStyleKey] is Style badgeStyle)
        {
            badge.Style = badgeStyle;
        }
        if (Application.Current.Resources[textStyleKey] is Style textStyle)
        {
            text.Style = textStyle;
        }
    }

    private void DrawOnboardingPractice()
    {
        var activeKey = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win";
        OnboardingPracticeKeyChipText.Text = _onboarding.Choices.RecordingMode == DictationRecordingMode.Toggle
            ? $"Toggle: {activeKey}"
            : $"Hold: {activeKey} (or double-press for hands-free)";
        OnboardingPracticeFootnote.Text = _onboarding.TakeInFlight
            ? OnboardingCopy.PracticeBusy
            : OnboardingCopy.PracticeFootnote;
        OnboardingPracticeMicrophoneButton.Visibility = _onboarding.PracticeState == OnboardingPracticeState.CannotHear
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DrawOnboardingClosing()
    {
        var beat = _onboarding.CurrentClosingBeat;
        OnboardingClosingBeatStepText.Text = $"{beat.Index + 1} OF {OnboardingFlow.ClosingBeatCount}";
        OnboardingClosingBeatTitle.Text = beat.Title;
        OnboardingClosingBeatSubtitle.Text = beat.Subtitle;
        OnboardingClosingBeatIcon.Glyph = beat.Kind switch
        {
            OnboardingClosingBeatKind.Apps => "\uE71D",
            OnboardingClosingBeatKind.Land => "\uE8A7",
            OnboardingClosingBeatKind.Copied => "\uE8C8",
            OnboardingClosingBeatKind.Learn => "\uE82D",
            OnboardingClosingBeatKind.More => "\uE713",
            OnboardingClosingBeatKind.Yours => "\uE720",
            _ => "\uE73E",
        };

        var speechName = _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper ? "Whisper" : "Parakeet";
        var polishName = _onboarding.Choices.PolishChoice switch
        {
            OnboardingPolishChoiceKind.EgOne => EnginePolishPresentation.DescribeEgOneTier(
                EgOneModelCatalog.NormalizeOrDefault(_onboarding.Choices.EgOneModelId)).Title,
            OnboardingPolishChoiceKind.OpenAI => "OpenAI",
            OnboardingPolishChoiceKind.Claude => "Claude",
            OnboardingPolishChoiceKind.Gemini => "Gemini",
            OnboardingPolishChoiceKind.Ollama => "Ollama",
            _ => "Off",
        };
        var keyName = _onboarding.Choices.HotkeyGestureText ?? _onboarding.RecordingKey ?? "Ctrl+Win";

        OnboardingClosingSummaryText.Text = OnboardingCopy.FormatClosingSummary(beat, speechName, polishName, keyName);

        var languageLabel = string.Equals(_onboarding.Choices.LanguageCode, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase)
            ? LanguageCatalog.AutomaticTitle
            : LanguageCatalog.TryGetEntry(_onboarding.Choices.LanguageCode, out var entry)
                ? entry.DisplayName
                : _onboarding.Choices.LanguageCode;
        OnboardingClosingLanguageSummaryText.Text = languageLabel;
        OnboardingClosingSpeechSummaryText.Text = speechName;
        OnboardingClosingPolishSummaryText.Text = polishName;
        OnboardingClosingKeySummaryText.Text = keyName;

        var activeBrush = Application.Current.Resources["BrandAccentSolidBrush"] as Brush;
        var idleBrush = Application.Current.Resources["BrandDividerBrush"] as Brush;
        for (var i = 0; i < _onboardingClosingDotButtons.Length; i++)
        {
            if (_onboardingClosingDotButtons[i].Content is Border dot)
            {
                dot.Background = i == beat.Index ? activeBrush : idleBrush;
            }
        }
    }

    private void DrawOnboardingFooter(OnboardingStep step)
    {
        var (ctaText, ctaEnabled, skipText, showSkip) = step switch
        {
            OnboardingStep.Welcome => ("Set up dictation", true, string.Empty, false),
            OnboardingStep.PolishIntro => ("Set up AI polish", true, "Skip AI polish", true),
            OnboardingStep.Permissions => ("Continue", _onboarding.MicrophoneAllowed, string.Empty, false),
            OnboardingStep.GettingReady when _onboarding.CanSkipAheadDuringGate => ("Skip ahead", true, "Continue without AI polish", true),
            OnboardingStep.GettingReady when _onboarding.CanContinueWithoutAiPolishOnGate => ("Continue without AI polish", true, string.Empty, false),
            OnboardingStep.GettingReady => ("Continue", _onboarding.EvaluateGate() == OnboardingGateDecision.ReadyAutoSkip, string.Empty, false),
            OnboardingStep.Practice => ("Continue", _onboarding.CanFinishPractice, "Skip practice", _onboarding.CanSkipPractice),
            OnboardingStep.Closing => ("Start using EnviousWispr", true, string.Empty, false),
            _ => ("Continue", true, string.Empty, false),
        };

        FinishOnboardingButton.Content = ctaText;
        FinishOnboardingButton.IsEnabled = ctaEnabled;
        OnboardingSkipButton.Content = skipText;
        OnboardingSkipButton.Visibility = showSkip ? Visibility.Visible : Visibility.Collapsed;
        OnboardingFooterText.Visibility = step is OnboardingStep.Welcome or OnboardingStep.Closing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ArmClosingBeatTimer()
    {
        _onboardingBeat.Stop();
        if (_onboarding.Step != OnboardingStep.Closing ||
            _onboardingClosingPaused ||
            !ShouldAnimateMotion() ||
            _onboarding.ClosingBeatIndex + 1 >= OnboardingFlow.ClosingBeatCount)
        {
            return;
        }

        var seconds = Math.Max(2.5, _onboarding.CurrentClosingBeat.DurationSeconds);
        _onboardingBeat.Interval = TimeSpan.FromSeconds(seconds);
        _onboardingBeat.Tick -= OnClosingBeatTick;
        _onboardingBeat.Tick += OnClosingBeatTick;
        _onboardingBeat.Start();
    }

    private void OnClosingBeatTick(object? sender, object e)
    {
        _onboardingBeat.Stop();
        if (OnboardingView.Visibility != Visibility.Visible ||
            _onboarding.Step != OnboardingStep.Closing ||
            _onboardingClosingPaused)
        {
            return;
        }

        if (_onboarding.ClosingBeatIndex + 1 < OnboardingFlow.ClosingBeatCount)
        {
            _onboarding.AdvanceClosingBeat();
            RenderOnboarding();
            ArmClosingBeatTimer();
        }
    }

    private void ArmMicrophonePoll()
    {
        var shouldPoll = OnboardingView.Visibility == Visibility.Visible &&
            (_onboarding.Step is OnboardingStep.Permissions or OnboardingStep.GettingReady ||
             _onboarding.PracticeState == OnboardingPracticeState.CannotHear);
        if (!shouldPoll)
        {
            _onboardingMicrophonePoll.Stop();
            return;
        }

        if (!_onboardingMicrophonePoll.IsEnabled)
        {
            _onboardingMicrophonePoll.Tick -= OnMicrophonePoll;
            _onboardingMicrophonePoll.Tick += OnMicrophonePoll;
            _onboardingMicrophonePoll.Start();
        }
    }

    private async void OnMicrophonePoll(object? sender, object e)
    {
        if (OnboardingView.Visibility != Visibility.Visible || _shuttingDownProductWindows)
        {
            _onboardingMicrophonePoll.Stop();
            return;
        }

        try
        {
            await LoadMicrophonesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            ObserveMicrophoneForOnboarding(
                _onboardingMicrophone ?? MicrophoneReadinessReport.For(MicrophoneConsent.Unknown, null, enumerationFailed: true),
                WindowsMicrophoneConsent.Read());
        }
    }

    private void StopOnboardingTimers()
    {
        _onboardingBeat.Stop();
        _onboardingMicrophonePoll.Stop();
        _onboardingKeyHoldTimer.Stop();
        CancelOnboardingKeyCapture();
        if (_onboardingLipsAnimating)
        {
            _onboardingLipsAnimating = false;
            OnboardingLipsBreathStoryboard.Stop();
        }
    }

    private static bool ShouldAnimateMotion()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch (COMException)
        {
            return true;
        }
    }

    private Control? FocusedOnboardingControl() =>
        _onboardingFocusOrphaned ||
        OnboardingView.XamlRoot is not { } root ||
        FocusManager.GetFocusedElement(root) is not Control focused ||
        !IsInside(focused, OnboardingView)
            ? null
            : focused;

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void RescueOnboardingFocus(Control? focusedBefore)
    {
        var wasOrphaned = _onboardingFocusOrphaned;
        _onboardingFocusOrphaned = focusedBefore is not null &&
            (focusedBefore.Visibility != Visibility.Visible || !focusedBefore.IsEnabled);
        if (wasOrphaned && !_onboardingFocusOrphaned)
        {
            FocusOnboardingScreen(_onboarding.Step);
        }
    }

    private void FocusOnboardingScreen(OnboardingStep step)
    {
        switch (step)
        {
            case OnboardingStep.Language:
                OnboardingLanguageSearchBox.Focus(FocusState.Programmatic);
                break;
            case OnboardingStep.Practice:
                OnboardingPracticeBox.Focus(FocusState.Programmatic);
                break;
            case OnboardingStep.Permissions when !_onboarding.MicrophoneAllowed:
                OnboardingOpenMicSettingsButton.Focus(FocusState.Programmatic);
                break;
            case OnboardingStep.Key:
                OnboardingKeycapButton.Focus(FocusState.Programmatic);
                break;
            default:
                FinishOnboardingButton.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void Announce(string text)
    {
        if (OnboardingView.Visibility == Visibility.Visible &&
            !string.IsNullOrWhiteSpace(text) &&
            FrameworkElementAutomationPeer.FromElement(OnboardingTitle) is { } peer)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.ImportantMostRecent,
                text,
                "EnviousWisprOnboarding");
        }
    }

    public sealed record OnboardingLanguageRow(
        string Code,
        string DisplayTitle,
        string Subtitle,
        string EngineLabel,
        bool IsSelected = false)
    {
        public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }
}
