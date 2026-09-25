using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App;

/// <summary>The first-run flow's five screens: drawing what <see cref="OnboardingFlow"/> decides, and telling it what happened.</summary>
/// <remarks>
/// THE DECISIONS ARE NOT HERE. Which screen, what may move it on, what a take's ending means and every Windows
/// difference from macOS live in <see cref="OnboardingFlow"/> and are tested there; the sentences chosen from state
/// are <see cref="OnboardingCopy"/>. This file reads the live facts the window already receives - the engine, the
/// model download, the microphone switch, the recording key, the dictation edges - hands them over, and draws the
/// answer. Nothing here probes anything of its own: every readiness source is the one the rest of the window shows.
/// </remarks>
public sealed partial class MainWindow
{
    private const string WelcomeSubtitle = "The privacy-first dictation app built for Windows.";

    /// <summary>How long a completed checklist stays on screen before the permission, so the ticks read as a beat.</summary>
    private static readonly TimeSpan ChecklistCompleteBeat = TimeSpan.FromMilliseconds(800);

    /// <summary>How often a blocked microphone switch is read again while a screen is waiting on it: macOS's permission poll.</summary>
    private static readonly TimeSpan MicrophonePollInterval = TimeSpan.FromSeconds(2);

    private readonly OnboardingFlow _onboarding = new();

    private readonly DispatcherTimer _onboardingBeat = new();

    private readonly DispatcherTimer _onboardingMicrophonePoll = new() { Interval = MicrophonePollInterval };

    /// <summary>The engine's own sentence, shown on the checklist once it is ready ("ready on the processor" is true and worth saying).</summary>
    private string _onboardingEngineSentence = OnboardingCopy.StartingEngine;

    /// <summary>The last word from the model download, for the checklist's line and bar.</summary>
    private ModelDeliveryPresentation? _onboardingModel;

    private MicrophoneReadiness? _onboardingMicrophone;

    /// <summary>True between a dictation's first edge and its last, from the session's own record of it.</summary>
    private bool _dictationActive;

    /// <summary>True once the warm-up screen has been up for its display floor.</summary>
    private bool _warmingFloorElapsed;

    private string? _welcomeTitle;

    private OnboardingScreen? _drawnScreen;

    private OnboardingSetupPhase? _drawnPhase;

    private OnboardingPracticeState? _drawnPractice;

    private OnboardingWarmingOutcome? _drawnWarming;

    private OnboardingChecklistStatus? _drawnModelItem;

    /// <summary>True while the keyboard's last control was hidden and nothing on the screen could take it yet.</summary>
    private bool _onboardingFocusOrphaned;

    /// <summary>The person asked for local transcription to be started again, from the checklist or the warm-up.</summary>
    public event Action? TranscriptionRetryRequested;

    /// <summary>Whether local transcription can take a dictation, said by the code that started it.</summary>
    public void SetTranscriptionReadiness(SpeechEngineReadiness readiness)
    {
        _onboarding.ReportEngine(readiness);
        RenderOnboarding();
    }

    /// <summary>A dictation began or ended, from the edge the session records in its run state.</summary>
    public void SetDictationActive(bool active)
    {
        var ended = _dictationActive && !active;
        _dictationActive = active;
        RunSetupAgainButton.IsEnabled = !active;
        if (ended)
        {
            _onboarding.TakeEnded(OnboardingPracticeBox.Text);
            if (_onboarding.PracticeState == OnboardingPracticeState.MissedTheBox && OnboardingView.Visibility == Visibility.Visible)
            {
                // PUT THE CURSOR BACK, so the next attempt cannot miss for the same reason. The advice is useless if
                // acting on it needs a click nobody was told about (macOS, founder-found in live UAT).
                OnboardingPracticeBox.Focus(FocusState.Programmatic);
            }
        }

        RenderOnboarding();
    }

    /// <summary>Hands a status to the practice box, which needs to know when a take starts and whether it failed.</summary>
    private void ObserveStatusForOnboarding(DictationStatus status)
    {
        if (OnboardingView.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_onboarding.Screen == OnboardingScreen.Practice)
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

        // THE ENGINE'S OWN SENTENCE MAY HAVE CHANGED TOO, and the checklist shows it once the engine is ready.
        RenderOnboarding();
    }

