using EnviousWispr.App.Controls;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App.Pages;

/// <summary>Standalone load-on-demand page for <see cref="DictionaryPage"/>.</summary>
public sealed partial class DictionaryPageView : UserControl
{
    internal const string PackSaveErrorMessage = "Couldn't save this pack change. Nothing was changed. Try again.";

    private readonly EnviousWispr.App.Collections.BulkObservableCollection<DictionaryWordRow> _dictionaryRows = new();
    private readonly EnviousWispr.App.Collections.BulkObservableCollection<PackWordRowItem> _packWordRows = new();
    private readonly Dictionary<string, PackCardControls> _packCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packAliasDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackWordRowItem> _packRowItemsByCanonical = new(StringComparer.Ordinal);
    private readonly PackWordRowCallbacks _packWordCallbacks;
    private long _visibilityCallbackToken;
    private double[]? _wordFilterChipWidths;
    private int _lastWordFilterChipSplitIndex = -1;
    private CancellationTokenSource? _dictionaryRefreshCts;
    private int _dictionaryRefreshGeneration;
    private bool _dictionaryViewDirty;
    private (IReadOnlyList<CustomWordEntry> Source, DictionaryWordRow[] Rows)? _dictionaryRowCache;
    private string _wordFilterCategory = "all";
    private bool _isDictionaryMassEdit;
    private bool _syncingVocabularyPacks;
    private string? _openPackId;
    private string _packWordFilter = "all";
    private IReadOnlyList<string> _enabledPackIds = Array.Empty<string>();
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> _packOverrides =
        VocabularyPacks.EmptyOverrides;

