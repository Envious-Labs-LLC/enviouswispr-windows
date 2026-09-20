using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
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

/// <summary>What the shell shows when persistence changes what the person should see.</summary>
public interface ISessionPersistenceEffects
{
    /// <summary>Recovered text is waiting: bring the window forward and show it.</summary>
    void ShowPendingRecovery(RecoveryTextRecord record);

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
public sealed class SessionPersistence
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

    /// <summary>What the store held when the app started.</summary>
    public void AdoptStartupRecovery(RecoveryTextLoadResult recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        HasPendingRecovery = recovery.Status == RecoveryTextLoadStatus.Found;
        PendingRecord = recovery.Record;
    }

    /// <summary>The person dealt with the recovered text on screen; nothing is pending any more.</summary>
    public void ForgetPendingRecovery()
    {
        HasPendingRecovery = false;
        PendingRecord = null;
    }

    public async Task SaveRecoveryTextAsync(ProcessedText text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            return;
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
        _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.RecoveryTextCleared));
        _effects.ClearRecoveredText();
    }

    /// <summary>Offers the pending record on screen, if there is one.</summary>
    public void ShowPendingRecovery()
    {
        if (PendingRecord is { } record)
        {
            _effects.ShowPendingRecovery(record);
        }
    }

    public async Task SaveHistoryAsync(Transcript transcript, string text, HistoryWriteIntent intent)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(intent);
        var preferences = _historyPreferences();
        if ((!preferences.IsEnabled && !intent.Force) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var result = await _history.AddAsync(
            DictationHistoryEntry.Create(
                now,
                text,
                transcript.EngineId,
                intent.WasPolished,
                intent.WasDelivered,
                intent.ExpiresAt),
            preferences.RetentionDays,
            now).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _effects.NotifyHistoryChanged();
        }
    }
}