    /// <summary>Where a take's words went, as the delivery reported it.</summary>
    private void ObserveDeliveryForOnboarding(DictationStatus delivered) =>
        _onboarding.TakeDelivered(delivered.State switch
        {
            DictationOverlayState.Success => OnboardingTakeDelivery.Delivered,
            DictationOverlayState.Warning => OnboardingTakeDelivery.Clipboard,
            _ => OnboardingTakeDelivery.Failed,
        });

    private void ObserveModelForOnboarding(ModelDeliveryPresentation presentation)
    {
        // THE GRAPHICS RUNTIME SHARES THE RECORD AND SAYS NOTHING ABOUT THE SPEECH MODEL. Only a stage the delivery
        // code set moves the checklist.
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

    /// <summary>Starts a visit at the welcome screen.</summary>
    private void BeginOnboardingVisit()
    {
        _welcomeTitle ??= OnboardingTitle.Text;
        _onboarding.Begin();
        OnboardingPracticeBox.Text = string.Empty;
        _drawnScreen = null;
        _drawnPhase = null;
        _drawnPractice = null;
        _drawnWarming = null;
        _drawnModelItem = null;
        RenderOnboarding();
    }

    private async void FinishOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_onboarding.Screen)
        {
            case OnboardingScreen.Welcome:
                _onboarding.GetStarted();
                break;
            case OnboardingScreen.SettingUp when _onboarding.SetupPhase == OnboardingSetupPhase.Checklist:
                _onboarding.AdvanceToPermission();
                break;
            case OnboardingScreen.SettingUp:
                // READ AGAIN AT THE PRESS, as macOS rechecks Accessibility at Continue: the switch can be turned off
                // between two polls, and a stale answer here is the whole failure the gate exists to prevent.
                _onboarding.ContinueFromPermission(WindowsMicrophoneConsent.Read());
                break;
            case OnboardingScreen.Ready:
                _onboarding.BeginWarming();
                break;
            case OnboardingScreen.Practice:
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
        // BOTH SKIPS FINISH SETUP, as on macOS: the warm-up's "skip ahead" and the practice's "skip this step" are
        // each the person choosing not to wait, and neither leaves them anywhere but the app.
        if (_onboarding.MayFinish(OnboardingExit.SkipWarming) || _onboarding.MayFinish(OnboardingExit.SkipPractice) ||
            _onboarding.MayFinish(OnboardingExit.SkipUnavailableModel))
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

    /// <summary>"Run setup again" on Diagnostics: the flow from the welcome, where macOS keeps its Restart Onboarding.</summary>
    /// <remarks>
    /// STORED AS NOT DONE, AS macOS STORES IT, so a person who closes the app part way through meets setup again on
    /// the next launch rather than a half-finished visit. Nothing else is touched.
    /// </remarks>
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
        ProductNavigation.SelectedItem = HomeNavItem;
        HomeNavItem.Focus(FocusState.Programmatic);
    }

