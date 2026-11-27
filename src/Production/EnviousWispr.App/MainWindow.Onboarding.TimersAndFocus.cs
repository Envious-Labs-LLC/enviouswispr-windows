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
        if (OnboardingView is not { Visibility: Visibility.Visible } ||
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
        _onboardingBeat.Stop();
        _onboardingMicrophonePoll.Stop();
        _onboardingKeyHoldTimer.Stop();
        CancelOnboardingKeyCapture();
        if (_onboardingLipsAnimating)
        {
            _onboardingLipsAnimating = false;
            OnboardingLipsBreathStoryboard?.Stop();
        }
        foreach (var dot in _onboardingClosingDotButtons)
        {
            dot.Click -= OnboardingClosingDot_Click;
        }
        _onboardingClosingDotButtons = [];
        _onboardingProgressSegments = [];
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
