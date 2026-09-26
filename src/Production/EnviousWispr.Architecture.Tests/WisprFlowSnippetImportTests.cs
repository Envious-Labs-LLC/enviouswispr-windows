using System.Security.Cryptography;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Snippets;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Reading Wispr Flow's snippets on Windows: its real table shape, built as a fixture through the SQLite Windows
/// ships, with every row the reader must refuse present and counted, and the source left byte-for-byte alone.
/// </summary>
public sealed class WisprFlowSnippetImportTests : IDisposable
{
    /// <summary>Wispr Flow's Dictionary table as the Windows app creates it (read from a private copy of a real store).</summary>
    private const string Schema =
        "CREATE TABLE \"Dictionary\" (`id` VARCHAR(36) NOT NULL PRIMARY KEY, `phrase` VARCHAR(255) NOT NULL, " +
        "`replacement` VARCHAR(255), `teamDictionaryId` VARCHAR(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000', " +
        "`lastUsed` DATETIME, `frequencyUsed` INTEGER NOT NULL DEFAULT 0, `remoteFrequencyUsed` INTEGER NOT NULL DEFAULT 0, " +
        "`manualEntry` TINYINT(1) NOT NULL DEFAULT 0, `createdAt` DATETIME NOT NULL, `modifiedAt` DATETIME NOT NULL, " +
        "`isDeleted` TINYINT(1) NOT NULL DEFAULT 0, `source` VARCHAR(255), `isSnippet` TINYINT(1) NOT NULL DEFAULT 0, " +
        "`observedSource` VARCHAR(255), `isStarred` TINYINT(1) NOT NULL DEFAULT 0, `replacementHtml` TEXT);";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ew-wispr-" + Guid.NewGuid().ToString("N"));

    public WisprFlowSnippetImportTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void OnlyLiveSnippetsComeAcrossAndEverythingElseIsCounted()
    {
        var path = Fixture(
            Row("1", "my email", "me@example.com", deleted: 0, snippet: 1),
            Row("2", "old sig", "Gone", deleted: 1, snippet: 1),
            Row("3", "kubernetes", "Kubernetes", deleted: 0, snippet: 0),
            Row("4", "no text", null, deleted: 0, snippet: 1),
            Row("5", "blank text", "   ", deleted: 0, snippet: 1),
            Row("6", "  sign off  ", "Best,\nSam", deleted: 0, snippet: 1));

        var batch = new WisprFlowSnippetApp(path).Load();

        Assert.Equal("wispr_flow", batch.SourceId);
        Assert.Equal("Wispr Flow", batch.SourceDisplayName);
        Assert.Equal(
            [new SnippetImportCandidate("my email", "me@example.com"), new SnippetImportCandidate("sign off", "Best,\nSam")],
            batch.Candidates);
        var notice = Assert.Single(batch.Notices);
        Assert.IsType<SnippetImportNotice.IncompatibleSourceEntriesExcluded>(notice);
        Assert.Equal(4, notice.Count);
        Assert.Equal("4 entries were left out because EnviousWispr can't use them.", SnippetImportCopy.Notice(notice));
    }

    [Fact]
    public void TheSourceFolderIsLeftExactlyAsItWas()
    {
        var path = Fixture(Row("1", "my email", "me@example.com", deleted: 0, snippet: 1));
        var before = Snapshot();

        _ = new WisprFlowSnippetApp(path).Load();

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void AMissingStoreIsNotFoundAndAnUnreadableOneSaysSoWithoutBlame()
    {
        var missing = Assert.Throws<SnippetImportException>(() => new WisprFlowSnippetApp(Path.Combine(_folder, "absent.sqlite")).Load());
        Assert.Equal("Couldn't find any Wispr Flow snippets on this PC.", missing.Message);

        var garbage = Path.Combine(_folder, "garbage.sqlite");
        File.WriteAllText(garbage, "this is not a database");
        var unreadable = Assert.Throws<SnippetImportException>(() => new WisprFlowSnippetApp(garbage).Load());
        Assert.Equal(
            "Couldn't read your Wispr Flow snippets, so nothing was imported. If Wispr Flow is running, quitting it and trying again can help.",
            unreadable.Message);
    }

    [Fact]
    public void AHalfWrittenTransactionIsRefusedRatherThanRead()
    {
        var path = Fixture(Row("1", "my email", "me@example.com", deleted: 0, snippet: 1));
        File.WriteAllBytes(path + "-journal", [1, 2, 3]);

        var refusal = Assert.Throws<SnippetImportException>(() => new WisprFlowSnippetApp(path).Load());
        Assert.StartsWith("Couldn't read your Wispr Flow snippets", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnOfTheWrongTypeRefusesTheReadRatherThanGuessing()
    {
        var path = Fixture(Row("1", "my email", "me@example.com", deleted: 0, snippet: 1));
        WindowsSqlite.ExecuteOnNewDatabase(path, "UPDATE Dictionary SET isSnippet = 'yes';");

        Assert.Throws<SnippetImportException>(() => new WisprFlowSnippetApp(path).Load());
    }

    [Fact]
    public void AStoreFullerThanTheCeilingIsRefusedEvenWhenMostRowsAreWords()
    {
        var rows = Enumerable.Range(0, SnippetImportLimits.MaximumSourceEntries + 1)
            .Select(index => Row($"{index:D6}", $"word {index}", null, deleted: 0, snippet: 0))
            .ToArray();
        var path = Fixture(rows);

        var refusal = Assert.Throws<SnippetImportException>(() => new WisprFlowSnippetApp(path).Load());
        Assert.Equal(
            "Wispr Flow has more than 5000 entries where it keeps snippets, counting ones that can't be snippets here. EnviousWispr stopped without importing anything.",
            refusal.Message);
    }

    [Fact]
    public void TheAppListIsWisprFlowAloneAndPointsAtItsRoamingFolder()
    {
        var app = Assert.Single(SnippetImportApps.All);
        Assert.Equal("Wispr Flow", app.DisplayName);
        Assert.Equal("Wispr Flow", SnippetImportApps.SupportedNames);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "flow.sqlite"),
            WisprFlowSnippetApp.DefaultDatabasePath);
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_folder))
        {
            File.Delete(file);
        }

        Directory.Delete(_folder);
    }

    private string Fixture(params string[] rows)
    {
        var path = Path.Combine(_folder, "flow.sqlite");
        WindowsSqlite.ExecuteOnNewDatabase(path, Schema + "BEGIN;" + string.Concat(rows) + "COMMIT;");
        return path;
    }

    private static string Row(string id, string phrase, string? replacement, int deleted, int snippet)
    {
        var text = replacement is null ? "NULL" : "'" + replacement.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "INSERT INTO Dictionary (id, phrase, replacement, createdAt, modifiedAt, isDeleted, isSnippet) VALUES " +
            $"('{id}', '{phrase}', {text}, '2026-01-01', '2026-01-01', {deleted}, {snippet});";
    }

    private SortedDictionary<string, string> Snapshot() => new(
        Directory.GetFiles(_folder).ToDictionary(
            file => Path.GetFileName(file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))),
        StringComparer.Ordinal);
}
