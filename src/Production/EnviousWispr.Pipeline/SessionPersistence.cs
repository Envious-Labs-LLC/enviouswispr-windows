using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

/// <summary>Why a dictation is being written to history, said once, instead of three booleans.</summary>
/// <param name="WasPolished">The text is the polished one rather than the deterministic one.</param>
/// <param name="WasDelivered">The text reached the app it was started in.</param>
/// <param name="ExpiresAt">When the entry ages out on its own, or null for the ordinary retention.</param>
/// <param name="Force">Write even when history is switched off - an Escape Recovery has nowhere else to go.</param>
public sealed record HistoryWriteIntent(
    bool WasPolished,
    bool WasDelivered,
    DateTimeOffset? ExpiresAt = null,
    bool Force = false)
{
    /// <summary>The ordinary case: the words went where they were meant to go, or the clipboard caught them.</summary>
    public static HistoryWriteIntent Delivered(bool wasPolished, bool wasDelivered) => new(wasPolished, wasDelivered);

    /// <summary>Nothing was delivered; the text is held for the person to copy.</summary>
    public static HistoryWriteIntent Held(bool wasPolished) => new(wasPolished, WasDelivered: false);

    /// <summary>
    /// An Escape Recovery: kept for a day whether or not history is on, because the person asked for
    /// the words to be kept and this is the only place they can be.
    /// </summary>
    public static HistoryWriteIntent EscapeRecovery(bool wasPolished, DateTimeOffset expiresAt) =>
        new(wasPolished, WasDelivered: false, expiresAt, Force: true);
}

/// <summary>The one-shot Undo an Escape Recovery offers on Home, held in memory for this run only.</summary>
/// <param name="SessionId">The take the recovery copy on Home belongs to; the offer stands only beside that copy.</param>
/// <param name="EntryId">The History entry the take was saved as. Undo reads the words from it again at the press.</param>
/// <param name="Target">
/// The window and field the take was aimed at, frozen when the key went down: Undo puts the words back there.
/// Never written to disk - a window handle means nothing to the next launch.
/// </param>
public sealed record EscapeRecoveryUndoOffer(
    DictationSessionId SessionId,
    Guid EntryId,
    TargetWindowId Target);

/// <summary>What the shell shows when persistence changes what the person should see.</summary>
public interface ISessionPersistenceEffects
{
    /// <summary>Recovered text is waiting: bring the window forward and show it.</summary>
    /// <param name="undoOffered">The copy is an Escape Recovery whose one-shot Undo still stands.</param>
    void ShowPendingRecovery(RecoveryTextRecord record, bool undoOffered);

    /// <summary>The recovered text is gone; the screen should stop offering it.</summary>
    void ClearRecoveredText();

    void NotifyHistoryChanged();
}

/// <summary>
/// Owns what is written for a dictation beyond its delivery: the recovery copy that survives a crash,
/// the pending-recovery state the next press must respect, and the history entry.
/// </summary>
/// <remarks>
/// THE RECOVERY COPY IS WRITTEN BEFORE POLISH AND AGAIN AFTER, so a crash inside a slow provider costs
/// nobody their words. Whether it can be written at all is decided by the admission that let the
/// recording start: on a nearly full disk the record is held in memory, offered on screen, and the
/// log says why it was not saved. The in-memory pending state is the truth the next press checks; the
/// file is its backup.
///
/// HISTORY IS OPTIONAL AND ESCAPE RECOVERY IS NOT. A history switched off is honoured for every
/// ordinary dictation; an Escape Recovery is forced through with a day's expiry because the person
/// asked for those words to be kept and history is the only place they can go.
/// </remarks>
/// <summary>What the executor asks of the persistence owner: whether recovered text is waiting, whether a copy may be written, and to show what is pending.</summary>
public interface ISessionRecoveryState
{
    /// <summary>Recovered text from an earlier run is still waiting on Home.</summary>
    bool HasPendingRecovery { get; }

    /// <summary>Whether a recovery copy may be written; false when the disk is too low, decided at admission.</summary>
    bool CanPersistRecovery { get; set; }

