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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace EnviousWispr.App;

/// <summary>Dictation history diary, search, filtering, re-polish, playback, and recovery card UI.</summary>
public sealed partial class MainWindow
{
    private static HistoryItemViewModel? ResolveCardDataContext(object sender) =>
        sender is FrameworkElement element
            ? element.DataContext as HistoryItemViewModel ?? element.Tag as HistoryItemViewModel
            : null;

    private void CardBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetCardHoverOutline(sender, isHovered: true);
    }

    private void CardBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetCardHoverOutline(sender, isHovered: false);
    }

    private void CardBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        SetCardHoverOutline(sender, isHovered: false);
    }

    private static void SetCardHoverOutline(object sender, bool isHovered)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var outline = (element.Parent as FrameworkElement)?.FindName("CardHoverOutline") as UIElement
            ?? element.FindName("CardHoverOutline") as UIElement;
        if (outline is not null)
        {
            outline.Opacity = isHovered ? 0.45 : 0.0;
        }
    }

    private void CardShowEditsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        if (_showingEditsIds.Remove(card.Id))
        {
            card.SetShowingEdits(false);
            return;
        }

        _showingEditsIds.Add(card.Id);
        _editsViewModes[card.Id] = HistoryEditsViewMode.MarkedUp;
        var diffResult = card.HasPolishEdits
            ? _historyDiffCache.GetOrCompute(card.OriginalText, card.Text)
            : HistoryDiffResult.Empty;
        card.SetShowingEdits(true, HistoryEditsViewMode.MarkedUp, diffResult);
    }

    private void CardShowPolished_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        _editsViewModes[card.Id] = HistoryEditsViewMode.Polished;
        card.SetEditsViewMode(HistoryEditsViewMode.Polished);
    }

    private void CardShowMarkedUp_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        _editsViewModes[card.Id] = HistoryEditsViewMode.MarkedUp;
        card.SetEditsViewMode(HistoryEditsViewMode.MarkedUp);
    }

    private void CardShowOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        _editsViewModes[card.Id] = HistoryEditsViewMode.Original;
        card.SetEditsViewMode(HistoryEditsViewMode.Original);
    }

    private void CardShowMoreToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        if (!_expandedTranscriptIds.Remove(card.Id))
        {
            _expandedTranscriptIds.Add(card.Id);
        }

        card.SetTranscriptExpanded(_expandedTranscriptIds.Contains(card.Id));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "WinUI XAML generated event connector attaches this handler via an instance reference.")]
    private void CardTranscriptText_IsTextTrimmedChanged(RichTextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (sender.IsTextTrimmed && sender.DataContext is HistoryItemViewModel card)
        {
            card.MarkHasOverflow();
        }
    }

    private void CardPasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        _activeHistoryCardId = card.Id;
        _lastPasteRequestedEntryId = card.Id;
        ClearCardInlineNotice(card.Id);
        UpdateHistorySelectionDependentButtons();
        HistoryPasteRequested?.Invoke(card.Id);
    }

    private void CardCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(card.DisplayedCopyText);
        Clipboard.SetContent(package);
        Clipboard.Flush();

        _activeHistoryCardId = card.Id;
        var version = _copyPrimaryResetVersions.TryGetValue(card.Id, out var existing)
            ? existing + 1
            : 1;
        _copyPrimaryResetVersions[card.Id] = version;
        card.SetCopiedPrimary(true);
        _ = ResetPrimaryCopyBadgeAfterDelayAsync(card.Id, version);
    }

    private void CardCopyOriginalMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || ResolveCardDataContext(sender) is not { } card
            || string.IsNullOrWhiteSpace(card.OriginalText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(card.OriginalText);
        Clipboard.SetContent(package);
        Clipboard.Flush();

        _activeHistoryCardId = card.Id;
        SetCardInlineNotice(card.Id, "Original words copied to clipboard.");
    }

    private async void CardSaveAsTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || ResolveCardDataContext(sender) is not { } card)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = "Dictation",
            DefaultFileExtension = ".txt",
        };
        picker.FileTypeChoices.Add("Text", [".txt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(file.Path, card.DisplayedCopyText).ConfigureAwait(true);
            SetCardInlineNotice(card.Id, "Saved transcript to text file.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Not saved", "The transcript could not be written there. Choose another place.", InfoBarSeverity.Error);
        }
    }

    private void CardPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBulkDeletingHistory
            || _dictationActive
            || IsSpeechEngineBusy?.Invoke() == true
            || sender is not FrameworkElement element
            || element.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        var path = ResolveRecordingPath(card.Entry);
        if (path is null)
        {
            card.MarkRecordingMissing();
            SetCardInlineNotice(card.Id, "Recording is no longer available on this PC.");
            return;
        }

        _activeHistoryCardId = card.Id;
        ClearCardInlineNotice(card.Id);
        _audioPlayback.TogglePlayPause(card.Id, path, card.AudioDurationMs);
    }

    private void CardWaveform_SeekRequested(HistoryWaveformScrubber sender, double fraction)
    {
        if (_isBulkDeletingHistory
            || _dictationActive
            || IsSpeechEngineBusy?.Invoke() == true
            || sender.DataContext is not HistoryItemViewModel card
            || !card.CanTogglePlayback)
        {
            return;
        }

        var path = ResolveRecordingPath(card.Entry);
        if (path is null)
        {
            card.MarkRecordingMissing();
            SetCardInlineNotice(card.Id, "Recording is no longer available on this PC.");
            return;
        }

        _activeHistoryCardId = card.Id;
        ClearCardInlineNotice(card.Id);
        _audioPlayback.SeekFraction(card.Id, path, fraction, card.AudioDurationMs);
    }

    private void OnAudioPlaybackStateChanged(HistoryPlaybackSnapshot snapshot)
    {
        var previousId = _activePlaybackCardId;
        var nextId = snapshot.ActiveEntryId;
        _lastPlaybackSnapshot = snapshot;
        _activePlaybackCardId = nextId;

        if (previousId is { } prev && prev != nextId && _renderedItemsById.TryGetValue(prev, out var prevCard))
        {
            prevCard.SetPlaybackSnapshot(snapshot);
        }

        if (nextId is { } next && _renderedItemsById.TryGetValue(next, out var nextCard))
        {
            nextCard.SetPlaybackSnapshot(snapshot);
        }
    }

    private void OnAudioPlaybackFailed(Guid entryId, string message) => SetCardInlineNotice(entryId, message);

    private async Task ResetPrimaryCopyBadgeAfterDelayAsync(Guid entryId, int expectedVersion)
    {
        await Task.Delay(2000).ConfigureAwait(true);
        if (_shuttingDownProductWindows
            || !_copyPrimaryResetVersions.TryGetValue(entryId, out var current)
            || current != expectedVersion)
        {
            return;
        }

        _copyPrimaryResetVersions.Remove(entryId);
        if (_renderedItemsById.TryGetValue(entryId, out var card))
        {
            card.SetCopiedPrimary(false);
        }
    }

    private async void CardRepolishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory)
        {
            return;
        }

        if (ResolveCardDataContext(sender) is not { } selected)
        {
            return;
        }

        if (selected.IsTemporary)
        {
            SetCardInlineNotice(selected.Id, "Choose Keep first before re-polishing an Escape Recovery dictation.");
            return;
        }

        var readiness = GetCurrentPolishReadiness();
        if (!readiness.IsEnabled || !readiness.IsReady)
        {
            SetCardInlineNotice(
                selected.Id,
                readiness.IsEnabled
                    ? $"AI Polish is not ready ({readiness.StatusText}). Finish setup in AI Polish settings."
                    : "AI Polish is off. Enable an AI Polish provider in settings first.");
            UpdateHistoryPolishState();
            return;
        }

        _activeHistoryCardId = selected.Id;
        ClearCardInlineNotice(selected.Id);
        _isRepolishing = true;
        _repolishingHistoryEntryId = selected.Id;
        UpdateHistorySelectionDependentButtons();

        try
        {
            var result = await _session.History.RepolishEntryAsync(selected.Id).ConfigureAwait(true);
            if (result.Status == HistoryRepolishStatus.Closing || _session.Closing)
            {
                return;
            }

            if (result.Succeeded)
            {
                _editsViewModes.Remove(selected.Id);
                await RefreshHistoryAsync(showLoadingState: false).ConfigureAwait(true);
            }
            else
            {
                var message = result.Status switch
                {
                    HistoryRepolishStatus.NoProviderConfigured => "Enable an AI polish provider in settings first.",
                    HistoryRepolishStatus.EscapeRecoveryEntryMustBeKept => "Choose Keep before re-polishing this entry.",
                    HistoryRepolishStatus.PolishRefused => "The active AI polish provider returned no updated transcript.",
                    HistoryRepolishStatus.EntryNotFound => "This entry may have already been removed.",
                    HistoryRepolishStatus.EntryExpired => "This entry is past retention.",
                    _ => "The AI polish service encountered an error.",
                };
                SetCardInlineNotice(selected.Id, message);
            }
        }
        finally
        {
            _isRepolishing = false;
            _repolishingHistoryEntryId = null;
            UpdateHistorySelectionDependentButtons();
        }
    }

    private async void CardDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || ResolveCardDataContext(sender) is not { } selected)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            RequestedTheme = WindowRoot.ActualTheme,
            Title = "Delete dictation?",
            Content = "This permanently removes this dictation and its saved audio recording from local history.",
            PrimaryButtonText = "Delete dictation",
            PrimaryButtonStyle = Application.Current.Resources["BrandDestructiveButtonStyle"] as Style,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _audioPlayback.Stop(selected.Id);
        var result = await _session.History.DeleteEntryAsync(selected.Id).ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            CleanupEntryUiState(selected.Id);
            await RefreshHistoryAsync(showLoadingState: false).ConfigureAwait(true);
        }
        else
        {
            ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async void CardKeepButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || sender is not FrameworkElement element
            || element.DataContext is not HistoryItemViewModel selected)
        {
            return;
        }

        var result = await _session.History.KeepEntryAsync(selected.Id).ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            await RefreshHistoryAsync(showLoadingState: false).ConfigureAwait(true);
            ShowMessage("History entry kept", "Its 24-hour Escape Recovery expiry was removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }


    private void RefreshCardSelectionVisuals()
    {
        foreach (var item in _renderedHistoryItems)
        {
            item.SetSelectionState(_isHistorySelectionMode, _selectedHistoryIds.Contains(item.Id));
        }
    }

    private void UpdateHistorySelectionDependentButtons()
    {
        if (HistoryList is null || ClearHistoryButton is null)
        {
            return;
        }

        var busy = _isRepolishing || _isBulkDeletingHistory;
        var activePasteId = _activeHistoryCardId is { } tracked
            && _renderedItemsById.ContainsKey(tracked)
            ? tracked
            : _renderedHistoryItems.FirstOrDefault()?.Id;
        foreach (var item in _renderedHistoryItems)
        {
            item.SetOperationState(busy, _repolishingHistoryEntryId == item.Id, item.Id == activePasteId);
        }

        ClearHistoryButton.IsEnabled = _historyTotalStoredCount > 0 && !busy;
        if (HistoryMoreDeleteAllItem is not null)
        {
            HistoryMoreDeleteAllItem.IsEnabled = _historyTotalStoredCount > 0 && !busy;
        }
        if (DeleteSelectedHistoryButton is not null)
        {
            DeleteSelectedHistoryButton.IsEnabled = _selectedHistoryIds.Count > 0 && !busy;
        }
        if (HistorySelectAllButton is not null)
        {
            var allSelected = _allMatchingSelected
                || (_historyMatchingCount > 0
                    && _renderedHistoryItems.Count == _historyMatchingCount
                    && _renderedHistoryItems.All(item => _selectedHistoryIds.Contains(item.Id)));
            HistorySelectAllButton.IsEnabled = _historyMatchingCount > 0 && !busy;
            HistorySelectAllButton.Content = allSelected ? "Deselect all matching" : "Select all matching";
        }
        if (HistorySelectionCountText is not null)
        {
            HistorySelectionCountText.Text = HistoryDiaryPresentation.FormatSelectionCount(_selectedHistoryIds.Count);
        }
        if (HistorySelectionDetailText is not null)
        {
            HistorySelectionDetailText.Text = !string.IsNullOrWhiteSpace(_lastBulkDeletionSummary)
                ? _lastBulkDeletionSummary
                : "Tick cards below, or use Select all matching.";
        }
    }

    internal HistoryPolishReadiness GetCurrentPolishReadiness()
    {
        if (_settings is null)
        {
            return HistoryPlaybackPresentation.DescribePolishReadiness(
                PolishProvider.None,
                modelId: null,
                isEgOneReady: false,
                isOllamaEndpointValid: false,
                hasCloudCredential: false,
                isCloudKeyRejected: false);
        }

        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var modelId = provider == PolishProvider.EgOne
            ? ResolveSelectedEgOneModelId()
            : (!string.IsNullOrWhiteSpace(_selectedPolishModelId)
                ? _selectedPolishModelId
                : _settings.Preferences.Polish.ModelId);
        var normalizedEgId = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        var isEgOneReady = provider == PolishProvider.EgOne
            && _egOneServerExecutableExists
            && _egOneModelStates.TryGetValue(normalizedEgId, out var egState)
            && egState.IsInstalled;
        var ollamaEndpoint = OllamaEndpointTextBox is not null && !string.IsNullOrWhiteSpace(OllamaEndpointTextBox.Text)
            ? OllamaEndpointTextBox.Text
            : _settings.Preferences.Polish.OllamaEndpoint;
        var isOllamaEndpointValid = provider == PolishProvider.Ollama
            && OllamaEndpointPolicy.TryNormalize(ollamaEndpoint, out _);
        var hasCloudCredential = IsCloudProvider(provider)
            && _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found;
        var isCloudKeyRejected = IsCloudProvider(provider)
            && _cloudKeyRejectedProvider == provider;

        return HistoryPlaybackPresentation.DescribePolishReadiness(
            provider,
            modelId,
            isEgOneReady,
            isOllamaEndpointValid,
            hasCloudCredential,
            isCloudKeyRejected);
    }

    private void UpdateHistoryPolishState()
    {
        UpdateHistoryStatusCards();
        var readiness = GetCurrentPolishReadiness();
        foreach (var item in _renderedHistoryItems)
        {
            item.SetPolishReadiness(readiness);
        }
    }

    private void UpdateHistoryStatusCards()
    {
        if (HistoryEngineStatusText is not null && _settings is not null)
        {
            HistoryEngineStatusText.Text = EnginePolishPresentation
                .DescribeEngine(_settings.Preferences.Dictation.FinalEngine)
                .Name;
        }
        if (HistoryPolishStatusText is not null)
        {
            HistoryPolishStatusText.Text = GetCurrentPolishReadiness().StatusText;
        }
        if (HistoryDetailReadyHotkeyText is not null && _settings is not null)
        {
            HistoryDetailReadyHotkeyText.Text = $"Keybind: {_settings.Preferences.Dictation.PushToTalkGesture}";
        }
    }

    private void UpdateHistoryListVisibility(string query, int itemCount)
    {
        var hasItems = itemCount > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var hasFilters = hasQuery
            || _historyKindFilter != HistoryKindFilter.Dictation
            || _historyDateRangeFilter != HistoryDateRangeFilter.AnyTime;
        var showFilteredEmpty = !hasItems && (_historyTotalStoredCount > 0 && hasFilters || hasQuery);
        var historyUnavailable = _historyLoadStatus is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable;
        ShowCard(HistoryLoadingState, _isHistoryLoading);
        ShowCard(HistoryList, !_isHistoryLoading && hasItems);
        ShowCard(HistoryEmptyState, !_isHistoryLoading && !hasItems && !showFilteredEmpty && !historyUnavailable);
        ShowCard(HistorySearchEmptyState, !_isHistoryLoading && showFilteredEmpty && !historyUnavailable);

        ShowCard(HistoryUnavailableState, !_isHistoryLoading && !hasItems && historyUnavailable);

        if (!_isHistoryLoading && showFilteredEmpty && !historyUnavailable)
        {
            if (HistorySearchEmptyDescription is not null)
            {
                HistorySearchEmptyDescription.Text = hasQuery
                    ? $"No saved dictations match “{query}” under the active filters."
                    : "No saved dictations match the active tab or date filter.";
            }
            if (HistoryEmptyClearSearchButton is not null)
            {
                HistoryEmptyClearSearchButton.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        if (!hasItems && historyUnavailable)
        {
            HistoryUnavailableDescription.Text = _historyLoadStatus == HistoryLoadStatus.Invalid
                ? "The local history file is invalid. It was left untouched for recovery."
                : "Windows could not open the private history file. It was left untouched.";
        }

        var normalisedQuery = query.Trim().ToLowerInvariant();
        var filterKey = $"{(int)_historyKindFilter}:{(int)_historyDateRangeFilter}";
        var terminal = _isHistoryLoading
            ? null
            : historyUnavailable ? "unavailable"
            : hasItems ? $"loaded:{itemCount}:{normalisedQuery}:{filterKey}"
            : showFilteredEmpty ? $"no-matches:{normalisedQuery}:{filterKey}"
            : "empty";

        _pendingHistoryAnnouncement = terminal;
        if (hasQuery && terminal is not null)
        {
            _historyAnnounceDebounce.Stop();
            _historyAnnounceDebounce.Start();
            return;
        }

        _historyAnnounceDebounce.Stop();
        AnnounceHistoryStateIfChanged(terminal, historyUnavailable, hasItems, showFilteredEmpty, itemCount);
    }

    /// <summary>Announces the history outcome, if it is one nobody has been told yet.</summary>
    private void AnnounceHistoryStateIfChanged(
        string? terminal,
        bool historyUnavailable,
        bool hasItems,
        bool hasQuery,
        int itemCount)
    {
        if (terminal is null)
        {
            _lastAnnouncedHistoryState = null;
        }
        else if (!string.Equals(terminal, _lastAnnouncedHistoryState, StringComparison.Ordinal))
        {
            var announced = historyUnavailable
                ? AnnounceLiveRegion(HistoryUnavailableTitle)
                : hasItems
                    ? AnnounceHistoryCount(itemCount)
                    : hasQuery
                        ? AnnounceLiveRegion(HistorySearchEmptyTitle)
                        : AnnounceLiveRegion(HistoryEmptyTitle);

            if (announced)
            {
                _lastAnnouncedHistoryState = terminal;
            }
        }
    }

    /// <summary>Announces the history outcome when the page becomes visible.</summary>
    private void OnHistoryAnnounceDue(object? sender, object e)
    {
        _historyAnnounceDebounce.Stop();
        AnnounceHistoryOnPageShown();
    }

    /// <summary>Says whatever history result is still waiting, if anything can hear it now.</summary>
    public void AnnouncePendingHistoryState() => AnnounceHistoryOnPageShown();

    private void AnnounceHistoryOnPageShown()
    {
        if (_isHistoryLoading || _pendingHistoryAnnouncement is null || !AppWindow.IsVisible)
        {
            return;
        }

        var hasFilteredEmpty = HistorySearchEmptyState.Visibility == Visibility.Visible;
        AnnounceHistoryStateIfChanged(
            _pendingHistoryAnnouncement,
            _historyLoadStatus is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable,
            HistoryList.Visibility == Visibility.Visible,
            hasFilteredEmpty,
            _renderedHistoryItems.Count);
    }

    /// <summary>The last history outcome a screen reader was told about.</summary>
    private string? _lastAnnouncedHistoryState;

    /// <summary>The outcome waiting to be announced, if the page or the typing is not ready.</summary>
    private string? _pendingHistoryAnnouncement;

    /// <summary>Holds a search result back until the typing stops.</summary>
    private readonly DispatcherTimer _historyAnnounceDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(600),
    };

    /// <summary>Announces how many dictations the list is showing.</summary>
    private bool AnnounceHistoryCount(int itemCount)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HistoryList,
            $"Transcript history, {itemCount} dictation{(itemCount == 1 ? string.Empty : "s")}.");
        return AnnounceLiveRegion(HistoryList);
    }

    private string? ResolveRecordingPath(DictationHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.AudioFileName)
            || string.IsNullOrWhiteSpace(DictationRecordingsDirectory)
            || Path.IsPathRooted(entry.AudioFileName)
            || !string.Equals(Path.GetFileName(entry.AudioFileName), entry.AudioFileName, StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetFullPath(DictationRecordingsDirectory);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, entry.AudioFileName));
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
            ? full
            : null;
    }

    private void ScheduleMissingWaveformLoads(List<(Guid EntryId, string FileName, string Path)> items)
    {
        if (items.Count == 0 || _shuttingDownProductWindows)
        {
            return;
        }

        _waveformLoadCts ??= new CancellationTokenSource();
        var token = _waveformLoadCts.Token;
        var generation = Volatile.Read(ref _waveformLoadGeneration);

        _ = Task.Run(() =>
        {
            foreach (var (entryId, fileName, path) in items)
            {
                if (_shuttingDownProductWindows || token.IsCancellationRequested || generation != Volatile.Read(ref _waveformLoadGeneration))
                {
                    return;
                }

                if (!WaveFile.TryAnalyzeFile(path, HistoryPlaybackPresentation.DefaultWaveformBarCount, out var peaks, out var durationMs))
                {
                    DispatcherQueue?.TryEnqueue(() => _waveformLoadInFlight.Remove(entryId));
                    continue;
                }

                var normalized = HistoryPlaybackPresentation.NormalizeOrFallbackPeaks(
                    peaks,
                    HistoryPlaybackPresentation.DefaultWaveformBarCount);
                DispatcherQueue?.TryEnqueue(() =>
                {
                    _waveformLoadInFlight.Remove(entryId);
                    if (_shuttingDownProductWindows
                        || token.IsCancellationRequested
                        || generation != Volatile.Read(ref _waveformLoadGeneration))
                    {
                        return;
                    }

                    _waveformCache[entryId] = (fileName, normalized, durationMs);
                    if (_renderedItemsById.TryGetValue(entryId, out var card)
                        && string.Equals(card.Entry.AudioFileName, fileName, StringComparison.Ordinal))
                    {
                        card.SetWaveformData(normalized, durationMs);
                    }
                });
            }
        }, token);
    }

    private void SetCardInlineNotice(Guid entryId, string message)
    {
        _cardInlineNotices[entryId] = message;
        var version = _inlineNoticeVersions.TryGetValue(entryId, out var prev) ? prev + 1 : 1;
        _inlineNoticeVersions[entryId] = version;
        if (_renderedItemsById.TryGetValue(entryId, out var card))
        {
            card.SetInlineNotice(message);
        }
        _ = ClearInlineNoticeAfterDelayAsync(entryId, version);
    }

    private void ClearCardInlineNotice(Guid entryId)
    {
        _cardInlineNotices.Remove(entryId);
        _inlineNoticeVersions.Remove(entryId);
        if (_renderedItemsById.TryGetValue(entryId, out var card))
        {
            card.SetInlineNotice(null);
        }
    }

    private async Task ClearInlineNoticeAfterDelayAsync(Guid entryId, int expectedVersion)
    {
        await Task.Delay(5000).ConfigureAwait(true);
        if (_shuttingDownProductWindows
            || !_inlineNoticeVersions.TryGetValue(entryId, out var current)
            || current != expectedVersion)
        {
            return;
        }

        ClearCardInlineNotice(entryId);
    }

    private void CleanupEntryUiState(Guid entryId)
    {
        _selectedHistoryIds.Remove(entryId);
        _expandedTranscriptIds.Remove(entryId);
        _showingEditsIds.Remove(entryId);
        _editsViewModes.Remove(entryId);
        _copyPrimaryResetVersions.Remove(entryId);
        _cardInlineNotices.Remove(entryId);
        _inlineNoticeVersions.Remove(entryId);
        _waveformCache.Remove(entryId);
        _waveformLoadInFlight.Remove(entryId);
        if (_activeHistoryCardId == entryId)
        {
            _activeHistoryCardId = null;
        }
        if (_lastPasteRequestedEntryId == entryId)
        {
            _lastPasteRequestedEntryId = null;
        }
    }
}
