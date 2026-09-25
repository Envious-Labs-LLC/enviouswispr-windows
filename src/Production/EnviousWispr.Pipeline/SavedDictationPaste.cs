using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Pipeline;

/// <summary>Which door a saved dictation is being pasted back through.</summary>
public enum SavedDictationPasteAction
{
    /// <summary>Home's one-shot Undo after an Escape Recovery: back into the window and field the take was aimed at.</summary>
    Undo,

    /// <summary>History's Paste: into the window the person was working in before they came to EnviousWispr.</summary>
    HistoryPaste,
}

/// <summary>How one paste of a saved dictation ended - a closed set, so the log can say it without the words.</summary>
public enum SavedDictationPasteOutcome
{
    /// <summary>The words were written into the target by one of the delivery's routes.</summary>
    Pasted,

    /// <summary>
    /// The target refused the paste, or was not the field the words belong in, or (Undo) no field was named at the
    /// key press to return to; the clipboard caught them.
    /// </summary>
    KeptOnClipboard,

    /// <summary>
    /// The write may or may not have landed: a direct write that could not be verified, or a commit that threw after
    /// it began. Nothing is retried and Home keeps its copy, because a second paste could put the words in twice.
    /// </summary>
    MayHavePasted,

    /// <summary>Undo only: no offer stands - it was already used, or the copy on Home is not that take's any more.</summary>
    NotOffered,

    /// <summary>The History entry was deleted or had expired by the press. Nothing is pasted from a copy the person was told had gone.</summary>
    NoLongerAvailable,

    /// <summary>A dictation is recording or being delivered; it owns the target and the clipboard next.</summary>
    DictationInProgress,

    /// <summary>Another saved-dictation paste is still running. Two at once would race for one clipboard.</summary>
    Busy,

    /// <summary>Nothing to paste into, or it has gone.</summary>
    NoTarget,

    /// <summary>The delivery wrote nothing and the clipboard did not catch the words either.</summary>
    Failed,
}

public sealed record SavedDictationPasteResult(
    SavedDictationPasteAction Action,
    SavedDictationPasteOutcome Outcome,
    DeliveryResult? Delivery = null);

/// <summary>Everything the paste touches, handed in so a test drives it without a desktop.</summary>
/// <param name="LoadHistory">The store's current entries.</param>
/// <param name="Now">The clock the 24-hour expiry is read against.</param>
/// <param name="IsDictationActive">True while a take is recording, finalising or being delivered.</param>
/// <param name="Delivery">
/// A delivery of its OWN over the shared adapter, as Paste Last Dictation has: `ContextAwareTextDelivery`
/// keeps one recovery slot, and sharing the dictation's would overwrite a take's recovery text.
/// </param>
/// <param name="DeliveryOptions">The dictation's delivery options, read at each use (the clipboard restore).</param>
/// <param name="Reacquire">Brings back a window remembered without its field and names that field, or null.</param>
/// <param name="Recovery">The owner of the copy on Home and of the one-shot Undo that stands beside it.</param>
public sealed record SavedDictationPasteEnvironment(
    Func<CancellationToken, Task<IReadOnlyList<DictationHistoryEntry>>> LoadHistory,
    Func<DateTimeOffset> Now,
    Func<bool> IsDictationActive,
    ITextDelivery Delivery,
    Func<TextDeliveryOptions> DeliveryOptions,
    Func<TargetWindowId, CancellationToken, Task<TargetWindowId?>> Reacquire,
    SessionPersistence Recovery);

/// <summary>Home's Undo and History's Paste: a saved dictation pasted back through the dictation's own delivery.</summary>
/// <remarks>
/// Ported from macOS `EscapeRecoveryPasteAction` and History's Paste (#2087). What each does, from that source:
/// <list type="bullet">
/// <item>UNDO is the Escape Recovery pill's one action. It re-reads the saved entry BY ID at the press - a copy
/// that lapsed between the offer and the press is never pasted, because that would hand back words the person
/// was told had gone - then returns to the app and field the take was aimed at and pastes. A field that cannot
/// be reached gets no keystroke: the words go to the clipboard instead ("clipboardOnly"), never into whatever
/// is in front. It is ONE-SHOT: a double click restores once.</item>
/// <item>HISTORY'S PASTE puts an entry's words into the app the person is using now. On Windows that is the
/// last window they were working in before EnviousWispr's own, which the foreground history keeps - the same
/// answer the tray's Paste Last Dictation uses.</item>
/// <item>NEITHER CHANGES THE HISTORY ENTRY. A restored Escape Recovery keeps its 24-hour expiry until Keep, as
/// macOS keeps a restored row pending. What leaves the pending state is Home's copy: once the words have
/// landed or reached the clipboard, Home stops holding them and the next recording may start.</item>
/// </list>
/// Where Windows differs, and why: the paste goes through the dictation's own delivery - three routes, caret
/// repair, the clipboard borrowed and given back - not a bare clipboard write and Ctrl+V, for the reason
/// `LastDictationReuse` gives. And the target is named, never "whatever is in front": macOS hides itself so
/// its Cmd+V lands in the next app, while this delivery addresses the target window itself and brings it
/// forward, so EnviousWispr's window can stay open behind it.
/// </remarks>
public sealed class SavedDictationPaste
{
    private readonly SavedDictationPasteEnvironment _environment;