    public DictionaryPageView(MainWindow host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
        _packWordCallbacks = new PackWordRowCallbacks
        {
            SetWordEnabledAsync = HandlePackWordEnabledAsync,
            SetWordAliasesAsync = HandlePackWordAliasesAsync,
            RestoreWordAsync = HandlePackWordRestoreAsync,
            GetDraft = GetPackAliasDraft,
            SetDraft = SetPackAliasDraft,
        };
        InitializeComponent();
        DictionaryList.ItemsSource = _dictionaryRows;
        PackWordsList.ItemsSource = _packWordRows;
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
        ClosePackDetail();
        _packCards.Clear();
        VocabularyPacksList.Children.Clear();
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

            ExportWordsButton.IsEnabled = totalWordCount > 0;
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
            allRows = new DictionaryWordRow[allWords.Count];
            for (var i = 0; i < allWords.Count; i++)
            {
                allRows[i] = new DictionaryWordRow(allWords[i]);
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

        var byEntry = new Dictionary<CustomWordEntry, DictionaryWordRow>(allRows.Length, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < allRows.Length; i++)
        {
            byEntry[allRows[i].Entry] = allRows[i];
        }

        var result = new DictionaryWordRow[matching.Count];
        for (var i = 0; i < matching.Count; i++)
        {
            result[i] = byEntry.TryGetValue(matching[i], out var row)
                ? row
                : new DictionaryWordRow(matching[i]);
        }

        return (result, nextCache);
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

    internal void ApplyVocabularyPacks(
        IReadOnlyList<string> enabledPackIds,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>> packOverrides)
    {
        _enabledPackIds = enabledPackIds ?? Array.Empty<string>();
        _packOverrides = packOverrides ?? VocabularyPacks.EmptyOverrides;

        EnsurePackListCardsBuilt();
        SyncPackListCards();
        if (_openPackId is not null)
        {
            RefreshOpenPackDetail();
        }
    }

    internal void ClosePackDetail()
    {
        _openPackId = null;
        _packWordFilter = "all";
        _packAliasDrafts.Clear();
        _packRowItemsByCanonical.Clear();
        _packWordRows.Clear();

        if (PackSearchBox is not null && !string.IsNullOrEmpty(PackSearchBox.Text))
        {
            PackSearchBox.Text = string.Empty;
        }

        if (PackSearchClearButton is not null)
        {
            PackSearchClearButton.Visibility = Visibility.Collapsed;
        }

        ClearPackDetailError();
        if (PackDetailPanel is not null)
        {
            PackDetailPanel.Visibility = Visibility.Collapsed;
        }

        if (PackListScrollViewer is not null)
        {
            PackListScrollViewer.Visibility = Visibility.Visible;
        }
    }

    private void OpenPackDetail(VocabularyPack pack)
    {
        ClearPackListError();
        ClearPackDetailError();
        _openPackId = pack.Id;
        _packWordFilter = "all";
        _packAliasDrafts.Clear();
        _packRowItemsByCanonical.Clear();
        _packWordRows.Clear();

        PackSearchBox.Text = string.Empty;
        PackSearchBox.PlaceholderText = $"Search {pack.Name.ToLowerInvariant()} words or mishearings";
        AutomationProperties.SetName(PackSearchBox, $"Search {pack.Name} words or mishearings");
        PackSearchClearButton.Visibility = Visibility.Collapsed;

        PackListScrollViewer.Visibility = Visibility.Collapsed;
        PackDetailPanel.Visibility = Visibility.Visible;
        RefreshOpenPackDetail();
    }

    private void EnsurePackListCardsBuilt()
    {
        if (VocabularyPacksList.Children.Count == VocabularyPacks.All.Count
            && _packCards.Count == VocabularyPacks.All.Count)
        {
            return;
        }

        VocabularyPacksList.Children.Clear();
        _packCards.Clear();
        foreach (var pack in VocabularyPacks.All)
        {
            var card = BuildVocabularyPackCard(pack);
            VocabularyPacksList.Children.Add(card.Root);
            _packCards[pack.Id] = card;
        }
    }

    private void SyncPackListCards()
    {
        var enabledSet = new HashSet<string>(_enabledPackIds, StringComparer.OrdinalIgnoreCase);

        _syncingVocabularyPacks = true;
        try
        {
            foreach (var pack in VocabularyPacks.All)
            {
                if (!_packCards.TryGetValue(pack.Id, out var controls))
                {
                    continue;
                }

                controls.Toggle.IsOn = enabledSet.Contains(pack.Id);
                var editedCount = VocabularyPacks.CountEditedWords(pack, _packOverrides);
                controls.EditedBadge.Visibility = editedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                controls.EditedBadgeText.Text = $"{editedCount} edited";
            }
        }
        finally
        {
            _syncingVocabularyPacks = false;
        }
    }

    private PackCardControls BuildVocabularyPackCard(VocabularyPack pack)
    {
        var icon = new FontIcon
        {
            Glyph = pack.IconGlyph,
            Style = (Style)Application.Current.Resources["BrandSectionEyebrowGlyphStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconTile = new Border
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(0, 2, 0, 0),
            Style = (Style)Application.Current.Resources["BrandIconTileStyle"],
            VerticalAlignment = VerticalAlignment.Top,
            Child = icon,
        };

        var titleText = new TextBlock
        {
            Text = pack.Name,
            Style = (Style)Application.Current.Resources["BrandRowLabelStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var editedBadgeText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BrandWarningChipTextStyle"],
            Text = "0 edited",
        };
        var editedBadge = new Border
        {
            Style = (Style)Application.Current.Resources["BrandWarningChipStyle"],
            Padding = new Thickness(8, 2, 8, 2),
            Visibility = Visibility.Collapsed,
            Child = editedBadgeText,
        };
        var titleCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { titleText, editedBadge },
        };

        var seeAllButton = new Button
        {
            Content = "See all",
            Style = (Style)Application.Current.Resources["BrandQuietButtonStyle"],
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(seeAllButton, $"See all words in {pack.Name}");
        seeAllButton.Click += (_, _) => OpenPackDetail(pack);

        var toggle = new ToggleSwitch
        {
            Style = (Style)Application.Current.Resources["BrandToggleSwitchStyle"],
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            Tag = pack.Id,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetLabeledBy(toggle, titleText);
        AutomationProperties.SetHelpText(toggle, pack.Description);
        toggle.Toggled += async (_, _) =>
        {
            if (_syncingVocabularyPacks)
            {
                return;
            }

            var enabled = toggle.IsOn;
            var saveResult = await Host.SetVocabularyPackEnabledAsync(pack.Id, enabled).ConfigureAwait(true);
            if (_visibilityCallbackToken == 0)
            {
                return;
            }

            if (!saveResult.Saved)
            {
                _syncingVocabularyPacks = true;
                try
                {
                    toggle.IsOn = !enabled;
                }
                finally
                {
                    _syncingVocabularyPacks = false;
                }

                if (saveResult.Refusal != SettingsSaveRefusal.Closing)
                {
                    ShowPackListError(PackSaveErrorMessage);
                }

                return;
            }

            ClearPackListError();
        };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(titleCluster);
        Grid.SetColumn(seeAllButton, 1);
        headerRow.Children.Add(seeAllButton);
        Grid.SetColumn(toggle, 2);
        headerRow.Children.Add(toggle);

        var descText = new TextBlock
        {
            Text = pack.Description,
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var summaryText = new TextBlock
        {
            Text = VocabularyPacks.FormatSummary(pack),
            Style = (Style)Application.Current.Resources["BrandHelperStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        var bodyStack = new StackPanel
        {
            Spacing = 4,
            Children = { headerRow, descText, summaryText },
        };

        var cardGrid = new Grid { ColumnSpacing = 12 };
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardGrid.Children.Add(iconTile);
        Grid.SetColumn(bodyStack, 1);
        cardGrid.Children.Add(bodyStack);

        var root = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            Style = (Style)Application.Current.Resources["BrandSettingsCardStyle"],
            Child = cardGrid,
        };
        return new PackCardControls(root, toggle, editedBadge, editedBadgeText);
    }

    private void RefreshOpenPackDetail()
    {
        if (_openPackId is null || VocabularyPacks.FindById(_openPackId) is not { } pack)
        {
            ClosePackDetail();
            return;
        }

        var states = VocabularyPacks.GetPackWordStates(pack, _packOverrides);
        var editedCount = states.Count(state => state.IsEdited);
        var isPackEnabled = _enabledPackIds.Contains(pack.Id, StringComparer.OrdinalIgnoreCase);

        if (editedCount == 0 && string.Equals(_packWordFilter, "edited", StringComparison.Ordinal))
        {
            _packWordFilter = "all";
        }

        PackDetailTitleText.Text = pack.Name;
        var wordNoun = states.Count == 1 ? "word" : "words";
        PackDetailSubtitleText.Text = editedCount > 0
            ? $"{states.Count} {wordNoun} \u00B7 {editedCount} edited"
            : $"{states.Count} {wordNoun}";
        PackDetailRestoreAllButton.Visibility = editedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        _syncingVocabularyPacks = true;
        try
        {
            PackDetailToggle.IsOn = isPackEnabled;
        }
        finally
        {
            _syncingVocabularyPacks = false;
        }

        PackFilterChipsPanel.Visibility = editedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        PackFilterEditedChip.Content = $"Edited ({editedCount})";
        SyncPackFilterChipStyles();

        var query = PackSearchBox.Text?.Trim() ?? string.Empty;
        var filtered = new List<PackWordRowItem>(states.Count);
        foreach (var state in states)
        {
            if (string.Equals(_packWordFilter, "edited", StringComparison.Ordinal) && !state.IsEdited)
            {
                continue;
            }

            if (query.Length > 0 && !MatchesPackWordQuery(state, query))
            {
                continue;
            }

            if (!_packRowItemsByCanonical.TryGetValue(state.Canonical, out var rowItem))
            {
                rowItem = new PackWordRowItem(state, _packWordCallbacks);
                _packRowItemsByCanonical[state.Canonical] = rowItem;
            }
            else
            {
                rowItem.UpdateState(state);
            }

            filtered.Add(rowItem);
        }

        _packWordRows.Reconcile(
            filtered,
            static (left, right) => string.Equals(left.Canonical, right.Canonical, StringComparison.Ordinal));

        var totalShown = filtered.Count;
        PackWordsCountText.Text = query.Length == 0 && string.Equals(_packWordFilter, "all", StringComparison.Ordinal)
            ? $"{states.Count} {wordNoun}"
            : $"Showing {totalShown} of {states.Count} {wordNoun}";

        var isEmpty = totalShown == 0;
        PackWordsList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        PackWordsEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        PackWordsEmptyText.Text = isEmpty
            ? (query.Length > 0
                ? $"No words match \u201C{query}\u201D."
                : "No edited words in this pack.")
            : string.Empty;
    }

    private static bool MatchesPackWordQuery(VocabularyPackWordState state, string query)
    {
        if (state.Canonical.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var i = 0; i < state.Aliases.Count; i++)
        {
            if (state.Aliases[i].Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void SyncPackFilterChipStyles()
    {
        var active = (Style)Application.Current.Resources["BrandTabStripActiveButtonStyle"];
        var normal = (Style)Application.Current.Resources["BrandTabStripButtonStyle"];
        var isEdited = string.Equals(_packWordFilter, "edited", StringComparison.Ordinal);
        PackFilterAllChip.Style = isEdited ? normal : active;
        PackFilterEditedChip.Style = isEdited ? active : normal;
    }

    private void PackDetailBackButton_Click(object sender, RoutedEventArgs e) => ClosePackDetail();

    private void PackDetailHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PackDetailActionsPanel is null || e.NewSize.Width <= 0)
        {
            return;
        }

        var narrow = e.NewSize.Width < 580;
        if (narrow)
        {
            Grid.SetRow(PackDetailActionsPanel, 1);
            Grid.SetColumn(PackDetailActionsPanel, 0);
            Grid.SetColumnSpan(PackDetailActionsPanel, 3);
            PackDetailActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            PackDetailActionsPanel.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            Grid.SetRow(PackDetailActionsPanel, 0);
            Grid.SetColumn(PackDetailActionsPanel, 2);
            Grid.SetColumnSpan(PackDetailActionsPanel, 1);
            PackDetailActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            PackDetailActionsPanel.Margin = new Thickness(0);
        }
    }

    private async void PackDetailToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingVocabularyPacks || _openPackId is not { } packId)
        {
            return;
        }

        var enabled = PackDetailToggle.IsOn;
        var saveResult = await Host.SetVocabularyPackEnabledAsync(packId, enabled).ConfigureAwait(true);
        if (_visibilityCallbackToken == 0 || !string.Equals(_openPackId, packId, StringComparison.Ordinal))
        {
            return;
        }

        if (!saveResult.Saved)
        {
            _syncingVocabularyPacks = true;
            try
            {
                PackDetailToggle.IsOn = !enabled;
            }
            finally
            {
                _syncingVocabularyPacks = false;
            }

            if (saveResult.Refusal != SettingsSaveRefusal.Closing)
            {
                ShowPackDetailError(PackSaveErrorMessage);
            }

            return;
        }

        ClearPackDetailError();
    }

    private async void PackDetailRestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openPackId is not { } packId || VocabularyPacks.FindById(packId) is not { } pack)
        {
            return;
        }

        var editedCount = VocabularyPacks.CountEditedWords(pack, _packOverrides);
        if (editedCount == 0)
        {
            return;
        }

        if (editedCount > 1 && !await Host.ConfirmRestoreVocabularyPackAsync(pack.Name, editedCount).ConfigureAwait(true))
        {
            return;
        }

        if (_visibilityCallbackToken == 0 || !string.Equals(_openPackId, packId, StringComparison.Ordinal))
        {
            return;
        }

        var saveResult = await Host.RestoreVocabularyPackAsync(pack.Id).ConfigureAwait(true);
        if (_visibilityCallbackToken == 0 || !string.Equals(_openPackId, packId, StringComparison.Ordinal))
        {
            return;
        }

        if (!saveResult.Saved)
        {
            if (saveResult.Refusal != SettingsSaveRefusal.Closing)
            {
                ShowPackDetailError(PackSaveErrorMessage);
            }

            return;
        }

        _packAliasDrafts.Clear();
        ClearPackDetailError();
    }

    private void PackSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PackSearchClearButton is not null)
        {
            PackSearchClearButton.Visibility = string.IsNullOrWhiteSpace(PackSearchBox?.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (_openPackId is not null)
        {
            RefreshOpenPackDetail();
        }
    }

    private void PackSearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (PackSearchBox is not null)
        {
            PackSearchBox.Text = string.Empty;
            PackSearchBox.Focus(FocusState.Programmatic);
        }

        if (string.Equals(_packWordFilter, "edited", StringComparison.Ordinal))
        {
            _packWordFilter = "all";
            RefreshOpenPackDetail();
        }
    }

    private void PackFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            _packWordFilter = tag;
            RefreshOpenPackDetail();
        }
    }

