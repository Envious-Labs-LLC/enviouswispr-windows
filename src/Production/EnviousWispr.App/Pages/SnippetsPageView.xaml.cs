using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App.Pages;

/// <summary>Standalone load-on-demand page for <see cref="SnippetsPage"/>.</summary>
public sealed partial class SnippetsPageView : UserControl
{
    private long _visibilityCallbackToken;

    public SnippetsPageView(MainWindow host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        InitializeComponent();
        Visibility = SnippetsPage.Visibility;
        _visibilityCallbackToken = SnippetsPage.RegisterPropertyChangedCallback(
            VisibilityProperty,
            (_, _) => Visibility = SnippetsPage.Visibility);
    }

    internal MainWindow Host { get; }

    internal void Detach()
    {
        if (_visibilityCallbackToken != 0)
        {
            SnippetsPage.UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }
    }

    private void AddSnippetToolbarButton_Click(object sender, RoutedEventArgs e) => Host.AddSnippetToolbarButton_Click(sender, e);

    private void ClearSnippetSearchButton_Click(object sender, RoutedEventArgs e) => Host.ClearSnippetSearchButton_Click(sender, e);

    private void EditSnippetRowButton_Click(object sender, RoutedEventArgs e) => Host.EditSnippetRowButton_Click(sender, e);

    private void ExportSnippetsButton_Click(object sender, RoutedEventArgs e) => Host.ExportSnippetsButton_Click(sender, e);

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) => Host.ListSelectionChanged(sender, e);

    private void LoadExampleSnippetsButton_Click(object sender, RoutedEventArgs e) => Host.LoadExampleSnippetsButton_Click(sender, e);

    private void OpenNewSnippetDialogButton_Click(object sender, RoutedEventArgs e) => Host.OpenNewSnippetDialogButton_Click(sender, e);

    private void OpenSnippetsFileMenuItem_Click(object sender, RoutedEventArgs e) => Host.OpenSnippetsFileMenuItem_Click(sender, e);

    private void PasteSnippetsMenuItem_Click(object sender, RoutedEventArgs e) => Host.PasteSnippetsMenuItem_Click(sender, e);

    private void SnippetKeywordBox_KeyDown(object sender, KeyRoutedEventArgs e) => Host.SnippetKeywordBox_KeyDown(sender, e);

    private void SnippetKeywordBox_LostFocus(object sender, RoutedEventArgs e) => Host.SnippetKeywordBox_LostFocus(sender, e);

    private void SnippetList_ItemClick(object sender, ItemClickEventArgs e) => Host.SnippetList_ItemClick(sender, e);

    private void SnippetSearchBox_TextChanged(object sender, TextChangedEventArgs e) => Host.SnippetSearchBox_TextChanged(sender, e);

    private void SnippetsFromAnotherAppMenuItem_Click(object sender, RoutedEventArgs e) => Host.SnippetsFromAnotherAppMenuItem_Click(sender, e);

    private void SnippetsToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.SnippetsToolbarGrid_SizeChanged(sender, e);
}