    /// <summary>1 while a paste is running. A flag, not a lock: a second paste is refused, never waited for.</summary>
    private int _running;

    public SavedDictationPaste(SavedDictationPasteEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>The entry is one a person may still paste: present, not expired, and not blank.</summary>
    public static bool IsPasteable(DictationHistoryEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (entry.ExpiresAt is null || entry.ExpiresAt > now) && !string.IsNullOrWhiteSpace(entry.Text);
    }

    /// <summary>Home's one-shot Undo: the offer's entry, back where the take was aimed.</summary>
    public async Task<SavedDictationPasteResult> UndoAsync(CancellationToken cancellationToken = default)
    {
        const SavedDictationPasteAction action = SavedDictationPasteAction.Undo;
        if (_environment.IsDictationActive())
        {
            return new(action, SavedDictationPasteOutcome.DictationInProgress);
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new(action, SavedDictationPasteOutcome.Busy);
        }

        try
        {
            // TAKEN BEFORE ANYTHING IS AWAITED, so a second press - or one racing it - finds nothing.
            if (_environment.Recovery.TakeUndoOffer() is not { } offer)
            {
                return new(action, SavedDictationPasteOutcome.NotOffered);
            }

            var entry = await FindAsync(offer.EntryId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return new(action, SavedDictationPasteOutcome.NoLongerAvailable);
            }

            // A TAKE THAT HAD NO WINDOW IN FRONT HAS NOWHERE TO GO BACK TO, and a guess would be whatever
            // is in front now - EnviousWispr itself. Refused; Copy on Home still has the words.
            if (!offer.Target.IsValid)
            {
                return new(action, SavedDictationPasteOutcome.NoTarget);
            }

            // A TAKE THAT FROZE NO FIELD HAS NO FIELD TO GO BACK TO. Reading whichever field has the focus now
            // would put the words wherever the caret happens to be, which is exactly what macOS refuses when it
            // cannot refocus the original field: it stops at the clipboard. So does this - a requested copy
            // through the same delivery, and no keystroke. History's Paste reacquires by design; Undo does not.
            var clipboardOnly = offer.Target.FocusedElementId is null;

            if (_environment.IsDictationActive())
            {
                // Put back: nothing was pasted, and the person may press again once the dictation ends.
                _environment.Recovery.OfferUndo(offer);
                return new(action, SavedDictationPasteOutcome.DictationInProgress);
            }

            // THE OWN-WINDOW RULE IS THE DICTATION'S, NOT A NEW ONE. Ordinary delivery refuses only a target that
            // is not valid; it never asks whose window it is, so a take dictated into one of EnviousWispr's own
            // fields is delivered there. Undo returns the words to where that take was aimed, under the same rule
            // and the same delivery checks (window alive, same process, integrity, the field's id).
            var result = await DeliverAsync(action, entry, offer.Target, clipboardOnly, cancellationToken).ConfigureAwait(false);
            await SettleHomeCopyAsync(result, offer.SessionId).ConfigureAwait(false);
            return result;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>History's Paste: the entry the person selected, into the window they were working in.</summary>
    /// <param name="target">The window the caller named at the click - the last one really used before EnviousWispr's.</param>
    public async Task<SavedDictationPasteResult> PasteAsync(
        Guid entryId,
        TargetWindowId? target,
        CancellationToken cancellationToken = default)
    {
        const SavedDictationPasteAction action = SavedDictationPasteAction.HistoryPaste;
        if (_environment.IsDictationActive())
        {
            return new(action, SavedDictationPasteOutcome.DictationInProgress);
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new(action, SavedDictationPasteOutcome.Busy);
        }

        try
        {
            // NO OWN-WINDOW RULE OF ITS OWN: the dictation's delivery has none, and this applies exactly that one.
            // The target comes from the foreground history, which never records EnviousWispr's windows anyway.
            if (target is null || !target.Value.IsValid)
            {
                return new(action, SavedDictationPasteOutcome.NoTarget);
            }

            var entry = await FindAsync(entryId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return new(action, SavedDictationPasteOutcome.NoLongerAvailable);
            }

            if (_environment.IsDictationActive())
            {
                return new(action, SavedDictationPasteOutcome.DictationInProgress);
            }

            var aimed = target.Value;
            if (aimed.FocusedElementId is null)
            {
                if (await _environment.Reacquire(aimed, cancellationToken).ConfigureAwait(false) is not { } reacquired)
                {
                    return new(action, SavedDictationPasteOutcome.NoTarget);
                }

                aimed = reacquired;
                if (_environment.IsDictationActive())
                {
                    return new(action, SavedDictationPasteOutcome.DictationInProgress);
                }
            }

            var result = await DeliverAsync(action, entry, aimed, clipboardOnly: false, cancellationToken).ConfigureAwait(false);

            // THE SAME TAKE AS THE COPY ON HOME: its words went back through History instead, so Home stops
            // holding them and the Undo beside them is spent. Any other entry leaves Home as it was.
            if (_environment.Recovery.UndoOffer is { } offer && offer.EntryId == entry.Id &&
                result.Outcome is SavedDictationPasteOutcome.Pasted or SavedDictationPasteOutcome.KeptOnClipboard &&
                _environment.Recovery.TakeUndoOffer() is { } taken)
            {
                await SettleHomeCopyAsync(result, taken.SessionId).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<DictationHistoryEntry?> FindAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entries = await _environment.LoadHistory(cancellationToken).ConfigureAwait(false);
        var now = _environment.Now();
        foreach (var entry in entries)
        {
            if (entry.Id == entryId)
            {
                return IsPasteable(entry, now) ? entry : null;
            }
        }

        return null;
    }

    private async Task<SavedDictationPasteResult> DeliverAsync(
        SavedDictationPasteAction action,
        DictationHistoryEntry entry,
        TargetWindowId aimed,
        bool clipboardOnly,
        CancellationToken cancellationToken)
    {
        var options = _environment.DeliveryOptions() with
        {
            // The person asked for a paste; the dictation's copy-instead setting does not turn it into a copy.
            // Only an Undo with no field to return to is a copy, and that is this code's choice, not a setting.
            CopyInsteadOfPaste = clipboardOnly,
        };
        var delivery = await _environment.Delivery.DeliverAsync(
            new TextDeliveryRequest(
                new ProcessedText(DictationSessionId.Create(), entry.Text),
                aimed,
                LanguageCode: null,
                options,
                // A History entry does not record whether a snippet fired, so its words take the ordinary route.
                SnippetExpanded: false),
            cancellationToken).ConfigureAwait(false);
        var outcome = delivery switch
        {
            { ClipboardFallback: true } => SavedDictationPasteOutcome.KeptOnClipboard,
            // A requested copy "delivers" to the clipboard: the words are there and nothing was typed.
            { Delivered: true } when clipboardOnly => SavedDictationPasteOutcome.KeptOnClipboard,
            { Delivered: true } => SavedDictationPasteOutcome.Pasted,
            _ when MayHaveLanded(delivery) => SavedDictationPasteOutcome.MayHavePasted,
            _ => SavedDictationPasteOutcome.Failed,
        };
        return new(action, outcome, delivery);
    }

    /// <summary>A delivery that stopped where the words may already be in the field.</summary>
    /// <remarks>
    /// Two ways, both named by the delivery itself: a direct write whose read-back did not match (issued, effect
    /// unknown), and an exception out of the commit (the stage where text is written). Every other refusal comes
    /// before anything is written. The caller's own cancellation is not here: it is the app leaving, and no result
    /// is shown for it.
    /// </remarks>
    internal static bool MayHaveLanded(DeliveryResult delivery) =>
        delivery.RefusalReason == TextDeliveryRefusalReason.DirectWriteUnverified ||
        delivery.Fault?.Stage == DeliveryStage.Commit;

    /// <summary>Once the words landed or reached the clipboard, Home stops holding that take's copy.</summary>
    private Task SettleHomeCopyAsync(SavedDictationPasteResult result, DictationSessionId sessionId) =>
        result.Outcome is SavedDictationPasteOutcome.Pasted or SavedDictationPasteOutcome.KeptOnClipboard
            ? _environment.Recovery.ClearRecoveryTextForAsync(sessionId)
            : Task.CompletedTask;
}

/// <summary>The log's name for each ending, so no two endings share a line.</summary>
public static class SavedDictationPasteDiagnostics
{
    public static AppEventCode EventFor(SavedDictationPasteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var undo = result.Action == SavedDictationPasteAction.Undo;
        return result.Outcome switch
        {
            SavedDictationPasteOutcome.Pasted => undo ? AppEventCode.EscapeRecoveryUndoPasted : AppEventCode.HistoryEntryPasted,
            SavedDictationPasteOutcome.KeptOnClipboard => undo
                ? AppEventCode.EscapeRecoveryUndoKeptOnClipboard
                : AppEventCode.HistoryEntryKeptOnClipboard,
            SavedDictationPasteOutcome.NotOffered => AppEventCode.SavedDictationUndoNotOffered,
            SavedDictationPasteOutcome.NoLongerAvailable => AppEventCode.SavedDictationNoLongerAvailable,
            SavedDictationPasteOutcome.DictationInProgress => AppEventCode.SavedDictationDeclinedDictationInProgress,
            SavedDictationPasteOutcome.Busy => AppEventCode.SavedDictationDeclinedBusy,
            SavedDictationPasteOutcome.NoTarget => AppEventCode.SavedDictationDeclinedNoTarget,
            SavedDictationPasteOutcome.MayHavePasted => AppEventCode.SavedDictationMayHavePasted,
            SavedDictationPasteOutcome.Failed => AppEventCode.SavedDictationPasteFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, null),
        };
    }
}
