using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App.Pages;

/// <summary>Standalone load-on-demand page for <see cref="OnboardingView"/>.</summary>
public sealed partial class OnboardingPageView : UserControl
{
    private long _visibilityCallbackToken;

    public OnboardingPageView(MainWindow host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        InitializeComponent();
        Visibility = OnboardingView.Visibility;
        _visibilityCallbackToken = OnboardingView.RegisterPropertyChangedCallback(
            VisibilityProperty,
            (_, _) => Visibility = OnboardingView.Visibility);
    }

    internal MainWindow Host { get; }

    internal void Detach()
    {
        if (_visibilityCallbackToken != 0)
        {
            OnboardingView.UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }
    }

    private void FinishOnboardingButton_Click(object sender, RoutedEventArgs e) => Host.FinishOnboardingButton_Click(sender, e);

    private void OnboardingCancelSetupButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingCancelSetupButton_Click(sender, e);

    private void OnboardingPracticeBox_TextChanged(object sender, TextChangedEventArgs e) => Host.OnboardingPracticeBox_TextChanged(sender, e);

    private void OnboardingRetryButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingRetryButton_Click(sender, e);

    private void OnboardingSkipButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingSkipButton_Click(sender, e);

    private void OpenMicrophonePrivacyButton_Click(object sender, RoutedEventArgs e) => Host.OpenMicrophonePrivacyButton_Click(sender, e);
}
