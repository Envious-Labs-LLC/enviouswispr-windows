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

namespace EnviousWispr.App;

/// <summary>Dictation history diary, search, filtering, re-polish, playback, and recovery card UI.</summary>
public sealed partial class MainWindow
{
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
                ClearOnboardingReliabilityNotice();
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
            ClearOnboardingReliabilityNotice();
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

}