    /// <summary>Draws the current screen, and carries out the moves the flow says are due.</summary>
    private void RenderOnboarding()
    {
        if (OnboardingView.Visibility != Visibility.Visible)
        {
            StopOnboardingTimers();
            return;
        }

        // THE MOVES THE FLOW DECIDES ARE DUE, before anything is drawn, so the screen is drawn once in its new state.
        if (_onboarding.ShouldStartDownload)
        {
            _onboarding.DownloadStarted();
            ModelDownloadRequested?.Invoke();
        }

        if (_onboarding.Screen == OnboardingScreen.WarmingUp && _warmingFloorElapsed)
        {
            _onboarding.BeginPractice();
        }

        var screenChanged = _drawnScreen != _onboarding.Screen || _drawnPhase != _onboarding.SetupPhase;
        if (screenChanged)
        {
            ArmBeatFor(_onboarding.Screen);
        }

        ArmMicrophonePoll();
        if (_onboarding.ShouldAdvanceFromChecklist && !_onboardingBeat.IsEnabled)
        {
            ArmBeat(ChecklistCompleteBeat);
        }

        OnboardingStepText.Text = OnboardingCopy.StepOf(_onboarding.StepNumber);
        SetPanel(OnboardingWelcomePanel, OnboardingScreen.Welcome);
        OnboardingChecklistPanel.Visibility = _onboarding.Screen == OnboardingScreen.SettingUp &&
            _onboarding.SetupPhase == OnboardingSetupPhase.Checklist ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPermissionPanel.Visibility = _onboarding.Screen == OnboardingScreen.SettingUp &&
            _onboarding.SetupPhase == OnboardingSetupPhase.Permission ? Visibility.Visible : Visibility.Collapsed;
        SetPanel(OnboardingReadyPanel, OnboardingScreen.Ready);
        SetPanel(OnboardingWarmingPanel, OnboardingScreen.WarmingUp);
        SetPanel(OnboardingPracticePanel, OnboardingScreen.Practice);
        OnboardingFooterText.Visibility = _onboarding.Screen == OnboardingScreen.Welcome ? Visibility.Visible : Visibility.Collapsed;

        var (title, subtitle) = _onboarding.Screen switch
        {
            OnboardingScreen.Welcome => (_welcomeTitle ?? OnboardingTitle.Text, WelcomeSubtitle),
            OnboardingScreen.SettingUp when _onboarding.SetupPhase == OnboardingSetupPhase.Permission =>
                ("Almost there. Just one permission.", "This lets EnviousWispr listen for you."),
            OnboardingScreen.SettingUp => ("Warming up the AI…", _onboarding.Model is SpeechModelDelivery.Missing
                    or SpeechModelDelivery.Downloading or SpeechModelDelivery.Cancelled or SpeechModelDelivery.Failed
                ? "Downloading the local speech model so EnviousWispr can run privately on your PC."
                : "Setting up the local speech model so EnviousWispr can run privately on your PC."),
            OnboardingScreen.Ready => ("Ready to Wispr!", "This is the key you dictate with.\nPress GET STARTED! to try it."),
            OnboardingScreen.WarmingUp => _onboarding.WarmingOutcome == OnboardingWarmingOutcome.Failed
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
            _drawnScreen = _onboarding.Screen;
            _drawnPhase = _onboarding.SetupPhase;
            _drawnPractice = _onboarding.PracticeState;
            _drawnWarming = _onboarding.WarmingOutcome;
            OnboardingScroll.ChangeView(null, 0, null, disableAnimation: true);
            FocusOnboardingScreen();
            Announce($"{OnboardingCopy.StepOf(_onboarding.StepNumber)}. {title}. {subtitle}");
        }
        else if (_onboarding.Screen == OnboardingScreen.Practice && _drawnPractice != _onboarding.PracticeState)
        {
            _drawnPractice = _onboarding.PracticeState;
            Announce($"{title}. {subtitle}");
        }
        else if (_onboarding.Screen == OnboardingScreen.WarmingUp && _drawnWarming != _onboarding.WarmingOutcome)
        {
            _drawnWarming = _onboarding.WarmingOutcome;
            Announce($"{title}. {subtitle}");
            if (_onboarding.WarmingOutcome == OnboardingWarmingOutcome.Failed)
            {
                OnboardingWarmingRetryButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void SetPanel(UIElement panel, OnboardingScreen screen) =>
        panel.Visibility = _onboarding.Screen == screen ? Visibility.Visible : Visibility.Collapsed;

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

        // THE NOTE UNDER THE CARD SAYS WHAT STOPPED AND WHAT TO DO, as macOS's does: an error with Retry, a pause with
        // "Try setup again", or "Cancelling…" while a cancel drains. Nothing at all while the setup is simply working.
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
            if (item == OnboardingChecklistStatus.Error && _onboarding.Screen == OnboardingScreen.SettingUp &&
                OnboardingRetryButton.Visibility == Visibility.Visible)
            {
                OnboardingRetryButton.Focus(FocusState.Programmatic);
            }
        }
    }

    private void DrawPermission()
    {
        var readiness = _onboardingMicrophone;
        SetLiveText(OnboardingMicrophoneText, readiness?.Sentence ?? "Checking Windows audio devices…");
        var allowed = _onboarding.MicrophoneConsent == MicrophoneConsent.Allowed;
        OnboardingMicrophoneAllowedBadge.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        OnboardingMicrophonePrivacyButton.Visibility = !allowed && (readiness?.OffersPrivacySettings ?? false)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DrawReady()
    {
        OnboardingKeycapText.Text = _onboarding.RecordingKey ?? "…";
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
        OnboardingWarmingRing.IsActive = _onboarding.Screen == OnboardingScreen.WarmingUp && outcome != OnboardingWarmingOutcome.Failed;
        OnboardingWarmingFailure.Visibility = outcome == OnboardingWarmingOutcome.Failed ? Visibility.Visible : Visibility.Collapsed;
        SetLiveText(
            OnboardingWarmingFailureText,
            outcome == OnboardingWarmingOutcome.Failed ? OnboardingCopy.WarmingFailedMessage : string.Empty);
        // A CAPTION SAYING NOTHING ELSE IS NEEDED DOES NOT SIT BESIDE A PANEL ASKING FOR A RETRY (macOS, local review).
        OnboardingWarmingCaption.Visibility = outcome == OnboardingWarmingOutcome.Failed ? Visibility.Collapsed : Visibility.Visible;
        DrawWarmRow(OnboardingWarmMicMark, OnboardingWarmMicPending, _onboarding.MicrophoneAllowed);
        DrawWarmRow(OnboardingWarmKeyMark, OnboardingWarmKeyPending, _onboarding.RecordingKey is not null);
        DrawWarmRow(OnboardingWarmEngineMark, OnboardingWarmEnginePending, outcome == OnboardingWarmingOutcome.Ready);
    }

    /// <summary>A tick or an empty ring, both declared in the markup so each takes its colour from a theme token.</summary>
    private static void DrawWarmRow(FontIcon done, FontIcon pending, bool isDone)
    {
        done.Visibility = isDone ? Visibility.Visible : Visibility.Collapsed;
        pending.Visibility = isDone ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DrawPractice()
    {
        var cannotHear = _onboarding.PracticeState == OnboardingPracticeState.CannotHear;
        // A SINGLE SPACE, NOT EMPTY, so the line keeps its height when there is nothing to say (macOS).
        OnboardingPracticeFootnote.Text = cannotHear || _onboarding.PracticeSucceeded ? " " : OnboardingCopy.PracticeFootnote;
        OnboardingPracticeMicrophoneButton.Visibility = cannotHear ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DrawFooter()
    {
        var (label, help, visible, enabled) = _onboarding.Screen switch
        {
            OnboardingScreen.Welcome => ("Get Started", "Starts setup.", true, true),
            OnboardingScreen.SettingUp when _onboarding.SetupPhase == OnboardingSetupPhase.Checklist =>
                ("Continue", "Available once the speech model is ready.", true, _onboarding.ChecklistComplete),
            OnboardingScreen.SettingUp =>
                ("Continue", "Available once Windows allows microphone access.", true, _onboarding.MicrophoneAllowed),
            OnboardingScreen.Ready => ("GET STARTED!", "Checks the speech engine, then opens a box to try your first dictation.", true, true),
            OnboardingScreen.WarmingUp => ("Continue", string.Empty, false, false),
            _ => ("FINISH SETUP", "Available once your first dictation has reached the box. Opens the EnviousWispr home page.", true, _onboarding.CanFinishPractice),
        };
        FinishOnboardingButton.Content = label;
        AutomationProperties.SetHelpText(FinishOnboardingButton, help);
        FinishOnboardingButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FinishOnboardingButton.IsEnabled = enabled;

        var (skip, skipVisible, skipEnabled) = _onboarding.Screen switch
        {
            OnboardingScreen.WarmingUp => (
                _onboarding.WarmingOutcome == OnboardingWarmingOutcome.Failed
                    ? OnboardingCopy.WarmingSkipAfterFailure
                    : OnboardingCopy.WarmingSkip,
                true,
                true),
            OnboardingScreen.Practice => (
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

    /// <summary>Puts keyboard focus where the new screen's first action is.</summary>
    /// <remarks>
    /// THE FIRST CANDIDATE THAT CAN TAKE FOCUS, IN THE SCREEN'S READING ORDER. A collapsed or disabled control refuses
    /// focus without a word, so a single fixed target left the keyboard nowhere whenever that one was not on screen.
    /// </remarks>
    private void FocusOnboardingScreen()
    {
        Control[] candidates = _onboarding.Screen switch
        {
            OnboardingScreen.Practice => [OnboardingPracticeBox, OnboardingPracticeMicrophoneButton, OnboardingSkipButton],
            OnboardingScreen.WarmingUp => [OnboardingWarmingRetryButton, OnboardingSkipButton],
            OnboardingScreen.SettingUp when _onboarding.SetupPhase == OnboardingSetupPhase.Permission =>
                [OnboardingMicrophonePrivacyButton, FinishOnboardingButton],
            OnboardingScreen.SettingUp =>
                [OnboardingCancelSetupButton, OnboardingRetryButton, FinishOnboardingButton, OnboardingSkipButton],
            _ => [FinishOnboardingButton],
        };
        _onboardingFocusOrphaned = candidates.FirstOrDefault(CanTakeFocus)?.Focus(FocusState.Programmatic) != true;
    }

    private static bool CanTakeFocus(Control control) =>
        control.IsEnabled && IsOnAVisiblePage(control);

    /// <summary>The control holding the keyboard inside the setup view, if one does.</summary>
    private Control? FocusedOnboardingControl() =>
        OnboardingView.XamlRoot is { } root &&
        FocusManager.GetFocusedElement(root) is Control focused &&
        IsInside(focused, OnboardingView)
            ? focused
            : null;

    /// <summary>Moves the keyboard on when the control holding it has just been hidden or disabled.</summary>
    /// <remarks>
    /// MEASURED ON THE RUNNING APP: pressing Cancel collapsed it while it held focus, and the accessibility tree went
    /// on reporting the collapsed button as focused, so the next Tab started from a control nobody could see. Cancel
    /// becomes "Try setup again", Retry becomes Cancel, FINISH SETUP is held during a take; any of them can be under
    /// the focus when it goes, so the rescue is general rather than one pair's. And the replacement is not always
    /// on screen at the same moment - "Try setup again" arrives only once the cancel has drained - so a focus left
    /// with nowhere to go is remembered and placed on the next render that has somewhere.
    /// </remarks>
    private void RescueOnboardingFocus(Control? focusedBefore)
    {
        if (focusedBefore is not null && !CanTakeFocus(focusedBefore))
        {
            _onboardingFocusOrphaned = true;
        }

        if (_onboardingFocusOrphaned)
        {
            FocusOnboardingScreen();
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Says the new screen once, the way the release-notes mark does.</summary>
    /// <remarks>
    /// A NOTIFICATION, NOT A LIVE REGION ON THE TITLE. The title changes on every screen and a live region there would
    /// be read on top of the focus moving to the screen's first control; one notification carrying the step, the
    /// heading and the line under it is heard once, and MostRecent drops a screen skipped past before it was read.
    /// </remarks>
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

    /// <summary>Arms the display beat a screen needs on arrival: the warm-up's floor.</summary>
    private void ArmBeatFor(OnboardingScreen screen)
    {
        _onboardingBeat.Stop();
        _warmingFloorElapsed = false;
        if (screen == OnboardingScreen.WarmingUp)
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
        if (_onboarding.Screen == OnboardingScreen.WarmingUp)
        {
            _warmingFloorElapsed = true;
        }
        else
        {
            _onboarding.AdvanceToPermission();
        }

        RenderOnboarding();
    }

    /// <summary>Reads the Windows switch again every two seconds while a screen is waiting on it.</summary>
    /// <remarks>
    /// macOS polls its permissions every two seconds while the permission screen is up, and again from the practice
    /// screen while it cannot hear. Windows raises nothing when the switch is turned on, so the same poll, scoped to
    /// the same two waits, is the only way the screen can notice without a click. A registry read; nothing heavier.
    /// </remarks>
    private void ArmMicrophonePoll()
    {
        var waiting = (_onboarding.Screen == OnboardingScreen.SettingUp &&
                _onboarding.SetupPhase == OnboardingSetupPhase.Permission &&
                !_onboarding.MicrophoneAllowed) ||
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

        // THE SWITCH CAME ON: the full read, devices included, so the row names the microphone it will use.
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
