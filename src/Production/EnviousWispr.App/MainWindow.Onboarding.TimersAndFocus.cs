using System.Globalization;
using System.Runtime.InteropServices;
using EnviousWispr.App.Controls;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace EnviousWispr.App;

/// <summary>Onboarding motion, Rainbow Lips choreography, timers, theme refresh, focus management, and UAT hooks.</summary>
public sealed partial class MainWindow
{
    private const double OnboardingEntranceOffsetPixels = 16.0;
    private const double OnboardingEntranceMilliseconds = 280.0;

    internal void OnboardingView_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateOnboardingLipsSize(_onboarding.Step);

    private void RefreshOnboardingThemeBrushes()
    {
        if (!IsOnboardingLoaded || OnboardingView is null)
        {
            return;
        }

        var hitTestBrush = ResolveWindowThemeBrush("BrandHitTestBrush");
        var trackBrush = ResolveWindowThemeBrush("BrandDividerBrush");
        var fillBrush = ResolveWindowThemeBrush("BrandAccentSolidBrush");
        for (var i = 0; i < _onboardingClosingDotButtons.Length; i++)
        {
            _onboardingClosingDotButtons[i].Background = hitTestBrush;
        }

        for (var i = 0; i < _onboardingClosingSegmentTracks.Length; i++)
        {
            _onboardingClosingSegmentTracks[i].Background = trackBrush;
        }

        for (var i = 0; i < _onboardingClosingSegmentFills.Length; i++)
        {
            _onboardingClosingSegmentFills[i].Background = fillBrush;
        }

        if (OnboardingView.Visibility == Visibility.Visible)
        {
            RenderOnboarding();
        }
    }

    private void ApplyOnboardingStyle(FrameworkElement element, string styleKey)
    {
        if (ThemeResourceLookup.Resolve<Style>(WindowRoot, styleKey) is { } style)
        {
            element.Style = style;
        }
    }

    private void DrawOnboardingRainbowLips(OnboardingStep step, bool screenChanged)
    {
        if (OnboardingRainbowLipsContainer is null || OnboardingRainbowLips is null)
        {
            return;
        }

        if (step is OnboardingStep.Closing or OnboardingStep.Done)
        {
            OnboardingRainbowLipsContainer.Visibility = Visibility.Collapsed;
            return;
        }

        OnboardingRainbowLipsContainer.Visibility = Visibility.Visible;
        OnboardingRainbowLips.ReduceMotion = !ShouldAnimateMotion();
        UpdateOnboardingLipsSize(step);

        var mood = step switch
        {
            OnboardingStep.Welcome => RainbowLipsMood.Welcome,
            OnboardingStep.Language => _onboardingLanguageSpeakingActive ? RainbowLipsMood.Speaking : RainbowLipsMood.Idle,
            OnboardingStep.SpeechPick or OnboardingStep.SpeechCompare => _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper
                ? RainbowLipsMood.ModelVoice
                : RainbowLipsMood.ModelFast,
            OnboardingStep.PolishIntro or OnboardingStep.PolishPick or OnboardingStep.PolishSeeAll => RainbowLipsMood.Shimmer,
            OnboardingStep.Downloads or OnboardingStep.GettingReady => RainbowLipsMood.Pulse,
            OnboardingStep.Permissions => _onboarding.MicrophoneAllowed ? RainbowLipsMood.Smile : RainbowLipsMood.Idle,
            OnboardingStep.Key => _onboardingRehearsalKeyDown
                ? RainbowLipsMood.Equalizer
                : (_onboardingHoldRehearsed || _onboardingDoubleTapRehearsed ? RainbowLipsMood.Happy : RainbowLipsMood.Ready),
            OnboardingStep.Practice => _onboarding.PracticeState switch
            {
                OnboardingPracticeState.Listening => RainbowLipsMood.Equalizer,
                OnboardingPracticeState.Worked => RainbowLipsMood.Triumph,
                OnboardingPracticeState.CannotHear or OnboardingPracticeState.SomethingBroke => RainbowLipsMood.Idle,
                _ => _onboarding.PracticeSucceeded ? RainbowLipsMood.Smile : RainbowLipsMood.Welcome,
            },
            _ => RainbowLipsMood.Idle,
        };

        OnboardingRainbowLips.Mood = mood;

        var showGreetingBubble = step == OnboardingStep.Language ||
            (step == OnboardingStep.SpeechPick && _onboarding.Choices.SpeechEngine == FinalAsrEngine.Whisper);
        if (OnboardingGreetingBubble is not null && OnboardingGreetingBubbleText is not null)
        {
            OnboardingGreetingBubble.Visibility = showGreetingBubble ? Visibility.Visible : Visibility.Collapsed;
            if (showGreetingBubble)
            {
                OnboardingGreetingBubbleText.Text = SetupLanguageMetadata.GetGreeting(
                    _onboarding.Choices.LanguageCode,
                    CultureInfo.CurrentUICulture);
            }
        }

        if (screenChanged && step is OnboardingStep.SpeechPick or OnboardingStep.PolishIntro or OnboardingStep.Practice)
        {
            OnboardingRainbowLips.TriggerBurst();
        }
    }

