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
}

/// <summary>What the history page shows after a load: the rows, and the one line about them.</summary>
public sealed record HistoryView(HistoryLoadStatus Status, IReadOnlyList<DictationHistoryEntry> Entries, HistorySummary Summary)
{
    public static HistoryView Loading { get; } = new(HistoryLoadStatus.Missing, [], HistorySummary.Empty);
}

/// <summary>How a history command went, and the page that follows it when it worked.</summary>
/// <param name="View">The reloaded rows after a command that changed the file; null when nothing changed.</param>
public sealed record HistoryCommandResult(bool Succeeded, HistoryView? View)
{
    public static HistoryCommandResult Untouched { get; } = new(false, null);
}

/// <summary>The history page's decisions about its stores, without the page.</summary>
/// <remarks>
/// THE PAGE KEEPS THE CONFIRMATIONS, THE CLIPBOARD, THE LIST AND THE ANNOUNCEMENTS. What moves here
/// is what happens to the stores and what the page is shown afterwards: a command that failed leaves
/// the rows as they were and says so; one that worked reloads, so the rows are what the file now
/// holds rather than what the page remembers. The retention window and the clock are the page's
/// preference and this presenter's clock, read at the moment of the load.
/// </remarks>
public sealed class HistoryPresenter
{
    private readonly IHistoryStore _history;
    private readonly IRecoveryTextStore _recovery;
    private readonly Func<HistoryPreferences> _preferences;
    private readonly TimeProvider _clock;

    /// <param name="preferences">
    /// The history preferences in force - retention, and whether history is on - read at the moment
    /// of each load, so a preference saved between two loads governs the second.
    /// </param>
    public HistoryPresenter(IHistoryStore history, IRecoveryTextStore recovery, Func<HistoryPreferences> preferences, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(preferences);
        _history = history;
        _recovery = recovery;
        _preferences = preferences;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads the file under the current retention window and says what the page shows.</summary>
    public async Task<HistoryView> LoadAsync(CancellationToken cancellationToken = default)
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
    public static IReadOnlyList<DictationHistoryEntry> Filter(IReadOnlyList<DictationHistoryEntry> entries, string query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        var trimmed = query.Trim();
        return trimmed.Length == 0
            ? entries
            : entries.Where(entry => entry.Text.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    /// <summary>Removes one dictation. A refusal leaves the rows as they were.</summary>
    public Task<HistoryCommandResult> DeleteAsync(Guid id) => ApplyAsync(() => _history.DeleteAsync(id));

    /// <summary>Keeps a temporary recovery entry: its 24-hour expiry is removed.</summary>
    public Task<HistoryCommandResult> KeepAsync(Guid id) => ApplyAsync(() => _history.KeepAsync(id));

    /// <summary>Removes every dictation. The page has already asked; this does not ask again.</summary>
    public Task<HistoryCommandResult> ClearAsync() => ApplyAsync(() => _history.ClearAsync());

    /// <summary>Removes the encrypted recovery copy. The page has already asked.</summary>
    /// <returns>True when the copy is gone; false when Windows left the file untouched.</returns>
    public Task<bool> DeleteRecoveryAsync() => _recovery.ClearAsync();

    private async Task<HistoryCommandResult> ApplyAsync(Func<Task<HistoryOperationResult>> command)
    {
        var result = await command().ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return HistoryCommandResult.Untouched;
        }

        // RELOADED, NOT EDITED IN PLACE. The store applies retention and ordering of its own; the
        // page shows what the file holds now, which is the only answer that cannot drift from it.
        return new HistoryCommandResult(true, await LoadAsync().ConfigureAwait(false));
    }
}
