using System.Diagnostics;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Services.History;

namespace EnviousWispr.Architecture.Tests;

public sealed class SqliteHistoryStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly System.Text.Json.JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task AddLoadKeepRepolishDeleteAndClearRoundTripAndPreserveOrder()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var dbPath = Path.Combine(directory, "history.db");
            var recordingsDir = Path.Combine(directory, "recordings", "dictations");
            using var store = new SqliteHistoryStore(dbPath, Path.Combine(directory, "history.json"), recordingsDir);

            var first = DictationHistoryEntry.Create(Now, "first take", "parakeet-tdt-v3:cpu", false, true);
            var second = DictationHistoryEntry.Create(Now, "second take", "parakeet-tdt-v3:cpu", false, true) with
            {
                ExpiresAt = Now.AddHours(12),
            };
            var third = DictationHistoryEntry.Create(Now.AddMinutes(1), "third take", "whisper-small:file", false, true);

            Assert.True((await store.AddAsync(first, 30, Now)).Succeeded);
            Assert.True((await store.AddAsync(second, 30, Now)).Succeeded);
            Assert.True((await store.AddAsync(third, 30, Now)).Succeeded);

            var loaded = await store.LoadAsync(30, Now);
            Assert.Equal(HistoryLoadStatus.Loaded, loaded.Status);
            // Same-timestamp second beats first; newer third leads.
            Assert.Equal([third.Id, second.Id, first.Id], loaded.Entries.Select(e => e.Id));

            Assert.True((await store.KeepAsync(second.Id)).Succeeded);
            var kept = await store.GetAsync(second.Id);
            Assert.NotNull(kept);
            Assert.Null(kept.ExpiresAt);

            var stale = await store.UpdateRepolishedAsync(first.Id, "wrong current", "updated", true, "ollama", "llama3");
            Assert.Equal(HistoryRepolishUpdateStatus.Stale, stale.Status);

            var repolished = await store.UpdateRepolishedAsync(first.Id, "first take", "First take, polished.", true, "ollama", "llama3.2:latest");
            Assert.Equal(HistoryRepolishUpdateStatus.Updated, repolished.Status);
            Assert.Equal("first take", repolished.UpdatedEntry!.OriginalText);
            Assert.Equal("First take, polished.", repolished.UpdatedEntry.Text);

            Assert.True((await store.DeleteAsync(second.Id)).Succeeded);
            Assert.Equal([third.Id, first.Id], (await store.LoadAsync(30, Now)).Entries.Select(e => e.Id));

            Assert.True((await store.ClearAsync()).Succeeded);
            Assert.Empty((await store.LoadAsync(30, Now)).Entries);
        });
    }

    [Fact]
    public async Task UnicodeAndSubstringAndFriendlyLabelSearchWorkAcrossFtsAndLikePaths()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var dbPath = Path.Combine(directory, "history.db");
            using var store = new SqliteHistoryStore(dbPath);

            var unicode = DictationHistoryEntry.Create(
                Now,
                "مرحبا ١٢٣ | שלום | unpolished token | 👨‍👩‍👧‍👦 | 東京",
                "parakeet-tdt-v3:directml",
                wasPolished: true,
                wasDelivered: true,
                originalText: "Cafe\u0301 meetup notes",
                languageCode: "es",
                polishProviderId: "ollama",
                polishModelId: "eg-1-mini");
            var fileEntry = DictationHistoryEntry.Create(
                Now.AddDays(-2),
                "Quarterly board slides summary",
                "whisper-small:transcribe",
                wasPolished: false,
                wasDelivered: true);

            Assert.True((await store.AddAsync(unicode, 30, Now)).Succeeded);
            Assert.True((await store.AddAsync(fileEntry, 30, Now)).Succeeded);

            // Mid-word substring via FTS5 trigram + LIKE
            var polishPage = await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "polish"));
            Assert.Equal(unicode.Id, Assert.Single(polishPage.Entries).Id);

            // Friendly engine, polish, and language labels
            Assert.Single((await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "Parakeet"))).Entries);
            Assert.Single((await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "EG-1 Mini"))).Entries);
            Assert.Single((await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "Spanish"))).Entries);
            Assert.Single((await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "Ollama"))).Entries);

            // Short (<3 chars) and Unicode combining search via custom culture-aware function
            Assert.Single((await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "東京"))).Entries);
            Assert.Single((await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "CAFÉ"))).Entries);

            // Kind filter + matching IDs
            var fileIds = await store.QueryMatchingIdsAsync(new HistoryPageQuery(30, Now, 0, "", HistoryEntryKindFilter.TranscribeFile));
            Assert.Equal([fileEntry.Id], fileIds);

            var dictationPage = await store.QueryPageAsync(new HistoryPageQuery(30, Now, 30, "", HistoryEntryKindFilter.Dictation));
            Assert.Equal([unicode.Id], dictationPage.Entries.Select(e => e.Id));
            Assert.Equal(2, dictationPage.TotalStoredCount);
            Assert.Equal(1, dictationPage.TotalMatchingCount);
        });
    }

    [Fact]
    public async Task LegacyJsonMigrationMigratesValidEntriesAndPreservesCorruptSource()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var jsonPath = Path.Combine(directory, "history.json");
            var dbPath = Path.Combine(directory, "history.db");

            var newer = DictationHistoryEntry.Create(Now, "migrated newer", "parakeet", false, true);
            var older = DictationHistoryEntry.Create(Now.AddHours(-2), "migrated older", "parakeet", false, true);
            using (var legacy = new JsonHistoryStore(jsonPath))
            {
                Assert.True((await legacy.AddAsync(older, 30, Now)).Succeeded);
                Assert.True((await legacy.AddAsync(newer, 30, Now)).Succeeded);
            }

            using (var sqlite = new SqliteHistoryStore(dbPath, jsonPath))
            {
                var loaded = await sqlite.LoadAsync(30, Now);
                Assert.Equal(HistoryLoadStatus.Loaded, loaded.Status);
                Assert.Equal([newer.Id, older.Id], loaded.Entries.Select(e => e.Id));
            }

            Assert.True(File.Exists(dbPath));
            Assert.False(File.Exists(jsonPath));
            Assert.True(File.Exists($"{jsonPath}.migrated"));

            // Corrupt legacy JSON must fail closed and avoid creating history.db
            var corruptDir = Path.Combine(directory, "corrupt");
            Directory.CreateDirectory(corruptDir);
            var corruptJson = Path.Combine(corruptDir, "history.json");
            var corruptDb = Path.Combine(corruptDir, "history.db");
            await File.WriteAllTextAsync(corruptJson, "{ not valid json");

            using var closedSqlite = new SqliteHistoryStore(corruptDb, corruptJson);
            var corruptLoad = await closedSqlite.LoadAsync(30, Now);
            Assert.Equal(HistoryLoadStatus.Invalid, corruptLoad.Status);
            Assert.Equal(AppErrorCode.InvalidData, corruptLoad.Error?.Code);
            Assert.False(File.Exists(corruptDb));
        });
    }

    [Fact]
    public async Task LockedAudioRefusalAndStagedDeleteRecoveryMatchArchiveSafetyGuarantees()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var dbPath = Path.Combine(directory, "history.db");
            var recordingsDir = Path.Combine(directory, "recordings", "dictations");
            Directory.CreateDirectory(recordingsDir);

            using var store = new SqliteHistoryStore(dbPath, recordingsDirectory: recordingsDir);
            var archive = new LocalDictationAudioArchive(recordingsDir);
            var audio = new CapturedAudio(DictationSessionId.Create(), new float[1600], 16_000, 1);

            var entry1 = DictationHistoryEntry.Create(Now, "Take one", "parakeet", false, true);
            var saved1 = archive.SaveForEntry(entry1.Id, audio)!;
            Assert.True((await store.AddAsync(entry1 with { AudioFileName = saved1.FileName, AudioDurationMs = saved1.DurationMs }, 30, Now)).Succeeded);

            var wav1 = Path.Combine(recordingsDir, saved1.FileName);
            using (new FileStream(wav1, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var del = await store.DeleteAsync(entry1.Id);
                Assert.False(del.Succeeded);
                Assert.Equal(AppErrorCode.StorageUnavailable, del.Error?.Code);
                Assert.NotNull(await store.GetAsync(entry1.Id));
                Assert.True(File.Exists(wav1));
            }

            Assert.True((await store.ClearAsync()).Succeeded);
            Assert.Empty(Directory.GetFiles(recordingsDir));

            // Passive pruning retries deletion once lock releases and recovers .deltmp files for live rows
            var live = DictationHistoryEntry.Create(Now, "Live row", "parakeet", false, true);
            var savedLive = archive.SaveForEntry(live.Id, audio)!;
            Assert.True((await store.AddAsync(live with { AudioFileName = savedLive.FileName, AudioDurationMs = savedLive.DurationMs }, 30, Now)).Succeeded);
            var liveWav = Path.Combine(recordingsDir, savedLive.FileName);
            var stagedLive = $"{liveWav}.{Guid.NewGuid():N}.deltmp";
            File.Move(liveWav, stagedLive);

            var afterLoad = await store.LoadAsync(30, Now.AddMinutes(1));
            Assert.Equal(HistoryLoadStatus.Loaded, afterLoad.Status);
            Assert.True(File.Exists(liveWav));
            Assert.False(File.Exists(stagedLive));
        });
    }

    [Fact]
    public async Task DisposeReleasesDatabaseHandlesAndLargeCatalogRemainsFast()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ew-sqlite-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var dbPath = Path.Combine(tempRoot, "history.db");
            var legacyPath = Path.Combine(tempRoot, "history.json");

            // Seed 3,000 entries via JsonHistoryStore once to test real migration + query performance.
            var seedList = new List<DictationHistoryEntry>(3_000);
            for (var i = 0; i < 3_000; i++)
            {
                seedList.Add(DictationHistoryEntry.Create(
                    Now.AddSeconds(-i),
                    i == 2_468 ? "Needle milestone-2468 dictation transcript" : $"Routine dictation number {i} testing scroll and memory performance.",
                    "parakeet-tdt-v3:cpu",
                    wasPolished: false,
                    wasDelivered: true));
            }

            using (var jsonStore = new JsonHistoryStore(legacyPath))
            {
                // Write directly via JSON doc shape through AddAsync on small count? Writing the valid v3 JSON is instant.
                var entriesJson = System.Text.Json.JsonSerializer.Serialize(
                    new { schemaVersion = 3, entries = seedList },
                    CamelCaseJson);
                await File.WriteAllTextAsync(legacyPath, entriesJson);
            }

            using (var store = new SqliteHistoryStore(dbPath, legacyPath))
            {
                var swPage = Stopwatch.StartNew();
                var page = await store.QueryPageAsync(new HistoryPageQuery(30, Now.AddMinutes(1), Limit: 30, LocalTimeZone: TimeZoneInfo.Utc));
                swPage.Stop();

                Assert.Equal(HistoryLoadStatus.Loaded, page.Status);
                Assert.Equal(30, page.Entries.Count);
                Assert.Equal(3_000, page.TotalStoredCount);
                Assert.Equal(3_000, page.TotalMatchingCount);
                Assert.Single(page.DayCounts);

                var swSearch = Stopwatch.StartNew();
                var hits = await store.QueryPageAsync(new HistoryPageQuery(30, Now.AddMinutes(1), Limit: 30, SearchQuery: "milestone-2468"));
                swSearch.Stop();

                Assert.Single(hits.Entries);
                Assert.Equal("Needle milestone-2468 dictation transcript", hits.Entries[0].Text);
                Assert.True(swSearch.Elapsed < TimeSpan.FromSeconds(1), $"Search took {swSearch.Elapsed.TotalMilliseconds}ms");
            }

            // Dispose must release every file handle immediately so directory deletion succeeds cleanly.
            Directory.Delete(tempRoot, recursive: true);
            Assert.False(Directory.Exists(tempRoot));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
