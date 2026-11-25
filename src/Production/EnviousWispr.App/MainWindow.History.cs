using System.ComponentModel;
using System.Globalization;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EnviousWispr.App;

/// <summary>Dictation history diary, search, filtering, re-polish, and recovery card UI.</summary>
public sealed partial class MainWindow
{
    private readonly List<DictationHistoryEntry> _history = [];

    private HistoryLoadStatus _historyLoadStatus = HistoryLoadStatus.Missing;
    private bool _isHistoryLoading = true;

    private bool _isRepolishing;

    public event Action? RecoveryCleared;

    /// <summary>The person pressed Undo beside an Escape Recovery on Home.</summary>
    public event Action? RecoveryUndoRequested;

    /// <summary>The person asked for the selected History entry to be pasted into the app they were using.</summary>
    public event Action<Guid>? HistoryPasteRequested;

    public async Task NotifyHistoryChangedAsync() => await ReloadHistoryAsync().ConfigureAwait(true);

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
            ShowMessage("Not pasted", SavedDictationPasteRefusalSentence(refusal), InfoBarSeverity.Informational);
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
                    "Nothing was typed, and your clipboard could not be restored. Copy it from Home or History instead.",
                    InfoBarSeverity.Error);
            }

            return;
        }

        switch (outcome)
        {
            case SavedDictationPasteOutcome.KeptOnClipboard:
                ShowMessage(
                    "Your dictation is on the clipboard",
                    undo
                        ? "EnviousWispr could not get back to the place you were dictating into, so nothing was typed there. Paste it where you want it."
                        : "That app refused the paste. Paste it where you want it.",
                    InfoBarSeverity.Warning);
                break;
            case SavedDictationPasteOutcome.MayHavePasted:
                // NOT "NOTHING WAS TYPED", AND NO INVITATION TO PRESS AGAIN: the words may already be there, and a
                // second paste would put them in twice. Only Undo promises a copy: Home keeps it, and a History entry may not.
                ShowMessage(
                    "Your dictation may have been pasted",
                    undo
                        ? "EnviousWispr could not confirm it. Check the app before you try again. The text is still on Home."
                        : "EnviousWispr could not confirm it. Check the app before you try again.",
                    InfoBarSeverity.Warning);
                break;
            case SavedDictationPasteOutcome.NoLongerAvailable:
                ShowMessage(
                    "That dictation is no longer in History",
                    "It was deleted or its 24 hours ran out, so nothing was pasted.",
                    InfoBarSeverity.Informational);
                break;
            case SavedDictationPasteOutcome.DictationInProgress or SavedDictationPasteOutcome.Busy:
                ShowMessage(
                    "Not pasted",
                    "EnviousWispr is busy with a dictation. Try again in a moment.",
                    InfoBarSeverity.Informational);
                break;
            case SavedDictationPasteOutcome.NoTarget:
                ShowMessage(
                    "Not pasted",
                    undo
                        ? "The app you were dictating into has gone. Copy the text from Home instead."
                        : "Click into the app you want it in, then come back and try again.",
                    InfoBarSeverity.Informational);
                break;
            case SavedDictationPasteOutcome.Failed:
                ShowMessage(
                    "Your dictation could not be pasted",
                    "Nothing was typed. Copy it from Home or History instead.",
                    InfoBarSeverity.Error);
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
        if (InlineClearHistorySearchButton is not null)
        {
            InlineClearHistorySearchButton.Visibility = string.IsNullOrEmpty(HistorySearchBox.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        RefreshHistoryView();
    }

    private void ClearHistorySearchButton_Click(object sender, RoutedEventArgs e) =>
        HistorySearchBox.Text = string.Empty;

    private void ClearHistoryFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        HistorySearchBox.Text = string.Empty;
        _historyKindFilter = HistoryKindFilter.All;
        _historyDateRangeFilter = HistoryDateRangeFilter.AnyTime;
        _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;
        if (HistoryDateFilterText is not null)
        {
            HistoryDateFilterText.Text = HistoryDiaryPresentation.DateRangeLabel(_historyDateRangeFilter);
        }

        UpdateHistoryFilterSegmentButtons();
        RefreshHistoryView();
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
        _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;
        if (HistoryDateFilterText is not null)
        {
            HistoryDateFilterText.Text = HistoryDiaryPresentation.DateRangeLabel(_historyDateRangeFilter);
        }

        UpdateHistoryFilterSegmentButtons();
        RefreshHistoryView();
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
        HistoryHelpItemsControl.ItemsSource = HistoryDiaryPresentation.HelpItems(retentionDays);
    }

    private void SelectAllMatchingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastMatchingHistoryEntries.Count == 0 || _isRepolishing || _isBulkDeletingHistory)
        {
            return;
        }

        var allSelected = _lastMatchingHistoryEntries.All(entry => _selectedHistoryIds.Contains(entry.Id));
        _lastBulkDeletionSummary = null;
        if (allSelected)
        {
            foreach (var entry in _lastMatchingHistoryEntries)
            {
                _selectedHistoryIds.Remove(entry.Id);
            }
        }
        else
        {
            foreach (var entry in _lastMatchingHistoryEntries)
            {
                _selectedHistoryIds.Add(entry.Id);
            }
        }

        RefreshCardSelectionVisuals();
        UpdateHistorySelectionDependentButtons();
    }

    private void LoadMoreHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _historyVisibleLimit += HistoryDiaryPresentation.DefaultPageSize;
        RefreshHistoryView();
    }

    private void CardSelectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        _lastBulkDeletionSummary = null;
        if (!_selectedHistoryIds.Remove(card.Id))
        {
            _selectedHistoryIds.Add(card.Id);
        }

        card.SetSelectionState(_isHistorySelectionMode, _selectedHistoryIds.Contains(card.Id));
        UpdateHistorySelectionDependentButtons();
    }

    private void CardShowEditsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        if (!_showingEditsIds.Remove(card.Id))
        {
            _showingEditsIds.Add(card.Id);
        }

        card.SetShowingEdits(_showingEditsIds.Contains(card.Id));
    }

    private void CardShowPolished_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        _showingOriginalIds.Remove(card.Id);
        card.SetShowingOriginal(false);
    }

    private void CardShowOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        _showingOriginalIds.Add(card.Id);
        card.SetShowingOriginal(true);
    }

    private void CardShowMoreToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not HistoryItemViewModel card)
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
    private void CardTranscriptText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (sender.IsTextTrimmed && sender.DataContext is HistoryItemViewModel card)
        {
            card.MarkHasOverflow();
        }
    }

    private void CardPasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || sender is not FrameworkElement element
            || element.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        HistoryPasteRequested?.Invoke(card.Id);
    }

    private void CardCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || sender is not FrameworkElement element
            || element.DataContext is not HistoryItemViewModel card)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(card.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage("Copied", "The saved dictation is on your clipboard.", InfoBarSeverity.Success);
    }

    private void CardCopyAlternateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing || _isBulkDeletingHistory
            || sender is not FrameworkElement element
            || element.DataContext is not HistoryItemViewModel card
            || string.IsNullOrWhiteSpace(card.ComparisonOtherText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(card.ComparisonOtherText);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage(
            "Copied",
            card.IsShowingOriginalView
                ? "The AI-polished version is on your clipboard."
                : "The original unpolished words are on your clipboard.",
            InfoBarSeverity.Success);
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
                ShowMessage(
                    "Recovered text copied",
                    "The recovered dictation is on your clipboard, and its private recovery copy was removed from this PC.",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage(
                    "Recovered text copied",
                    "The recovered dictation is on your clipboard. Its private recovery copy could not be removed, so delete it when you no longer need it.",
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _copyingRecovery = false;
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

        if (sender is not FrameworkElement element || element.DataContext is not HistoryItemViewModel selected)
        {
            return;
        }

        if (selected.IsTemporary)
        {
            ShowMessage("Keep dictation first", "Escape recovery entries must be kept before re-polishing.", InfoBarSeverity.Informational);
            return;
        }

        if (_settings?.Preferences.Polish.Provider == PolishProvider.None)
        {
            ShowMessage("AI polish is off", "Enable an AI polish provider in settings first.", InfoBarSeverity.Warning);
            return;
        }

        _isRepolishing = true;
        _repolishingHistoryEntryId = selected.Id;
        UpdateHistorySelectionDependentButtons();

        try
        {
            var result = await _session.History.RepolishAsync(selected.Id).ConfigureAwait(true);
            if (result.Status == HistoryRepolishStatus.Closing || _session.Closing)
            {
                return;
            }

            if (result.Succeeded)
            {
                _showingOriginalIds.Remove(selected.Id);
                if (result.View is not null)
                {
                    ShowHistory(result.View);
                    var refreshed = _renderedHistoryItems.FirstOrDefault(i => i.Id == selected.Id);
                    if (refreshed is not null)
                    {
                        HistoryList.SelectedItem = refreshed;
                    }
                }
                ShowMessage("Re-polished dictation", "Transcript updated with active polish model.", InfoBarSeverity.Success);
            }
            else
            {
                var (title, message, severity) = result.Status switch
                {
                    HistoryRepolishStatus.NoProviderConfigured => ("AI polish is off", "Enable an AI polish provider in settings first.", InfoBarSeverity.Warning),
                    HistoryRepolishStatus.EscapeRecoveryEntryMustBeKept => ("Keep dictation first", "Escape recovery entries must be kept before re-polishing.", InfoBarSeverity.Informational),
                    HistoryRepolishStatus.PolishRefused => ("Re-polish refused", "The polish provider returned no response.", InfoBarSeverity.Warning),
                    HistoryRepolishStatus.EntryNotFound => ("History entry not found", "The entry may have already been removed.", InfoBarSeverity.Warning),
                    HistoryRepolishStatus.EntryExpired => ("History entry expired", "The entry is past retention.", InfoBarSeverity.Warning),
                    _ => ("Re-polish failed", "The polish service encountered an error.", InfoBarSeverity.Error),
                };
                ShowMessage(title, message, severity);
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
            || sender is not FrameworkElement element
            || element.DataContext is not HistoryItemViewModel selected)
        {
            return;
        }

        var result = await _session.History.DeleteAsync(selected.Id).ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            _selectedHistoryIds.Remove(selected.Id);
            _expandedTranscriptIds.Remove(selected.Id);
            _showingEditsIds.Remove(selected.Id);
            _showingOriginalIds.Remove(selected.Id);
            BeginHistoryReload();
            ShowHistory(result.View!);
            ShowMessage("History entry deleted", "The local copy was removed.", InfoBarSeverity.Success);
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

        var result = await _session.History.KeepAsync(selected.Id).ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            BeginHistoryReload();
            ShowHistory(result.View!);
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
            var result = await _session.History.DeleteManyAsync(idsToDelete).ConfigureAwait(true);
            if (result.Closing || _session.Closing)
            {
                return;
            }

            if (result.Succeeded && result.View is not null)
            {
                var remaining = HistoryDiaryPresentation.ReconcileSelectionAfterDelete(idsToDelete, result.View.Entries);
                var deletedCount = idsToDelete.Length - remaining.Count;
                _selectedHistoryIds.Clear();
                foreach (var remainingId in remaining)
                {
                    _selectedHistoryIds.Add(remainingId);
                }
                foreach (var id in idsToDelete.Where(id => !remaining.Contains(id)))
                {
                    _expandedTranscriptIds.Remove(id);
                    _showingEditsIds.Remove(id);
                    _showingOriginalIds.Remove(id);
                }

                _lastBulkDeletionSummary = remaining.Count > 0
                    ? $"{remaining.Count} item{(remaining.Count == 1 ? string.Empty : "s")} could not be deleted and stayed selected."
                    : deletedCount == 1 ? "1 dictation deleted." : $"{deletedCount} dictations deleted.";
                BeginHistoryReload();
                ShowHistory(result.View);
                ShowMessage(
                    deletedCount == 1 ? "1 dictation deleted" : $"{deletedCount} dictations deleted",
                    "The selected local history copies were removed.",
                    InfoBarSeverity.Success);
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

        var result = await _session.History.ClearAsync().ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            _expandedTranscriptIds.Clear();
            _showingEditsIds.Clear();
            _showingOriginalIds.Clear();
            SetHistorySelectionMode(false);
            UpdateHistoryFilterSegmentButtons();
            BeginHistoryReload();
            ShowHistory(result.View!);
            ShowMessage("History cleared", "All locally saved dictations were removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be cleared", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async Task ReloadHistoryAsync()
    {
        BeginHistoryReload();
        var view = await _session.History.LoadAsync().ConfigureAwait(true);
        ShowHistory(view);
    }

    /// <summary>The loading transition every reload goes through, whether the page or a command asked for it.</summary>
    /// <remarks>
    /// THE SAME TRANSITION FOR A COMMAND'S RELOAD AS FOR THE PAGE'S. The rows go away and the loading
    /// card shows, which is also what resets the announcement bookkeeping - so a command that ends
    /// with the same count as before is still announced, exactly as it was when every command
    /// reloaded through the page's own path.
    /// </remarks>
    private void BeginHistoryReload()
    {
        _isHistoryLoading = true;
        UpdateHistoryListVisibility(HistorySearchBox.Text.Trim(), itemCount: 0);
    }

    /// <summary>Puts what the presenter loaded on the page: the rows, and the one line about them.</summary>
    private void ShowHistory(HistoryView view)
    {
        // A LOAD THE EXIT REFUSED OR STOPPED IS NOT DRAWN, AND NEITHER IS ONE THAT FINISHED AS THE
        // EXIT BEGAN: a load inside a store that ignores its token completes after the drain started
        // and reaches this thread with an honest answer for a page that is on its way out. The
        // loading card it already shows is the last thing drawn on it.
        if (view.Summary == HistorySummary.Closing || _session.Closing)
        {
            return;
        }

        _historyLoadStatus = view.Status;
        _history.Clear();
        _history.AddRange(view.Entries);
        var reconciled = HistoryDiaryPresentation.ReconcileSelectionAfterDelete(_selectedHistoryIds, _history);
        _selectedHistoryIds.Clear();
        foreach (var id in reconciled)
        {
            _selectedHistoryIds.Add(id);
        }

        _isHistoryLoading = false;
        RefreshHistoryView();
    }

    private HistoryKindFilter _historyKindFilter = HistoryKindFilter.All;
    private HistoryDateRangeFilter _historyDateRangeFilter = HistoryDateRangeFilter.AnyTime;
    private bool _isHistorySelectionMode;
    private bool _isBulkDeletingHistory;
    private int _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;
    private Guid? _repolishingHistoryEntryId;
    private string? _lastBulkDeletionSummary;
    private readonly HashSet<Guid> _selectedHistoryIds = [];
    private readonly HashSet<Guid> _expandedTranscriptIds = [];
    private readonly HashSet<Guid> _showingEditsIds = [];
    private readonly HashSet<Guid> _showingOriginalIds = [];
    private List<DictationHistoryEntry> _lastMatchingHistoryEntries = [];
    private List<HistoryItemViewModel> _renderedHistoryItems = [];

    private void HistoryFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            return;
        }

        _historyKindFilter = tag switch
        {
            "Dictation" => HistoryKindFilter.Dictation,
            "TranscribeFile" => HistoryKindFilter.TranscribeFile,
            _ => HistoryKindFilter.All,
        };
        _historyVisibleLimit = HistoryDiaryPresentation.DefaultPageSize;

        UpdateHistoryFilterSegmentButtons();
        RefreshHistoryView();
    }

    private void UpdateHistoryFilterSegmentButtons()
    {
        var activeTab = Application.Current.Resources["BrandTabStripActiveButtonStyle"] as Style;
        var normalTab = Application.Current.Resources["BrandTabStripButtonStyle"] as Style;
        if (activeTab is not null && normalTab is not null)
        {
            HistoryFilterAllButton.Style = _historyKindFilter == HistoryKindFilter.All ? activeTab : normalTab;
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

    private void RefreshHistoryView()
    {
        var previousSelectedId = (HistoryList.SelectedItem as HistoryItemViewModel)?.Id;
        var query = HistorySearchBox.Text.Trim();
        var now = DateTimeOffset.Now;
        var matching = HistoryPresenter.Filter(_history, query, _historyKindFilter, _historyDateRangeFilter, now);
        _lastMatchingHistoryEntries = matching.ToList();

        var fullDayCounts = HistoryDiaryPresentation.CountByDay(matching);
        var paged = HistoryDiaryPresentation.TakePage(matching, _historyVisibleLimit);
        var dayGroups = HistoryDiaryPresentation.GroupByDay(paged.Items, now);
        var rendered = new List<HistoryItemViewModel>(paged.Items.Count);
        var busy = _isRepolishing || _isBulkDeletingHistory;
        foreach (var group in dayGroups)
        {
            var totalDayCount = fullDayCounts.TryGetValue(group.Header.Day, out var dayCount)
                ? dayCount
                : group.Entries.Count;
            var isFirstInDay = true;
            foreach (var entry in group.Entries)
            {
                var vm = new HistoryItemViewModel(
                    entry,
                    dayHeader: isFirstInDay ? group.Header : null,
                    dayEntryCount: totalDayCount,
                    isSelectionMode: _isHistorySelectionMode,
                    isSelected: _selectedHistoryIds.Contains(entry.Id),
                    isTranscriptExpanded: _expandedTranscriptIds.Contains(entry.Id),
                    isShowingEdits: _showingEditsIds.Contains(entry.Id),
                    isShowingOriginal: _showingOriginalIds.Contains(entry.Id),
                    isGlobalBusy: busy,
                    isRepolishingThisCard: _repolishingHistoryEntryId == entry.Id,
                    isPrimaryPasteTarget: false);
                rendered.Add(vm);
                isFirstInDay = false;
            }
        }

        _renderedHistoryItems = rendered;
        HistoryList.ItemsSource = rendered;
        if (HistoryCountText is not null)
        {
            HistoryCountText.Text = HistoryDiaryPresentation.FormatItemCount(matching.Count);
        }
        if (HistoryLoadMoreContainer is not null && LoadMoreHistoryButton is not null)
        {
            HistoryLoadMoreContainer.Visibility = paged.HasMore ? Visibility.Visible : Visibility.Collapsed;
            if (paged.HasMore)
            {
                LoadMoreHistoryButton.Content = $"Show older dictations ({paged.RemainingCount} more)";
            }
        }
        if (ResetHistoryFiltersBarButton is not null)
        {
            var hasAnyFilter = !string.IsNullOrWhiteSpace(query)
                || _historyKindFilter != HistoryKindFilter.All
                || _historyDateRangeFilter != HistoryDateRangeFilter.AnyTime;
            ResetHistoryFiltersBarButton.Visibility = hasAnyFilter ? Visibility.Visible : Visibility.Collapsed;
        }
        if (previousSelectedId is { } id)
        {
            HistoryList.SelectedItem = rendered.FirstOrDefault(item => item.Id == id);
        }

        PopulateHistoryHelpItems();
        UpdateHistoryListVisibility(query, rendered.Count);
        UpdateHistorySelectionDependentButtons();
        UpdateHistoryStatusCards();
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
        var activePasteId = (HistoryList.SelectedItem as HistoryItemViewModel)?.Id
            ?? _renderedHistoryItems.FirstOrDefault()?.Id;
        foreach (var item in _renderedHistoryItems)
        {
            item.SetOperationState(busy, _repolishingHistoryEntryId == item.Id, item.Id == activePasteId);
        }

        ClearHistoryButton.IsEnabled = _history.Count > 0 && !busy;
        if (HistoryMoreDeleteAllItem is not null)
        {
            HistoryMoreDeleteAllItem.IsEnabled = _history.Count > 0 && !busy;
        }
        if (DeleteSelectedHistoryButton is not null)
        {
            DeleteSelectedHistoryButton.IsEnabled = _selectedHistoryIds.Count > 0 && !busy;
        }
        if (HistorySelectAllButton is not null)
        {
            var allSelected = _lastMatchingHistoryEntries.Count > 0
                && _lastMatchingHistoryEntries.All(entry => _selectedHistoryIds.Contains(entry.Id));
            HistorySelectAllButton.IsEnabled = _lastMatchingHistoryEntries.Count > 0 && !busy;
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

    private void UpdateHistoryStatusCards()
    {
        if (HistoryEngineStatusText is not null && _settings is not null)
        {
            HistoryEngineStatusText.Text = EnginePolishPresentation
                .DescribeEngine(_settings.Preferences.Dictation.FinalEngine)
                .Name;
        }
        if (HistoryPolishStatusText is not null && _settings is not null)
        {
            var providerIndex = PolishProviderIndex(_settings.Preferences.Polish.Provider);
            HistoryPolishStatusText.Text = providerIndex > 0 && providerIndex < PolishProviderChoices.Length
                ? PolishProviderChoices[providerIndex].Name
                : "Off";
        }
        if (HistoryDetailReadyHotkeyText is not null && _settings is not null)
        {
            HistoryDetailReadyHotkeyText.Text = $"Keybind: {_settings.Preferences.Dictation.PushToTalkGesture}";
        }
        PopulateHistoryHelpItems();
    }

    private void UpdateHistoryListVisibility(string query, int itemCount)
    {
        var hasItems = itemCount > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var hasFilters = hasQuery
            || _historyKindFilter != HistoryKindFilter.All
            || _historyDateRangeFilter != HistoryDateRangeFilter.AnyTime;
        var showFilteredEmpty = !hasItems && (_history.Count > 0 && hasFilters || hasQuery);
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
            HistoryList.Items.Count);
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

    public sealed class HistoryItemViewModel : INotifyPropertyChanged
    {
        private readonly DictationHistoryEntry _entry;
        private bool _isSelectionMode;
        private bool _isSelected;
        private bool _isTranscriptExpanded;
        private bool _isShowingEdits;
        private bool _isShowingOriginal;
        private bool _hasOverflow;
        private bool _isGlobalBusy;
        private bool _isRepolishingThisCard;
        private bool _isPrimaryPasteTarget;

        public HistoryItemViewModel(
            DictationHistoryEntry entry,
            HistoryDayHeader? dayHeader,
            int dayEntryCount,
            bool isSelectionMode,
            bool isSelected,
            bool isTranscriptExpanded,
            bool isShowingEdits,
            bool isShowingOriginal,
            bool isGlobalBusy,
            bool isRepolishingThisCard,
            bool isPrimaryPasteTarget)
        {
            _entry = entry;
            _isSelectionMode = isSelectionMode;
            _isSelected = isSelected;
            _isTranscriptExpanded = isTranscriptExpanded;
            _isShowingEdits = isShowingEdits;
            _isShowingOriginal = isShowingOriginal;
            _isGlobalBusy = isGlobalBusy;
            _isRepolishingThisCard = isRepolishingThisCard;
            _isPrimaryPasteTarget = isPrimaryPasteTarget;

            Id = entry.Id;
            Text = entry.Text;
            var localCreated = entry.CreatedAt.ToLocalTime();
            CreatedDisplay = localCreated.ToString("f", CultureInfo.CurrentCulture);
            TimeDisplay = localCreated.ToString("t", CultureInfo.CurrentCulture);
            IsTemporary = entry.ExpiresAt is not null;
            HasPolishEdits = HistoryDiaryPresentation.HasPolishEdits(entry);

            DayHeaderTitle = dayHeader?.PrimaryTitle ?? string.Empty;
            DayHeaderSubtitle = dayHeader?.SecondaryDateLabel ?? string.Empty;
            DayCountText = HistoryDiaryPresentation.FormatItemCount(dayEntryCount);
            DayHeaderVisibility = dayHeader is not null ? Visibility.Visible : Visibility.Collapsed;
            DayHeaderSubtitleVisibility = !string.IsNullOrEmpty(DayHeaderSubtitle)
                ? Visibility.Visible
                : Visibility.Collapsed;

            FileChipVisibility = HistoryPresenter.IsFileTranscription(entry) ? Visibility.Visible : Visibility.Collapsed;
            RecoveryChipVisibility = IsTemporary ? Visibility.Visible : Visibility.Collapsed;
            RecoveryChipText = entry.ExpiresAt is { } expires
                ? FormatRecoveryExpiryChip(expires, localCreated)
                : string.Empty;
            HeldSafelyChipVisibility = !entry.WasDelivered ? Visibility.Visible : Visibility.Collapsed;

            ProvenanceText = BuildProvenanceText(entry);
            _hasOverflow = ShouldOfferShowMore(DisplayText);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid Id { get; }

        public string Text { get; }

        public string CreatedDisplay { get; }

        public string TimeDisplay { get; }

        public bool IsTemporary { get; }

        public bool HasPolishEdits { get; }

        public string DayHeaderTitle { get; }

        public string DayHeaderSubtitle { get; }

        public string DayCountText { get; }

        public Visibility DayHeaderVisibility { get; }

        public Visibility DayHeaderSubtitleVisibility { get; }

        public Visibility FileChipVisibility { get; }

        public Visibility RecoveryChipVisibility { get; }

        public string RecoveryChipText { get; }

        public Visibility HeldSafelyChipVisibility { get; }

        public string ProvenanceText { get; }

        public Visibility KeepButtonVisibility => IsTemporary ? Visibility.Visible : Visibility.Collapsed;

        public Visibility HasPolishEditsVisibility => HasPolishEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SelectionCheckBoxVisibility => _isSelectionMode ? Visibility.Visible : Visibility.Collapsed;

        public bool IsSelected => _isSelected;

        public string SelectionCheckBoxLabel => $"Select dictation from {TimeDisplay}";

        public Visibility SelectedAccentVisibility =>
            _isSelectionMode && _isSelected ? Visibility.Visible : Visibility.Collapsed;

        public string ShowEditsButtonText => _isShowingEdits ? "Hide edits" : "Show edits";

        public Visibility ShowEditsPanelVisibility =>
            HasPolishEdits && _isShowingEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PolishedActiveVisibility => !_isShowingOriginal ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PolishedInactiveVisibility => _isShowingOriginal ? Visibility.Visible : Visibility.Collapsed;

        public Visibility OriginalActiveVisibility => _isShowingOriginal ? Visibility.Visible : Visibility.Collapsed;

        public Visibility OriginalInactiveVisibility => !_isShowingOriginal ? Visibility.Visible : Visibility.Collapsed;

        public bool IsShowingOriginalView => _isShowingOriginal;

        public string ComparisonStatusText =>
            _isShowingOriginal ? "Speech engine words before AI Polish" : "AI-polished transcript";

        public string ComparisonOtherHeader =>
            _isShowingOriginal ? "AI-polished version" : "Before AI Polish (speech engine output)";

        public string CopyAlternateButtonLabel =>
            _isShowingOriginal ? "Copy polished" : "Copy original";

        public string ComparisonOtherText =>
            (_isShowingOriginal ? _entry.Text : _entry.OriginalText) ?? string.Empty;

        public string DisplayText =>
            HasPolishEdits && _isShowingEdits && _isShowingOriginal && !string.IsNullOrWhiteSpace(_entry.OriginalText)
                ? _entry.OriginalText
                : _entry.Text;

        public int TranscriptMaxLines => _isTranscriptExpanded || (_isShowingEdits && HasPolishEdits) ? 0 : 4;

        public Visibility ShowMoreToggleVisibility =>
            _hasOverflow && !(_isShowingEdits && HasPolishEdits) ? Visibility.Visible : Visibility.Collapsed;

        public string ShowMoreButtonText => _isTranscriptExpanded ? "Show less" : "Show more";

        public bool IsRepolishing => _isRepolishingThisCard;

        public Visibility RepolishingVisibility => _isRepolishingThisCard ? Visibility.Visible : Visibility.Collapsed;

        public bool CanRunCardActions => !_isGlobalBusy;

        public bool CanRepolish => !_isGlobalBusy && !IsTemporary;

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
                nameof(SelectedAccentVisibility));
        }

        public void SetShowingEdits(bool isShowingEdits)
        {
            if (_isShowingEdits == isShowingEdits)
            {
                return;
            }

            _isShowingEdits = isShowingEdits;
            _hasOverflow = _hasOverflow || ShouldOfferShowMore(DisplayText);
            Notify(
                nameof(ShowEditsButtonText),
                nameof(ShowEditsPanelVisibility),
                nameof(DisplayText),
                nameof(TranscriptMaxLines),
                nameof(ShowMoreToggleVisibility));
        }

        public void SetShowingOriginal(bool isShowingOriginal)
        {
            if (_isShowingOriginal == isShowingOriginal)
            {
                return;
            }

            _isShowingOriginal = isShowingOriginal;
            _hasOverflow = _hasOverflow || ShouldOfferShowMore(DisplayText);
            Notify(
                nameof(PolishedActiveVisibility),
                nameof(PolishedInactiveVisibility),
                nameof(OriginalActiveVisibility),
                nameof(OriginalInactiveVisibility),
                nameof(ComparisonStatusText),
                nameof(ComparisonOtherHeader),
                nameof(CopyAlternateButtonLabel),
                nameof(ComparisonOtherText),
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

        private static string FormatRecoveryExpiryChip(DateTimeOffset expiresAt, DateTimeOffset createdLocal)
        {
            var localExpiry = expiresAt.ToLocalTime();
            var pattern = DateOnly.FromDateTime(localExpiry.DateTime) == DateOnly.FromDateTime(DateTime.Now)
                ? "t"
                : "g";
            return $"Recovery · expires {localExpiry.ToString(pattern, CultureInfo.CurrentCulture)}";
        }

        private static string BuildProvenanceText(DictationHistoryEntry entry)
        {
            var engineName = HistoryDiaryPresentation.FormatEngineDisplayName(entry.EngineId);
            var parts = new List<string>(3)
            {
                engineName,
            };

            if (entry.WasPolished)
            {
                var polishName = HistoryDiaryPresentation.FormatPolishModelLabel(entry.PolishModelId)
                    ?? HistoryDiaryPresentation.FormatPolishProviderLabel(entry.PolishProviderId);
                parts.Add(string.IsNullOrWhiteSpace(polishName) ? "AI Polish" : polishName);
            }
            else
            {
                parts.Add("Deterministic cleanup");
            }

            if (!string.IsNullOrWhiteSpace(entry.LanguageCode))
            {
                parts.Add(LanguageCatalog.DisplayNameFor(entry.LanguageCode));
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