    private void DismissPackListErrorButton_Click(object sender, RoutedEventArgs e) => ClearPackListError();

    private void DismissPackDetailErrorButton_Click(object sender, RoutedEventArgs e) => ClearPackDetailError();

    private void ShowPackListError(string message)
    {
        PackListErrorBanner.Visibility = Visibility.Visible;
        MainWindow.SetLiveRegion(PackListErrorText, message, Visibility.Visible);
    }

    private void ClearPackListError()
    {
        if (PackListErrorBanner is not null)
        {
            PackListErrorBanner.Visibility = Visibility.Collapsed;
        }

        if (PackListErrorText is not null)
        {
            MainWindow.SetLiveRegion(PackListErrorText, string.Empty, Visibility.Collapsed);
        }
    }

    private void ShowPackDetailError(string message)
    {
        PackDetailErrorBanner.Visibility = Visibility.Visible;
        MainWindow.SetLiveRegion(PackDetailErrorText, message, Visibility.Visible);
    }

    private void ClearPackDetailError()
    {
        if (PackDetailErrorBanner is not null)
        {
            PackDetailErrorBanner.Visibility = Visibility.Collapsed;
        }

        if (PackDetailErrorText is not null)
        {
            MainWindow.SetLiveRegion(PackDetailErrorText, string.Empty, Visibility.Collapsed);
        }
    }

