using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App.Pages;

/// <summary>Standalone load-on-demand page for <see cref="SettingsPage"/>.</summary>
public sealed partial class SettingsPageView : UserControl
{
    private long _visibilityCallbackToken;

    public SettingsPageView(MainWindow host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        InitializeComponent();
        Visibility = SettingsPage.Visibility;
        _visibilityCallbackToken = SettingsPage.RegisterPropertyChangedCallback(
            VisibilityProperty,
            (_, _) => Visibility = SettingsPage.Visibility);
    }

    internal MainWindow Host { get; }

    internal void Detach()
    {
        if (_visibilityCallbackToken != 0)
        {
            SettingsPage.UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }
    }

    private void AppTab_Click(object sender, RoutedEventArgs e) => Host.AppTab_Click(sender, e);

    private void ApplyUpdateButton_Click(object sender, RoutedEventArgs e) => Host.ApplyUpdateButton_Click(sender, e);

    private void AutoStopToggle_Toggled(object sender, RoutedEventArgs e) => Host.AutoStopToggle_Toggled(sender, e);

    private void BottomPositionSegmentButton_Click(object sender, RoutedEventArgs e) => Host.BottomPositionSegmentButton_Click(sender, e);

    private void CancelEgOneDownloadButton_Click(object sender, RoutedEventArgs e) => Host.CancelEgOneDownloadButton_Click(sender, e);

    private void CancelGraphicsRuntimeDownloadButton_Click(object sender, RoutedEventArgs e) => Host.CancelGraphicsRuntimeDownloadButton_Click(sender, e);

    private void CancelLivePreviewWhisperDownloadButton_Click(object sender, RoutedEventArgs e) => Host.CancelLivePreviewWhisperDownloadButton_Click(sender, e);

    private void CancelModelDownloadButton_Click(object sender, RoutedEventArgs e) => Host.CancelModelDownloadButton_Click(sender, e);

