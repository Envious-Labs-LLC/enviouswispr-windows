using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EnviousWispr.App;

public sealed record LanguagePickerOption(
    string Code,
    string DisplayTitle,
    string Subtitle,
    bool IsSelected)
{
    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public string AccessibilityLabel => IsSelected
        ? $"{DisplayTitle}, {Subtitle}, Selected"
        : $"{DisplayTitle}, {Subtitle}";

    public override string ToString() => DisplayTitle;
}

public sealed partial class LanguagePickerDialog : ContentDialog
{
    private readonly FinalAsrEngine _engine;
    private readonly string? _selectedEntryCode;
    private readonly bool _isAutomaticSelected;
    private readonly IReadOnlyList<LanguageEntry> _recentEntries;
    private LanguagePickerOption[] _visibleOptions = [];

    public LanguagePickerDialog(
        FinalAsrEngine engine,
        string? currentStoredCode,
        EnglishSpelling currentSpelling,
        IReadOnlyList<string>? recentCodes)
    {
        InitializeComponent();
        _engine = engine;
        var selected = LanguageCatalog.ResolveSelection(currentStoredCode, currentSpelling);
        _selectedEntryCode = selected?.Code;
        _isAutomaticSelected = selected is null;
        _recentEntries = LanguageCatalog.ResolveRecentEntries(recentCodes, engine);
        RecentListView.ContainerContentChanging += LanguageListView_ContainerContentChanging;
        AllLanguagesListView.ContainerContentChanging += LanguageListView_ContainerContentChanging;

        ScopeText.Text = engine == FinalAsrEngine.Parakeet
            ? "Parakeet supports 25 European languages with script pinning, or use Automatic detection."
            : "Whisper supports 99+ languages, or use Automatic detection.";
        AutomaticCheckIcon.Visibility = _isAutomaticSelected ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(
            AutomaticOptionButton,
            _isAutomaticSelected ? "Selected" : string.Empty);

        RefreshOptions(string.Empty);
    }

    public string? SelectedPickerCode { get; private set; }

    private void LanguagePickerDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshOptions(SearchBox.Text);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        if (_visibleOptions.Length == 1)
        {
            SelectAndClose(_visibleOptions[0].Code);
            e.Handled = true;
            return;
        }

        if (_visibleOptions.Length == 0 && AutomaticOptionButton.Visibility == Visibility.Visible)
        {
            SelectAndClose(LanguageCatalog.AutomaticCode);
            e.Handled = true;
        }
    }

    private void AutomaticOptionButton_Click(object sender, RoutedEventArgs e)
    {
        SelectAndClose(LanguageCatalog.AutomaticCode);
    }

    private void LanguageListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LanguagePickerOption option)
        {
            SelectAndClose(option.Code);
        }
    }

    private static void LanguageListView_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is null || args.Item is not LanguagePickerOption option)
        {
            return;
        }

        AutomationProperties.SetName(args.ItemContainer, option.AccessibilityLabel);
        AutomationProperties.SetItemStatus(args.ItemContainer, option.IsSelected ? "Selected" : string.Empty);
    }

    private void SelectAndClose(string pickerCode)
    {
        SelectedPickerCode = pickerCode;
        Hide();
    }

    private void RefreshOptions(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        var isSearching = trimmed.Length > 0;

        var showAutomatic = !isSearching ||
            LanguageCatalog.AutomaticTitle.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            LanguageCatalog.AutomaticCode.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        AutomaticOptionButton.Visibility = showAutomatic ? Visibility.Visible : Visibility.Collapsed;

        var showRecent = !isSearching && _recentEntries.Count > 0;
        RecentSectionPanel.Visibility = showRecent ? Visibility.Visible : Visibility.Collapsed;
        RecentListView.ItemsSource = showRecent
            ? _recentEntries.Select(ToOption).ToArray()
            : Array.Empty<LanguagePickerOption>();

        var matches = LanguageCatalog.FilterEntries(_engine, trimmed)
            .Select(ToOption)
            .ToArray();
        _visibleOptions = matches;

        CatalogHeaderText.Text = isSearching
            ? "Matching languages"
            : _engine == FinalAsrEngine.Parakeet
                ? "European languages (25)"
                : "All languages";

        AllLanguagesListView.ItemsSource = matches;
        if (matches.Length == 0)
        {
            AllLanguagesListView.Visibility = Visibility.Collapsed;
            EmptyStateText.Text = isSearching
                ? $"No languages match \"{trimmed}\"."
                : "No languages available.";
            EmptyStateText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            AllLanguagesListView.Visibility = Visibility.Visible;
        }
    }

    private LanguagePickerOption ToOption(LanguageEntry entry) => new(
        entry.Code,
        entry.DisplayName,
        entry.PickerSubtitle,
        !_isAutomaticSelected && string.Equals(entry.Code, _selectedEntryCode, StringComparison.Ordinal));
}
