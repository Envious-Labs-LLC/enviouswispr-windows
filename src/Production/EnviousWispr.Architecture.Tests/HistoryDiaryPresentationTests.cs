using System.Globalization;
using System.IO;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.History;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class HistoryDiaryPresentationTests
{
    private static readonly TimeZoneInfo UtcZone = TimeZoneInfo.Utc;

    private static DictationHistoryEntry Entry(
        DateTimeOffset createdAt,
        string text,
        string engineId = "parakeet",
        bool wasPolished = false,
        string? originalText = null,
        string? polishModelId = null,
        bool wasDelivered = true,
        DateTimeOffset? expiresAt = null) =>
        new(
            Guid.NewGuid(),
            createdAt,
            text,
            engineId,
            wasPolished,
            wasDelivered,
            expiresAt,
            originalText,
            "en",
            wasPolished ? "eg_one" : null,
            polishModelId);

    [Fact]
    public void FilterAppliesDateRangeKindAndMultiFieldSearchTogether()
    {
        var now = new DateTimeOffset(2026, 11, 25, 16, 0, 0, TimeSpan.Zero);
        var todayEntry = Entry(
            now.AddHours(-2),
            "Meeting notes polished",
            engineId: "parakeet",
            wasPolished: true,
            originalText: "Raw rough words",
            polishModelId: "EG-1 Mini");
        var yesterdayFile = Entry(
            now.AddDays(-1).AddHours(-1),
            "Uploaded audio transcript",
            engineId: "transcribe_file");
        var weekAgo = Entry(
            now.AddDays(-6),
            "Quarterly planning",
            engineId: "whisper");
        var monthAgo = Entry(
            now.AddDays(-25),
            "Architecture draft",
            engineId: "parakeet");
        var oldEntry = Entry(
            now.AddDays(-45),
            "Archived transcript",
            engineId: "parakeet");

        DictationHistoryEntry[] entries = [todayEntry, yesterdayFile, weekAgo, monthAgo, oldEntry];

        Assert.Equal([todayEntry.Id], HistoryPresenter.Filter(entries, string.Empty, HistoryKindFilter.All, HistoryDateRangeFilter.Today, now, UtcZone).Select(e => e.Id));
        Assert.Equal([yesterdayFile.Id], HistoryPresenter.Filter(entries, string.Empty, HistoryKindFilter.All, HistoryDateRangeFilter.Yesterday, now, UtcZone).Select(e => e.Id));
        Assert.Equal(
            [todayEntry.Id, yesterdayFile.Id, weekAgo.Id],
            HistoryPresenter.Filter(entries, string.Empty, HistoryKindFilter.All, HistoryDateRangeFilter.Last7Days, now, UtcZone).Select(e => e.Id));
        Assert.Equal(
            [todayEntry.Id, yesterdayFile.Id, weekAgo.Id, monthAgo.Id],
            HistoryPresenter.Filter(entries, string.Empty, HistoryKindFilter.All, HistoryDateRangeFilter.Last30Days, now, UtcZone).Select(e => e.Id));

        // Multi-field search matches OriginalText, EngineId/DisplayName, and PolishModelId.
        Assert.Equal([todayEntry.Id], HistoryPresenter.Filter(entries, "rough words", HistoryKindFilter.Dictation, HistoryDateRangeFilter.Today, now, UtcZone).Select(e => e.Id));
        Assert.Equal([todayEntry.Id], HistoryPresenter.Filter(entries, "EG-1 Mini", HistoryKindFilter.All, HistoryDateRangeFilter.AnyTime, now, UtcZone).Select(e => e.Id));
        Assert.Equal([weekAgo.Id], HistoryPresenter.Filter(entries, "Whisper", HistoryKindFilter.All, HistoryDateRangeFilter.AnyTime, now, UtcZone).Select(e => e.Id));
        Assert.Equal([yesterdayFile.Id], HistoryPresenter.Filter(entries, "Uploaded", HistoryKindFilter.TranscribeFile, HistoryDateRangeFilter.Last7Days, now, UtcZone).Select(e => e.Id));
    }

    [Fact]
    public void HasPolishEditsDetectsMeaningfulOriginalDifferencesOnly()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(HistoryDiaryPresentation.HasPolishEdits(Entry(now, "hello", wasPolished: false, originalText: "raw")));
        Assert.False(HistoryDiaryPresentation.HasPolishEdits(Entry(now, "hello", wasPolished: true, originalText: null)));
        Assert.False(HistoryDiaryPresentation.HasPolishEdits(Entry(now, "hello", wasPolished: true, originalText: "   ")));
        Assert.False(HistoryDiaryPresentation.HasPolishEdits(Entry(now, "hello", wasPolished: true, originalText: "hello")));
        Assert.True(HistoryDiaryPresentation.HasPolishEdits(Entry(now, "Hello.", wasPolished: true, originalText: "hello")));
    }

    [Fact]
    public void FormatDayHeaderAndGroupByDayFormatsRelativeAndCalendarDaysCleanly()
    {
        var now = new DateTimeOffset(2026, 11, 25, 14, 0, 0, TimeSpan.Zero);
        var today = new DateOnly(2026, 11, 25);
        var culture = CultureInfo.GetCultureInfo("en-US");

        var todayHeader = HistoryDiaryPresentation.FormatDayHeader(today, today, culture);
        Assert.Equal("Today", todayHeader.PrimaryTitle);
        Assert.Equal("Wednesday, November 25", todayHeader.SecondaryDateLabel);
        Assert.Equal("Today", todayHeader.RelativeLabel);

        var yesterdayHeader = HistoryDiaryPresentation.FormatDayHeader(today.AddDays(-1), today, culture);
        Assert.Equal("Yesterday", yesterdayHeader.PrimaryTitle);
        Assert.Equal("Tuesday, November 24", yesterdayHeader.SecondaryDateLabel);

        var olderSameYear = HistoryDiaryPresentation.FormatDayHeader(today.AddDays(-3), today, culture);
        Assert.Equal("Sunday, November 22", olderSameYear.PrimaryTitle);
        Assert.Null(olderSameYear.SecondaryDateLabel);

        var priorYear = HistoryDiaryPresentation.FormatDayHeader(new DateOnly(2025, 11, 24), today, culture);
        Assert.Equal("Monday, November 24, 2025", priorYear.PrimaryTitle);

        var a = Entry(now, "first");
        var b = Entry(now.AddHours(-3), "second");
        var c = Entry(now.AddDays(-1), "third");
        var groups = HistoryDiaryPresentation.GroupByDay([a, b, c], now, UtcZone, culture);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Today", groups[0].Header.PrimaryTitle);
        Assert.Equal(2, groups[0].Entries.Count);
        Assert.Equal("Yesterday", groups[1].Header.PrimaryTitle);
        Assert.Single(groups[1].Entries);
    }

    [Fact]
    public void TakePageAndSelectionReconciliationWorkAcrossPagedBulkDeletions()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 5).Select(i => Entry(now.AddMinutes(-i), $"item {i}")).ToArray();

        var page = HistoryDiaryPresentation.TakePage(entries, 3);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(2, page.RemainingCount);
        Assert.True(page.HasMore);

        var surviving = entries.Skip(2).ToArray();
        var selected = new[] { entries[0].Id, entries[2].Id, entries[4].Id };
        var kept = HistoryDiaryPresentation.ReconcileSelectionAfterDelete(selected, surviving);
        Assert.Equal([entries[2].Id, entries[4].Id], kept.OrderBy(id => Array.FindIndex(entries, e => e.Id == id)));
    }

    [Fact]
    public async Task DeleteManyAsyncRemovesSelectedEntriesInJsonStoreAndPresenter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ew-history-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new JsonHistoryStore(Path.Combine(tempDir, "history.json"));
            var now = DateTimeOffset.UtcNow;
            var first = Entry(now, "one");
            var second = Entry(now.AddMinutes(-1), "two");
            var third = Entry(now.AddMinutes(-2), "three");

            await store.AddAsync(first, retentionDays: 30, now);
            await store.AddAsync(second, retentionDays: 30, now);
            await store.AddAsync(third, retentionDays: 30, now);

            var presenter = new HistoryPresenter(
                store,
                new StubRecoveryStore(),
                () => HistoryPreferences.Default,
                TimeProvider.System,
                new PresentationAdmission(),
                new NoopRepolishService());

            var result = await presenter.DeleteManyAsync([first.Id, third.Id]);
            Assert.True(result.Succeeded);
            Assert.NotNull(result.View);
            Assert.Equal([second.Id], result.View!.Entries.Select(e => e.Id));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void HelpItemsReflectActualWindowsRetention()
    {
        var forever = HistoryDiaryPresentation.HelpItems(0);
        Assert.Contains(forever, item =>
            item.Body.Contains("until you delete it", StringComparison.Ordinal) &&
            item.Body.Contains("after 24 hours unless you choose Keep", StringComparison.Ordinal));

        var oneDay = HistoryDiaryPresentation.HelpItems(1);
        Assert.Contains(oneDay, item =>
            item.Body.Contains("for 1 day", StringComparison.Ordinal) &&
            item.Body.Contains("under your 1-day history retention", StringComparison.Ordinal));

        var thirtyDays = HistoryDiaryPresentation.HelpItems(30);
        Assert.Contains(thirtyDays, item =>
            item.Body.Contains("for 30 days", StringComparison.Ordinal) &&
            item.Body.Contains("under that 30-day retention", StringComparison.Ordinal));
        Assert.All(thirtyDays, item => Assert.DoesNotContain("\u2014", item.Body, StringComparison.Ordinal));
    }

    [Fact]
    public void FilterMatchesFriendlyEngineModelProviderAndLanguageLabels()
    {
        var now = new DateTimeOffset(2026, 11, 25, 16, 0, 0, TimeSpan.Zero);
        var localEntry = Entry(
            now,
            "Sample local entry",
            engineId: "parakeet-file-transcribe",
            wasPolished: true,
            originalText: "sample local entry",
            polishModelId: "eg-1-mini");
        var openAiEntry = new DictationHistoryEntry(
            Guid.NewGuid(),
            now.AddMinutes(-5),
            "Sample OpenAI entry",
            "whisper",
            WasPolished: true,
            WasDelivered: true,
            ExpiresAt: null,
            OriginalText: "sample openai entry",
            LanguageCode: "en",
            PolishProviderId: "openai",
            PolishModelId: "gpt-4o-mini");

        var entries = new[] { localEntry, openAiEntry };

        var egMiniMatches = HistoryPresenter.Filter(
            entries,
            "EG-1 Mini",
            HistoryKindFilter.All,
            HistoryDateRangeFilter.AnyTime,
            now,
            UtcZone);
        Assert.Equal([localEntry.Id], egMiniMatches.Select(entry => entry.Id));

        var parakeetMatches = HistoryPresenter.Filter(
            entries,
            "Parakeet",
            HistoryKindFilter.All,
            HistoryDateRangeFilter.AnyTime,
            now,
            UtcZone);
        Assert.Equal([localEntry.Id], parakeetMatches.Select(entry => entry.Id));

        var openAiMatches = HistoryPresenter.Filter(
            entries,
            "OpenAI",
            HistoryKindFilter.All,
            HistoryDateRangeFilter.AnyTime,
            now,
            UtcZone);
        Assert.Equal([openAiEntry.Id], openAiMatches.Select(entry => entry.Id));

        var englishMatches = HistoryPresenter.Filter(
            entries,
            "English",
            HistoryKindFilter.All,
            HistoryDateRangeFilter.AnyTime,
            now,
            UtcZone);
        Assert.Equal([localEntry.Id, openAiEntry.Id], englishMatches.Select(entry => entry.Id));
    }

    [Fact]
    public void CountByDayReturnsFullUnpagedDayTotalsEvenWhenPageTruncatesDay()
    {
        var now = new DateTimeOffset(2026, 11, 25, 16, 0, 0, TimeSpan.Zero);
        var entries = new List<DictationHistoryEntry>();
        for (var i = 0; i < 40; i++)
        {
            entries.Add(Entry(now.AddMinutes(-i), $"Today item {i}"));
        }

        entries.Add(Entry(now.AddDays(-1), "Yesterday one"));
        entries.Add(Entry(now.AddDays(-1).AddMinutes(-5), "Yesterday two"));

        var paged = HistoryDiaryPresentation.TakePage(entries, 30);
        Assert.Equal(30, paged.Items.Count);
        Assert.Equal(12, paged.RemainingCount);

        var countsByDay = HistoryDiaryPresentation.CountByDay(entries, UtcZone);
        Assert.Equal(40, countsByDay[new DateOnly(2026, 11, 25)]);
        Assert.Equal(2, countsByDay[new DateOnly(2026, 11, 24)]);
    }

    private sealed class StubRecoveryStore : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing, null));

        public Task<bool> SaveAsync(
            RecoveryTextRecord record,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class NoopRepolishService : IHistoryRepolishService
    {
        public Task<HistoryRepolishResult> RepolishAsync(
            Guid entryId,
            bool copyToClipboard = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryRepolishResult(HistoryRepolishStatus.NoProviderConfigured, null));

        public Task<HistoryRepolishResult> RepolishAsync(
            DictationHistoryEntry entry,
            bool copyToClipboard = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryRepolishResult(HistoryRepolishStatus.NoProviderConfigured, null));
    }
}
