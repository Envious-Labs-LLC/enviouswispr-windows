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
    public async Task UnicodeCombiningEmojiAndRightToLeftTextRoundTripExactly()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            const string international = "مرحبا ١٢٣ | שלום | Cafe\u0301 | 👨‍👩‍👧‍👦 | 東京";
            using var store = new JsonHistoryStore(Path.Combine(directory, "history.json"));

            Assert.True((await store.AddAsync(CreateEntry(Now, international), 30, Now)).Succeeded);

            var loaded = await store.LoadAsync(30, Now);
            Assert.Equal(international, Assert.Single(loaded.Entries).Text);
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
    public async Task EscapeRecoveryExpiresAfterOneDayUnlessKept()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            using var store = new JsonHistoryStore(Path.Combine(directory, "history.json"));
            var expiring = CreateEntry(Now, "temporary recovery") with
            {
                ExpiresAt = Now.AddHours(24),
            };
            Assert.True((await store.AddAsync(expiring, 0, Now)).Succeeded);
            Assert.Single((await store.LoadAsync(0, Now.AddHours(23))).Entries);
            Assert.Empty((await store.LoadAsync(0, Now.AddHours(25))).Entries);

            var kept = CreateEntry(Now, "kept recovery") with { ExpiresAt = Now.AddHours(24) };
            Assert.True((await store.AddAsync(kept, 0, Now)).Succeeded);
            Assert.True((await store.KeepAsync(kept.Id)).Succeeded);
            Assert.Single((await store.LoadAsync(0, Now.AddDays(2))).Entries);
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

    [Fact]
    public async Task SchemaV1AndV2MigrateToV3OnWriteAndPreserveBackwardCompatibility()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "history.json");
            var id = Guid.NewGuid();
            var v1Json = $$"""
            {
              "schemaVersion": 1,
              "entries": [
                {
                  "id": "{{id}}",
                  "createdAt": "2026-08-26T12:00:00+00:00",
                  "text": "legacy v1 transcript",
                  "engineId": "synthetic-engine",
                  "wasPolished": false,
                  "wasDelivered": true,
                  "expiresAt": null
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(path, v1Json);
            using var store = new JsonHistoryStore(path);

            var loaded = await store.LoadAsync(30, Now);
            Assert.Equal(HistoryLoadStatus.Loaded, loaded.Status);
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal(id, entry.Id);
            Assert.Equal("legacy v1 transcript", entry.Text);
            Assert.Null(entry.OriginalText);
            Assert.Null(entry.LanguageCode);
            Assert.Null(entry.PolishProviderId);
            Assert.Null(entry.PolishModelId);

            // Re-saving migrates schemaVersion to 3
            var repolish = await store.UpdateRepolishedAsync(
                id,
                expectedCurrentText: "legacy v1 transcript",
                newText: "migrated repolished text",
                wasPolished: true,
                providerId: "openai",
                modelId: "gpt-4o-mini");

            Assert.True(repolish.Succeeded);
            Assert.Equal("migrated repolished text", repolish.UpdatedEntry!.Text);
            Assert.Equal("legacy v1 transcript", repolish.UpdatedEntry.OriginalText);
            Assert.Equal("openai", repolish.UpdatedEntry.PolishProviderId);
            Assert.Equal("gpt-4o-mini", repolish.UpdatedEntry.PolishModelId);

            var fileContent = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 3", fileContent);
        });
    }

    [Fact]
    public async Task UpdateRepolishedPreservesPreAiOriginalTextAcrossMultipleRepolishes()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "history.json");
            using var store = new JsonHistoryStore(path);
            var original = CreateEntry(Now, "first draft") with
            {
                OriginalText = "first draft",
                LanguageCode = "en",
            };
            Assert.True((await store.AddAsync(original, 30, Now)).Succeeded);

            // First repolish: "first draft" -> "second draft polished"
            var firstRepolish = await store.UpdateRepolishedAsync(
                original.Id,
                expectedCurrentText: "first draft",
                newText: "second draft polished",
                wasPolished: true,
                providerId: "ollama",
                modelId: "llama3.2");

            Assert.True(firstRepolish.Succeeded);
            Assert.Equal("second draft polished", firstRepolish.UpdatedEntry!.Text);
            Assert.Equal("first draft", firstRepolish.UpdatedEntry.OriginalText);
            Assert.True(firstRepolish.UpdatedEntry.WasPolished);

            // Second repolish: "second draft polished" -> "third draft polished"
            var secondRepolish = await store.UpdateRepolishedAsync(
                original.Id,
                expectedCurrentText: "second draft polished",
                newText: "third draft polished",
                wasPolished: true,
                providerId: "anthropic",
                modelId: "claude-3-5-haiku");

            Assert.True(secondRepolish.Succeeded);
            Assert.Equal("third draft polished", secondRepolish.UpdatedEntry!.Text);
            Assert.Equal("first draft", secondRepolish.UpdatedEntry.OriginalText); // PRESERVED! Not "second draft polished"
            Assert.Equal("anthropic", secondRepolish.UpdatedEntry.PolishProviderId);
            Assert.Equal("claude-3-5-haiku", secondRepolish.UpdatedEntry.PolishModelId);
            Assert.Equal(original.CreatedAt, secondRepolish.UpdatedEntry.CreatedAt);
            Assert.Equal(original.EngineId, secondRepolish.UpdatedEntry.EngineId);
            Assert.Equal(original.WasDelivered, secondRepolish.UpdatedEntry.WasDelivered);

            var retrieved = await store.GetAsync(original.Id);
            Assert.NotNull(retrieved);
            Assert.Equal("third draft polished", retrieved.Text);
            Assert.Equal("first draft", retrieved.OriginalText);
        });
    }

    [Fact]
    public async Task UpdateRepolishedGuardsAgainstStaleEntryAndNotFound()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "history.json");
            using var store = new JsonHistoryStore(path);
            var entry = CreateEntry(Now, "actual text");
            Assert.True((await store.AddAsync(entry, 30, Now)).Succeeded);

            // Stale check
            var staleResult = await store.UpdateRepolishedAsync(
                entry.Id,
                expectedCurrentText: "wrong stale text",
                newText: "new text",
                wasPolished: true,
                providerId: "ollama",
                modelId: "llama3");

            Assert.False(staleResult.Succeeded);
            Assert.Equal(HistoryRepolishUpdateStatus.Stale, staleResult.Status);

            // Missing check
            var missingResult = await store.UpdateRepolishedAsync(
                Guid.NewGuid(),
                expectedCurrentText: "actual text",
                newText: "new text",
                wasPolished: true,
                providerId: "ollama",
                modelId: "llama3");

            Assert.False(missingResult.Succeeded);
            Assert.Equal(HistoryRepolishUpdateStatus.NotFound, missingResult.Status);
        });
    }

    [Fact]
    public async Task UpdateRepolishedRejectsExpiredEntry()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "history.json");
            using var store = new JsonHistoryStore(path);
            var expired = CreateEntry(Now.AddHours(-25), "expired recovery") with
            {
                ExpiresAt = Now.AddHours(-1),
            };
            Assert.True((await store.AddAsync(expired, 0, Now)).Succeeded);

            var result = await store.UpdateRepolishedAsync(
                expired.Id,
                expectedCurrentText: "expired recovery",
                newText: "attempted repolish",
                wasPolished: true,
                providerId: "ollama",
                modelId: "llama3");

            Assert.False(result.Succeeded);
            Assert.Equal(HistoryRepolishUpdateStatus.Expired, result.Status);
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
