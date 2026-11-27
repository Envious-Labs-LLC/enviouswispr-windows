using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Presentation;
using EnviousWispr.ModelDelivery;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App;

/// <summary>
/// Minimal compile and runtime bridge mapping <see cref="OnboardingFlow"/> onto the existing
/// 5-panel <c>OnboardingView</c> until Wave C introduces the full 14-screen XAML UI.
/// </summary>
public sealed partial class MainWindow
{
    private enum LegacyOnboardingPanel
    {
        Welcome,
        Checklist,
        Permission,
        Ready,
        WarmingUp,
        Practice,
    }

    private const string WelcomeSubtitle = "The privacy-first dictation app built for Windows.";

    private static readonly TimeSpan ChecklistCompleteBeat = TimeSpan.FromMilliseconds(800);

    private static readonly TimeSpan MicrophonePollInterval = TimeSpan.FromSeconds(2);

    private readonly OnboardingFlow _onboarding = new();

    private readonly DispatcherTimer _onboardingBeat = new();

    private readonly DispatcherTimer _onboardingMicrophonePoll = new() { Interval = MicrophonePollInterval };

    private string _onboardingEngineSentence = OnboardingCopy.StartingEngine;

    private ModelDeliveryPresentation? _onboardingModel;

    private MicrophoneReadiness? _onboardingMicrophone;

    private bool _dictationActive;

    private bool _warmingFloorElapsed;

    private string? _welcomeTitle;

    private LegacyOnboardingPanel? _drawnPanel;

    private OnboardingPracticeState? _drawnPractice;

    private OnboardingWarmingOutcome? _drawnWarming;

    private OnboardingChecklistStatus? _drawnModelItem;

    private bool _onboardingFocusOrphaned;

    public event Action? TranscriptionRetryRequested;

    public event Action? DictationEnded;

    public event Action<string, string?, bool>? OnboardingDualDownloadRequested;

    public event Action? OnboardingDualDownloadCancelRequested;

    public event Action? OnboardingDualDownloadRetryRequested;

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

    internal void RetryOnboardingDualDownload() =>
        OnboardingDualDownloadRetryRequested?.Invoke();

    internal void ObserveOnboardingDownloadSnapshot(OnboardingDownloadSnapshot snapshot)
    {
        _onboarding.ReportModel(ToSpeechModelDelivery(snapshot.Speech.Stage));
        if (snapshot.Polish is { } polish)
        {
            _onboarding.ReportPolishModel(ToSpeechModelDelivery(polish.Stage));
        }

        RenderOnboarding();
    }

    private LegacyOnboardingPanel CurrentLegacyPanel() => _onboarding.Step switch
    {
        OnboardingStep.Welcome => LegacyOnboardingPanel.Welcome,
        OnboardingStep.Downloads => LegacyOnboardingPanel.Checklist,
        OnboardingStep.Permissions => LegacyOnboardingPanel.Permission,
        OnboardingStep.Key => LegacyOnboardingPanel.Ready,
        OnboardingStep.GettingReady => LegacyOnboardingPanel.WarmingUp,
        _ => LegacyOnboardingPanel.Practice,
    };

    public void SetTranscriptionReadiness(SpeechEngineReadiness readiness)
    {
        _onboarding.ReportEngine(readiness);
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
            _onboarding.TakeEnded(OnboardingPracticeBox.Text);
            if (_onboarding.PracticeState == OnboardingPracticeState.MissedTheBox && OnboardingView.Visibility == Visibility.Visible)
            {
                OnboardingPracticeBox.Focus(FocusState.Programmatic);
            }

            DictationEnded?.Invoke();
        }

