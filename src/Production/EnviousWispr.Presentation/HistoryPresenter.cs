using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>What the history page's summary line is about.</summary>
public enum HistorySummary
{
    /// <summary>The local file is invalid; the source was preserved for recovery.</summary>
    Invalid,

    /// <summary>Windows could not open the private history file.</summary>
    Unavailable,

    /// <summary>History is off; new dictations will not be saved.</summary>
    Off,

    /// <summary>Nothing saved yet.</summary>
    Empty,

    /// <summary>Some number of local dictations, in <see cref="HistoryView.Entries"/>.</summary>
    Saved,

    /// <summary>The presentation is closing; nothing was read, and the page is not redrawn.</summary>
    Closing,
}

/// <summary>Which kind of history entries to display: All, live Dictation, or Transcribe a File.</summary>
public enum HistoryKindFilter
{
    All,
    Dictation,
    TranscribeFile,
}

/// <summary>What the history page shows after a load: the rows, and the one line about them.</summary>
public sealed record HistoryView(HistoryLoadStatus Status, IReadOnlyList<DictationHistoryEntry> Entries, HistorySummary Summary)
{
    public static HistoryView Loading { get; } = new(HistoryLoadStatus.Missing, [], HistorySummary.Empty);

    /// <summary>The load was refused because the presentation is closing; the page shows nothing new.</summary>
    public static HistoryView Closing { get; } = new(HistoryLoadStatus.Missing, [], HistorySummary.Closing);
}

/// <summary>How a history command went, and the page that follows it when it worked.</summary>
/// <param name="View">The reloaded rows after a command that changed the file; null when nothing changed.</param>
/// <param name="Closing">True when the command was refused or stopped because the presentation is closing; the page says nothing.</param>
public sealed record HistoryCommandResult(bool Succeeded, HistoryView? View, bool Closing = false)
{
    public static HistoryCommandResult Untouched { get; } = new(false, null);

    public static HistoryCommandResult Refused { get; } = new(false, null, Closing: true);
}

/// <summary>How a repolish command went, and the page that follows it when it worked.</summary>
public sealed record HistoryRepolishCommandResult(
    HistoryRepolishResult Result,
    HistoryView? View = null)
{
    public bool Succeeded => Result.Succeeded;
    public HistoryRepolishStatus Status => Result.Status;
    public DictationHistoryEntry? Entry => Result.Entry;
    public bool CopiedToClipboard => Result.CopiedToClipboard;
    public bool Closing => Result.Status == HistoryRepolishStatus.Closing;
}

/// <summary>The history page's decisions about its stores, without the page.</summary>
/// <remarks>
/// THE PAGE KEEPS THE CONFIRMATIONS, THE CLIPBOARD, THE LIST AND THE ANNOUNCEMENTS. What moves here
/// is what happens to the stores and what the page is shown afterwards: a command that failed leaves
/// the rows as they were and says so; one that worked reloads, so the rows are what the file now
/// holds rather than what the page remembers. The retention window and the clock are the page's
/// preference and this presenter's clock, read at the moment of the load.
///
/// EVERY STORE CALL IS MADE INSIDE THE PRESENTATION'S GATE. The exit disposes the history store once
/// the presentation has closed; a load or a command that was inside the store at that moment would
/// fault, so each takes a lease first and runs under the closing token, and one that arrives after
/// the close, or is stopped by it, answers <see cref="HistorySummary.Closing"/> or
/// <see cref="HistoryCommandResult.Refused"/> without touching the store.
/// </remarks>
public sealed class HistoryPresenter
{
    private readonly IHistoryStore _history;
    private readonly IRecoveryTextStore _recovery;
    private readonly Func<HistoryPreferences> _preferences;
    private readonly PresentationAdmission _admission;
    private readonly TimeProvider _clock;
    private readonly IHistoryRepolishService? _repolishService;
    private readonly Action? _notifyHistoryChanged;

    public event Action? HistoryChanged;

