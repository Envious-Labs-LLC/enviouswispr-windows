using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App.Pages;

/// <summary>Standalone load-on-demand page for <see cref="DictionaryPage"/>.</summary>
public sealed partial class DictionaryPageView : UserControl
{
    private readonly EnviousWispr.App.Collections.BulkObservableCollection<DictionaryWordRow> _dictionaryRows = new();
    private long _visibilityCallbackToken;
    private double[]? _wordFilterChipWidths;
    private int _lastWordFilterChipSplitIndex = -1;
    private CancellationTokenSource? _dictionaryRefreshCts;
    private int _dictionaryRefreshGeneration;
    private bool _dictionaryViewDirty;
    private (IReadOnlyList<CustomWordEntry> Source, DictionaryWordRow[] Rows)? _dictionaryRowCache;
    private string _wordFilterCategory = "all";
    private bool _isDictionaryMassEdit;

    public DictionaryPageView(MainWindow host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        InitializeComponent();
        DictionaryList.ItemsSource = _dictionaryRows;
        _dictionaryViewDirty = true;
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

        InvalidatePendingRefresh();
        _dictionaryRowCache = null;
        _dictionaryRows.Clear();
    }

    internal void ApplyWords(IReadOnlyList<CustomWordEntry> allWords, bool isPageVisible)
    {
        _dictionaryViewDirty = true;
        if (isPageVisible)
        {
            ScheduleRefresh(allWords, debounce: false);
            return;
        }

        InvalidatePendingRefresh();
        var wordUnit = allWords.Count == 1 ? "WORD" : "WORDS";
        WordCountText.Text = $"{allWords.Count} {wordUnit}";
        UpdateSelectionUi(allWords.Count);
    }

    internal void FlushPendingRefresh(IReadOnlyList<CustomWordEntry> allWords)
    {
        if (_dictionaryViewDirty)
        {
            ScheduleRefresh(allWords, debounce: false);
        }
    }

    internal CustomWordEntry[] GetSelectedWords() =>
        DictionaryList.SelectionMode == ListViewSelectionMode.None
            ? Array.Empty<CustomWordEntry>()
            : DictionaryList.SelectedItems
                .Select(item => item is DictionaryWordRow row ? row.Entry : item as CustomWordEntry)
                .OfType<CustomWordEntry>()
                .ToArray();

    internal void UpdateSelectionUi(int totalWordCount)
    {
        if (RemoveWordButton is not null
            && SelectAllWordsButton is not null
            && WordSelectionCountText is not null
            && DictionaryList is not null
            && ExportWordsButton is not null)
        {
            var selectedWords = DictionaryList.SelectionMode == ListViewSelectionMode.None
                ? 0
                : DictionaryList.SelectedItems.Count;
            RemoveWordButton.IsEnabled = selectedWords > 0;
            SelectAllWordsButton.IsEnabled = totalWordCount > 0;

            MainWindow.SetLiveRegion(
                WordSelectionCountText,
                selectedWords > 1 ? $"{selectedWords} selected" : string.Empty,
                selectedWords > 1 ? Visibility.Visible : Visibility.Collapsed);

            ExportWordsButton.IsEnabled = Host.CurrentUserWordCount > 0;
        }
    }

    private void AddWordButton_Click(object sender, RoutedEventArgs e) => Host.AddWordButton_Click(sender, e);

    private void DictionaryEnabledToggle_Toggled(object sender, RoutedEventArgs e) => Host.DictionaryEnabledToggle_Toggled(sender, e);

