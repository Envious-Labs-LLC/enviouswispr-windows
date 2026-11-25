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
            ShowMessage(
                "Interrupted dictation recovered",
                "Review or copy the private recovery text on History. It was not pasted automatically.",
                InfoBarSeverity.Warning);
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

    private void PasteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to paste.", InfoBarSeverity.Informational);
            return;
        }

        HistoryPasteRequested?.Invoke(selected.Id);
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistoryView();

    private void ClearHistorySearchButton_Click(object sender, RoutedEventArgs e) =>
        HistorySearchBox.Text = string.Empty;

    private void CopyHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to copy.", InfoBarSeverity.Informational);
            return;
        }

        var package = new DataPackage();
        package.SetText(selected.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage("Copied", "The selected dictation is on your clipboard.", InfoBarSeverity.Success);
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

    private async void RepolishHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRepolishing)
        {
            return;
        }

        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to re-polish.", InfoBarSeverity.Informational);
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
        if (RepolishHistoryProgress is not null)
        {
            RepolishHistoryProgress.IsActive = true;
            RepolishHistoryProgress.Visibility = Visibility.Visible;
        }
        UpdateSelectionDependentButtons();

        try
        {
            var result = await _session.History.RepolishAsync(selected.Id).ConfigureAwait(true);
            if (result.Status == HistoryRepolishStatus.Closing || _session.Closing)
            {
                return;
            }

            if (result.Succeeded)
            {
                if (result.View is not null)
                {
                    ShowHistory(result.View);
                    if (HistoryList.ItemsSource is HistoryItemViewModel[] items)
                    {
                        var reselected = items.FirstOrDefault(i => i.Id == selected.Id);
                        if (reselected is not null)
                        {
                            HistoryList.SelectedItem = reselected;
                        }
                    }
                }
                if (result.Entry is not null)
                {
                    HistoryDetailTextBox.Text = result.Entry.Text;
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
            if (RepolishHistoryProgress is not null)
            {
                RepolishHistoryProgress.IsActive = false;
                RepolishHistoryProgress.Visibility = Visibility.Collapsed;
            }
            UpdateSelectionDependentButtons();
        }
    }

    private async void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the history entry you want to delete.", InfoBarSeverity.Informational);
            return;
        }

        var result = await _session.History.DeleteAsync(selected.Id).ConfigureAwait(true);
        if (result.Closing || _session.Closing)
        {
            return;
        }

        if (result.Succeeded)
        {
            BeginHistoryReload();
            ShowHistory(result.View!);
            ShowMessage("History entry deleted", "The local copy was removed.", InfoBarSeverity.Success);
        }
        else
        {
            ShowMessage("History could not be changed", "The private history file was left untouched.", InfoBarSeverity.Error);
        }
    }

    private async void KeepHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItemViewModel selected)
        {
            ShowMessage("Select a dictation first", "Choose the temporary recovery entry you want to keep.", InfoBarSeverity.Informational);
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

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
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
        _isHistoryLoading = false;
        RefreshHistoryView();
    }

    private HistoryKindFilter _historyKindFilter = HistoryKindFilter.All;

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

        UpdateHistoryFilterSegmentButtons();
        RefreshHistoryView();
    }

    private void UpdateHistoryFilterSegmentButtons()
    {
        ApplySegmentButtonState(HistoryFilterAllButton, _historyKindFilter == HistoryKindFilter.All);
        ApplySegmentButtonState(HistoryFilterDictationButton, _historyKindFilter == HistoryKindFilter.Dictation);
        ApplySegmentButtonState(HistoryFilterTranscribeButton, _historyKindFilter == HistoryKindFilter.TranscribeFile);
    }

    private void RefreshHistoryView()
    {
        var previousSelectedId = (HistoryList.SelectedItem as HistoryItemViewModel)?.Id;
        var query = HistorySearchBox.Text.Trim();
        var visibleHistory = HistoryPresenter.Filter(_history, query, _historyKindFilter)
            .Select(entry => new HistoryItemViewModel(entry))
            .ToArray();
        HistoryList.ItemsSource = visibleHistory;
        if (HistoryCountText is not null)
        {
            HistoryCountText.Text = visibleHistory.Length.ToString("N0", CultureInfo.CurrentCulture);
        }
        if (previousSelectedId is { } id)
        {
            HistoryList.SelectedItem = visibleHistory.FirstOrDefault(item => item.Id == id);
        }

        UpdateHistoryListVisibility(query, visibleHistory.Length);
        UpdateSelectionDependentButtons();
        UpdateHistoryStatusCards();
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
    }

    private void UpdateHistoryListVisibility(string query, int itemCount)
    {
        var hasItems = itemCount > 0;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var historyUnavailable = _historyLoadStatus is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable;
        ShowCard(HistoryLoadingState, _isHistoryLoading);
        ShowCard(HistoryList, !_isHistoryLoading && hasItems);
        ShowCard(HistoryEmptyState, !_isHistoryLoading && !hasItems && !hasQuery && !historyUnavailable);
        ShowCard(HistorySearchEmptyState, !_isHistoryLoading && !hasItems && hasQuery && !historyUnavailable);

        ShowCard(HistoryUnavailableState, !_isHistoryLoading && !hasItems && historyUnavailable);

        // ANNOUNCED AFTER EVERY CARD HAS ITS VISIBILITY, NOT BEFORE. A collapsed element is not in
        // the layout, so raising while the unavailable card was still hidden was refused by the very
        // ancestor check that stops announcements from pages nobody is looking at - and the one
        // state a person most needs told about was the one that stayed silent.
        //
        // AND ONLY WHEN THE ANSWER CHANGED. This runs on every refresh and every search keystroke,
        // and the card titles are fixed text, so nothing else stops "No matching dictations" being
        // spoken again on each letter typed. The key carries the count too, because a list that grew
        // is news even though it is still a loaded list.
        // THE CARDS GET THEIR WORDS BEFORE ANYTHING IS ANNOUNCED, because the announcement reads what
        // is on screen and a card still carrying the previous search's text would be read out.
        if (!hasItems && hasQuery && !historyUnavailable)
        {
            HistorySearchEmptyDescription.Text = $"No saved dictations match “{query}”. Try another search or clear it.";
        }

        if (!hasItems && historyUnavailable)
        {
            HistoryUnavailableDescription.Text = _historyLoadStatus == HistoryLoadStatus.Invalid
                ? "The local history file is invalid. It was left untouched for recovery."
                : "Windows could not open the private history file. It was left untouched.";
        }

        // THE QUERY IS PART OF THE ANSWER. "No matching dictations" for one search and for a
        // different search are two different facts, and a key without the query announced only the
        // first of them. The same is true of a count that happens to repeat under a new search.
        var normalisedQuery = query.Trim().ToLowerInvariant();
        var terminal = _isHistoryLoading
            ? null
            : historyUnavailable ? "unavailable"
            : hasItems ? $"loaded:{itemCount}:{normalisedQuery}"
            : hasQuery ? $"no-matches:{normalisedQuery}"
            : "empty";

        // A SEARCH ANNOUNCES WHEN THE TYPING STOPS, NOT PER LETTER. Putting the query in the key
        // makes every keystroke a new answer, which without this would restore exactly the
        // every-keystroke repetition the key was added to remove. A result the user did not type
        // their way to - a finished load, a cleared search - is not delayed.
        _pendingHistoryAnnouncement = terminal;
        if (hasQuery && terminal is not null)
        {
            _historyAnnounceDebounce.Stop();
            _historyAnnounceDebounce.Start();
            return;
        }

        _historyAnnounceDebounce.Stop();
        AnnounceHistoryStateIfChanged(terminal, historyUnavailable, hasItems, hasQuery, itemCount);
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
            // CLEARED WHILE LOADING, so an explicit refresh that lands on the same answer still says
            // so. Without this, pressing Refresh and getting the same twelve dictations is silent.
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

            // RECORDED ONLY IF IT WAS ACTUALLY SPOKEN. Remembering a state the ancestor check
            // refused would swallow the announcement for good once the page is opened.
            if (announced)
            {
                _lastAnnouncedHistoryState = terminal;
            }
        }
    }

    /// <summary>Announces the history outcome when the page becomes visible.</summary>
    /// <remarks>
    /// A RESULT THAT ARRIVED WHILE YOU WERE ELSEWHERE IS STILL NEWS WHEN YOU GET HERE. History loads
    /// at startup, so on any other page the announcement is refused by the ancestor check and, with
    /// nothing to re-run it, never happened at all - the page that most needed a spoken result was
    /// the one that never gave one.
    /// </remarks>
    private void OnHistoryAnnounceDue(object? sender, object e)
    {
        _historyAnnounceDebounce.Stop();
        AnnounceHistoryOnPageShown();
    }

    /// <summary>Says whatever history result is still waiting, if anything can hear it now.</summary>
    public void AnnouncePendingHistoryState() => AnnounceHistoryOnPageShown();

    private void AnnounceHistoryOnPageShown()
    {
        // A HIDDEN WINDOW IS NOT A VISIBLE PAGE, AND THE XAML TREE CANNOT SEE THE DIFFERENCE. Closing
        // this window hides it to the notification area without collapsing anything inside it, so the
        // ancestor check still reports every parent as showing and a debounce landing afterwards
        // would announce from a window nobody can see. The pending announcement is KEPT, so it
        // arrives when the window is opened again rather than being lost.
        if (_isHistoryLoading || _pendingHistoryAnnouncement is null || !AppWindow.IsVisible)
        {
            return;
        }

        var query = HistorySearchBox.Text.Trim();
        AnnounceHistoryStateIfChanged(
            _pendingHistoryAnnouncement,
            _historyLoadStatus is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable,
            HistoryList.Visibility == Visibility.Visible,
            !string.IsNullOrWhiteSpace(query),
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
    /// <remarks>
    /// THE COUNT GOES ON THE NAME BEFORE THE RAISE, because a live region announces what it is
    /// called, and a list's own contents are not a sentence anybody wants read out on every refresh.
    /// </remarks>
    private bool AnnounceHistoryCount(int itemCount)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HistoryList,
            $"Transcript history, {itemCount} dictation{(itemCount == 1 ? string.Empty : "s")}.");
        return AnnounceLiveRegion(HistoryList);
    }

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(DictationHistoryEntry entry)
        {
            Id = entry.Id;
            Text = entry.Text;
            CreatedDisplay = entry.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
            IsTemporary = entry.ExpiresAt is not null;
            TagText = IsTemporary
                ? "Recovery"
                : HistoryPresenter.IsFileTranscription(entry)
                    ? "File"
                    : string.Empty;
            TagVisibility = string.IsNullOrEmpty(TagText) ? Visibility.Collapsed : Visibility.Visible;
            var retention = entry.ExpiresAt is null
                ? string.Empty
                : $" · Escape Recovery expires {entry.ExpiresAt.Value.ToLocalTime():g} unless kept";
            Details = $"{TranscriptionEngineNames.DisplayName(entry.EngineId)} · {(entry.WasPolished ? "AI polished" : "deterministic cleanup")} · {(entry.WasDelivered ? "delivered" : "held safely")}{retention}";
        }

        public Guid Id { get; }

        public string Text { get; }

        public string CreatedDisplay { get; }

        public string Details { get; }

        public string TagText { get; }

        public Visibility TagVisibility { get; }

        public bool IsTemporary { get; }

        /// <summary>
        /// What a screen reader announces for this row.
        /// </summary>
        /// <remarks>
        /// Reads as a sentence rather than a field dump: the point is what a person HEARS, so a
        /// visual separator like a middle dot is not good enough - a screen reader either
        /// announces it literally or drops it, and neither is a word.
        ///
        /// Without this a list row falls back to ToString on the bound item, and a plain class
        /// returns its fully-qualified type name - so the row would announce
        /// "EnviousWispr.App.MainWindow+HistoryItemViewModel" before reaching the dictation. The
        /// two other list types had the record version of the same defect, measured on the running
        /// app; this one is fixed from the same reading rather than waiting for history to exist.
        /// </remarks>
        public override string ToString() => $"{Text}, dictated {CreatedDisplay}";
    }
}