    private void UpdateOnboardingLipsSize(OnboardingStep step)
    {
        if (OnboardingRainbowLips is null || OnboardingView is null)
        {
            return;
        }

        var compact = OnboardingView.ActualHeight > 0 &&
            OnboardingView.ActualHeight < LayoutDouble("BrandOnboardingCompactHeightThreshold");
        var size = compact
            ? LayoutDouble("BrandOnboardingLipsCompactSize")
            : step == OnboardingStep.Welcome
                ? LayoutDouble("BrandOnboardingLipsHeroSize")
                : LayoutDouble("BrandOnboardingLipsStepSize");

        if (Math.Abs(OnboardingRainbowLips.Width - size) >= 0.5)
        {
            OnboardingRainbowLips.Width = size;
            OnboardingRainbowLips.Height = size;
        }
    }

    private void PlayOnboardingStageEntrance(OnboardingStep? previous, OnboardingStep next)
    {
        if (OnboardingStageHost is null || OnboardingStageTranslate is null)
        {
            return;
        }

        var isSameScreenSwap = previous is { } prev && IsOnboardingSameScreenSwap(prev, next);
        if (isSameScreenSwap || !previous.HasValue || !ShouldAnimateMotion())
        {
            OnboardingStageTranslate.X = 0;
            OnboardingStageHost.Opacity = 1;
            return;
        }

        var goingBack = (int)next < (int)previous.Value;
        OnboardingStageTranslate.X = goingBack ? -OnboardingEntranceOffsetPixels : OnboardingEntranceOffsetPixels;
        OnboardingStageHost.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(OnboardingEntranceMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(slide, OnboardingStageTranslate);
        Storyboard.SetTargetProperty(slide, "X");

        var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(fade, OnboardingStageHost);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private static bool IsOnboardingSameScreenSwap(OnboardingStep previous, OnboardingStep next) =>
        (previous == OnboardingStep.SpeechPick && next == OnboardingStep.SpeechCompare) ||
        (previous == OnboardingStep.SpeechCompare && next == OnboardingStep.SpeechPick) ||
        (previous == OnboardingStep.PolishPick && next == OnboardingStep.PolishSeeAll) ||
        (previous == OnboardingStep.PolishSeeAll && next == OnboardingStep.PolishPick);

    private void OnOnboardingClosingFilmFrameEvaluated(object? sender, ClosingFilmEvaluation frame)
    {
        for (var i = 0; i < _onboardingClosingSegmentScales.Length && i < frame.SegmentProgress.Length; i++)
        {
            _onboardingClosingSegmentScales[i].ScaleX = Math.Clamp(frame.SegmentProgress[i], 0.0, 1.0);
        }
    }

    private void OnOnboardingClosingFilmSceneChanged(object? sender, int sceneIndex) =>
        _onboarding.SetClosingBeat(sceneIndex);

    private void OnOnboardingClosingFilmPlaybackStateChanged(object? sender, EventArgs e) =>
        UpdateOnboardingClosingPausePlayButton();

    private void UpdateOnboardingClosingPausePlayButton()
    {
        if (OnboardingClosingPausePlayButton is null || OnboardingClosingFilm is null)
        {
            return;
        }

        if (OnboardingClosingFilm.ReduceMotion)
        {
            OnboardingClosingPausePlayButton.Content = "Next tip";
            AutomationProperties.SetName(OnboardingClosingPausePlayButton, "Next closing film tip");
            return;
        }

        OnboardingClosingPausePlayButton.Content = OnboardingClosingFilm.IsPaused ? "Play" : "Pause";
        AutomationProperties.SetName(
            OnboardingClosingPausePlayButton,
            OnboardingClosingFilm.IsPaused ? "Play closing film" : "Pause closing film");
    }

    private void ArmSlowPolishGateTimer(OnboardingStep step)
    {
        var waitingForPolish = step == OnboardingStep.GettingReady &&
            _onboarding.EvaluateGate() == OnboardingGateDecision.WaitingWithPolishDownloading &&
            !_onboardingSlowPolishCardVisible;
        if (!waitingForPolish)
        {
            _onboardingSlowPolishTimer.Stop();
            return;
        }

        if (!_onboardingSlowPolishTimer.IsEnabled)
        {
            _onboardingSlowPolishTimer.Tick -= OnSlowPolishGateTick;
            _onboardingSlowPolishTimer.Tick += OnSlowPolishGateTick;
            _onboardingSlowPolishTimer.Start();
        }
    }

    private void OnSlowPolishGateTick(object? sender, object e)
    {
        _onboardingSlowPolishTimer.Stop();
        if (OnboardingView is { Visibility: Visibility.Visible } &&
            _onboarding.Step == OnboardingStep.GettingReady &&
            _onboarding.EvaluateGate() == OnboardingGateDecision.WaitingWithPolishDownloading)
        {
            _onboardingSlowPolishCardVisible = true;
            RenderOnboarding();
        }
    }

    private void ArmSlowDownloadBannerTimer(OnboardingStep step)
    {
        var activelyDownloading = step == OnboardingStep.Downloads &&
            _onboardingDownloadSnapshot?.IsRunning == true &&
            !_onboardingSlowDownloadBannerVisible;
        if (!activelyDownloading)
        {
            _onboardingSlowDownloadTimer.Stop();
            return;
        }

        if (!_onboardingSlowDownloadTimer.IsEnabled)
        {
            _onboardingSlowDownloadTimer.Tick -= OnSlowDownloadBannerTick;
            _onboardingSlowDownloadTimer.Tick += OnSlowDownloadBannerTick;
            _onboardingSlowDownloadTimer.Start();
        }
    }

    private void OnSlowDownloadBannerTick(object? sender, object e)
    {
        _onboardingSlowDownloadTimer.Stop();
        if (OnboardingView is { Visibility: Visibility.Visible } &&
            _onboarding.Step == OnboardingStep.Downloads &&
            _onboardingDownloadSnapshot?.IsRunning == true)
        {
            _onboardingSlowDownloadBannerVisible = true;
            RenderOnboarding();
        }
    }

    private void ArmMicrophonePoll()
    {
        var shouldPoll = OnboardingView is { Visibility: Visibility.Visible } &&
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
        if (OnboardingView is not { Visibility: Visibility.Visible } || _shuttingDownProductWindows)
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
        _onboardingMicrophonePoll.Stop();
        _onboardingKeyHoldTimer.Stop();
        _onboardingSlowPolishTimer.Stop();
        _onboardingSlowDownloadTimer.Stop();
        CancelOnboardingKeyCapture();

        if (_onboardingClosingFilmWired && OnboardingClosingFilm is not null)
        {
            _onboardingClosingFilmWired = false;
            OnboardingClosingFilm.FrameEvaluated -= OnOnboardingClosingFilmFrameEvaluated;
            OnboardingClosingFilm.SceneChanged -= OnOnboardingClosingFilmSceneChanged;
            OnboardingClosingFilm.PlaybackStateChanged -= OnOnboardingClosingFilmPlaybackStateChanged;
            OnboardingClosingFilm.SceneAnnounced = null;
            OnboardingClosingFilm.Pause();
        }

        foreach (var dot in _onboardingClosingDotButtons)
        {
            dot.Click -= OnboardingClosingDot_Click;
        }

        _onboardingClosingDotButtons = [];
        _onboardingClosingSegmentTracks = [];
        _onboardingClosingSegmentFills = [];
        _onboardingClosingSegmentScales = [];
        _onboardingProgressSegments = [];
    }

    private static OnboardingStep? TryGetUatPinnedOnboardingStep()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENVIOUSWISPR_DATA_DIRECTORY")))
        {
            return null;
        }

        var raw = Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_ONBOARDING_STEP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Enum.TryParse<OnboardingStep>(raw.Trim(), ignoreCase: true, out var parsed) &&
               parsed != OnboardingStep.Done
            ? parsed
            : null;
    }

    private static bool IsUatReduceMotionForced()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENVIOUSWISPR_DATA_DIRECTORY")))
        {
            return false;
        }

        var raw = Environment.GetEnvironmentVariable("ENVIOUSWISPR_UAT_ONBOARDING_REDUCE_MOTION");
        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal) ||
               string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAnimateMotion()
    {
        if (IsUatReduceMotionForced())
        {
            return false;
        }

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
        OnboardingView?.XamlRoot is not { } root ||
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
            case OnboardingStep.Practice:
                OnboardingPracticeBox.Focus(FocusState.Programmatic);
                break;
            case OnboardingStep.Permissions when !_onboarding.MicrophoneAllowed:
                OnboardingOpenMicSettingsButton.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void Announce(string text)
    {
        if (OnboardingView is { Visibility: Visibility.Visible } &&
            !string.IsNullOrWhiteSpace(text) &&
            OnboardingTitle is not null &&
            FrameworkElementAutomationPeer.FromElement(OnboardingTitle) is { } peer)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.ImportantMostRecent,
                text,
                "EnviousWisprOnboarding");
        }
    }
}
