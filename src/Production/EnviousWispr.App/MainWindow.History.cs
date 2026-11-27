using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <param name="undoOffered">
    /// The text is an Escape Recovery whose one-shot Undo still stands: the card speaks macOS's words for it,
    /// the pill's title and the announcement that names both ways back, and shows the Undo button.
    /// </param>
    public void SetRecoveredText(RecoveryTextLoadResult result, bool undoOffered = false)
    {
        if (result.Status == RecoveryTextLoadStatus.Found && result.Record is not null)
        {
            RecoveryTextBox.Text = result.Record.Text;
            RecoveryCard.Visibility = Visibility.Visible;
            if (undoOffered)
            {
                RecoveryTitleText.Text = EscapeRecoveryCardTitle;
                RecoveryBodyText.Text = EscapeRecoveryUndoBody;
                UndoRecoveryButton.Visibility = Visibility.Visible;
                UndoRecoveryButton.IsEnabled = true;
                return;
            }

            ResetRecoveryCardWords();
            SetOnboardingReliabilityNotice(
                "Interrupted dictation recovered",
                "Finish setup, then review or copy the private recovery text on History. It will not be pasted automatically.");
            return;
        }

        ClearRecoveredText();
        if (result.Status == RecoveryTextLoadStatus.Invalid)
        {
            SetOnboardingReliabilityNotice(
                "Recovery data needs attention",
                "The encrypted recovery file is invalid. It was preserved and no text was exposed.");
            ShowMessage(
                "Recovery data needs attention",
                "The encrypted recovery file is invalid. It was preserved and no text was exposed.",
                InfoBarSeverity.Warning);
        }
        else if (result.Status == RecoveryTextLoadStatus.Unavailable)
        {
            SetOnboardingReliabilityNotice(
                "Recovery storage is unavailable",
                "Windows could not open the encrypted recovery file. Dictation remains available.");
            ShowMessage(
                "Recovery storage is unavailable",
                "Windows could not open the encrypted recovery file. Dictation remains available.",
                InfoBarSeverity.Warning);
        }
    }

    /// <summary>Says a dictation was lost, and only when one actually was.</summary>
    /// <remarks>
    /// THIS REPLACES A BANNER THAT ACCUSED THE PRODUCT ON A NUMBER IT COULD NOT JUSTIFY. Home used
    /// to read "EnviousWispr did not close properly last time" plus "That has now happened N times
    /// in a row" whenever the previous run left no clean-exit flag. Nothing in this app can tell a
    /// fault from a closed laptop, a Restart chosen from the Start menu, a log off or Task Manager,
    /// so the tally was not evidence of anything, and on the test machine it reached nineteen with
    /// almost all of it a build script releasing a file lock.
    ///
    /// AND YET SOMETHING HAD TO STAY, WHICH IS THE HALF THAT NEARLY GOT LOST. Recovery text is
    /// written only after transcription finishes, so a stop DURING a dictation leaves nothing to
    /// restore and reads exactly like an idle restart. That is the one case where a person must be
    /// told, because their words are gone and only they can say them again. StartupNoticeDecision
    /// separates the two, and this is only ever raised for the second.
    ///
    /// NO COUNT, AND NO BLAME. It says what happened to their dictation and what they may have to
    /// do, which is the whole of what they can act on.
    /// </remarks>
    public void SetPossiblyLostDictationNotice()
    {
        const string title = "A dictation may not have finished";
        const string message =
            "EnviousWispr stopped while a dictation was in progress, and there was nothing saved to "
            + "restore. You may need to say it again.";
        ShowMessage(title, message, InfoBarSeverity.Warning);
        SetOnboardingReliabilityNotice(title, message);
    }

    public void ClearRecoveredText()
    {
        RecoveryTextBox.Text = string.Empty;
        RecoveryCard.Visibility = Visibility.Collapsed;
        ResetRecoveryCardWords();
    }

    // macOS's words for an Escape Recovery: the pill's title, and its spoken offer after the title.
    private const string EscapeRecoveryCardTitle = "Dictation cancelled";
    private const string EscapeRecoveryUndoBody = "Press Undo to get it back, or find it in History.";

    /// <summary>The card's words for text found after an unfinished run, and no Undo.</summary>
    private void ResetRecoveryCardWords()
    {
        RecoveryTitleText.Text = "Recovered unfinished dictation";
        RecoveryBodyText.Text =
            "EnviousWispr found text that had not reached its destination before the previous run ended. "
            + "Review it here; it will never be pasted automatically.";
        UndoRecoveryButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>What a paste of a saved dictation came to, in this window, and whether Home's Undo still stands.</summary>
    /// <remarks>
    /// ONE-SHOT ON SCREEN AS WELL AS IN THE OWNER. The button goes the moment the offer is spent (used, or its
    /// copy moved on) and is live again only when a refusal left the offer standing. A landed paste says
    /// nothing here: the words arriving in front of the person say it, and Home's copy has already gone.
    /// </remarks>
    public void ShowSavedDictationPasteResult(
        SavedDictationPasteAction action,
        SavedDictationPasteOutcome? outcome,
        SessionHoldAttempt? refusal,
        bool undoStanding,
        bool clipboardNotRestored)
    {
        var undo = action == SavedDictationPasteAction.Undo;
        if (RecoveryCard.Visibility == Visibility.Visible && UndoRecoveryButton.Visibility == Visibility.Visible)
        {
            UndoRecoveryButton.IsEnabled = undoStanding;
            if (!undoStanding)
            {
                UndoRecoveryButton.Visibility = Visibility.Collapsed;
                RecoveryBodyText.Text = "Copy it from here, or find it in History.";
            }
        }

        if (refusal is not null)
        {
            SurfacePasteNotice(
                action,
                "Could not paste right now",
                SavedDictationPasteRefusalSentence(refusal),
                HistoryPasteNoticeTone.Informational);
            return;
        }

        // THE ONE LANDED PASTE THAT SAYS SOMETHING HERE (#242): the paste borrowed the clipboard and could not give
        // it back, so it may now hold these words instead of what the person had copied. A paste that failed with
        // the clipboard in that state says both.
        if (clipboardNotRestored)
        {
            if (outcome == SavedDictationPasteOutcome.Pasted)
            {
                ShowMessage(
                    "Pasted, but your clipboard could not be restored",
                    "It may now hold this dictation instead of what you had copied.",
                    InfoBarSeverity.Warning);
            }
            else
            {
                ShowMessage(
                    "Your dictation could not be pasted",
                    "Nothing was typed, and your clipboard could not be restored. Copy it from History instead.",
                    InfoBarSeverity.Error);
            }

            return;
        }

        switch (outcome)
        {
            case SavedDictationPasteOutcome.KeptOnClipboard:
                SurfacePasteNotice(
                    action,
                    "Dictation placed on clipboard",
                    undo
                        ? "Could not return to the previous app, so your words are on the clipboard instead."
                        : "That app refused the paste, so your words are on the clipboard. Paste where you want them.",
                    HistoryPasteNoticeTone.Warning);
                break;
            case SavedDictationPasteOutcome.MayHavePasted:
                SurfacePasteNotice(
                    action,
                    "Paste could not be confirmed",
                    undo
                        ? "Could not confirm the paste. Check the app before trying again; the copy is still here."
                        : "Could not confirm the paste. Check the app before trying again.",
                    HistoryPasteNoticeTone.Warning);
                break;
            case SavedDictationPasteOutcome.NoLongerAvailable:
                SurfacePasteNotice(
                    action,
                    "Dictation is no longer available",
                    "This dictation was deleted or expired, so nothing was pasted.",
                    HistoryPasteNoticeTone.Informational);
                break;
            case SavedDictationPasteOutcome.DictationInProgress or SavedDictationPasteOutcome.Busy:
                SurfacePasteNotice(
                    action,
                    "Could not paste right now",
                    "EnviousWispr is busy with a dictation. Try again in a moment.",
                    HistoryPasteNoticeTone.Informational);
                break;
            case SavedDictationPasteOutcome.NoTarget:
                SurfacePasteNotice(
                    action,
                    "No active target to paste into",
                    undo
                        ? "The app you were dictating into has closed. Copy the text from here instead."
                        : "Click into the app you want it in, then try Paste again.",
                    HistoryPasteNoticeTone.Warning);
                break;
            case SavedDictationPasteOutcome.Failed:
                SurfacePasteNotice(
                    action,
                    "Dictation could not be pasted",
                    "Could not paste into that app. Copy the text instead.",
                    HistoryPasteNoticeTone.Error);
                break;
        }
    }

    private void SurfacePasteNotice(
        SavedDictationPasteAction action,
        string title,
        string sentence,
        HistoryPasteNoticeTone tone)
    {
        var isTargetCardRendered = _lastPasteRequestedEntryId is { } targetId
            && _renderedItemsById.ContainsKey(targetId);
        var plan = HistoryPlaybackPresentation.PlanSavedDictationPasteNotice(
            isUndoAction: action == SavedDictationPasteAction.Undo,
            isRecoveryCardVisible: RecoveryCard.Visibility == Visibility.Visible,
            lastPasteRequestedEntryId: _lastPasteRequestedEntryId,
            isTargetCardRendered: isTargetCardRendered,
            title: title,
            sentence: sentence,
            tone: tone);

        switch (plan.Target)
        {
            case HistoryPasteNoticeTarget.RecoveryCard:
                RecoveryBodyText.Text = plan.Sentence;
                break;

            case HistoryPasteNoticeTarget.CardInline when plan.TargetEntryId is { } entryId:
                SetCardInlineNotice(entryId, plan.Sentence);
                break;

            default:
                var severity = plan.Tone switch
                {
                    HistoryPasteNoticeTone.Error => InfoBarSeverity.Error,
                    HistoryPasteNoticeTone.Warning => InfoBarSeverity.Warning,
                    _ => InfoBarSeverity.Informational,
                };
                ShowMessage(plan.Title, plan.Sentence, severity);
                break;
        }
    }

    /// <summary>Why a paste could not start, in the words the other refusals use.</summary>
    private static string SavedDictationPasteRefusalSentence(SessionHoldAttempt refusal) => refusal switch
    {
        { Refusal: SessionHoldRefusal.Dictation } => "A dictation is running. Try again when it finishes.",
        { HeldBy: SessionHolder.UpdateApply } => "EnviousWispr is updating. Try again when it finishes.",
        { HeldBy: SessionHolder.LastDictationReuse } => "EnviousWispr is pasting your last dictation. Try again in a moment.",
        { HeldBy: SessionHolder.FileTranscription } => "A file is being transcribed. Try again when it finishes.",
        { HeldBy: SessionHolder.DataDeletion } => "EnviousWispr is deleting your data and will close.",
        { HeldBy: SessionHolder.GraphicsRuntimeSwitch } => "EnviousWispr is moving dictation to your graphics card. Try again in a moment.",
        { HeldBy: SessionHolder.SpeechEngineSwitch } => "EnviousWispr is updating its speech engine. Try again in a moment.",
        { HeldBy: SessionHolder.SavedDictationPaste } => "EnviousWispr is pasting your dictation. Try again in a moment.",
        _ => "EnviousWispr is closing.",
    };

    private void UndoRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        // ONE PRESS: greyed at once, so a double click asks once. The owner's exchange is the guarantee;
        // this is the picture of it.
        UndoRecoveryButton.IsEnabled = false;
        RecoveryUndoRequested?.Invoke();
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

    private bool _copyingRecovery;

    /// <remarks>
    /// COPYING IS DEALING WITH IT. Once the recovered words are on the clipboard and the encrypted
    /// copy has been removed, the card closes and the app forgets the pending recovery so no stale
    /// banner stays behind. If deletion fails, keep the card visible and pending so the user can
    /// review or retry removing it.
    /// </remarks>
    private async void CopyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_copyingRecovery)
        {
            return;
        }

        var recovered = RecoveryTextBox.Text;
        if (string.IsNullOrWhiteSpace(recovered))
        {
            return;
        }

        _copyingRecovery = true;
        var recoveryCopyVersion = ++_recoveryCopyResetVersion;
        if (CopyRecoveryIcon is not null)
        {
            CopyRecoveryIcon.Glyph = "\uE73E";
        }
        if (CopyRecoveryLabel is not null)
        {
            CopyRecoveryLabel.Text = "Copied";
        }
        try
        {
            var package = new DataPackage();
            package.SetText(recovered);
            Clipboard.SetContent(package);
            Clipboard.Flush();

            var recoveryDeleted = await _session.History.DeleteRecoveryAsync().ConfigureAwait(true);
            if (_session.Closing)
            {
                return;
            }

            if (recoveryDeleted)
            {
                ClearRecoveredText();
                OnboardingReliabilityInfoBar.IsOpen = false;
                RecoveryCleared?.Invoke();
                ResetRecoveryCopyButtonVisuals();
            }
            else
            {
                RecoveryBodyText.Text = "Copied to clipboard. The encrypted recovery file could not be removed yet.";
                _ = ResetRecoveryCopyButtonAfterDelayAsync(recoveryCopyVersion);
            }
        }
        finally
        {
            _copyingRecovery = false;
        }
    }

    private async Task ResetRecoveryCopyButtonAfterDelayAsync(int expectedVersion)
    {
        await Task.Delay(2000).ConfigureAwait(true);
        if (_shuttingDownProductWindows || _recoveryCopyResetVersion != expectedVersion)
        {
            return;
        }

        ResetRecoveryCopyButtonVisuals();
    }

    private void ResetRecoveryCopyButtonVisuals()
    {
        if (CopyRecoveryIcon is not null)
        {
            CopyRecoveryIcon.Glyph = "\uE8C8";
        }
        if (CopyRecoveryLabel is not null)
        {
            CopyRecoveryLabel.Text = "Copy recovered text";
        }
    }

    private async void DeleteRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecoveryTextBox.Text))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Delete the recovered dictation?",
            Content = "This permanently removes the encrypted recovery copy from this PC. It does not change history or clipboard contents.",
            PrimaryButtonText = "Delete recovery copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var recoveryDeleted = await _session.History.DeleteRecoveryAsync().ConfigureAwait(true);
        if (_session.Closing)
        {
            return;
        }

        if (recoveryDeleted)
        {
            ClearRecoveredText();
            OnboardingReliabilityInfoBar.IsOpen = false;
            RecoveryCleared?.Invoke();
            ShowMessage(
                "Recovery copy deleted",
                "The encrypted local recovery copy was removed.",
                InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage(
                "Recovery copy could not be deleted",
                "Windows left the encrypted recovery file untouched.",
                InfoBarSeverity.Error);
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
        var succeeded = await _session.History.DeleteEntryAsync(selected.Id).ConfigureAwait(true);
        if (succeeded is null || _session.Closing)
        {
            return;
        }

        if (succeeded.Value)
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

        var succeeded = await _session.History.KeepEntryAsync(selected.Id).ConfigureAwait(true);
        if (succeeded is null || _session.Closing)
        {
            return;
        }

        if (succeeded.Value)
        {
            await RefreshHistoryAsync(showLoadingState: false).ConfigureAwait(true);
            ShowMessage("History entry kept", "Its 24-hour Escape Recovery expiry was removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
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

            var succeeded = await _session.History.DeleteEntriesAsync(idsToDelete).ConfigureAwait(true);
            if (succeeded is null || _session.Closing)
            {
                return;
            }

            if (succeeded.Value)
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
        var succeeded = await _session.History.ClearEntriesAsync().ConfigureAwait(true);
        if (succeeded is null || _session.Closing)
        {
            return;
        }

        if (succeeded.Value)
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
        HistoryPageView pageView,
        string query,
        DateTimeOffset localNow,
        IReadOnlyDictionary<Guid, string?> resolvedRecordingPaths,
        IReadOnlyList<Guid>? existingIdsForSelection = null)
    {
        _historyLoadStatus = pageView.Status;
        _historyTotalStoredCount = pageView.TotalStoredCount;
        _historyMatchingCount = pageView.TotalMatchingCount;
        _historyHasMore = pageView.HasMore;
        _historyRemainingCount = pageView.RemainingCount;

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
        RenderHistoryPage(pageView.Entries, pageView.DayCounts, query, localNow, resolvedRecordingPaths);
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

    public sealed class HistoryDayHeaderViewModel
    {
        public HistoryDayHeaderViewModel(HistoryDayHeader header, int dayEntryCount)
        {
            DayHeaderTitle = !string.IsNullOrWhiteSpace(header.SecondaryDateLabel)
                ? header.SecondaryDateLabel
                : header.PrimaryTitle;
            DayHeaderSubtitle = !string.IsNullOrWhiteSpace(header.RelativeLabel)
                ? header.RelativeLabel.ToUpper(CultureInfo.CurrentCulture)
                : string.Empty;
            DayHeaderSubtitleVisibility = !string.IsNullOrEmpty(DayHeaderSubtitle)
                ? Visibility.Visible
                : Visibility.Collapsed;
            DayCountText = HistoryDiaryPresentation.FormatItemCount(dayEntryCount);
            AccessibilityLabel = $"{header.AccessibilityLabel}, {DayCountText}";
        }

        public string DayHeaderTitle { get; }

        public string DayHeaderSubtitle { get; }

        public Visibility DayHeaderSubtitleVisibility { get; }

        public string DayCountText { get; }

        public string AccessibilityLabel { get; }

        public override string ToString() => AccessibilityLabel;
    }

    public sealed class HistoryItemViewModel : INotifyPropertyChanged
    {
        private readonly DictationHistoryEntry _entry;
        private HistoryDiffResult _diffResult;
        private bool _isSelectionMode;
        private bool _isSelected;
        private bool _isTranscriptExpanded;
        private bool _isShowingEdits;
        private HistoryEditsViewMode _editsViewMode;
        private bool _hasOverflow;
        private bool _isGlobalBusy;
        private bool _isRepolishingThisCard;
        private bool _isPrimaryPasteTarget;
        private HistoryPolishReadiness _polishReadiness;
        private bool _hasAudioRecording;
        private long _audioDurationMs;
        private IReadOnlyList<float> _waveformPeaks;
        private bool _isPlaybackActive;
        private bool _isPlaying;
        private long _playbackPositionMs;
        private bool _isCopiedPrimary;
        private string? _inlineNotice;

        public HistoryItemViewModel(
            DictationHistoryEntry entry,
            bool isSelectionMode,
            bool isSelected,
            bool isTranscriptExpanded,
            bool isShowingEdits,
            HistoryEditsViewMode editsViewMode,
            HistoryDiffResult diffResult,
            bool isGlobalBusy,
            bool isRepolishingThisCard,
            bool isPrimaryPasteTarget,
            HistoryPolishReadiness polishReadiness,
            bool hasAudioRecording,
            long audioDurationMs,
            IReadOnlyList<float>? waveformPeaks,
            HistoryPlaybackSnapshot playbackSnapshot,
            bool isCopiedPrimary,
            string? inlineNotice)
        {
            _entry = entry;
            _diffResult = diffResult ?? HistoryDiffResult.Empty;
            _isSelectionMode = isSelectionMode;
            _isSelected = isSelected;
            _isTranscriptExpanded = isTranscriptExpanded;
            _isShowingEdits = isShowingEdits;
            _editsViewMode = editsViewMode;
            _isGlobalBusy = isGlobalBusy;
            _isRepolishingThisCard = isRepolishingThisCard;
            _isPrimaryPasteTarget = isPrimaryPasteTarget;
            _polishReadiness = polishReadiness;
            _hasAudioRecording = hasAudioRecording;
            _audioDurationMs = Math.Max(0L, audioDurationMs);
            _waveformPeaks = HistoryPlaybackPresentation.NormalizeOrFallbackPeaks(waveformPeaks);
            _isCopiedPrimary = isCopiedPrimary;
            _inlineNotice = inlineNotice;

            Id = entry.Id;
            Text = entry.Text;
            OriginalText = entry.OriginalText ?? string.Empty;
            var localCreated = entry.CreatedAt.ToLocalTime();
            CreatedDisplay = localCreated.ToString("f", CultureInfo.CurrentCulture);
            TimeDisplay = localCreated.ToString("t", CultureInfo.CurrentCulture);
            IsTemporary = entry.ExpiresAt is not null;
            HasPolishEdits = HistoryDiaryPresentation.HasPolishEdits(entry);

            FileChipVisibility = HistoryPresenter.IsFileTranscription(entry) ? Visibility.Visible : Visibility.Collapsed;
            RecoveryChipVisibility = IsTemporary ? Visibility.Visible : Visibility.Collapsed;
            RecoveryChipText = entry.ExpiresAt is { } expires
                ? FormatRecoveryExpiryChip(expires)
                : string.Empty;
            HeldSafelyChipVisibility = !entry.WasDelivered ? Visibility.Visible : Visibility.Collapsed;

            _hasOverflow = ShouldOfferShowMore(DisplayedCopyText);
            ApplySnapshotFields(playbackSnapshot);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public DictationHistoryEntry Entry => _entry;

        public Guid Id { get; }

        public string Text { get; }

        public string OriginalText { get; }

        public string CreatedDisplay { get; }

        public string TimeDisplay { get; }

        public bool IsTemporary { get; }

        public bool HasPolishEdits { get; }

        public bool HasAudioRecording => _hasAudioRecording;

        public long AudioDurationMs => _audioDurationMs;

        public Visibility FileChipVisibility { get; }

        public Visibility RecoveryChipVisibility { get; }

        public string RecoveryChipText { get; }

        public Visibility HeldSafelyChipVisibility { get; }

        public string ProvenanceText => BuildProvenanceText(_entry, _hasAudioRecording, _audioDurationMs);

        public Visibility KeepButtonVisibility => IsTemporary ? Visibility.Visible : Visibility.Collapsed;

        public Visibility HasPolishEditsVisibility => HasPolishEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SelectionCheckBoxVisibility => _isSelectionMode ? Visibility.Visible : Visibility.Collapsed;

        public bool IsSelected => _isSelected;

        public string SelectionCheckBoxLabel => $"Select dictation from {TimeDisplay}";

        public Visibility SelectedAccentVisibility =>
            _isSelectionMode && _isSelected ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ActiveCardOutlineVisibility =>
            (_isSelectionMode && _isSelected) || _isPlaying ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ShowEditsInactiveVisibility =>
            HasPolishEdits && !_isShowingEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ShowEditsActiveVisibility =>
            HasPolishEdits && _isShowingEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ShowEditsPanelVisibility =>
            HasPolishEdits && _isShowingEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PolishedActiveVisibility =>
            _editsViewMode == HistoryEditsViewMode.Polished ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PolishedInactiveVisibility =>
            _editsViewMode != HistoryEditsViewMode.Polished ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MarkedUpActiveVisibility =>
            _editsViewMode == HistoryEditsViewMode.MarkedUp ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MarkedUpInactiveVisibility =>
            _editsViewMode != HistoryEditsViewMode.MarkedUp ? Visibility.Visible : Visibility.Collapsed;

        public Visibility OriginalActiveVisibility =>
            _editsViewMode == HistoryEditsViewMode.Original ? Visibility.Visible : Visibility.Collapsed;

        public Visibility OriginalInactiveVisibility =>
            _editsViewMode != HistoryEditsViewMode.Original ? Visibility.Visible : Visibility.Collapsed;

        public HistoryEditsViewMode ActiveTranscriptViewMode =>
            HasPolishEdits && _isShowingEdits ? _editsViewMode : HistoryEditsViewMode.Polished;

        public IReadOnlyList<MarkedUpRun> MarkedUpRuns => _diffResult.Runs;

        public Visibility MarkedUpLegendVisibility =>
            HasPolishEdits
            && _isShowingEdits
            && _editsViewMode == HistoryEditsViewMode.MarkedUp
            && _diffResult.ChangeCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public bool IsShowingOriginalView =>
            HasPolishEdits && _isShowingEdits && _editsViewMode == HistoryEditsViewMode.Original;

        public string ComparisonStatusText => _editsViewMode switch
        {
            HistoryEditsViewMode.Original => HistoryEditsPresentation.BeforePolishStatusText,
            HistoryEditsViewMode.MarkedUp => _diffResult.SummaryText,
            _ => HistoryEditsPresentation.PolishedStatusText,
        };

        public string CopyButtonGlyph => _isCopiedPrimary ? "\uE73E" : "\uE8C8";

        public string CopyButtonText => _isCopiedPrimary ? "Copied" : "Copy";

        public string DisplayedCopyText =>
            IsShowingOriginalView && !string.IsNullOrWhiteSpace(_entry.OriginalText)
                ? _entry.OriginalText
                : _entry.Text;

        public string DisplayText => DisplayedCopyText;

        public int TranscriptMaxLines => _isTranscriptExpanded || (_isShowingEdits && HasPolishEdits) ? 0 : 4;

        public Visibility ShowMoreToggleVisibility =>
            _hasOverflow && !(_isShowingEdits && HasPolishEdits) ? Visibility.Visible : Visibility.Collapsed;

        public string ShowMoreButtonText => _isTranscriptExpanded ? "Show less" : "Show more";

        public Visibility PlaybackRowVisibility => _hasAudioRecording ? Visibility.Visible : Visibility.Collapsed;

        public bool CanTogglePlayback =>
            HistoryPlaybackPresentation.CanTogglePlayback(_hasAudioRecording, _isGlobalBusy, _isPlaying);

        public string PlaybackButtonGlyph => _isPlaying ? "\uE769" : "\uE768";

        public string PlaybackButtonAutomationName =>
            _isPlaying ? $"Pause recording from {TimeDisplay}" : $"Play recording from {TimeDisplay}";

        public string PlaybackScrubberLabel => $"Recording playback position, {PlaybackClockText}";

        public IReadOnlyList<float> WaveformPeaks => _waveformPeaks;

        public bool IsPlaybackActive => _isPlaybackActive;

        public double PlaybackProgress =>
            _isPlaybackActive && _audioDurationMs > 0
                ? Math.Clamp((double)_playbackPositionMs / _audioDurationMs, 0.0, 1.0)
                : 0.0;

        public string PlaybackCurrentClockText =>
            HistoryPlaybackPresentation.FormatClock(
                _isPlaybackActive ? Math.Clamp(_playbackPositionMs, 0L, Math.Max(0L, _audioDurationMs)) : 0L);

        public string PlaybackTotalClockText => HistoryPlaybackPresentation.FormatClock(_audioDurationMs);

        public string PlaybackClockText =>
            HistoryPlaybackPresentation.FormatClockDisplay(_playbackPositionMs, _audioDurationMs, _isPlaybackActive);

        public string InlineNoticeText => _inlineNotice ?? string.Empty;

        public Visibility InlineNoticeVisibility =>
            !string.IsNullOrWhiteSpace(_inlineNotice) ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRepolishing => _isRepolishingThisCard;

        public Visibility RepolishingVisibility => _isRepolishingThisCard ? Visibility.Visible : Visibility.Collapsed;

        public bool CanRunCardActions => !_isGlobalBusy;

        public Visibility RepolishButtonVisibility =>
            _polishReadiness.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

        public bool CanRepolish => _polishReadiness.CanRepolishEntry(_isGlobalBusy, IsTemporary);

        public string PasteButtonAutomationId => _isPrimaryPasteTarget ? "PasteHistoryButton" : string.Empty;

        public void SetSelectionState(bool isSelectionMode, bool isSelected)
        {
            if (_isSelectionMode == isSelectionMode && _isSelected == isSelected)
            {
                return;
            }

            _isSelectionMode = isSelectionMode;
            _isSelected = isSelected;
            Notify(
                nameof(SelectionCheckBoxVisibility),
                nameof(IsSelected),
                nameof(SelectedAccentVisibility),
                nameof(ActiveCardOutlineVisibility));
        }

        public void SetPolishReadiness(HistoryPolishReadiness readiness)
        {
            if (_polishReadiness == readiness)
            {
                return;
            }

            _polishReadiness = readiness;
            Notify(nameof(RepolishButtonVisibility), nameof(CanRepolish));
        }

        public void SetCopiedPrimary(bool isCopied)
        {
            if (_isCopiedPrimary == isCopied)
            {
                return;
            }

            _isCopiedPrimary = isCopied;
            Notify(nameof(CopyButtonGlyph), nameof(CopyButtonText));
        }

        public void SetInlineNotice(string? notice)
        {
            var normalized = string.IsNullOrWhiteSpace(notice) ? null : notice.Trim();
            if (string.Equals(_inlineNotice, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _inlineNotice = normalized;
            Notify(nameof(InlineNoticeText), nameof(InlineNoticeVisibility));
        }

        public void SetWaveformData(IReadOnlyList<float> peaks, long durationMs)
        {
            _waveformPeaks = peaks;
            if (durationMs > 0)
            {
                _audioDurationMs = durationMs;
            }

            Notify(
                nameof(WaveformPeaks),
                nameof(PlaybackProgress),
                nameof(PlaybackCurrentClockText),
                nameof(PlaybackTotalClockText),
                nameof(PlaybackClockText),
                nameof(PlaybackScrubberLabel),
                nameof(ProvenanceText));
        }

        public void MarkRecordingMissing()
        {
            if (!_hasAudioRecording)
            {
                return;
            }

            _hasAudioRecording = false;
            _isPlaybackActive = false;
            _isPlaying = false;
            _playbackPositionMs = 0;
            Notify(
                nameof(PlaybackRowVisibility),
                nameof(CanTogglePlayback),
                nameof(IsPlaybackActive),
                nameof(ActiveCardOutlineVisibility),
                nameof(ProvenanceText));
        }

        public void SetPlaybackSnapshot(HistoryPlaybackSnapshot snapshot)
        {
            var wasActive = _isPlaybackActive;
            var wasPlaying = _isPlaying;
            var prevPos = _playbackPositionMs;
            var prevDuration = _audioDurationMs;
            var prevProgress = PlaybackProgress;
            var prevCurrentClock = PlaybackCurrentClockText;
            var prevTotalClock = PlaybackTotalClockText;

            ApplySnapshotFields(snapshot);
            if (wasActive == _isPlaybackActive
                && wasPlaying == _isPlaying
                && prevPos == _playbackPositionMs
                && prevDuration == _audioDurationMs)
            {
                return;
            }

            if (wasPlaying != _isPlaying)
            {
                Notify(nameof(PlaybackButtonGlyph), nameof(PlaybackButtonAutomationName), nameof(CanTogglePlayback));
            }

            if (wasActive != _isPlaybackActive)
            {
                Notify(nameof(IsPlaybackActive));
            }

            if (wasPlaying != _isPlaying)
            {
                Notify(nameof(ActiveCardOutlineVisibility));
            }

            if (Math.Abs(prevProgress - PlaybackProgress) > 1e-6 || wasActive != _isPlaybackActive)
            {
                Notify(nameof(PlaybackProgress));
            }

            var currentClockChanged = !string.Equals(prevCurrentClock, PlaybackCurrentClockText, StringComparison.Ordinal);
            var totalClockChanged = !string.Equals(prevTotalClock, PlaybackTotalClockText, StringComparison.Ordinal);
            if (currentClockChanged)
            {
                Notify(nameof(PlaybackCurrentClockText));
            }

            if (totalClockChanged)
            {
                Notify(nameof(PlaybackTotalClockText));
            }

            if (currentClockChanged || totalClockChanged || wasActive != _isPlaybackActive)
            {
                Notify(nameof(PlaybackClockText), nameof(PlaybackScrubberLabel));
            }
        }

        private void ApplySnapshotFields(HistoryPlaybackSnapshot snapshot)
        {
            var isTarget = snapshot.ActiveEntryId == Id;
            _isPlaybackActive = isTarget;
            _isPlaying = isTarget && snapshot.IsPlaying;
            _playbackPositionMs = isTarget ? snapshot.PositionMs : 0;
            if (isTarget && snapshot.DurationMs > 0)
            {
                _audioDurationMs = snapshot.DurationMs;
            }
        }

        public void SetShowingEdits(
            bool isShowingEdits,
            HistoryEditsViewMode? initialMode = null,
            HistoryDiffResult? diffResult = null)
        {
            var modeChanged = initialMode is { } mode && _editsViewMode != mode;
            var diffChanged = diffResult is not null && !ReferenceEquals(_diffResult, diffResult);
            if (_isShowingEdits == isShowingEdits && !modeChanged && !diffChanged)
            {
                return;
            }

            _isShowingEdits = isShowingEdits;
            if (initialMode is { } nextMode)
            {
                _editsViewMode = nextMode;
            }

            if (diffResult is not null)
            {
                _diffResult = diffResult;
            }

            _hasOverflow = _hasOverflow || ShouldOfferShowMore(DisplayedCopyText);
            Notify(
                nameof(ShowEditsInactiveVisibility),
                nameof(ShowEditsActiveVisibility),
                nameof(ShowEditsPanelVisibility),
                nameof(ActiveTranscriptViewMode),
                nameof(PolishedActiveVisibility),
                nameof(PolishedInactiveVisibility),
                nameof(MarkedUpActiveVisibility),
                nameof(MarkedUpInactiveVisibility),
                nameof(OriginalActiveVisibility),
                nameof(OriginalInactiveVisibility),
                nameof(ComparisonStatusText),
                nameof(MarkedUpRuns),
                nameof(MarkedUpLegendVisibility),
                nameof(IsShowingOriginalView),
                nameof(DisplayedCopyText),
                nameof(DisplayText),
                nameof(TranscriptMaxLines),
                nameof(ShowMoreToggleVisibility));
        }

        public void SetEditsViewMode(HistoryEditsViewMode viewMode)
        {
            if (_editsViewMode == viewMode)
            {
                return;
            }

            _editsViewMode = viewMode;
            _hasOverflow = _hasOverflow || ShouldOfferShowMore(DisplayedCopyText);
            Notify(
                nameof(ActiveTranscriptViewMode),
                nameof(PolishedActiveVisibility),
                nameof(PolishedInactiveVisibility),
                nameof(MarkedUpActiveVisibility),
                nameof(MarkedUpInactiveVisibility),
                nameof(OriginalActiveVisibility),
                nameof(OriginalInactiveVisibility),
                nameof(ComparisonStatusText),
                nameof(MarkedUpLegendVisibility),
                nameof(IsShowingOriginalView),
                nameof(DisplayedCopyText),
                nameof(DisplayText),
                nameof(ShowMoreToggleVisibility));
        }

        public void SetTranscriptExpanded(bool isExpanded)
        {
            if (_isTranscriptExpanded == isExpanded)
            {
                return;
            }

            _isTranscriptExpanded = isExpanded;
            Notify(nameof(TranscriptMaxLines), nameof(ShowMoreButtonText));
        }

        public void MarkHasOverflow()
        {
            if (_hasOverflow)
            {
                return;
            }

            _hasOverflow = true;
            Notify(nameof(ShowMoreToggleVisibility));
        }

        public void SetOperationState(bool isGlobalBusy, bool isRepolishingThisCard, bool isPrimaryPasteTarget)
        {
            if (_isGlobalBusy == isGlobalBusy
                && _isRepolishingThisCard == isRepolishingThisCard
                && _isPrimaryPasteTarget == isPrimaryPasteTarget)
            {
                return;
            }

            _isGlobalBusy = isGlobalBusy;
            _isRepolishingThisCard = isRepolishingThisCard;
            _isPrimaryPasteTarget = isPrimaryPasteTarget;
            Notify(
                nameof(IsRepolishing),
                nameof(RepolishingVisibility),
                nameof(CanRunCardActions),
                nameof(CanTogglePlayback),
                nameof(RepolishButtonVisibility),
                nameof(CanRepolish),
                nameof(PasteButtonAutomationId));
        }

        private void Notify(params string[] propertyNames)
        {
            if (PropertyChanged is null)
            {
                return;
            }

            foreach (var name in propertyNames)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(name));
            }
        }

        private static string FormatRecoveryExpiryChip(DateTimeOffset expiresAt)
        {
            var localExpiry = expiresAt.ToLocalTime();
            var pattern = DateOnly.FromDateTime(localExpiry.DateTime) == DateOnly.FromDateTime(DateTime.Now)
                ? "t"
                : "g";
            return $"Recovery · expires {localExpiry.ToString(pattern, CultureInfo.CurrentCulture)}";
        }

        private static string BuildProvenanceText(
            DictationHistoryEntry entry,
            bool hasAudioRecording,
            long audioDurationMs)
        {
            var parts = new List<string>(5);

            if (hasAudioRecording && audioDurationMs > 0)
            {
                parts.Add(HistoryPlaybackPresentation.FormatClock(audioDurationMs));
            }

            parts.Add(HistoryDiaryPresentation.FormatEngineDisplayName(entry.EngineId));

            if (entry.WasPolished)
            {
                var polishName = HistoryDiaryPresentation.FormatPolishModelLabel(entry.PolishModelId)
                    ?? HistoryDiaryPresentation.FormatPolishProviderLabel(entry.PolishProviderId);
                parts.Add(string.IsNullOrWhiteSpace(polishName) ? "AI Polish" : polishName);
            }
            else
            {
                parts.Add("Text cleanup");
            }

            if (!string.IsNullOrWhiteSpace(entry.LanguageCode))
            {
                parts.Add(LanguageCatalog.DisplayNameFor(entry.LanguageCode));
            }

            if (!hasAudioRecording)
            {
                parts.Add(string.IsNullOrWhiteSpace(entry.AudioFileName) ? "No recording" : "Recording unavailable");
            }

            return string.Join(" · ", parts);
        }

        private static bool ShouldOfferShowMore(string text)
        {
            if (text.Length > 240)
            {
                return true;
            }

            var lineBreaks = 0;
            foreach (var ch in text)
            {
                if (ch == '\n' && ++lineBreaks >= 3)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// What a screen reader announces for this row.
        /// </summary>
        public override string ToString() => $"{Text}, dictated {CreatedDisplay}";
    }
}

public sealed class HistoryDiaryRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DayHeaderTemplate { get; set; }

    public DataTemplate? CardTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        MainWindow.HistoryDayHeaderViewModel => DayHeaderTemplate,
        MainWindow.HistoryItemViewModel => CardTemplate,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
