using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App.Pages;

/// <summary>Standalone load-on-demand page for <see cref="DictionaryPage"/>.</summary>
public sealed partial class DictionaryPageView : UserControl
{
    private long _visibilityCallbackToken;

    public DictionaryPageView(MainWindow host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        InitializeComponent();
        Visibility = DictionaryPage.Visibility;
        _visibilityCallbackToken = DictionaryPage.RegisterPropertyChangedCallback(
            VisibilityProperty,
            (_, _) => Visibility = DictionaryPage.Visibility);
    }

    internal MainWindow Host { get; }

    internal void Detach()
    {
        if (_visibilityCallbackToken != 0)
        {
            DictionaryPage.UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }
    }

    private void AddWordButton_Click(object sender, RoutedEventArgs e) => Host.AddWordButton_Click(sender, e);

    private void DictionaryEnabledToggle_Toggled(object sender, RoutedEventArgs e) => Host.DictionaryEnabledToggle_Toggled(sender, e);

    private void DictionarySearchBox_TextChanged(object sender, TextChangedEventArgs e) => Host.DictionarySearchBox_TextChanged(sender, e);

    private void DictionarySearchClearButton_Click(object sender, RoutedEventArgs e) => Host.DictionarySearchClearButton_Click(sender, e);

    private void DictionaryTab_Click(object sender, RoutedEventArgs e) => Host.DictionaryTab_Click(sender, e);

    private void EditWordRowButton_Click(object sender, RoutedEventArgs e) => Host.EditWordRowButton_Click(sender, e);

    private void ExportWordsButton_Click(object sender, RoutedEventArgs e) => Host.ExportWordsButton_Click(sender, e);

    private void FocusAddWordButton_Click(object sender, RoutedEventArgs e) => Host.FocusAddWordButton_Click(sender, e);

    private void ForgetLearnMemoryButton_Click(object sender, RoutedEventArgs e) => Host.ForgetLearnMemoryButton_Click(sender, e);

    private void GoToQuickAddKeybindButton_Click(object sender, RoutedEventArgs e) => Host.GoToQuickAddKeybindButton_Click(sender, e);

    private void ImportWordsButton_Click(object sender, RoutedEventArgs e) => Host.ImportWordsButton_Click(sender, e);

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) => Host.ListSelectionChanged(sender, e);

    private void MassEditWordsToggle_Click(object sender, RoutedEventArgs e) => Host.MassEditWordsToggle_Click(sender, e);

    private void OpenAddWordDialogButton_Click(object sender, RoutedEventArgs e) => Host.OpenAddWordDialogButton_Click(sender, e);

    private void PasteWordsButton_Click(object sender, RoutedEventArgs e) => Host.PasteWordsButton_Click(sender, e);

    private void QuickAddStepsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.QuickAddStepsGrid_SizeChanged(sender, e);

    private void RemoveWordButton_Click(object sender, RoutedEventArgs e) => Host.RemoveWordButton_Click(sender, e);

    private void SelectAllWordsButton_Click(object sender, RoutedEventArgs e) => Host.SelectAllWordsButton_Click(sender, e);

    private void SettingRow_Tapped(object sender, TappedRoutedEventArgs e) => Host.SettingRow_Tapped(sender, e);

    private void SettingsTabStrip_SizeChanged(object sender, SizeChangedEventArgs e) => Host.SettingsTabStrip_SizeChanged(sender, e);

    private void SuggestAliasesButton_Click(object sender, RoutedEventArgs e) => Host.SuggestAliasesButton_Click(sender, e);

    private void WordFilterChip_Click(object sender, RoutedEventArgs e) => Host.WordFilterChip_Click(sender, e);

    private void WordFilterChipsContainer_SizeChanged(object sender, SizeChangedEventArgs e) => Host.WordFilterChipsContainer_SizeChanged(sender, e);

    private void WordListHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.WordListHeaderGrid_SizeChanged(sender, e);

    private void WordsFromAnotherAppButton_Click(object sender, RoutedEventArgs e) => Host.WordsFromAnotherAppButton_Click(sender, e);
}
