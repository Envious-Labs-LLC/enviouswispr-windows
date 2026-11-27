using System.Collections.ObjectModel;
using System.Globalization;
using EnviousWispr.App.Controls;
using EnviousWispr.App.Services;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App;

/// <summary>Dictation history diary, search, filtering, re-polish, playback, and recovery card UI.</summary>
public sealed partial class MainWindow
{
    private readonly WindowsAudioPlaybackService _audioPlayback;

    private HistoryLoadStatus _historyLoadStatus = HistoryLoadStatus.Missing;
    private bool _isHistoryLoading = true;

    private bool _isRepolishing;

    public string? DictationRecordingsDirectory { get; set; }

    public event Action? RecoveryCleared;

    /// <summary>The person pressed Undo beside an Escape Recovery on Home.</summary>
    public event Action? RecoveryUndoRequested;

    /// <summary>The person asked for the selected History entry to be pasted into the app they were using.</summary>
    public event Action<Guid>? HistoryPasteRequested;

    public async Task NotifyHistoryChangedAsync() => await ReloadHistoryAsync().ConfigureAwait(true);

    private void InitializeHistoryUi()
    {
        if (HistoryList is not null)
        {
            HistoryList.ItemsSource = _historyRows;
        }

        _historyAnnounceDebounce.Tick += OnHistoryAnnounceDue;
        _historySearchDebounce.Interval = TimeSpan.FromMilliseconds(150);
        _historySearchDebounce.Tick += OnHistorySearchDebounceDue;
    }

    private void ShutdownHistoryUi()
    {
        _historyAnnounceDebounce.Stop();
        _historyAnnounceDebounce.Tick -= OnHistoryAnnounceDue;
        _historySearchDebounce.Stop();
        _historySearchDebounce.Tick -= OnHistorySearchDebounceDue;
        CancelActiveHistoryQuery();
        CancelWaveformLoads();
    }


    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;
        _allMatchingSelected = false;
        var hasText = !string.IsNullOrEmpty(HistorySearchBox.Text);
        if (InlineClearHistorySearchButton is not null)
        {
            InlineClearHistorySearchButton.Visibility = hasText
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!hasText)
        {
            _historySearchDebounce.Stop();
            _ = RefreshHistoryAsync(showLoadingState: false);
            return;
        }

