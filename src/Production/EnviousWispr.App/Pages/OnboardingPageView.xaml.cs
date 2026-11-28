using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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

    private void OnboardingBackButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingBackButton_Click(sender, e);

    private void OnboardingClosingPausePlayButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingClosingPausePlayButton_Click(sender, e);

    private void OnboardingClosingWatchAgainButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingClosingWatchAgainButton_Click(sender, e);

    private void OnboardingCloudApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) => Host.OnboardingCloudApiKeyBox_PasswordChanged(sender, e);

    private void OnboardingCloudCheckKeyButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingCloudCheckKeyButton_Click(sender, e);

    private void OnboardingCloudClearKeyButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingCloudClearKeyButton_Click(sender, e);

    private void OnboardingCloudGetKeyLink_Click(object sender, RoutedEventArgs e) => Host.OnboardingCloudGetKeyLink_Click(sender, e);

    private void OnboardingCloudModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.OnboardingCloudModelComboBox_SelectionChanged(sender, e);

    private void OnboardingCloudProviderButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingCloudProviderButton_Click(sender, e);

    private void OnboardingDetectAutoCard_Checked(object sender, RoutedEventArgs e) => Host.OnboardingDetectAutoCard_Checked(sender, e);

    private void OnboardingDownloadCancelButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingDownloadCancelButton_Click(sender, e);

    private void OnboardingDownloadPillButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingDownloadPillButton_Click(sender, e);

    private void OnboardingDownloadRetryButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingDownloadRetryButton_Click(sender, e);

    private void OnboardingGateSkipPolishButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingGateSkipPolishButton_Click(sender, e);

    private void OnboardingGateSlowSkipButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingGateSlowSkipButton_Click(sender, e);

    private void OnboardingGateSlowWaitButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingGateSlowWaitButton_Click(sender, e);

    private void OnboardingKeycapButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingKeycapButton_Click(sender, e);

    private void OnboardingLanguageListView_ItemClick(object sender, ItemClickEventArgs e) => Host.OnboardingLanguageListView_ItemClick(sender, e);

    private void OnboardingLanguageSearchBox_KeyDown(object sender, KeyRoutedEventArgs e) => Host.OnboardingLanguageSearchBox_KeyDown(sender, e);

    private void OnboardingLanguageSearchBox_TextChanged(object sender, TextChangedEventArgs e) => Host.OnboardingLanguageSearchBox_TextChanged(sender, e);

    private void OnboardingOpenPolishSeeAllButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingOpenPolishSeeAllButton_Click(sender, e);

    private void OnboardingOpenSpeechCompareButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingOpenSpeechCompareButton_Click(sender, e);

    private void OnboardingPolishLowSpecCloudButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingPolishLowSpecCloudButton_Click(sender, e);

    private void OnboardingPolishPickGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.OnboardingPolishPickGrid_SizeChanged(sender, e);

    private void OnboardingPolishPrimaryCard_Checked(object sender, RoutedEventArgs e) => Host.OnboardingPolishPrimaryCard_Checked(sender, e);

    private void OnboardingPolishTabCloudButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingPolishTabCloudButton_Click(sender, e);

    private void OnboardingPolishTabLocalButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingPolishTabLocalButton_Click(sender, e);

    private void OnboardingPracticeBox_TextChanged(object sender, TextChangedEventArgs e) => Host.OnboardingPracticeBox_TextChanged(sender, e);

    private void OnboardingPracticeMicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.OnboardingPracticeMicComboBox_SelectionChanged(sender, e);

    private void OnboardingPresetHotkeyButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingPresetHotkeyButton_Click(sender, e);

    private void OnboardingRefreshMicButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingRefreshMicButton_Click(sender, e);

    private void OnboardingSeeAllEgOneCard_Checked(object sender, RoutedEventArgs e) => Host.OnboardingSeeAllEgOneCard_Checked(sender, e);

    private void OnboardingSeeAllEgOneGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.OnboardingSeeAllEgOneGrid_SizeChanged(sender, e);

    private void OnboardingSkipButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingSkipButton_Click(sender, e);

    private void OnboardingSkipPolishAndAdvance_Click(object sender, RoutedEventArgs e) => Host.OnboardingSkipPolishAndAdvance_Click(sender, e);

    private void OnboardingSkipPolish_Click(object sender, RoutedEventArgs e) => Host.OnboardingSkipPolish_Click(sender, e);

    private void OnboardingSpeechCompareCard_Checked(object sender, RoutedEventArgs e) => Host.OnboardingSpeechCompareCard_Checked(sender, e);

    private void OnboardingSpeechCompareGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.OnboardingSpeechCompareGrid_SizeChanged(sender, e);

    private void OnboardingSuggestedLanguageButton_Click(object sender, RoutedEventArgs e) => Host.OnboardingSuggestedLanguageButton_Click(sender, e);

    private void OnboardingView_PreviewKeyDown(object sender, KeyRoutedEventArgs e) => Host.OnboardingView_PreviewKeyDown(sender, e);

    private void OnboardingView_PreviewKeyUp(object sender, KeyRoutedEventArgs e) => Host.OnboardingView_PreviewKeyUp(sender, e);

    private void OnboardingView_SizeChanged(object sender, SizeChangedEventArgs e) => Host.OnboardingView_SizeChanged(sender, e);

    private void OpenMicrophonePrivacyButton_Click(object sender, RoutedEventArgs e) => Host.OpenMicrophonePrivacyButton_Click(sender, e);
}
