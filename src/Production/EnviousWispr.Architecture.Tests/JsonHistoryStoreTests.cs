using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Services.History;

namespace EnviousWispr.Architecture.Tests;

public sealed class JsonHistoryStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddLoadDeleteAndClearRoundTripLocalEntries()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonHistoryStore(Path.Combine(directory, "history.json"));
            var first = CreateEntry(Now.AddMinutes(-2), "first local transcript");
            var second = CreateEntry(Now.AddMinutes(-1), "second local transcript");

            Assert.True((await store.AddAsync(first, 30, Now)).Succeeded);
            Assert.True((await store.AddAsync(second, 30, Now)).Succeeded);

            var loaded = await store.LoadAsync(30, Now);
            Assert.Equal(HistoryLoadStatus.Loaded, loaded.Status);
            Assert.Equal([second.Id, first.Id], loaded.Entries.Select(entry => entry.Id));

            Assert.True((await store.DeleteAsync(second.Id)).Succeeded);
            Assert.Equal(first.Id, Assert.Single((await store.LoadAsync(30, Now)).Entries).Id);

            Assert.True((await store.ClearAsync()).Succeeded);
            Assert.Empty((await store.LoadAsync(30, Now)).Entries);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        });
    }

    [Fact]
    public async Task LoadPrunesExpiredEntriesAndZeroRetentionKeepsEntries()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonHistoryStore(Path.Combine(directory, "history.json"));
            var old = CreateEntry(Now.AddDays(-31), "expired local transcript");
            var recent = CreateEntry(Now.AddDays(-1), "recent local transcript");
            Assert.True((await store.AddAsync(old, 0, Now)).Succeeded);
            Assert.True((await store.AddAsync(recent, 0, Now)).Succeeded);

            Assert.Equal(2, (await store.LoadAsync(0, Now)).Entries.Count);
            Assert.Equal(recent.Id, Assert.Single((await store.LoadAsync(30, Now)).Entries).Id);
            Assert.Equal(recent.Id, Assert.Single((await store.LoadAsync(0, Now)).Entries).Id);
        });
    }

    [Fact]
    public async Task CorruptHistoryFailsClosedWithoutChangingSource()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "history.json");
            const string corrupt = "{not-history";
            await File.WriteAllTextAsync(path, corrupt);
            var store = new JsonHistoryStore(path);

            var loaded = await store.LoadAsync(30, Now);
            var add = await store.AddAsync(CreateEntry(Now, "must not replace corruption"), 30, Now);

            Assert.Equal(HistoryLoadStatus.Invalid, loaded.Status);
            Assert.Equal(AppErrorCode.InvalidData, loaded.Error?.Code);
            Assert.False(add.Succeeded);
            Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task InvalidEntryIsRejectedWithoutCreatingHistory()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "history.json");
            var store = new JsonHistoryStore(path);
            var invalid = CreateEntry(Now, "") with { Id = Guid.Empty };

            var result = await store.AddAsync(invalid, 30, Now);

            Assert.False(result.Succeeded);
            Assert.Equal(AppErrorCode.InvalidData, result.Error?.Code);
            Assert.False(File.Exists(path));
        });
    }

    private static DictationHistoryEntry CreateEntry(DateTimeOffset createdAt, string text) => new(
        Guid.NewGuid(),
        createdAt,
        text,
        "synthetic-engine",
        WasPolished: false,
        WasDelivered: true);
}