    private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e) => Host.ChangeHotkeyButton_Click(sender, e);

    private void ChangePolishProviderButton_Click(object sender, RoutedEventArgs e) => Host.ChangePolishProviderButton_Click(sender, e);

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) => Host.CheckForUpdatesButton_Click(sender, e);

    private void ConfigureLivePreviewLink_Click(object sender, RoutedEventArgs e) => Host.ConfigureLivePreviewLink_Click(sender, e);

    private void DarkThemeSegmentButton_Click(object sender, RoutedEventArgs e) => Host.DarkThemeSegmentButton_Click(sender, e);

    private void DeleteAllDataButton_Click(object sender, RoutedEventArgs e) => Host.DeleteAllDataButton_Click(sender, e);

    private void DictationTab_Click(object sender, RoutedEventArgs e) => Host.DictationTab_Click(sender, e);

    private void DownloadGraphicsRuntimeButton_Click(object sender, RoutedEventArgs e) => Host.DownloadGraphicsRuntimeButton_Click(sender, e);

    private void ActivePolishProviderHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.ActivePolishProviderHeaderGrid_SizeChanged(sender, e);

    private void DownloadLivePreviewWhisperButton_Click(object sender, RoutedEventArgs e) => Host.DownloadLivePreviewWhisperButton_Click(sender, e);

    private void EgOneCardFooter_SizeChanged(object sender, SizeChangedEventArgs e) => Host.EgOneCardFooter_SizeChanged(sender, e);

    private void EgOneCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.EgOneCardsGrid_SizeChanged(sender, e);

    private void EgOneSectionHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.EgOneSectionHeaderGrid_SizeChanged(sender, e);

    private void EgOneChooseProviderButton_Click(object sender, RoutedEventArgs e) => Host.EgOneChooseProviderButton_Click(sender, e);

    private void EgOneDownloadButton_Click(object sender, RoutedEventArgs e) => Host.EgOneDownloadButton_Click(sender, e);

    private void EgOneMemoryPanel_SizeChanged(object sender, SizeChangedEventArgs e) => Host.EgOneMemoryPanel_SizeChanged(sender, e);

    private void EgOneRemoveButton_Click(object sender, RoutedEventArgs e) => Host.EgOneRemoveButton_Click(sender, e);

    private void EgOneSizeCard_Checked(object sender, RoutedEventArgs e) => Host.EgOneSizeCard_Checked(sender, e);

    private void EgOneUnloadPolicyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.EgOneUnloadPolicyComboBox_SelectionChanged(sender, e);

    private void EnableAiPolishToggle_Toggled(object sender, RoutedEventArgs e) => Host.EnableAiPolishToggle_Toggled(sender, e);

    private void EngineCardChecked(object sender, RoutedEventArgs e) => Host.EngineCardChecked(sender, e);

    private void EngineCardClicked(object sender, RoutedEventArgs e) => Host.EngineCardClicked(sender, e);

    private void EngineCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.EngineCardsGrid_SizeChanged(sender, e);

    private void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e) => Host.ExportDiagnosticsButton_Click(sender, e);

    private void ExportMyDataButton_Click(object sender, RoutedEventArgs e) => Host.ExportMyDataButton_Click(sender, e);

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e) => Host.ExportProfileButton_Click(sender, e);

    private void HandsFreeGestureButton_Click(object sender, RoutedEventArgs e) => Host.HandsFreeGestureButton_Click(sender, e);

    private void HotkeyBoxKeyDown(object sender, KeyRoutedEventArgs e) => Host.HotkeyBoxKeyDown(sender, e);

    private void HotkeyBoxKeyUp(object sender, KeyRoutedEventArgs e) => Host.HotkeyBoxKeyUp(sender, e);

    private void HotkeyBoxTapped(object sender, TappedRoutedEventArgs e) => Host.HotkeyBoxTapped(sender, e);

    private void HotkeyBoxTextChanged(object sender, TextChangedEventArgs e) => Host.HotkeyBoxTextChanged(sender, e);

    private void ImportProfileButton_Click(object sender, RoutedEventArgs e) => Host.ImportProfileButton_Click(sender, e);

    private void LightThemeSegmentButton_Click(object sender, RoutedEventArgs e) => Host.LightThemeSegmentButton_Click(sender, e);

    private void LivePreviewEngineCard_Checked(object sender, RoutedEventArgs e) => Host.LivePreviewEngineCard_Checked(sender, e);

    private void LivePreviewEngineCardsPanel_SizeChanged(object sender, SizeChangedEventArgs e) => Host.LivePreviewEngineCardsPanel_SizeChanged(sender, e);

    private void LivePreviewParakeetCardContainer_Tapped(object sender, TappedRoutedEventArgs e) => Host.LivePreviewParakeetCardContainer_Tapped(sender, e);

    private void LivePreviewToggle_Toggled(object sender, RoutedEventArgs e) => Host.LivePreviewToggle_Toggled(sender, e);

    private void LivePreviewWhisperCardContainer_Tapped(object sender, TappedRoutedEventArgs e) => Host.LivePreviewWhisperCardContainer_Tapped(sender, e);

    private void MicrophoneTestButton_Click(object sender, RoutedEventArgs e) => Host.MicrophoneTestButton_Click(sender, e);

    private void OllamaCheckAgainButton_Click(object sender, RoutedEventArgs e) => Host.OllamaCheckAgainButton_Click(sender, e);

    private void OllamaModelAction_Click(object sender, RoutedEventArgs e) => Host.OllamaModelAction_Click(sender, e);

    private void OllamaModelSearchBox_TextChanged(object sender, TextChangedEventArgs e) => Host.OllamaModelSearchBox_TextChanged(sender, e);

    private void OllamaSetupActionButton_Click(object sender, RoutedEventArgs e) => Host.OllamaSetupActionButton_Click(sender, e);

    private void OpenMicrophonePrivacyButton_Click(object sender, RoutedEventArgs e) => Host.OpenMicrophonePrivacyButton_Click(sender, e);

    private void OpenStartupSettingsButton_Click(object sender, RoutedEventArgs e) => Host.OpenStartupSettingsButton_Click(sender, e);

    private void ParakeetDownloadButton_Click(object sender, RoutedEventArgs e) => Host.ParakeetDownloadButton_Click(sender, e);

    private void ParakeetEngineCardContainer_Tapped(object sender, TappedRoutedEventArgs e) => Host.ParakeetEngineCardContainer_Tapped(sender, e);

    private void ParakeetRemoveButton_Click(object sender, RoutedEventArgs e) => Host.ParakeetRemoveButton_Click(sender, e);

    private void PickParakeetLanguageButton_Click(object sender, RoutedEventArgs e) => Host.PickParakeetLanguageButton_Click(sender, e);

    private void PillPreviewComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.PillPreviewComboBox_SelectionChanged(sender, e);

    private void PillPreviewDismissButton_Click(object sender, RoutedEventArgs e) => Host.PillPreviewDismissButton_Click(sender, e);

    private void PillPreviewHoldOpenToggle_Toggled(object sender, RoutedEventArgs e) => Host.PillPreviewHoldOpenToggle_Toggled(sender, e);

    private void PillPreviewNextButton_Click(object sender, RoutedEventArgs e) => Host.PillPreviewNextButton_Click(sender, e);

    private void PillPreviewPlayButton_Click(object sender, RoutedEventArgs e) => Host.PillPreviewPlayButton_Click(sender, e);

    private void PillReviewModeToggle_Toggled(object sender, RoutedEventArgs e) => Host.PillReviewModeToggle_Toggled(sender, e);

    private void PillStyleCardChecked(object sender, RoutedEventArgs e) => Host.PillStyleCardChecked(sender, e);

    private void PolishModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.PolishModelPicker_SelectionChanged(sender, e);

    private void PolishProviderCardChecked(object sender, RoutedEventArgs e) => Host.PolishProviderCardChecked(sender, e);

    private void PolishProviderCardClicked(object sender, RoutedEventArgs e) => Host.PolishProviderCardClicked(sender, e);

    private void PushToTalkSegmentButton_Click(object sender, RoutedEventArgs e) => Host.PushToTalkSegmentButton_Click(sender, e);

    private void RecordingPillCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.RecordingPillCardsGrid_SizeChanged(sender, e);

    private void RefreshPolishModelsButton_Click(object sender, RoutedEventArgs e) => Host.RefreshPolishModelsButton_Click(sender, e);

    private void RemoveApiKeyButton_Click(object sender, RoutedEventArgs e) => Host.RemoveApiKeyButton_Click(sender, e);

    private void RemoveLivePreviewWhisperButton_Click(object sender, RoutedEventArgs e) => Host.RemoveLivePreviewWhisperButton_Click(sender, e);

    private void ResetLanguageToAutoButton_Click(object sender, RoutedEventArgs e) => Host.ResetLanguageToAutoButton_Click(sender, e);

    private void RunSetupAgainButton_Click(object sender, RoutedEventArgs e) => Host.RunSetupAgainButton_Click(sender, e);

    private void RunSpeedCheckButton_Click(object sender, RoutedEventArgs e) => Host.RunSpeedCheckButton_Click(sender, e);

    private void SaveApiKeyButton_Click(object sender, RoutedEventArgs e) => Host.SaveApiKeyButton_Click(sender, e);

    private void SettingRow_Tapped(object sender, TappedRoutedEventArgs e) => Host.SettingRow_Tapped(sender, e);

    private void SettingsTabStrip_SizeChanged(object sender, SizeChangedEventArgs e) => Host.SettingsTabStrip_SizeChanged(sender, e);

    private void StartAtLoginToggle_Toggled(object sender, RoutedEventArgs e) => Host.StartAtLoginToggle_Toggled(sender, e);

    private void SystemThemeSegmentButton_Click(object sender, RoutedEventArgs e) => Host.SystemThemeSegmentButton_Click(sender, e);

    private void ToggleSegmentButton_Click(object sender, RoutedEventArgs e) => Host.ToggleSegmentButton_Click(sender, e);

    private void TopPositionSegmentButton_Click(object sender, RoutedEventArgs e) => Host.TopPositionSegmentButton_Click(sender, e);

    private void TranscribeFileCancelButton_Click(object sender, RoutedEventArgs e) => Host.TranscribeFileCancelButton_Click(sender, e);

    private void TranscribeFileChooseButton_Click(object sender, RoutedEventArgs e) => Host.TranscribeFileChooseButton_Click(sender, e);

    private void TranscribeFileCopyButton_Click(object sender, RoutedEventArgs e) => Host.TranscribeFileCopyButton_Click(sender, e);

    private void TranscribeFileDropZone_DragLeave(object sender, DragEventArgs e) => Host.TranscribeFileDropZone_DragLeave(sender, e);

    private void TranscribeFileDropZone_DragOver(object sender, DragEventArgs e) => Host.TranscribeFileDropZone_DragOver(sender, e);

    private void TranscribeFileDropZone_Drop(object sender, DragEventArgs e) => Host.TranscribeFileDropZone_Drop(sender, e);

    private void TranscribeFileDropZone_PointerEntered(object sender, PointerRoutedEventArgs e) => Host.TranscribeFileDropZone_PointerEntered(sender, e);

    private void TranscribeFileDropZone_PointerExited(object sender, PointerRoutedEventArgs e) => Host.TranscribeFileDropZone_PointerExited(sender, e);

    private void TranscribeFileDropZone_PointerPressed(object sender, PointerRoutedEventArgs e) => Host.TranscribeFileDropZone_PointerPressed(sender, e);

    private void TranscribeFileNewButton_Click(object sender, RoutedEventArgs e) => Host.TranscribeFileNewButton_Click(sender, e);

    private void TranscribeFileSaveButton_Click(object sender, RoutedEventArgs e) => Host.TranscribeFileSaveButton_Click(sender, e);

    private void WhisperDownloadButton_Click(object sender, RoutedEventArgs e) => Host.WhisperDownloadButton_Click(sender, e);

    private void WhisperEngineCardContainer_Tapped(object sender, TappedRoutedEventArgs e) => Host.WhisperEngineCardContainer_Tapped(sender, e);

    private void WhisperLanguageButton_Click(object sender, RoutedEventArgs e) => Host.WhisperLanguageButton_Click(sender, e);

    private void WhisperRemoveButton_Click(object sender, RoutedEventArgs e) => Host.WhisperRemoveButton_Click(sender, e);
}