    private void DictionarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DictionarySearchClearButton is not null)
        {
            DictionarySearchClearButton.Visibility = string.IsNullOrWhiteSpace(DictionarySearchBox?.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        ScheduleRefresh(Host.CurrentCustomWords, debounce: true);
    }

    private void DictionarySearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (DictionarySearchBox is not null)
        {
            DictionarySearchBox.Text = string.Empty;
            DictionarySearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void DictionaryTab_Click(object sender, RoutedEventArgs e) => Host.DictionaryTab_Click(sender, e);

    private void EditWordRowButton_Click(object sender, RoutedEventArgs e) => Host.EditWordRowButton_Click(sender, e);

    private void ExportWordsButton_Click(object sender, RoutedEventArgs e) => Host.ExportWordsButton_Click(sender, e);

    private void FocusAddWordButton_Click(object sender, RoutedEventArgs e) => Host.FocusAddWordButton_Click(sender, e);

    private void ForgetLearnMemoryButton_Click(object sender, RoutedEventArgs e) => Host.ForgetLearnMemoryButton_Click(sender, e);

    private void GoToQuickAddKeybindButton_Click(object sender, RoutedEventArgs e) => Host.GoToQuickAddKeybindButton_Click(sender, e);

    private void ImportWordsButton_Click(object sender, RoutedEventArgs e) => Host.ImportWordsButton_Click(sender, e);

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) => Host.ListSelectionChanged(sender, e);

    private void MassEditWordsToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDictionaryMassEdit = !_isDictionaryMassEdit;
        if (DictionaryList.SelectionMode != ListViewSelectionMode.None)
        {
            DictionaryList.SelectedItems.Clear();
        }

        DictionaryList.SelectionMode = _isDictionaryMassEdit
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.None;
        DictionaryMassEditBar.Visibility = _isDictionaryMassEdit ? Visibility.Visible : Visibility.Collapsed;
        MassEditWordsToggle.Style = (Style)Application.Current.Resources[
            _isDictionaryMassEdit ? "BrandQuietActiveButtonStyle" : "BrandQuietButtonStyle"];
        UpdateSelectionUi(Host.CurrentCustomWords.Count);
    }

    private void OpenAddWordDialogButton_Click(object sender, RoutedEventArgs e) => Host.OpenAddWordDialogButton_Click(sender, e);

    private void PasteWordsButton_Click(object sender, RoutedEventArgs e) => Host.PasteWordsButton_Click(sender, e);

    private void QuickAddStepsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.QuickAddStepsGrid_SizeChanged(sender, e);

    private void RemoveWordButton_Click(object sender, RoutedEventArgs e) => Host.RemoveWordButton_Click(sender, e);

    private void ReplacementBox_TextChanged(object sender, TextChangedEventArgs e) => Host.ReplacementBox_TextChanged(sender, e);

    /// <summary>Selects every word, or clears the selection when they are all already chosen.</summary>
    private void SelectAllWordsButton_Click(object sender, RoutedEventArgs e)
    {
        var shownCount = DictionaryList.Items.Count;
        if (DictionaryList.SelectionMode != ListViewSelectionMode.Multiple)
        {
            if (!_isDictionaryMassEdit)
            {
                MassEditWordsToggle_Click(sender, e);
            }
            else
            {
                DictionaryList.SelectionMode = ListViewSelectionMode.Multiple;
            }
        }

        if (DictionaryList.SelectionMode != ListViewSelectionMode.Multiple)
        {
            return;
        }

        if (DictionaryList.SelectedItems.Count == shownCount && shownCount > 0)
        {
            DictionaryList.SelectedItems.Clear();
            return;
        }

        DictionaryList.SelectAll();
    }

    private void SettingRow_Tapped(object sender, TappedRoutedEventArgs e) => Host.SettingRow_Tapped(sender, e);

    private void SettingsTabStrip_SizeChanged(object sender, SizeChangedEventArgs e) => Host.SettingsTabStrip_SizeChanged(sender, e);

    private void SuggestAliasesButton_Click(object sender, RoutedEventArgs e) => Host.SuggestAliasesButton_Click(sender, e);

    private void WordFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _wordFilterCategory = tag;
            var active = (Style)Application.Current.Resources["BrandTabStripActiveButtonStyle"];
            var normal = (Style)Application.Current.Resources["BrandTabStripButtonStyle"];
            WordFilterAllChip.Style = tag == "all" ? active : normal;
            WordFilterGeneralChip.Style = tag == "general" ? active : normal;
            WordFilterPersonChip.Style = tag == "person" ? active : normal;
            WordFilterBrandChip.Style = tag == "brand" ? active : normal;
            WordFilterAcronymChip.Style = tag == "acronym" ? active : normal;
            WordFilterDomainChip.Style = tag == "domain" ? active : normal;
            WordFilterAutoLearnedChip.Style = tag == "auto-learned" ? active : normal;
            ScheduleRefresh(Host.CurrentCustomWords, debounce: false);
        }
    }

    private void WordFilterChipsContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WordFilterChipsPrimaryRow is null || WordFilterChipsOverflowRow is null || e.NewSize.Width <= 0)
        {
            return;
        }

        Button[] allChips =
        [
            WordFilterAllChip,
            WordFilterGeneralChip,
            WordFilterPersonChip,
            WordFilterBrandChip,
            WordFilterAcronymChip,
            WordFilterDomainChip,
            WordFilterAutoLearnedChip,
        ];
        if (allChips.Any(c => c is null))
        {
            return;
        }

        if (_wordFilterChipWidths is null || _wordFilterChipWidths.Length != allChips.Length)
        {
            _wordFilterChipWidths = new double[allChips.Length];
            for (var i = 0; i < allChips.Length; i++)
            {
                if (allChips[i].DesiredSize.Width <= 0)
                {
                    allChips[i].Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                }

                _wordFilterChipWidths[i] = Math.Max(64, allChips[i].DesiredSize.Width);
            }
        }

        var available = e.NewSize.Width;
        var spacing = MainWindow.LayoutDouble("BrandSpacingXs");
        var usedWidth = 0.0;
        var splitIndex = allChips.Length;
        for (var i = 0; i < allChips.Length; i++)
        {
            var chipWidth = _wordFilterChipWidths[i];
            var nextWidth = i == 0 ? chipWidth : usedWidth + spacing + chipWidth;
            if (nextWidth <= available)
            {
                usedWidth = nextWidth;
            }
            else
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex == _lastWordFilterChipSplitIndex &&
            WordFilterChipsPrimaryRow.Children.Count == splitIndex &&
            WordFilterChipsOverflowRow.Children.Count == allChips.Length - splitIndex)
        {
            return;
        }

        _lastWordFilterChipSplitIndex = splitIndex;
        WordFilterChipsPrimaryRow.Children.Clear();
        WordFilterChipsOverflowRow.Children.Clear();
        for (var i = 0; i < allChips.Length; i++)
        {
            if (i < splitIndex)
            {
                WordFilterChipsPrimaryRow.Children.Add(allChips[i]);
            }
            else
            {
                WordFilterChipsOverflowRow.Children.Add(allChips[i]);
            }
        }

        WordFilterChipsOverflowRow.Visibility = WordFilterChipsOverflowRow.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void WordListHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) => Host.WordListHeaderGrid_SizeChanged(sender, e);

    private void WordsFromAnotherAppButton_Click(object sender, RoutedEventArgs e) => Host.WordsFromAnotherAppButton_Click(sender, e);

    private void InvalidatePendingRefresh()
    {
        _dictionaryRefreshGeneration++;
        _dictionaryRefreshCts?.Cancel();
        _dictionaryRefreshCts?.Dispose();
        _dictionaryRefreshCts = null;
    }

    private void ScheduleRefresh(IReadOnlyList<CustomWordEntry> allWords, bool debounce)
    {
        InvalidatePendingRefresh();
        var cts = new CancellationTokenSource();
        _dictionaryRefreshCts = cts;
        var generation = _dictionaryRefreshGeneration;
        var cachedSnapshot = _dictionaryRowCache;
        var category = _wordFilterCategory;
        var query = DictionarySearchBox?.Text?.Trim() ?? string.Empty;

        if (!debounce && allWords.Count < 128)
        {
            var build = BuildFilteredDictionaryRows(allWords, category, query, cachedSnapshot);
            _dictionaryRowCache = build.NextCache;
            ApplyFilteredDictionaryRows(build.Rows, allWords.Count);
            return;
        }

        _ = RefreshDictionaryAsync(allWords, category, query, cachedSnapshot, debounce, generation, cts.Token);
    }

    private async Task RefreshDictionaryAsync(
        IReadOnlyList<CustomWordEntry> allWords,
        string category,
        string query,
        (IReadOnlyList<CustomWordEntry> Source, DictionaryWordRow[] Rows)? cachedSnapshot,
        bool debounce,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(true);
            }

            if (cancellationToken.IsCancellationRequested
                || generation != _dictionaryRefreshGeneration
                || _visibilityCallbackToken == 0
                || Host.IsClosing)
            {
                return;
            }

            var build = await Task.Run(
                () => BuildFilteredDictionaryRows(allWords, category, query, cachedSnapshot),
                cancellationToken).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested
                || generation != _dictionaryRefreshGeneration
                || _visibilityCallbackToken == 0
                || Host.IsClosing)
            {
                return;
            }

            _dictionaryRowCache = build.NextCache;
            ApplyFilteredDictionaryRows(build.Rows, allWords.Count);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (DictionaryWordRow[] Rows, (IReadOnlyList<CustomWordEntry> Source, DictionaryWordRow[] Rows) NextCache) BuildFilteredDictionaryRows(
        IReadOnlyList<CustomWordEntry> allWords,
        string category,
        string query,
        (IReadOnlyList<CustomWordEntry> Source, DictionaryWordRow[] Rows)? cachedSnapshot)
    {
        DictionaryWordRow[] allRows;
        (IReadOnlyList<CustomWordEntry> Source, DictionaryWordRow[] Rows) nextCache;
        if (cachedSnapshot is { Source: var cachedSource } && ReferenceEquals(cachedSource, allWords))
        {
            allRows = cachedSnapshot.Value.Rows;
            nextCache = cachedSnapshot.Value;
        }
        else
        {
            var sorted = allWords
                .OrderBy(entry => entry.Replacement, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
            allRows = new DictionaryWordRow[sorted.Length];
            for (var i = 0; i < sorted.Length; i++)
            {
                allRows[i] = new DictionaryWordRow(sorted[i]);
            }

            nextCache = (allWords, allRows);
        }

        var noCategoryFilter = string.IsNullOrWhiteSpace(category) ||
            string.Equals(category, "all", StringComparison.OrdinalIgnoreCase);
        if (noCategoryFilter && string.IsNullOrEmpty(query))
        {
            return (allRows, nextCache);
        }

        var matching = VocabularyPresenter.FilterWords(allWords, category, query);
        if (matching.Count == 0)
        {
            return ([], nextCache);
        }

        if (matching.Count == allRows.Length)
        {
            return (allRows, nextCache);
        }

        var matchingSet = new HashSet<CustomWordEntry>(matching, ReferenceEqualityComparer.Instance);
        var result = new List<DictionaryWordRow>(matching.Count);
        for (var i = 0; i < allRows.Length; i++)
        {
            if (matchingSet.Contains(allRows[i].Entry))
            {
                result.Add(allRows[i]);
            }
        }

        return (result.ToArray(), nextCache);
    }

    private void ApplyFilteredDictionaryRows(DictionaryWordRow[] rows, int totalCount)
    {
        _dictionaryViewDirty = false;
        if (DictionaryList is not null)
        {
            _dictionaryRows.Reconcile(rows, static (left, right) => ReferenceEquals(left, right) || Equals(left.Entry, right.Entry));
            MainWindow.UpdateListAndEmptyStateVisibility(DictionaryList, DictionaryEmptyState, rows.Length);
        }

        if (WordCountText is not null)
        {
            var wordUnit = totalCount == 1 ? "WORD" : "WORDS";
            WordCountText.Text = rows.Length == totalCount
                ? $"{totalCount} {wordUnit}"
                : $"Showing {rows.Length} of {totalCount} {wordUnit}";
        }

        UpdateSelectionUi(totalCount);
    }
}
