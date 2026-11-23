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

        var asked = handler.IndexOf("var recoveryDeleted = await _session.History.DeleteRecoveryAsync()", StringComparison.Ordinal);
        var gone = handler.IndexOf("if (recoveryDeleted)", StringComparison.Ordinal);
        var refused = handler.IndexOf("\n        else\n", StringComparison.Ordinal);
        var notified = handler.IndexOf("RecoveryCleared?.Invoke();", StringComparison.Ordinal);
        Assert.True(asked >= 0, "The handler does not ask the presenter to delete the copy.");
        Assert.True(gone > asked, "The handler has no branch for a copy that is gone.");
        // A LATE ANSWER IS NOT DRAWN: the exit's refusal comes back false too, and the handler
        // leaves before it could say the file was left untouched.
        Assert.Contains("if (_session.Closing)", handler[asked..gone], StringComparison.Ordinal);
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

    [Fact]
    public void TheFilterSeparatesDictationFromTranscribedFiles()
    {
        var dictation = new DictationHistoryEntry(Guid.NewGuid(), Now, "Dictated notes", "whisper", WasPolished: false, WasDelivered: true);
        var file = new DictationHistoryEntry(Guid.NewGuid(), Now, "Meeting audio file", "file-transcription", WasPolished: true, WasDelivered: true);
        var entries = new[] { dictation, file };

        Assert.Equal(2, HistoryPresenter.Filter(entries, string.Empty, HistoryKindFilter.All).Count);
        Assert.Equal([dictation.Id], HistoryPresenter.Filter(entries, string.Empty, HistoryKindFilter.Dictation).Select(e => e.Id));
        Assert.Equal([file.Id], HistoryPresenter.Filter(entries, string.Empty, HistoryKindFilter.TranscribeFile).Select(e => e.Id));

        // Combined with query
        Assert.Equal([dictation.Id], HistoryPresenter.Filter(entries, "notes", HistoryKindFilter.Dictation).Select(e => e.Id));
        Assert.Empty(HistoryPresenter.Filter(entries, "audio", HistoryKindFilter.Dictation));
        Assert.Equal([file.Id], HistoryPresenter.Filter(entries, "audio", HistoryKindFilter.TranscribeFile).Select(e => e.Id));
    }

    [Fact]
    public async Task ARepolishThatWorkedShowsReloadedRowsAndFiresHistoryChanged()
    {
        var row = Entry("spoken words");
        var repolishedEntry = row with { Text = "polished words", WasPolished = true, OriginalText = "spoken words" };
        var repolish = new FakeRepolishService((id, _) => HistoryRepolishResult.Success(repolishedEntry));
        var (presenter, history, _, _) = Build(repolish);
        history.Rows.Add(row);

        var changedFired = false;
        presenter.HistoryChanged += () => changedFired = true;

        var result = await presenter.RepolishAsync(row.Id);

        Assert.True(result.Succeeded);
        Assert.True(changedFired);
        Assert.NotNull(result.View);
        Assert.Equal(HistorySummary.Saved, result.View.Summary);
        Assert.Equal(1, history.Loads);
    }

    [Fact]
    public async Task ARefusedRepolishLeavesViewNullAndPreservesRows()
    {
        var row = Entry("spoken words");
        var repolish = new FakeRepolishService((_, _) => HistoryRepolishResult.Refused());
        var (presenter, history, _, _) = Build(repolish);
        history.Rows.Add(row);

        var changedFired = false;
        presenter.HistoryChanged += () => changedFired = true;

        var result = await presenter.RepolishAsync(row.Id);

        Assert.False(result.Succeeded);
        Assert.False(changedFired);
        Assert.Null(result.View);
        Assert.Equal(0, history.Loads);
        Assert.Single(history.Rows);
    }

    [Fact]
    public async Task RepolishWhileClosingRefusesCleanly()
    {
        var admission = new PresentationAdmission();
        var repolish = new FakeRepolishService((_, _) => HistoryRepolishResult.Success(Entry("never")));
        var (presenter, history, _, _) = Build(repolish, admission);
        history.Rows.Add(Entry("spoken"));

        await admission.CloseAsync();

        var result = await presenter.RepolishAsync(Guid.NewGuid());

        Assert.True(result.Closing);
        Assert.False(result.Succeeded);
        Assert.Null(result.View);
        Assert.Equal(0, history.Loads);
    }

    private sealed class FakeRepolishService(Func<Guid, bool, HistoryRepolishResult> answer) : IHistoryRepolishService
    {
        public Task<HistoryRepolishResult> RepolishAsync(Guid entryId, bool copyToClipboard = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(answer(entryId, copyToClipboard));

        public Task<HistoryRepolishResult> RepolishAsync(DictationHistoryEntry entry, bool copyToClipboard = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(answer(entry.Id, copyToClipboard));
    }

    private static DictationHistoryEntry Entry(string text) =>
        new(Guid.NewGuid(), Now, text, "whisper", WasPolished: false, WasDelivered: true);

    private static (HistoryPresenter Presenter, FakeHistoryStore History, FakeRecoveryStore Recovery, PreferenceSource Preferences) Build(
        IHistoryRepolishService? repolish = null,
        PresentationAdmission? admission = null)
    {
        var history = new FakeHistoryStore();
        var recovery = new FakeRecoveryStore();
        var preferences = new PreferenceSource();
        var presenter = new HistoryPresenter(
            history,
            recovery,
            () => preferences.Current,
            new FrozenClock(Now),
            admission,
            repolish);
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

        public Task<DictationHistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.FirstOrDefault(entry => entry.Id == id));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HistoryRepolishUpdateResult> UpdateRepolishedAsync(
            Guid id,
            string expectedCurrentText,
            string newText,
            bool wasPolished,
            string? providerId,
            string? modelId,
            CancellationToken cancellationToken = default)
        {
            Changes++;
            if (RefuseChanges)
            {
                return Task.FromResult(HistoryRepolishUpdateResult.Failed());
            }

            var index = Rows.FindIndex(row => row.Id == id);
            if (index < 0)
            {
                return Task.FromResult(HistoryRepolishUpdateResult.NotFound());
            }

            var existing = Rows[index];
            if (existing.ExpiresAt is { } expires && expires <= Now)
            {
                return Task.FromResult(HistoryRepolishUpdateResult.Expired());
            }

            if (!string.Equals(existing.Text, expectedCurrentText, StringComparison.Ordinal))
            {
                return Task.FromResult(HistoryRepolishUpdateResult.Stale());
            }

            var updated = existing with
            {
                Text = newText,
                WasPolished = wasPolished,
                OriginalText = existing.OriginalText ?? existing.Text,
                PolishProviderId = providerId,
                PolishModelId = modelId,
            };

            Rows[index] = updated;
            return Task.FromResult(HistoryRepolishUpdateResult.Updated(updated));
        }

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