        RenderOnboarding();
    }

    private void ObserveStatusForOnboarding(DictationStatus status)
    {
        if (OnboardingView.Visibility != Visibility.Visible)
        {
            return;
        }

        if (CurrentLegacyPanel() == LegacyOnboardingPanel.Practice)
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

    private void ObserveDeliveryForOnboarding(DeliveryLanding landing) =>
        _onboarding.TakeDelivered(OnboardingTakeDeliveries.For(landing));

    private void ObserveModelForOnboarding(ModelDeliveryPresentation presentation)
    {
        if (presentation.Stage == SpeechModelDelivery.Unknown)
        {
            return;
        }

        _onboardingModel = presentation;
        _onboarding.ReportModel(presentation.Stage);
        RenderOnboarding();
    }

    private void ObserveMicrophoneForOnboarding(MicrophoneReadiness readiness, MicrophoneConsent consent)
    {
        _onboardingMicrophone = readiness;
        _onboarding.ReportMicrophone(consent);
        RenderOnboarding();
    }

    private void ObserveRecordingKeyForOnboarding(string gesture, EnviousWispr.Core.Settings.DictationRecordingMode mode)
    {
        _onboarding.ReportRecordingKey(gesture, mode);
        RenderOnboarding();
    }

    private void ObserveRecordingKeyProblemForOnboarding(string problem)
    {
        _onboarding.ReportRecordingKeyUnavailable(problem);
        RenderOnboarding();
    }

    private void BeginOnboardingVisit()
    {
        _welcomeTitle ??= OnboardingTitle.Text;
        _onboarding.Begin();
        OnboardingPracticeBox.Text = string.Empty;
        _drawnPanel = null;
        _drawnPractice = null;
        _drawnWarming = null;
        _drawnModelItem = null;
        RenderOnboarding();
    }

    private async void FinishOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        switch (CurrentLegacyPanel())
        {
            case LegacyOnboardingPanel.Welcome:
                _onboarding.StartLegacyShellFromWelcome();
                break;
            case LegacyOnboardingPanel.Checklist:
                _onboarding.AdvanceLegacyChecklistToPermissions();
                break;
            case LegacyOnboardingPanel.Permission:
                _onboarding.ContinueFromPermissions(WindowsMicrophoneConsent.Read());
                break;
            case LegacyOnboardingPanel.Ready:
                _onboarding.BeginLegacyWarming();
                break;
            case LegacyOnboardingPanel.Practice:
                if (_onboarding.MayFinish(OnboardingExit.FinishSetup))
                {
                    await FinishOnboardingAsync().ConfigureAwait(true);
                    return;
                }

                break;
        }

        RenderOnboarding();
    }

    private async void OnboardingSkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboarding.MayFinish(OnboardingExit.SkipWarming) ||
            _onboarding.MayFinish(OnboardingExit.SkipPractice) ||
            _onboarding.MayFinish(OnboardingExit.SkipUnavailableModel) ||
            _onboarding.MayFinish(OnboardingExit.SkipAheadWithoutPolish) ||
            _onboarding.MayFinish(OnboardingExit.ContinueWithoutPolish))
        {
            await FinishOnboardingAsync().ConfigureAwait(true);
        }
    }

    private void OnboardingRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((_onboarding.Model is SpeechModelDelivery.Failed or SpeechModelDelivery.Cancelled or SpeechModelDelivery.Missing) &&
            _onboarding.Engine != SpeechEngineReadiness.Ready)
        {
            _onboarding.DownloadStarted();
            ModelDownloadRequested?.Invoke();
        }
        else
        {
            TranscriptionRetryRequested?.Invoke();
        }
    }

    private void OnboardingCancelSetupButton_Click(object sender, RoutedEventArgs e)
    {
        _onboarding.RequestCancel();
        ModelDownloadCancelRequested?.Invoke();
        RenderOnboarding();
    }

    private void OnboardingPracticeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _onboarding.PracticeTextChanged(OnboardingPracticeBox.Text);
        RenderOnboarding();
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

        ShowOnboarding(show: true);
    }

    private async Task FinishOnboardingAsync()
    {
        if (!_settings.HasCompletedOnboarding &&
            !await TrySaveAsync(
                OnboardingFlow.Completed,
                "Setup complete",
                "Your choices were saved on this PC.").ConfigureAwait(true))
        {
            return;
        }

        ShowOnboarding(show: false);
        ProductNavigation.SelectedItem = HistoryNavItem;
        HistoryNavItem.Focus(FocusState.Programmatic);
    }

    private void RenderOnboarding()
    {
        if (OnboardingView.Visibility != Visibility.Visible)
        {
            StopOnboardingTimers();
            return;
        }

        if (_onboarding.ShouldStartDownload)
        {
            _onboarding.DownloadStarted();
            ModelDownloadRequested?.Invoke();
        }

        if (CurrentLegacyPanel() == LegacyOnboardingPanel.WarmingUp && _warmingFloorElapsed)
        {
            _onboarding.BeginPractice();
        }

        var currentPanel = CurrentLegacyPanel();
        var screenChanged = _drawnPanel != currentPanel;
        if (screenChanged)
        {
            ArmBeatFor(currentPanel);
        }

        ArmMicrophonePoll();
        if (_onboarding.ShouldAdvanceFromChecklist && !_onboardingBeat.IsEnabled)
        {
            ArmBeat(ChecklistCompleteBeat);
        }

        var stepNumber = _onboarding.LegacyStepNumber;
        OnboardingStepText.Text = OnboardingCopy.StepOf(stepNumber);
        SetPanel(OnboardingWelcomePanel, LegacyOnboardingPanel.Welcome);
        SetPanel(OnboardingChecklistPanel, LegacyOnboardingPanel.Checklist);
        SetPanel(OnboardingPermissionPanel, LegacyOnboardingPanel.Permission);
        SetPanel(OnboardingReadyPanel, LegacyOnboardingPanel.Ready);
        SetPanel(OnboardingWarmingPanel, LegacyOnboardingPanel.WarmingUp);
        SetPanel(OnboardingPracticePanel, LegacyOnboardingPanel.Practice);
        OnboardingFooterText.Visibility = currentPanel == LegacyOnboardingPanel.Welcome
            ? Visibility.Visible
            : Visibility.Collapsed;

        var (title, subtitle) = currentPanel switch
        {
            LegacyOnboardingPanel.Welcome => (_welcomeTitle ?? OnboardingTitle.Text, WelcomeSubtitle),
            LegacyOnboardingPanel.Permission =>
                ("Almost there. Just one permission.", "This lets EnviousWispr listen for you."),
            LegacyOnboardingPanel.Checklist => ("Warming up the AI\u2026", _onboarding.Model is SpeechModelDelivery.Missing
                    or SpeechModelDelivery.Downloading or SpeechModelDelivery.Cancelled or SpeechModelDelivery.Failed
                ? "Downloading the local speech model so EnviousWispr can run privately on your PC."
                : "Setting up the local speech model so EnviousWispr can run privately on your PC."),
            LegacyOnboardingPanel.Ready => ("Ready to Wispr!", "This is the key you dictate with.\nPress GET STARTED! to try it."),
            LegacyOnboardingPanel.WarmingUp => _onboarding.WarmingOutcome == OnboardingWarmingOutcome.Failed
                ? (OnboardingCopy.WarmingFailedTitle, OnboardingCopy.WarmingFailedSubtitle)
                : (OnboardingCopy.WarmingTitle, OnboardingCopy.WarmingSubtitle),
            _ => (
                OnboardingCopy.PracticeHeadline(_onboarding.PracticeState, _onboarding.PracticeSucceeded),
                OnboardingCopy.PracticeSubhead(
                    _onboarding.PracticeState,
                    _onboarding.PracticeSucceeded,
                    _onboarding.RecordingKey ?? "your dictation key",
                    _onboarding.RecordingMode,
                    _onboarding.MissedTo)),
        };
        OnboardingTitle.Text = title;
        OnboardingSubtitle.Text = subtitle;

        var focusedBefore = FocusedOnboardingControl();
        DrawChecklist();
        DrawPermission();
        DrawReady();
        DrawWarming();
        DrawPractice();
        DrawFooter();

        if (!screenChanged)
        {
            RescueOnboardingFocus(focusedBefore);
        }

        if (screenChanged)
        {
            _drawnPanel = currentPanel;
            _drawnPractice = _onboarding.PracticeState;
            _drawnWarming = _onboarding.WarmingOutcome;
            OnboardingScroll.ChangeView(null, 0, null, disableAnimation: true);
            FocusOnboardingScreen();
            Announce($"{OnboardingCopy.StepOf(stepNumber)}. {title}. {subtitle}");
        }
        else if (currentPanel == LegacyOnboardingPanel.Practice && _drawnPractice != _onboarding.PracticeState)
        {
            _drawnPractice = _onboarding.PracticeState;
            Announce($"{title}. {subtitle}");
        }
        else if (currentPanel == LegacyOnboardingPanel.WarmingUp && _drawnWarming != _onboarding.WarmingOutcome)
        {
            _drawnWarming = _onboarding.WarmingOutcome;
            Announce($"{title}. {subtitle}");
            if (_onboarding.WarmingOutcome == OnboardingWarmingOutcome.Failed)
            {
                OnboardingWarmingRetryButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void SetPanel(UIElement panel, LegacyOnboardingPanel targetPanel) =>
        panel.Visibility = CurrentLegacyPanel() == targetPanel ? Visibility.Visible : Visibility.Collapsed;

    private void DrawChecklist()
    {
        var item = _onboarding.ModelItem;
        OnboardingModelPendingMark.Visibility = item == OnboardingChecklistStatus.Pending ? Visibility.Visible : Visibility.Collapsed;
        OnboardingModelWorkingMark.Visibility = item == OnboardingChecklistStatus.InProgress ? Visibility.Visible : Visibility.Collapsed;
        OnboardingModelWorkingMark.IsActive = item == OnboardingChecklistStatus.InProgress;
        OnboardingModelDoneMark.Visibility = item == OnboardingChecklistStatus.Completed ? Visibility.Visible : Visibility.Collapsed;
        OnboardingModelErrorMark.Visibility = item == OnboardingChecklistStatus.Error ? Visibility.Visible : Visibility.Collapsed;

        var downloading = (_onboarding.Model is SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating) &&
            _onboarding.Engine != SpeechEngineReadiness.Ready;
        OnboardingModelText.Text = item switch
        {
            OnboardingChecklistStatus.Completed => _onboardingEngineSentence,
            OnboardingChecklistStatus.InProgress when downloading && _onboardingModel is { } model => model.Text,
            OnboardingChecklistStatus.InProgress => OnboardingCopy.StartingEngine,
            _ => OnboardingCopy.OneTimeSetup,
        };
        OnboardingModelProgress.Visibility = downloading && _onboardingModel?.Percent is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnboardingModelProgress.Value = _onboardingModel?.Percent ?? 0;

        var key = _onboarding.KeyItem;
        OnboardingKeyPendingMark.Visibility = key == OnboardingChecklistStatus.InProgress ? Visibility.Visible : Visibility.Collapsed;
        OnboardingKeyDoneMark.Visibility = key == OnboardingChecklistStatus.Completed ? Visibility.Visible : Visibility.Collapsed;
        OnboardingKeyErrorMark.Visibility = key == OnboardingChecklistStatus.Error ? Visibility.Visible : Visibility.Collapsed;
        OnboardingHotkeyText.Text = OnboardingCopy.KeyLine(_onboarding);

        var note = _onboarding.CancelRequested
            ? OnboardingCopy.Cancelling
            : _onboarding.SetupPaused
                ? OnboardingCopy.SetupPaused
                : item == OnboardingChecklistStatus.Error
                    ? (_onboarding.Model is SpeechModelDelivery.Failed or SpeechModelDelivery.CannotDownload) &&
                        _onboardingModel is { } failed
                        ? failed.Text
                        : OnboardingCopy.EngineUnavailable
                    : string.Empty;
        SetLiveRegion(
            OnboardingSetupNote,
            note,
            note.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        OnboardingRetryButton.Content = _onboarding.SetupPaused ? OnboardingCopy.TrySetupAgain : OnboardingCopy.Retry;
        OnboardingRetryButton.Visibility = (_onboarding.SetupPaused && !_onboarding.CancelRequested) || _onboarding.CanRetrySetup
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnboardingCancelSetupButton.Visibility = _onboarding.CanCancelSetup ? Visibility.Visible : Visibility.Collapsed;

        if (_drawnModelItem != item)
        {
            _drawnModelItem = item;
            if (item == OnboardingChecklistStatus.Error &&
                CurrentLegacyPanel() == LegacyOnboardingPanel.Checklist &&
                OnboardingRetryButton.Visibility == Visibility.Visible)
            {
                OnboardingRetryButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void DrawPermission()
    {
        var readiness = _onboardingMicrophone;
        SetLiveText(OnboardingMicrophoneText, readiness?.Sentence ?? "Checking Windows audio devices\u2026");
        var allowed = _onboarding.MicrophoneConsent == MicrophoneConsent.Allowed;
        OnboardingMicrophoneAllowedBadge.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        OnboardingMicrophonePrivacyButton.Visibility = !allowed && (readiness?.OffersPrivacySettings ?? false)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DrawReady()
    {
        OnboardingKeycapText.Text = _onboarding.RecordingKey ?? "\u2026";
        OnboardingKeyUsageText.Text = _onboarding.RecordingKey is null
            ? OnboardingCopy.KeyLine(_onboarding)
            : OnboardingCopy.KeyUsage(_onboarding.RecordingMode);
        OnboardingHandsFreeText.Visibility = _onboarding.RecordingKeyHasGestures &&
            _onboarding.RecordingMode == EnviousWispr.Core.Settings.DictationRecordingMode.PushToTalk
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void DrawWarming()
    {
        var outcome = _onboarding.WarmingOutcome;
        OnboardingWarmingActivity.Visibility = outcome == OnboardingWarmingOutcome.Failed ? Visibility.Collapsed : Visibility.Visible;
        OnboardingWarmingRing.IsActive = CurrentLegacyPanel() == LegacyOnboardingPanel.WarmingUp && outcome != OnboardingWarmingOutcome.Failed;
        OnboardingWarmingFailure.Visibility = outcome == OnboardingWarmingOutcome.Failed ? Visibility.Visible : Visibility.Collapsed;
        SetLiveText(
            OnboardingWarmingFailureText,
            outcome == OnboardingWarmingOutcome.Failed ? OnboardingCopy.WarmingFailedMessage : string.Empty);
        OnboardingWarmingCaption.Visibility = outcome == OnboardingWarmingOutcome.Failed ? Visibility.Collapsed : Visibility.Visible;
        DrawWarmRow(OnboardingWarmMicMark, OnboardingWarmMicPending, _onboarding.MicrophoneAllowed);
        DrawWarmRow(OnboardingWarmKeyMark, OnboardingWarmKeyPending, _onboarding.RecordingKey is not null);
        DrawWarmRow(OnboardingWarmEngineMark, OnboardingWarmEnginePending, outcome == OnboardingWarmingOutcome.Ready);
    }

    private static void DrawWarmRow(FontIcon done, FontIcon pending, bool isDone)
    {
        done.Visibility = isDone ? Visibility.Visible : Visibility.Collapsed;
        pending.Visibility = isDone ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DrawPractice()
    {
        var cannotHear = _onboarding.PracticeState == OnboardingPracticeState.CannotHear;
        OnboardingPracticeFootnote.Text = cannotHear || _onboarding.PracticeSucceeded ? " " : OnboardingCopy.PracticeFootnote;
        OnboardingPracticeMicrophoneButton.Visibility = cannotHear ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawFooter()
    {
        var currentPanel = CurrentLegacyPanel();
        var (label, help, visible, enabled) = currentPanel switch
        {
            LegacyOnboardingPanel.Welcome => ("Get Started", "Starts setup.", true, true),
            LegacyOnboardingPanel.Checklist =>
                ("Continue", "Available once the speech model is ready.", true, _onboarding.ChecklistComplete),
            LegacyOnboardingPanel.Permission =>
                ("Continue", "Available once Windows allows microphone access.", true, _onboarding.MicrophoneAllowed),
            LegacyOnboardingPanel.Ready => ("GET STARTED!", "Checks the speech engine, then opens a box to try your first dictation.", true, true),
            LegacyOnboardingPanel.WarmingUp => ("Continue", string.Empty, false, false),
            _ => ("FINISH SETUP", "Available once your first dictation has reached the box. Opens EnviousWispr History.", true, _onboarding.CanFinishPractice),
        };
        FinishOnboardingButton.Content = label;
        AutomationProperties.SetHelpText(FinishOnboardingButton, help);
        FinishOnboardingButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FinishOnboardingButton.IsEnabled = enabled;

        var (skip, skipVisible, skipEnabled) = currentPanel switch
        {
            LegacyOnboardingPanel.WarmingUp => (
                _onboarding.WarmingOutcome == OnboardingWarmingOutcome.Failed
                    ? OnboardingCopy.WarmingSkipAfterFailure
                    : OnboardingCopy.WarmingSkip,
                true,
                true),
            LegacyOnboardingPanel.Practice => (
                _onboarding.TakeInFlight ? OnboardingCopy.PracticeBusy : OnboardingCopy.PracticeSkip,
                true,
                _onboarding.CanSkipPractice),
            _ when _onboarding.CanFinishWithoutModel => (OnboardingCopy.FinishWithoutModel, true, true),
            _ => (string.Empty, false, false),
        };
        OnboardingSkipButton.Content = skip;
        OnboardingSkipButton.Visibility = skipVisible ? Visibility.Visible : Visibility.Collapsed;
        OnboardingSkipButton.IsEnabled = skipEnabled;
    }

    private void FocusOnboardingScreen()
    {
        Control[] candidates = CurrentLegacyPanel() switch
        {
            LegacyOnboardingPanel.Practice => [OnboardingPracticeBox, OnboardingPracticeMicrophoneButton, OnboardingSkipButton],
            LegacyOnboardingPanel.WarmingUp => [OnboardingWarmingRetryButton, OnboardingSkipButton],
            LegacyOnboardingPanel.Permission => [OnboardingMicrophonePrivacyButton, FinishOnboardingButton],
            LegacyOnboardingPanel.Checklist => [OnboardingCancelSetupButton, OnboardingRetryButton, FinishOnboardingButton, OnboardingSkipButton],
            _ => [FinishOnboardingButton],
        };
        _onboardingFocusOrphaned = candidates.FirstOrDefault(CanTakeFocus)?.Focus(FocusState.Programmatic) != true;
    }

    private static bool CanTakeFocus(Control control) =>
        control.IsEnabled && IsOnAVisiblePage(control);

    private Control? FocusedOnboardingControl() =>
        OnboardingView.XamlRoot is { } root &&
        FocusManager.GetFocusedElement(root) is Control focused &&
        IsInside(focused, OnboardingView)
            ? focused
            : null;

    private void RescueOnboardingFocus(Control? focusedBefore)
    {
        var nowFocused = FocusedOnboardingControl();
        var lostFocus = focusedBefore is not null && nowFocused is null;
        if (!lostFocus && !_onboardingFocusOrphaned)
        {
            return;
        }

        FocusOnboardingScreen();
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void Announce(string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(OnboardingTitle)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(OnboardingTitle);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.ImportantMostRecent,
            text.Replace('\n', ' '),
            "EnviousWisprSetupStep");
    }

    private void ArmBeatFor(LegacyOnboardingPanel panel)
    {
        _onboardingBeat.Stop();
        _warmingFloorElapsed = false;
        if (panel == LegacyOnboardingPanel.WarmingUp)
        {
            ArmBeat(OnboardingFlow.WarmingDisplayFloor);
        }
    }

    private void ArmBeat(TimeSpan interval)
    {
        _onboardingBeat.Stop();
        _onboardingBeat.Interval = interval;
        _onboardingBeat.Tick -= OnOnboardingBeat;
        _onboardingBeat.Tick += OnOnboardingBeat;
        _onboardingBeat.Start();
    }

    private void OnOnboardingBeat(object? sender, object e)
    {
        _onboardingBeat.Stop();
        if (CurrentLegacyPanel() == LegacyOnboardingPanel.WarmingUp)
        {
            _warmingFloorElapsed = true;
        }
        else
        {
            _onboarding.AdvanceLegacyChecklistToPermissions();
        }

        RenderOnboarding();
    }

    private void ArmMicrophonePoll()
    {
        var waiting = (CurrentLegacyPanel() == LegacyOnboardingPanel.Permission && !_onboarding.MicrophoneAllowed) ||
            _onboarding.PracticeState == OnboardingPracticeState.CannotHear;
        if (!waiting)
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
        if (WindowsMicrophoneConsent.Read() == MicrophoneConsent.Blocked)
        {
            return;
        }

        _onboardingMicrophonePoll.Stop();
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
    }
}