        CancelActiveHistoryQuery();
        _historySearchDebounce.Stop();
        _historySearchDebounce.Start();
    }

    private void OnHistorySearchDebounceDue(object? sender, object e)
    {
        _historySearchDebounce.Stop();
        if (_shuttingDownProductWindows || _session.Closing)
        {
            return;
        }

        _ = RefreshHistoryAsync(showLoadingState: false);
    }

    private void ClearHistorySearchButton_Click(object sender, RoutedEventArgs e) =>
        HistorySearchBox.Text = string.Empty;

    private void ClearHistoryFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _historySearchDebounce.Stop();
        _allMatchingSelected = false;
        HistorySearchBox.Text = string.Empty;
        _historyKindFilter = HistoryKindFilter.Dictation;
        _historyDateRangeFilter = HistoryDateRangeFilter.AnyTime;
        _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;
        if (HistoryDateFilterText is not null)
        {
            HistoryDateFilterText.Text = HistoryDiaryPresentation.DateRangeLabel(_historyDateRangeFilter);
        }

        UpdateHistoryFilterSegmentButtons();
        _ = RefreshHistoryAsync(showLoadingState: false);
    }

    private void HistoryDateFilterItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
        {
            return;
        }

        _historyDateRangeFilter = tag switch
        {
            "Today" => HistoryDateRangeFilter.Today,
            "Yesterday" => HistoryDateRangeFilter.Yesterday,
            "Last7Days" => HistoryDateRangeFilter.Last7Days,
            "Last30Days" => HistoryDateRangeFilter.Last30Days,
            _ => HistoryDateRangeFilter.AnyTime,
        };
        _allMatchingSelected = false;
        _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;
        if (HistoryDateFilterText is not null)
        {
            HistoryDateFilterText.Text = HistoryDiaryPresentation.DateRangeLabel(_historyDateRangeFilter);
        }

        UpdateHistoryFilterSegmentButtons();
        _ = RefreshHistoryAsync(showLoadingState: false);
    }

    private void HistorySelectToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetHistorySelectionMode(!_isHistorySelectionMode);
        UpdateHistoryFilterSegmentButtons();
        RefreshCardSelectionVisuals();
        UpdateHistorySelectionDependentButtons();
    }

    private void SetHistorySelectionMode(bool isSelectionMode)
    {
        _isHistorySelectionMode = isSelectionMode;
        if (!isSelectionMode)
        {
            _selectedHistoryIds.Clear();
            _allMatchingSelected = false;
            _lastBulkDeletionSummary = null;
        }

        if (HistorySelectionToolbar is not null)
        {
            HistorySelectionToolbar.Visibility = isSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        }
        if (HistorySelectToggleText is not null)
        {
            HistorySelectToggleText.Text = isSelectionMode ? "Done" : "Select";
        }
        if (HistorySelectToggleButton is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                HistorySelectToggleButton,
                isSelectionMode ? "Done selecting dictations" : "Select dictations");
        }
    }

    private void HistoryHelpButton_Click(object sender, RoutedEventArgs e) => PopulateHistoryHelpItems();

    private void PopulateHistoryHelpItems()
    {
        if (HistoryHelpItemsControl is null)
        {
            return;
        }

        var retentionDays = _settings?.Preferences.History.RetentionDays ?? UserPreferences.Default.History.RetentionDays;
        if (_lastHelpRetentionDays == retentionDays && HistoryHelpItemsControl.ItemsSource is not null)
        {
            return;
        }

        _lastHelpRetentionDays = retentionDays;
        HistoryHelpItemsControl.ItemsSource = HistoryDiaryPresentation.HelpItems(retentionDays);
    }

    private async void SelectAllMatchingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyMatchingCount == 0 || _isRepolishing || _isBulkDeletingHistory)
        {
            return;
        }

        var expectedQuery = HistorySearchBox.Text.Trim();
        var expectedKind = _historyKindFilter;
        var expectedDateRange = _historyDateRangeFilter;
        var expectedQueryVersion = Volatile.Read(ref _historyQueryVersion);
        var matchingIds = await _session.History.QueryMatchingIdsAsync(
            expectedQuery,
            expectedKind,
            expectedDateRange,
            DateTimeOffset.Now,
            TimeZoneInfo.Local).ConfigureAwait(true);
        if (_session.Closing
            || _shuttingDownProductWindows
            || matchingIds.Count == 0
            || !string.Equals(expectedQuery, HistorySearchBox?.Text.Trim() ?? string.Empty, StringComparison.Ordinal)
            || expectedKind != _historyKindFilter
            || expectedDateRange != _historyDateRangeFilter
            || expectedQueryVersion != Volatile.Read(ref _historyQueryVersion))
        {
            return;
        }

        var allSelected = _allMatchingSelected || matchingIds.All(_selectedHistoryIds.Contains);
        _lastBulkDeletionSummary = null;
        if (allSelected)
        {
            foreach (var id in matchingIds)
            {
                _selectedHistoryIds.Remove(id);
            }

            _allMatchingSelected = false;
        }
        else
        {
            foreach (var id in matchingIds)
            {
                _selectedHistoryIds.Add(id);
            }

            _allMatchingSelected = true;
        }

        RefreshCardSelectionVisuals();
        UpdateHistorySelectionDependentButtons();
    }

    private void LoadMoreHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _historyVisibleLimit += HistoryDiaryPresentation.DefaultPageSize;
        _ = RefreshHistoryAsync(showLoadingState: false);
    }

    private void CardSelectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        _lastBulkDeletionSummary = null;
        if (_selectedHistoryIds.Remove(card.Id))
        {
            _allMatchingSelected = false;
        }
        else
        {
            _selectedHistoryIds.Add(card.Id);
        }

        card.SetSelectionState(_isHistorySelectionMode, _selectedHistoryIds.Contains(card.Id));
        UpdateHistorySelectionDependentButtons();
    }


    private async void DeleteSelectedHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory || _selectedHistoryIds.Count == 0)
        {
            return;
        }

        var idsToDelete = _selectedHistoryIds.ToArray();
        var count = idsToDelete.Length;
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = count == 1 ? "Delete 1 selected dictation?" : $"Delete {count} selected dictations?",
            Content = "This permanently removes the selected transcripts from local history. Settings, dictionary entries, and snippets are not affected.",
            PrimaryButtonText = count == 1 ? "Delete dictation" : $"Delete {count} dictations",
            PrimaryButtonStyle = Application.Current.Resources["BrandDestructiveButtonStyle"] as Style,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isBulkDeletingHistory = true;
        if (BulkDeleteHistoryProgress is not null)
        {
            BulkDeleteHistoryProgress.IsActive = true;
            BulkDeleteHistoryProgress.Visibility = Visibility.Visible;
        }
        UpdateHistorySelectionDependentButtons();

        try
        {
            if (_audioPlayback.CurrentState.ActiveEntryId is { } activeId && idsToDelete.Contains(activeId))
            {
                _audioPlayback.Stop(activeId);
            }

            var result = await _session.History.DeleteEntriesAsync(idsToDelete).ConfigureAwait(true);
            if (result.Closing || _session.Closing)
            {
                return;
            }

            if (result.Succeeded)
            {
                foreach (var id in idsToDelete)
                {
                    CleanupEntryUiState(id);
                }

                _lastBulkDeletionSummary = count == 1 ? "1 dictation deleted." : $"{count} dictations deleted.";
                await RefreshHistoryAsync(showLoadingState: false).ConfigureAwait(true);
            }
            else
            {
                ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
            }
        }
        finally
        {
            _isBulkDeletingHistory = false;
            if (BulkDeleteHistoryProgress is not null)
            {
                BulkDeleteHistoryProgress.IsActive = false;
                BulkDeleteHistoryProgress.Visibility = Visibility.Collapsed;
            }
            UpdateHistorySelectionDependentButtons();
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Delete all dictation history?",
            Content = "This permanently removes every locally saved transcript. Settings, dictionary entries, and snippets are not affected.",
            PrimaryButtonText = "Delete all",
            PrimaryButtonStyle = Application.Current.Resources["BrandDestructiveButtonStyle"] as Style,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _audioPlayback.Stop();
        var result = await _session.History.ClearEntriesAsync().ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            CancelWaveformLoads();
            _expandedTranscriptIds.Clear();
            _showingEditsIds.Clear();
            _editsViewModes.Clear();
            _copyPrimaryResetVersions.Clear();
            _cardInlineNotices.Clear();
            _inlineNoticeVersions.Clear();
            _waveformCache.Clear();
            _waveformLoadInFlight.Clear();
            _activeHistoryCardId = null;
            _lastPasteRequestedEntryId = null;
            SetHistorySelectionMode(false);
            UpdateHistoryFilterSegmentButtons();
            await RefreshHistoryAsync(showLoadingState: false).ConfigureAwait(true);
        }
        else
        {
            ShowMessage("History could not be cleared", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private Task ReloadHistoryAsync() => RefreshHistoryAsync(showLoadingState: _renderedHistoryItems.Count == 0);

    private async Task RefreshHistoryAsync(bool showLoadingState)
    {
        if (_shuttingDownProductWindows || _session.Closing)
        {
            return;
        }

        CancelActiveHistoryQuery();
        var cts = new CancellationTokenSource();
        _historyQueryCts = cts;
        var version = Interlocked.Increment(ref _historyQueryVersion);

        if (showLoadingState)
        {
            BeginHistoryReload();
        }

        var query = HistorySearchBox?.Text.Trim() ?? string.Empty;
        var localNow = DateTimeOffset.Now;

        HistoryPageView pageView;
        Dictionary<Guid, string?> resolvedPaths;
        IReadOnlyList<Guid>? existingIdsForSelection = null;
        try
        {
            pageView = await _session.History
                .QueryPageAsync(
                    query,
                    _historyKindFilter,
                    _historyDateRangeFilter,
                    _historyVisibleLimit,
                    localNow,
                    TimeZoneInfo.Local,
                    cts.Token)
                .ConfigureAwait(true);

            if (pageView.Summary == HistorySummary.Closing
                || _session.Closing
                || _shuttingDownProductWindows
                || cts.IsCancellationRequested
                || version != _historyQueryVersion)
            {
                return;
            }

            if (_selectedHistoryIds.Count > 0)
            {
                existingIdsForSelection = await _session.History
                    .QueryMatchingIdsAsync(
                        string.Empty,
                        HistoryKindFilter.All,
                        HistoryDateRangeFilter.AnyTime,
                        localNow,
                        TimeZoneInfo.Local,
                        cts.Token)
                    .ConfigureAwait(true);
                if (_session.Closing
                    || _shuttingDownProductWindows
                    || cts.IsCancellationRequested
                    || version != _historyQueryVersion)
                {
                    return;
                }
            }

            var pageEntries = pageView.Entries;
            resolvedPaths = await Task.Run(() => ResolveRecordingPathsForPage(pageEntries), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested || _session.Closing || _shuttingDownProductWindows)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_historyQueryCts, cts))
            {
                _historyQueryCts = null;
            }

            cts.Dispose();
        }

        if (_session.Closing
            || _shuttingDownProductWindows
            || version != _historyQueryVersion)
        {
            return;
        }

        ApplyHistoryPageView(pageView, query, localNow, resolvedPaths, existingIdsForSelection);
    }

    private void CancelActiveHistoryQuery()
    {
        Interlocked.Increment(ref _historyQueryVersion);
        var active = _historyQueryCts;
        _historyQueryCts = null;
        if (active is null)
        {
            return;
        }

        try
        {
            active.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelWaveformLoads()
    {
        Interlocked.Increment(ref _waveformLoadGeneration);
        var active = _waveformLoadCts;
        _waveformLoadCts = null;
        if (active is null)
        {
            return;
        }

        try
        {
            active.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            active.Dispose();
        }
    }

    private Dictionary<Guid, string?> ResolveRecordingPathsForPage(IReadOnlyList<DictationHistoryEntry> entries)
    {
        var paths = new Dictionary<Guid, string?>(entries.Count);
        foreach (var entry in entries)
        {
            paths[entry.Id] = ResolveRecordingPath(entry);
        }

        return paths;
    }

    /// <summary>The loading transition every initial reload goes through.</summary>
    private void BeginHistoryReload()
    {
        _isHistoryLoading = true;
        UpdateHistoryListVisibility(HistorySearchBox?.Text.Trim() ?? string.Empty, itemCount: 0);
    }

    private void ApplyHistoryPageView(
        HistoryPageView view,
        string query,
        DateTimeOffset localNow,
        IReadOnlyDictionary<Guid, string?> resolvedRecordingPaths,
        IReadOnlyList<Guid>? existingIdsForSelection = null)
    {
        if (view.Summary == HistorySummary.Closing || _session.Closing)
        {
            return;
        }

        _historyLoadStatus = view.Status;
        _historyTotalStoredCount = view.TotalStoredCount;
        _historyMatchingCount = view.TotalMatchingCount;
        _historyHasMore = view.HasMore;
        _historyRemainingCount = view.RemainingCount;

        if (_selectedHistoryIds.Count > 0 && existingIdsForSelection is not null)
        {
            var existingSet = new HashSet<Guid>(existingIdsForSelection);
            if (_selectedHistoryIds.RemoveWhere(id => !existingSet.Contains(id)) > 0)
            {
                _allMatchingSelected = false;
            }
        }

        if (_historyTotalStoredCount == 0
            && _audioPlayback.CurrentState.ActiveEntryId is { } activePlaybackId)
        {
            _audioPlayback.Stop(activePlaybackId);
        }

        _isHistoryLoading = false;
        RenderHistoryPage(view.Entries, view.DayCounts, query, localNow, resolvedRecordingPaths);
    }

    private HistoryKindFilter _historyKindFilter = HistoryKindFilter.Dictation;
    private HistoryDateRangeFilter _historyDateRangeFilter = HistoryDateRangeFilter.AnyTime;
    private bool _isHistorySelectionMode;
    private bool _allMatchingSelected;
    private bool _isBulkDeletingHistory;
    private int _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;
    private int _historyTotalStoredCount;
    private int _historyMatchingCount;
    private bool _historyHasMore;
    private int _historyRemainingCount;
    private int _lastHelpRetentionDays = int.MinValue;
    private long _historyQueryVersion;
    private long _waveformLoadGeneration;
    private CancellationTokenSource? _historyQueryCts;
    private CancellationTokenSource? _waveformLoadCts;
    private Guid? _repolishingHistoryEntryId;
    private Guid? _activeHistoryCardId;
    private Guid? _lastPasteRequestedEntryId;
    private Guid? _activePlaybackCardId;
    private int _recoveryCopyResetVersion;
    private string? _lastBulkDeletionSummary;
    private HistoryPlaybackSnapshot _lastPlaybackSnapshot = new(null, false, 0, 0);
    private readonly DispatcherTimer _historySearchDebounce = new();
    private readonly HashSet<Guid> _selectedHistoryIds = [];
    private readonly HashSet<Guid> _expandedTranscriptIds = [];
    private readonly HashSet<Guid> _showingEditsIds = [];
    private readonly Dictionary<Guid, HistoryEditsViewMode> _editsViewModes = [];
    private readonly HistoryDiffCache _historyDiffCache = new();
    private readonly Dictionary<Guid, int> _copyPrimaryResetVersions = [];
    private readonly Dictionary<Guid, string> _cardInlineNotices = [];
    private readonly Dictionary<Guid, int> _inlineNoticeVersions = [];
    private readonly Dictionary<Guid, (string FileName, IReadOnlyList<float> Peaks, long DurationMs)> _waveformCache = [];
    private readonly HashSet<Guid> _waveformLoadInFlight = [];
    private readonly ObservableCollection<object> _historyRows = [];
    private readonly Dictionary<Guid, HistoryItemViewModel> _renderedItemsById = [];
    private List<HistoryItemViewModel> _renderedHistoryItems = [];

    private void HistoryFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            return;
        }

        _historySearchDebounce.Stop();
        _allMatchingSelected = false;
        _historyKindFilter = tag switch
        {
            "TranscribeFile" => HistoryKindFilter.TranscribeFile,
            _ => HistoryKindFilter.Dictation,
        };
        _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;

        UpdateHistoryFilterSegmentButtons();
        _ = RefreshHistoryAsync(showLoadingState: false);
    }

    private void UpdateHistoryFilterSegmentButtons()
    {
        var activeTab = Application.Current.Resources["BrandTabStripActiveButtonStyle"] as Style;
        var normalTab = Application.Current.Resources["BrandTabStripButtonStyle"] as Style;
        if (activeTab is not null && normalTab is not null)
        {
            HistoryFilterDictationButton.Style = _historyKindFilter == HistoryKindFilter.Dictation ? activeTab : normalTab;
            HistoryFilterTranscribeButton.Style = _historyKindFilter == HistoryKindFilter.TranscribeFile ? activeTab : normalTab;
        }

        var activeQuiet = Application.Current.Resources["BrandQuietActiveButtonStyle"] as Style;
        var normalQuiet = Application.Current.Resources["BrandQuietButtonStyle"] as Style;
        if (activeQuiet is not null && normalQuiet is not null)
        {
            if (HistoryDateFilterButton is not null)
            {
                HistoryDateFilterButton.Style = _historyDateRangeFilter != HistoryDateRangeFilter.AnyTime ? activeQuiet : normalQuiet;
            }
            if (HistorySelectToggleButton is not null)
            {
                HistorySelectToggleButton.Style = _isHistorySelectionMode ? activeQuiet : normalQuiet;
            }
        }
    }

    private void RenderHistoryPage(
        IReadOnlyList<DictationHistoryEntry> pageEntries,
        IReadOnlyDictionary<DateOnly, int> fullDayCounts,
        string query,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, string?> resolvedRecordingPaths)
    {
        var dayGroups = HistoryDiaryPresentation.GroupByDay(pageEntries, now);
        var renderedCards = new List<HistoryItemViewModel>(pageEntries.Count);
        var nextRenderedById = new Dictionary<Guid, HistoryItemViewModel>(pageEntries.Count);
        var nextRows = new List<object>(pageEntries.Count + dayGroups.Count);
        var missingWaveforms = new List<(Guid EntryId, string FileName, string Path)>();
        var busy = _isRepolishing || _isBulkDeletingHistory;
        var polishReadiness = GetCurrentPolishReadiness();

        foreach (var group in dayGroups)
        {
            var totalDayCount = fullDayCounts.TryGetValue(group.Header.Day, out var dayCount)
                ? dayCount
                : group.Entries.Count;
            nextRows.Add(new HistoryDayHeaderViewModel(group.Header, totalDayCount));

            foreach (var entry in group.Entries)
            {
                var recordingPath = resolvedRecordingPaths.GetValueOrDefault(entry.Id);
                var hasRecording = recordingPath is not null;
                IReadOnlyList<float>? cachedPeaks = null;
                var durationMs = entry.AudioDurationMs ?? 0;

                if (hasRecording && entry.AudioFileName is { } fileName)
                {
                    if (_waveformCache.TryGetValue(entry.Id, out var cached)
                        && string.Equals(cached.FileName, fileName, StringComparison.Ordinal))
                    {
                        cachedPeaks = cached.Peaks;
                        if (cached.DurationMs > 0)
                        {
                            durationMs = cached.DurationMs;
                        }
                    }
                    else if (_waveformLoadInFlight.Add(entry.Id))
                    {
                        missingWaveforms.Add((entry.Id, fileName, recordingPath!));
                    }
                }

                var isShowingEdits = _showingEditsIds.Contains(entry.Id);
                var diffResult = isShowingEdits && HistoryDiaryPresentation.HasPolishEdits(entry)
                    ? _historyDiffCache.GetOrCompute(entry.OriginalText, entry.Text)
                    : HistoryDiffResult.Empty;
                var editsViewMode = _editsViewModes.GetValueOrDefault(entry.Id, HistoryEditsViewMode.MarkedUp);
                var isSelected = _selectedHistoryIds.Contains(entry.Id);
                var isExpanded = _expandedTranscriptIds.Contains(entry.Id);
                var isCopied = _copyPrimaryResetVersions.ContainsKey(entry.Id);
                var inlineNotice = _cardInlineNotices.GetValueOrDefault(entry.Id);

                HistoryItemViewModel vm;
                if (_renderedItemsById.TryGetValue(entry.Id, out var existing)
                    && Equals(existing.Entry, entry)
                    && existing.HasAudioRecording == hasRecording)
                {
                    vm = existing;
                    vm.SetSelectionState(_isHistorySelectionMode, isSelected);
                    vm.SetTranscriptExpanded(isExpanded);
                    vm.SetShowingEdits(isShowingEdits, editsViewMode, diffResult);
                    vm.SetOperationState(busy, _repolishingHistoryEntryId == entry.Id, isPrimaryPasteTarget: false);
                    vm.SetPolishReadiness(polishReadiness);
                    if (cachedPeaks is not null && !ReferenceEquals(existing.WaveformPeaks, cachedPeaks))
                    {
                        vm.SetWaveformData(cachedPeaks, durationMs);
                    }
                    vm.SetPlaybackSnapshot(_lastPlaybackSnapshot);
                    vm.SetCopiedPrimary(isCopied);
                    vm.SetInlineNotice(inlineNotice);
                }
                else
                {
                    vm = new HistoryItemViewModel(
                        entry,
                        isSelectionMode: _isHistorySelectionMode,
                        isSelected: isSelected,
                        isTranscriptExpanded: isExpanded,
                        isShowingEdits: isShowingEdits,
                        editsViewMode: editsViewMode,
                        diffResult: diffResult,
                        isGlobalBusy: busy,
                        isRepolishingThisCard: _repolishingHistoryEntryId == entry.Id,
                        isPrimaryPasteTarget: false,
                        polishReadiness: polishReadiness,
                        hasAudioRecording: hasRecording,
                        audioDurationMs: durationMs,
                        waveformPeaks: cachedPeaks,
                        playbackSnapshot: _lastPlaybackSnapshot,
                        isCopiedPrimary: isCopied,
                        inlineNotice: inlineNotice);
                }

                renderedCards.Add(vm);
                nextRenderedById[vm.Id] = vm;
                nextRows.Add(vm);
            }
        }

        _renderedHistoryItems = renderedCards;
        _renderedItemsById.Clear();
        foreach (var kv in nextRenderedById)
        {
            _renderedItemsById[kv.Key] = kv.Value;
        }

        ReconcileHistoryRows(nextRows);
        if (HistoryCountText is not null)
        {
            HistoryCountText.Text = HistoryDiaryPresentation.FormatItemCount(_historyMatchingCount);
        }
        if (HistoryLoadMoreContainer is not null && LoadMoreHistoryButton is not null)
        {
            HistoryLoadMoreContainer.Visibility = _historyHasMore ? Visibility.Visible : Visibility.Collapsed;
            if (_historyHasMore)
            {
                LoadMoreHistoryButton.Content = $"Show older dictations ({_historyRemainingCount} more)";
            }
        }
        if (ResetHistoryFiltersBarButton is not null)
        {
            var hasAnyFilter = !string.IsNullOrWhiteSpace(query)
                || _historyKindFilter != HistoryKindFilter.Dictation
                || _historyDateRangeFilter != HistoryDateRangeFilter.AnyTime;
            ResetHistoryFiltersBarButton.Visibility = hasAnyFilter ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateHistoryListVisibility(query, renderedCards.Count);
        UpdateHistorySelectionDependentButtons();
        UpdateHistoryStatusCards();
        ScheduleMissingWaveformLoads(missingWaveforms);
    }

    private void ReconcileHistoryRows(List<object> nextRows)
    {
        var common = Math.Min(_historyRows.Count, nextRows.Count);
        for (var i = 0; i < common; i++)
        {
            var current = _historyRows[i];
            var next = nextRows[i];
            if (ReferenceEquals(current, next))
            {
                continue;
            }

            if (current is HistoryDayHeaderViewModel currentHeader
                && next is HistoryDayHeaderViewModel nextHeader
                && string.Equals(currentHeader.AccessibilityLabel, nextHeader.AccessibilityLabel, StringComparison.Ordinal))
            {
                continue;
            }

            _historyRows[i] = next;
        }

        while (_historyRows.Count > nextRows.Count)
        {
            _historyRows.RemoveAt(_historyRows.Count - 1);
        }

        for (var i = _historyRows.Count; i < nextRows.Count; i++)
        {
            _historyRows.Add(nextRows[i]);
        }
    }

}