    private async Task<bool> HandlePackWordEnabledAsync(string canonical, bool isEnabled)
    {
        if (_openPackId is not { } packId)
        {
            return false;
        }

        var result = await Host.SetVocabularyPackWordEnabledAsync(packId, canonical, isEnabled).ConfigureAwait(true);
        if (_visibilityCallbackToken == 0 || !string.Equals(_openPackId, packId, StringComparison.Ordinal))
        {
            return result.Saved;
        }

        if (!result.Saved)
        {
            if (result.Refusal != SettingsSaveRefusal.Closing)
            {
                ShowPackDetailError(PackSaveErrorMessage);
            }

            return false;
        }

        ClearPackDetailError();
        return true;
    }

    private async Task<bool> HandlePackWordAliasesAsync(string canonical, IReadOnlyList<string> aliases)
    {
        if (_openPackId is not { } packId)
        {
            return false;
        }

        var result = await Host.SetVocabularyPackWordAliasesAsync(packId, canonical, aliases).ConfigureAwait(true);
        if (_visibilityCallbackToken == 0 || !string.Equals(_openPackId, packId, StringComparison.Ordinal))
        {
            return result.Saved;
        }

        if (!result.Saved)
        {
            if (result.Refusal != SettingsSaveRefusal.Closing)
            {
                ShowPackDetailError(PackSaveErrorMessage);
            }

            return false;
        }

        ClearPackDetailError();
        return true;
    }

    private async Task<bool> HandlePackWordRestoreAsync(string canonical)
    {
        if (_openPackId is not { } packId)
        {
            return false;
        }

        var result = await Host.RestoreVocabularyPackWordAsync(packId, canonical).ConfigureAwait(true);
        if (_visibilityCallbackToken == 0 || !string.Equals(_openPackId, packId, StringComparison.Ordinal))
        {
            return result.Saved;
        }

        if (!result.Saved)
        {
            if (result.Refusal != SettingsSaveRefusal.Closing)
            {
                ShowPackDetailError(PackSaveErrorMessage);
            }

            return false;
        }

        ClearPackDetailError();
        return true;
    }

    private string GetPackAliasDraft(string canonical) =>
        _packAliasDrafts.TryGetValue(canonical, out var draft) ? draft : string.Empty;

    private void SetPackAliasDraft(string canonical, string draft)
    {
        if (string.IsNullOrEmpty(draft))
        {
            _packAliasDrafts.Remove(canonical);
        }
        else
        {
            _packAliasDrafts[canonical] = draft;
        }
    }

    private sealed record PackCardControls(
        Border Root,
        ToggleSwitch Toggle,
        Border EditedBadge,
        TextBlock EditedBadgeText);
}