    /// <param name="preferences">
    /// The history preferences in force - retention, and whether history is on - read at the moment
    /// of each load, so a preference saved between two loads governs the second.
    /// </param>
    /// <param name="admission">The presentation's gate; one of this presenter's own, never closed, when it stands alone.</param>
    public HistoryPresenter(
        IHistoryStore history,
        IRecoveryTextStore recovery,
        Func<HistoryPreferences> preferences,
        TimeProvider? clock = null,
        PresentationAdmission? admission = null,
        IHistoryRepolishService? repolishService = null,
        Action? notifyHistoryChanged = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(preferences);
        _history = history;
        _recovery = recovery;
        _preferences = preferences;
        _admission = admission ?? new PresentationAdmission();
        _clock = clock ?? TimeProvider.System;
        _repolishService = repolishService;
        _notifyHistoryChanged = notifyHistoryChanged;
    }

    /// <summary>Reads the file under the current retention window and says what the page shows.</summary>
    public async Task<HistoryView> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return HistoryView.Closing;
        }

        using (lease)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Closing);
            try
            {
                return await LoadInsideAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return HistoryView.Closing;
            }
        }
    }

    private async Task<HistoryView> LoadInsideAsync(CancellationToken cancellationToken)
    {
        var preferences = _preferences();
        var result = await _history
            .LoadAsync(preferences.RetentionDays, _clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        var summary = result.Status switch
        {
            HistoryLoadStatus.Invalid => HistorySummary.Invalid,
            HistoryLoadStatus.Unavailable => HistorySummary.Unavailable,
            _ when !preferences.IsEnabled => HistorySummary.Off,
            _ when result.Entries.Count == 0 => HistorySummary.Empty,
            _ => HistorySummary.Saved,
        };
        return new HistoryView(result.Status, result.Entries, summary);
    }

    /// <summary>The rows whose text contains the query, or all of them for a blank query.</summary>
    public static IReadOnlyList<DictationHistoryEntry> Filter(IReadOnlyList<DictationHistoryEntry> entries, string query) =>
        Filter(entries, query, HistoryKindFilter.All);

    /// <summary>The rows matching the text query and kind filter.</summary>
    public static IReadOnlyList<DictationHistoryEntry> Filter(
        IReadOnlyList<DictationHistoryEntry> entries,
        string query,
        HistoryKindFilter kind) =>
        Filter(entries, query, kind, HistoryDateRangeFilter.AnyTime, DateTimeOffset.Now);

    /// <summary>The rows matching the text query, kind filter, and local calendar date range.</summary>
    public static IReadOnlyList<DictationHistoryEntry> Filter(
        IReadOnlyList<DictationHistoryEntry> entries,
        string query,
        HistoryKindFilter kind,
        HistoryDateRangeFilter dateRange,
        DateTimeOffset localNow,
        TimeZoneInfo? localTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        var matchingKind = kind switch
        {
            HistoryKindFilter.Dictation => entries.Where(e => !IsFileTranscription(e)),
            HistoryKindFilter.TranscribeFile => entries.Where(IsFileTranscription),
            _ => entries,
        };

        var matchingDate = dateRange == HistoryDateRangeFilter.AnyTime
            ? matchingKind
            : matchingKind.Where(entry =>
                HistoryDiaryPresentation.MatchesDateRange(entry, dateRange, localNow, localTimeZone));

        var trimmed = query.Trim();
        return trimmed.Length == 0
            ? matchingDate.ToArray()
            : matchingDate.Where(entry => HistoryDiaryPresentation.MatchesSearch(entry, trimmed)).ToArray();
    }

    /// <summary>Whether a history entry was produced by Transcribe a File.</summary>
    public static bool IsFileTranscription(DictationHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.EngineId.Contains("file", StringComparison.OrdinalIgnoreCase) ||
               entry.EngineId.Contains("transcribe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes one dictation. A refusal leaves the rows as they were.</summary>
    public Task<HistoryCommandResult> DeleteAsync(Guid id) => ApplyAsync(token => _history.DeleteAsync(id, token));

    /// <summary>Keeps a temporary recovery entry: its 24-hour expiry is removed.</summary>
    public Task<HistoryCommandResult> KeepAsync(Guid id) => ApplyAsync(token => _history.KeepAsync(id, token));

    /// <summary>Removes every matching dictation ID. The page has already confirmed.</summary>
    public Task<HistoryCommandResult> DeleteManyAsync(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ApplyAsync(token => _history.DeleteManyAsync(ids, token));
    }

    /// <summary>Removes every dictation. The page has already asked; this does not ask again.</summary>
    public Task<HistoryCommandResult> ClearAsync() => ApplyAsync(token => _history.ClearAsync(token));

    /// <summary>Re-polishes an existing history entry with the active polish provider.</summary>
    public async Task<HistoryRepolishCommandResult> RepolishAsync(
        Guid id,
        bool copyToClipboard = false,
        CancellationToken cancellationToken = default)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return new HistoryRepolishCommandResult(HistoryRepolishResult.ClosingResult());
        }

        using (lease)
        {
            try
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Closing);
                if (_repolishService is null)
                {
                    return new HistoryRepolishCommandResult(HistoryRepolishResult.NoProvider());
                }

                var repolishResult = await _repolishService
                    .RepolishAsync(id, copyToClipboard, stop.Token)
                    .ConfigureAwait(false);

                if (!repolishResult.Succeeded)
                {
                    return new HistoryRepolishCommandResult(repolishResult);
                }

                var reloadedView = await LoadInsideAsync(stop.Token).ConfigureAwait(false);
                _notifyHistoryChanged?.Invoke();
                HistoryChanged?.Invoke();

                return new HistoryRepolishCommandResult(repolishResult, reloadedView);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return new HistoryRepolishCommandResult(HistoryRepolishResult.ClosingResult());
            }
        }
    }

    /// <summary>Re-polishes an existing history entry with the active polish provider.</summary>
    public async Task<HistoryRepolishCommandResult> RepolishAsync(
        DictationHistoryEntry entry,
        bool copyToClipboard = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_admission.TryEnter(out var lease))
        {
            return new HistoryRepolishCommandResult(HistoryRepolishResult.ClosingResult());
        }

        using (lease)
        {
            try
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Closing);
                if (_repolishService is null)
                {
                    return new HistoryRepolishCommandResult(HistoryRepolishResult.NoProvider());
                }

                var repolishResult = await _repolishService
                    .RepolishAsync(entry, copyToClipboard, stop.Token)
                    .ConfigureAwait(false);

                if (!repolishResult.Succeeded)
                {
                    return new HistoryRepolishCommandResult(repolishResult);
                }

                var reloadedView = await LoadInsideAsync(stop.Token).ConfigureAwait(false);
                _notifyHistoryChanged?.Invoke();
                HistoryChanged?.Invoke();

                return new HistoryRepolishCommandResult(repolishResult, reloadedView);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return new HistoryRepolishCommandResult(HistoryRepolishResult.ClosingResult());
            }
        }
    }

    /// <summary>Removes the encrypted recovery copy. The page has already asked.</summary>
    /// <returns>True when the copy is gone; false when Windows left the file untouched, or the presentation is closing.</returns>
    public async Task<bool> DeleteRecoveryAsync()
    {
        if (!_admission.TryEnter(out var lease))
        {
            return false;
        }

        using (lease)
        {
            try
            {
                return await _recovery.ClearAsync(lease.Closing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private async Task<HistoryCommandResult> ApplyAsync(Func<CancellationToken, Task<HistoryOperationResult>> command)
    {
        if (!_admission.TryEnter(out var lease))
        {
            return HistoryCommandResult.Refused;
        }

        using (lease)
        {
            try
            {
                var result = await command(lease.Closing).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return HistoryCommandResult.Untouched;
                }

                // RELOADED, NOT EDITED IN PLACE. The store applies retention and ordering of its own;
                // the page shows what the file holds now, which is the only answer that cannot drift
                // from it. Loaded under this command's lease, so the close cannot fall between them.
                return new HistoryCommandResult(true, await LoadInsideAsync(lease.Closing).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                return HistoryCommandResult.Refused;
            }
        }
    }
}