    /// <summary>Shows whatever recovery text is pending, if any.</summary>
    void ShowPendingRecovery();
}

public sealed class SessionPersistence : ISessionRecoveryState
{
    private readonly IRecoveryTextStore _recovery;
    private readonly IHistoryStore _history;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly Func<HistoryPreferences> _historyPreferences;
    private readonly ISessionPersistenceEffects _effects;

    /// <param name="historyPreferences">Read at each write, so a switch flipped mid-session takes effect on the next entry.</param>
    public SessionPersistence(
        IRecoveryTextStore recovery,
        IHistoryStore history,
        IAppLogger logger,
        TimeProvider clock,
        Func<HistoryPreferences> historyPreferences,
        ISessionPersistenceEffects effects)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(historyPreferences);
        ArgumentNullException.ThrowIfNull(effects);
        _recovery = recovery;
        _history = history;
        _logger = logger;
        _clock = clock;
        _historyPreferences = historyPreferences;
        _effects = effects;
    }

    /// <summary>Recovered text is waiting; a new recording must not start over it.</summary>
    public bool HasPendingRecovery { get; private set; }

    /// <summary>The record behind <see cref="HasPendingRecovery"/>, when one is in memory.</summary>
    public RecoveryTextRecord? PendingRecord { get; private set; }

    /// <summary>Whether the recording under way may write its recovery copy to disk; set by admission.</summary>
    public bool CanPersistRecovery { get; set; } = true;

    private EscapeRecoveryUndoOffer? _undoOffer;

    /// <summary>The Escape Recovery Undo that still stands beside the recovery copy on Home, or null.</summary>
    /// <remarks>
    /// ONLY BESIDE ITS OWN COPY. The offer names the take it belongs to and is answered only while the
    /// pending copy is that take's: a copy replaced, cleared or forgotten takes the offer with it, so
    /// Home can never show an Undo for words other than the ones it shows.
    /// </remarks>
    public EscapeRecoveryUndoOffer? UndoOffer
    {
        get
        {
            var offer = Volatile.Read(ref _undoOffer);
            return offer is not null && PendingRecord?.SessionId == offer.SessionId ? offer : null;
        }
    }

    /// <summary>Stands the one-shot Undo beside the take's recovery copy; refused when the copy on Home is another take's.</summary>
    public bool OfferUndo(EscapeRecoveryUndoOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (PendingRecord?.SessionId != offer.SessionId)
        {
            return false;
        }

        Volatile.Write(ref _undoOffer, offer);
        return true;
    }

    /// <summary>Takes the Undo, once: a second caller, or one after the copy moved on, gets null.</summary>
    /// <remarks>
    /// ONE-SHOT BY AN ATOMIC EXCHANGE, as the macOS pill's is by its `acted` flag: a double click, or a
    /// click racing another, restores once. The second restore would land after the first has already
    /// moved the person's caret.
    /// </remarks>
    public EscapeRecoveryUndoOffer? TakeUndoOffer()
    {
        var offer = Interlocked.Exchange(ref _undoOffer, null);
        return offer is not null && PendingRecord?.SessionId == offer.SessionId ? offer : null;
    }

    /// <summary>What the store held when the app started.</summary>
    public void AdoptStartupRecovery(RecoveryTextLoadResult recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        HasPendingRecovery = recovery.Status == RecoveryTextLoadStatus.Found;
        PendingRecord = recovery.Record;
        Volatile.Write(ref _undoOffer, null);
    }

    /// <summary>The person dealt with the recovered text on screen; nothing is pending any more.</summary>
    public void ForgetPendingRecovery()
    {
        HasPendingRecovery = false;
        PendingRecord = null;
        Volatile.Write(ref _undoOffer, null);
    }

    public async Task SaveRecoveryTextAsync(ProcessedText text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            return;
        }

        if (Volatile.Read(ref _undoOffer) is { } standing && standing.SessionId != text.SessionId)
        {
            Volatile.Write(ref _undoOffer, null);
        }

        var record = new RecoveryTextRecord(text.SessionId, _clock.GetUtcNow(), text.Text);
        PendingRecord = record;
        HasPendingRecovery = true;
        if (!CanPersistRecovery)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.RecoveryTextUnavailable,
                AppFailureCategory.ResourcePressure,
                ErrorCode: AppErrorCode.LowDiskSpace));
            return;
        }

        var saved = await _recovery.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        _logger.Write(new AppLogEntry(
            _clock.GetUtcNow(),
            saved ? AppEventCode.RecoveryTextSaved : AppEventCode.RecoveryTextUnavailable,
            saved ? AppFailureCategory.None : AppFailureCategory.Recovery,
            ErrorCode: saved ? null : AppErrorCode.StorageUnavailable));
    }

    public async Task ClearRecoveryTextAsync()
    {
        if (!await _recovery.ClearAsync().ConfigureAwait(false))
        {
            // THE PENDING STATE STAYS. A clear that failed leaves the file where it was, and the next
            // launch will find it; saying the text is gone while the file says otherwise would offer
            // the person a recovery they cannot see and a file they cannot explain.
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.RecoveryTextUnavailable,
                AppFailureCategory.Recovery,
                ErrorCode: AppErrorCode.StorageUnavailable));
            return;
        }

        PendingRecord = null;
        HasPendingRecovery = false;
        Volatile.Write(ref _undoOffer, null);
        _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.RecoveryTextCleared));
        _effects.ClearRecoveredText();
    }

    /// <summary>Clears the recovery copy only while it is still <paramref name="sessionId"/>'s; true when it was cleared.</summary>
    /// <remarks>
    /// THE WORDS WENT BACK, SO HOME STOPS HOLDING THEM. Used after an Undo or a History paste of the same
    /// take landed or reached the clipboard: the copy on Home has done its job and the next recording
    /// may start. The History entry is not touched - its 24 hours run on, as macOS keeps a restored
    /// row pending. A copy that is by now another take's is left alone.
    /// </remarks>
    public async Task<bool> ClearRecoveryTextForAsync(DictationSessionId sessionId)
    {
        // The clear's log line belongs to the take whose copy it was.
        using var dictation = DictationScope.Begin(sessionId.Value);
        if (PendingRecord?.SessionId != sessionId)
        {
            return false;
        }

        await ClearRecoveryTextAsync().ConfigureAwait(false);
        return PendingRecord is null;
    }

    /// <summary>Offers the pending record on screen, if there is one.</summary>
    public void ShowPendingRecovery()
    {
        if (PendingRecord is { } record)
        {
            _effects.ShowPendingRecovery(record, UndoOffer is not null);
        }
    }

    /// <summary>Writes the entry, or nothing; the id of the entry the store accepted, or null.</summary>
    /// <remarks>
    /// THE ID IS RETURNED SO AN ESCAPE RECOVERY CAN OFFER UNDO ONLY FOR WORDS THAT ARE DURABLY SAVED.
    /// macOS never offers a restore for a take that was not written; null here means there is none.
    /// </remarks>
    public async Task<Guid?> SaveHistoryAsync(Transcript transcript, string text, HistoryWriteIntent intent)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(intent);
        var preferences = _historyPreferences();
        if ((!preferences.IsEnabled && !intent.Force) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // TWO READS OF THE CLOCK, AS THE SHELL MADE THEM. The entry's own timestamp and the moment the
        // store prunes against are separate samples; collapsing them moves a retention decision that
        // lands exactly on an expiry boundary, and equivalence here means the same decisions.
        var entry = DictationHistoryEntry.Create(
            _clock.GetUtcNow(),
            text,
            transcript.EngineId,
            intent.WasPolished,
            intent.WasDelivered,
            intent.ExpiresAt);
        var result = await _history.AddAsync(
            entry,
            preferences.RetentionDays,
            _clock.GetUtcNow()).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        _effects.NotifyHistoryChanged();
        return entry.Id;
    }
}
