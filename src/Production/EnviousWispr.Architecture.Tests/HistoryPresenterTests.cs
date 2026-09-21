using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The history page's store decisions without the page: a failed delete preserves the rows, Keep
/// removes the expiry, a load reads the retention in force, a cancelled confirmation changes
/// nothing, and a recovery deletion says whether the copy is gone.
/// </summary>
public sealed class HistoryPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ALoadReadsTheRetentionInForceAndSaysWhatThePageShows()
    {
        var (presenter, history, _, preferences) = Build();
        history.Rows.Add(Entry("hello"));
        history.Rows.Add(Entry("world"));

        var view = await presenter.LoadAsync();

        Assert.Equal(HistoryLoadStatus.Loaded, view.Status);
        Assert.Equal(2, view.Entries.Count);
        Assert.Equal(HistorySummary.Saved, view.Summary);
        Assert.Equal((30, Now), history.LastLoad);

        // A preference saved between two loads governs the second.
        preferences.Current = new HistoryPreferences(IsEnabled: true, RetentionDays: 7);
        await presenter.LoadAsync();
        Assert.Equal((7, Now), history.LastLoad);
    }

    [Theory]
    [InlineData(HistoryLoadStatus.Invalid, true, 1, HistorySummary.Invalid)]
    [InlineData(HistoryLoadStatus.Unavailable, true, 1, HistorySummary.Unavailable)]
    [InlineData(HistoryLoadStatus.Loaded, false, 1, HistorySummary.Off)]
    [InlineData(HistoryLoadStatus.Loaded, true, 0, HistorySummary.Empty)]
    [InlineData(HistoryLoadStatus.Missing, true, 0, HistorySummary.Empty)]
    public async Task TheSummaryNamesTheStateInTheOrderTheShellHad(HistoryLoadStatus status, bool enabled, int rows, HistorySummary expected)
    {
        var (presenter, history, _, preferences) = Build();
        history.Status = status;
        preferences.Current = new HistoryPreferences(enabled, RetentionDays: 30);
        for (var i = 0; i < rows; i++)
        {
            history.Rows.Add(Entry($"row {i}"));
        }

        var view = await presenter.LoadAsync();

        Assert.Equal(expected, view.Summary);
    }

    [Fact]
    public async Task AFailedDeletePreservesTheRowsAndSaysSo()
    {
        var (presenter, history, _, _) = Build();
        var kept = Entry("keep me");
        history.Rows.Add(kept);
        history.RefuseChanges = true;

        var result = await presenter.DeleteAsync(kept.Id);

        Assert.False(result.Succeeded);
        Assert.Null(result.View);
        Assert.Single(history.Rows);
        Assert.Equal(0, history.Loads);
    }

    [Fact]
    public async Task ADeleteThatWorkedShowsWhatTheFileHoldsNow()
    {
        var (presenter, history, _, _) = Build();
        var gone = Entry("gone");
        var stays = Entry("stays");
        history.Rows.Add(gone);
        history.Rows.Add(stays);

        var result = await presenter.DeleteAsync(gone.Id);

        Assert.True(result.Succeeded);
        Assert.Equal([stays.Id], result.View!.Entries.Select(entry => entry.Id));
        Assert.Equal(HistorySummary.Saved, result.View.Summary);
    }

    [Fact]
    public async Task KeepRemovesTheExpiryAndShowsTheRowWithoutIt()
    {
        var (presenter, history, _, _) = Build();
        var temporary = Entry("temporary") with { ExpiresAt = Now.AddHours(24) };
        history.Rows.Add(temporary);

        var result = await presenter.KeepAsync(temporary.Id);

        Assert.True(result.Succeeded);
        var row = Assert.Single(result.View!.Entries);
        Assert.Null(row.ExpiresAt);
        Assert.Null(history.Rows.Single().ExpiresAt);
    }

    [Fact]
    public async Task ClearRemovesEverythingAndShowsAnEmptyPage()
    {
        var (presenter, history, _, _) = Build();
        history.Rows.Add(Entry("one"));
        history.Rows.Add(Entry("two"));

        var result = await presenter.ClearAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.View!.Entries);
        Assert.Equal(HistorySummary.Empty, result.View.Summary);
        Assert.Empty(history.Rows);
    }

    [Fact]
    public async Task AClearTheStoreRefusesLeavesEveryRow()
    {
        var (presenter, history, _, _) = Build();
        history.Rows.Add(Entry("one"));
        history.RefuseChanges = true;

        var result = await presenter.ClearAsync();

        Assert.False(result.Succeeded);
        Assert.Single(history.Rows);
    }

    [Fact]
    public async Task ARefusedKeepPreservesTheRowAndDoesNotReload()
    {
        var (presenter, history, _, _) = Build();
        var temporary = Entry("temporary") with { ExpiresAt = Now.AddHours(24) };
        history.Rows.Add(temporary);
        history.RefuseChanges = true;

        var result = await presenter.KeepAsync(temporary.Id);

        Assert.False(result.Succeeded);
        Assert.Null(result.View);
        Assert.NotNull(history.Rows.Single().ExpiresAt);
        Assert.Equal(0, history.Loads);
    }

    /// <summary>The page tells the app the recovery copy is gone only when the presenter says it is.</summary>
    /// <remarks>
    /// THE PAGE ASKS AND THE PAGE NOTIFIES; THE PRESENTER CLEARS. The confirmation dialog and the
    /// notice to the app both stay in the window, so the wiring is checked at the source: the notice
    /// sits inside the branch the presenter's true answer selects, and nowhere else in the handler.
    /// </remarks>
    [Fact]
    public void TheRecoveryDeletionHandlerNotifiesTheAppOnlyWhenTheCopyIsGone()
    {
        var window = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        var start = window.IndexOf("private async void DeleteRecoveryButton_Click(", StringComparison.Ordinal);
        Assert.True(start >= 0, "The recovery deletion handler is gone.");
        var end = window.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        var handler = window[start..end];

        var asked = handler.IndexOf("if (await _session.History.DeleteRecoveryAsync()", StringComparison.Ordinal);
        var refused = handler.IndexOf("\n        else\n", StringComparison.Ordinal);
        var notified = handler.IndexOf("RecoveryCleared?.Invoke();", StringComparison.Ordinal);
        Assert.True(asked >= 0, "The handler does not ask the presenter to delete the copy.");
        Assert.True(refused > asked, "The handler has no branch for a copy Windows left untouched.");
        Assert.True(notified > asked && notified < refused, "The app is not told inside the branch where the copy is gone.");
        Assert.Equal(notified, handler.LastIndexOf("RecoveryCleared?.Invoke();", StringComparison.Ordinal));
        Assert.Contains("ContentDialogResult.Primary", handler[..asked], StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    [Fact]
    public async Task RecoveryDeletionSaysWhetherTheCopyIsGone()
    {
        var (presenter, _, recovery, _) = Build();

        Assert.True(await presenter.DeleteRecoveryAsync());
        Assert.Equal(1, recovery.Clears);

        recovery.Refuse = true;
        Assert.False(await presenter.DeleteRecoveryAsync());
    }

    [Fact]
    public void TheFilterMatchesTextWithoutCaseAndABlankQueryKeepsEverything()
    {
        var rows = new[] { Entry("Hello World"), Entry("goodbye") };

        Assert.Equal(2, HistoryPresenter.Filter(rows, "   ").Count);
        Assert.Equal([rows[0].Id], HistoryPresenter.Filter(rows, "WORLD").Select(entry => entry.Id));
        Assert.Empty(HistoryPresenter.Filter(rows, "nothing"));
    }

    private static DictationHistoryEntry Entry(string text) =>
        new(Guid.NewGuid(), Now, text, "whisper", WasPolished: false, WasDelivered: true);

    private static (HistoryPresenter Presenter, FakeHistoryStore History, FakeRecoveryStore Recovery, PreferenceSource Preferences) Build()
    {
        var history = new FakeHistoryStore();
        var recovery = new FakeRecoveryStore();
        var preferences = new PreferenceSource();
        var presenter = new HistoryPresenter(history, recovery, () => preferences.Current, new FrozenClock(Now));
        return (presenter, history, recovery, preferences);
    }

    private sealed class PreferenceSource
    {
        public HistoryPreferences Current { get; set; } = new(IsEnabled: true, RetentionDays: 30);
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Rows { get; } = [];

        public HistoryLoadStatus Status { get; set; } = HistoryLoadStatus.Loaded;

        public bool RefuseChanges { get; set; }

        public int Changes { get; private set; }

        public (int RetentionDays, DateTimeOffset Now) LastLoad { get; private set; }

        public int Loads { get; private set; }

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Loads++;
            LastLoad = (retentionDays, now);
            return Task.FromResult(new HistoryLoadResult([.. Rows], Status));
        }

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Change(() => Rows.RemoveAll(row => row.Id == id));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) =>
            Change(() =>
            {
                var index = Rows.FindIndex(row => row.Id == id);
                Rows[index] = Rows[index] with { ExpiresAt = null };
            });

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
            Change(Rows.Clear);

        private Task<HistoryOperationResult> Change(Action change)
        {
            Changes++;
            if (RefuseChanges)
            {
                return Task.FromResult(new HistoryOperationResult(false));
            }

            change();
            return Task.FromResult(new HistoryOperationResult(true));
        }
    }

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public int Clears { get; private set; }

        public bool Refuse { get; set; }

        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            Clears++;
            return Task.FromResult(!Refuse);
        }
    }
}
