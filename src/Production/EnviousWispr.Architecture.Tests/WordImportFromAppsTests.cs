using System.Security.Cryptography;
using System.Text;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.AppImport;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Words;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Words from another app: each adapter's store built as a fixture with every row it must refuse present and counted,
/// the source left byte-for-byte alone, the mapping onto pairs, the conflict offer, the stale refusal, and the report.
/// Expectations are literals, never built through the code under test.
/// </summary>
public sealed class WordImportFromAppsTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Wispr Flow's Dictionary table as the Windows app creates it (read from a private copy of a real store).</summary>
    private const string Schema =
        "CREATE TABLE \"Dictionary\" (`id` VARCHAR(36) NOT NULL PRIMARY KEY, `phrase` VARCHAR(255) NOT NULL, " +
        "`replacement` VARCHAR(255), `teamDictionaryId` VARCHAR(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000', " +
        "`lastUsed` DATETIME, `frequencyUsed` INTEGER NOT NULL DEFAULT 0, `remoteFrequencyUsed` INTEGER NOT NULL DEFAULT 0, " +
        "`manualEntry` TINYINT(1) NOT NULL DEFAULT 0, `createdAt` DATETIME NOT NULL, `modifiedAt` DATETIME NOT NULL, " +
        "`isDeleted` TINYINT(1) NOT NULL DEFAULT 0, `source` VARCHAR(255), `isSnippet` TINYINT(1) NOT NULL DEFAULT 0, " +
        "`observedSource` VARCHAR(255), `isStarred` TINYINT(1) NOT NULL DEFAULT 0, `replacementHtml` TEXT);";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ew-words-" + Guid.NewGuid().ToString("N"));

    public WordImportFromAppsTests() => Directory.CreateDirectory(_folder);

    // ---- Wispr Flow ---------------------------------------------------------------------------------------

    [Fact]
    public void WisprFlowBringsLiveWordsAsPairsAndCountsDeletedSnippetBlankAndRepeatedRows()
    {
        var path = WisprFixture(
            Row("01", "kubernetes", null, deleted: 0, snippet: 0),
            Row("02", "anthropic", "Anthropic", deleted: 0, snippet: 0),
            Row("03", "gone word", null, deleted: 1, snippet: 0),
            Row("04", "my email", "me@example.com", deleted: 0, snippet: 1),
            Row("05", "  postgres  ", "   ", deleted: 0, snippet: 0),
            Row("06", "   ", null, deleted: 0, snippet: 0),
            Row("07", "Anthropic", "Anthropic Inc", deleted: 0, snippet: 0));

        var batch = new WisprFlowWordApp(path).Load();

        Assert.Equal("wispr_flow", batch.SourceId);
        Assert.Equal("Wispr Flow", batch.SourceDisplayName);
        Assert.Equal(
            [
                new CustomWordEntry("kubernetes", "kubernetes"),
                new CustomWordEntry("anthropic", "Anthropic"),
                new CustomWordEntry("postgres", "postgres"),
            ],
            batch.Entries);
        // deleted, snippet, blank phrase, and "Anthropic" claiming a spoken form row 02 already owns.
        Assert.Equal(4, batch.Excluded);
    }

    [Fact]
    public void WisprFlowsFolderIsLeftExactlyAsItWas()
    {
        // LEFT IN WAL MODE WITH NO SIDECARS, the shape Wispr Flow rests in after quitting: opening THIS file, even to
        // read, would create a -wal and a -shm beside it, so a reader that skipped the private copy shows up here.
        var path = WisprFixture(Row("1", "anthropic", "Anthropic", deleted: 0, snippet: 0));
        WindowsSqlite.ExecuteOnNewDatabase(path, "PRAGMA journal_mode=WAL;");
        var before = Snapshot();
        Assert.Equal(["flow.sqlite"], before.Keys);

        // HELD SHARED FOR READING ONLY, as a program that refuses writers holds it. A copy can still be taken; a
        // read-write open of the source itself cannot, and SQLite's read-only fallback cannot build the WAL index a
        // WAL-mode file needs - so reading the store in place, instead of a private copy, fails this test.
        CustomWordAppBatch batch;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            batch = new WisprFlowWordApp(path).Load();
        }

        Assert.Equal([new CustomWordEntry("anthropic", "Anthropic")], batch.Entries);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void WisprFlowMissingIsNotFoundAndUnreadableSaysSoWithoutBlame()
    {
        var missing = Assert.Throws<WordImportException>(() => new WisprFlowWordApp(Path.Combine(_folder, "absent.sqlite")).Load());
        Assert.Equal(WordImportFailure.AppNotFound, missing.Failure);
        Assert.Equal("Couldn't find any Wispr Flow words on this PC.", missing.Message);

        var garbage = Path.Combine(_folder, "garbage.sqlite");
        File.WriteAllText(garbage, "this is not a database");
        var unreadable = Assert.Throws<WordImportException>(() => new WisprFlowWordApp(garbage).Load());
        Assert.Equal(WordImportFailure.AppStoreUnreadable, unreadable.Failure);
        Assert.Equal(
            "Couldn't read your Wispr Flow words, so nothing was imported. If Wispr Flow is running, quitting it and trying again can help.",
            unreadable.Message);
    }

    [Fact]
    public void WisprFlowWithAColumnOfTheWrongTypeIsRefusedRatherThanGuessed()
    {
        var path = WisprFixture(Row("1", "anthropic", "Anthropic", deleted: 0, snippet: 0));
        WindowsSqlite.ExecuteOnNewDatabase(path, "UPDATE Dictionary SET isDeleted = 'no';");

        var refusal = Assert.Throws<WordImportException>(() => new WisprFlowWordApp(path).Load());
        Assert.Equal(WordImportFailure.AppStoreUnreadable, refusal.Failure);
    }

    [Fact]
    public void WisprFlowFullerThanTheCeilingIsRefusedEvenWhenMostRowsAreSnippets()
    {
        var rows = Enumerable.Range(0, WordImportLimits.MaximumSourceEntries + 1)
            .Select(index => Row($"{index:D6}", $"entry {index}", null, deleted: 0, snippet: index == 0 ? 0 : 1))
            .ToArray();
        var path = WisprFixture(rows);

        var refusal = Assert.Throws<WordImportException>(() => new WisprFlowWordApp(path).Load());
        Assert.Equal(WordImportFailure.TooMany, refusal.Failure);
        Assert.Equal(
            "Wispr Flow has more than 25000 dictionary entries, including entries it may hide or disable. EnviousWispr stopped without importing anything.",
            refusal.Message);
    }

    // ---- Handy --------------------------------------------------------------------------------------------

    [Fact]
    public void HandyBringsItsCustomWordsAndNeverItsFillerRemovalList()
    {
        var path = HandyFixture(
            "{\"settings\":{\"custom_words\":[\"Handy\",\"  Parakeet \",\"\",\"Handy\"],\"custom_filler_words\":[\"um\"],\"push_to_talk\":true},\"other\":1}");

        var batch = new HandyWordApp(path).Load();

        Assert.Equal("handy", batch.SourceId);
        Assert.Equal("Handy", batch.SourceDisplayName);
        Assert.Equal([new CustomWordEntry("Handy", "Handy"), new CustomWordEntry("Parakeet", "Parakeet")], batch.Entries);
        Assert.Equal(2, batch.Excluded); // the blank word and "Handy" listed twice
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"settings\":null}")]
    [InlineData("{\"settings\":{}}")]
    [InlineData("{\"settings\":{\"custom_words\":null}}")]
    [InlineData("{\"settings\":{\"custom_words\":[]}}")]
    public void AHandyStoreWithNoWordsIsEmptyNotDamaged(string json)
    {
        var batch = new HandyWordApp(HandyFixture(json)).Load();

        Assert.Empty(batch.Entries);
        Assert.Equal(0, batch.Excluded);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"settings\":[1]}")]
    [InlineData("{\"settings\":{\"custom_words\":\"Handy\"}}")]
    [InlineData("{\"settings\":{\"custom_words\":[\"Handy\",7]}}")]
    [InlineData("{\"settings\":{\"custom_words\":[\"Han")]
    [InlineData("")]
    public void AHandyStoreThatIsNotOneIsUnreadable(string json)
    {
        var refusal = Assert.Throws<WordImportException>(() => new HandyWordApp(HandyFixture(json)).Load());

        Assert.Equal(WordImportFailure.AppStoreUnreadable, refusal.Failure);
        Assert.Equal(
            "Couldn't read your Handy words, so nothing was imported. If Handy is running, quitting it and trying again can help.",
            refusal.Message);
    }

    [Fact]
    public void AHandyReadThatSpansARewriteIsRetriedAndOnlyAgreeingBytesAreUsed()
    {
        var path = HandyFixture("{}");
        var torn = Encoding.UTF8.GetBytes("{\"settings\":{\"custom_words\":[\"Old\"]}}");
        var good = Encoding.UTF8.GetBytes("{\"settings\":{\"custom_words\":[\"New\"]}}");
        // Attempt 1 disagrees; attempt 2 is two agreeing ZERO-BYTE reads (Handy's truncation window), which must retry
        // rather than fail; attempt 3 agrees on the real document.
        var reads = new Queue<byte[]>([torn, good, [], [], good, good]);

        var batch = new HandyWordApp(path, _ => reads.Dequeue()).Load();

        Assert.Equal([new CustomWordEntry("New", "New")], batch.Entries);
        Assert.Empty(reads);
    }

    [Fact]
    public void AHandyStoreThatNeverHoldsStillIsRefused()
    {
        var path = HandyFixture("{}");
        var generation = 0;

        var refusal = Assert.Throws<WordImportException>(() => new HandyWordApp(
            path,
            _ => Encoding.UTF8.GetBytes($"{{\"settings\":{{\"custom_words\":[\"w{generation++}\"]}}}}")).Load());

        Assert.Equal(WordImportFailure.AppStoreUnreadable, refusal.Failure);
        Assert.Equal(6, generation);
    }

    [Fact]
    public void HandysFolderIsLeftExactlyAsItWas()
    {
        var path = HandyFixture("{\"settings\":{\"custom_words\":[\"Handy\"]}}");
        var before = Snapshot();

        _ = new HandyWordApp(path).Load();

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void HandyMissingIsNotFound()
    {
        var refusal = Assert.Throws<WordImportException>(() => new HandyWordApp(Path.Combine(_folder, "absent.json")).Load());

        Assert.Equal("Couldn't find any Handy words on this PC.", refusal.Message);
    }

    [Fact]
    public void TheAppListIsWisprFlowThenHandyAtTheirVerifiedRoamingLocations()
    {
        Assert.Equal(["Wispr Flow", "Handy"], WordImportApps.All.Select(app => app.DisplayName));
        Assert.Equal(["wispr_flow", "handy"], WordImportApps.All.Select(app => app.Id));
        Assert.Equal("Wispr Flow and Handy", WordImportApps.SupportedNames);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(roaming, "com.pais.handy", "settings_store.json"), HandyWordApp.DefaultStorePath);
        Assert.Equal(Path.Combine(roaming, "Wispr Flow", "flow.sqlite"), WisprFlowDatabase.DefaultPath);
    }

    // ---- Mapping onto pairs -------------------------------------------------------------------------------

    [Fact]
    public void AWordWithSpellingsBecomesOnePairPerSpellingAndAPlainWordWritesItself()
    {
        var batch = CustomWordAppImport.Build("test", "Test", new ImportedAppWords(
            [
                new ImportedAppWord(" Kubernetes ", ["cuban eddies", " ", "kuber netties"]),
                new ImportedAppWord("Visual Studio"),
                new ImportedAppWord("\t"),
                new ImportedAppWord("Other", ["CUBAN EDDIES"]),
            ],
            Excluded: 5));

        Assert.Equal(
            [
                new CustomWordEntry("cuban eddies", "Kubernetes"),
                new CustomWordEntry("kuber netties", "Kubernetes"),
                new CustomWordEntry("Visual Studio", "Visual Studio"),
            ],
            batch.Entries);
        Assert.Equal(7, batch.Excluded); // five from the source, the blank word, the spelling "Kubernetes" already owns
    }

    [Fact]
    public void TheScannedCountIncludesWhatTheAdapterRefused()
    {
        var read = new ImportedAppWords([new ImportedAppWord("one")], Excluded: WordImportLimits.MaximumSourceEntries);

        var refusal = Assert.Throws<WordImportException>(() => CustomWordAppImport.Build("test", "Test", read));

        Assert.Equal(WordImportFailure.TooMany, refusal.Failure);
        Assert.Equal(
            "Test has more than 25000 dictionary entries, including entries it may hide or disable. EnviousWispr stopped without importing anything.",
            refusal.Message);
        _ = CustomWordAppImport.Build("test", "Test", read with { Excluded = WordImportLimits.MaximumSourceEntries - 1 });
    }

    [Fact]
    public void MoreSpellingsThanTheListCanHoldAreRefusedBeforeReview()
    {
        var spellings = Enumerable.Range(0, AppSettingsValidator.MaximumCustomWords + 1).Select(index => $"s{index}").ToArray();

        var refusal = Assert.Throws<WordImportException>(() => CustomWordAppImport.Build(
            "test", "Test", new ImportedAppWords([new ImportedAppWord("word", spellings)], 0)));

        Assert.Equal(WordImportFailure.TooMany, refusal.Failure);
        Assert.Equal("That would make more than 10000 words, which is more than EnviousWispr can keep. Nothing was imported.", refusal.Message);
    }

    // ---- The plan: conflicts offered, never decided --------------------------------------------------------

    [Fact]
    public void APairThatDisagreesWithTheListIsAConflictAndAnUnstorableOneIsCounted()
    {
        var existing = new[]
        {
            new CustomWordEntry("anthropic", "Anthropic Inc"),
            new CustomWordEntry("Kubernetes", "Kubernetes"),
        };

        var plan = CustomWordImport.Plan(
            [
                new CustomWordEntry("Anthropic", "Anthropic"),
                new CustomWordEntry("kubernetes", "Kubernetes"),
                new CustomWordEntry("zed", "Zed"),
                new CustomWordEntry("a,b", "AB"),
                new CustomWordEntry("line", "one\ntwo"),
                new CustomWordEntry(new string('x', 201), "long"),
            ],
            existing);

        Assert.Equal(
            [
                ImportedWordOutcome.Conflict,
                ImportedWordOutcome.AlreadyPresent,
                ImportedWordOutcome.Added,
                ImportedWordOutcome.Unreadable,
                ImportedWordOutcome.Unreadable,
                ImportedWordOutcome.Unreadable,
            ],
            plan.Lines.Select(line => line.Outcome));
        Assert.Equal([new CustomWordEntry("Anthropic", "Anthropic")], plan.Conflicts);
        Assert.Equal([new CustomWordEntry("zed", "Zed")], plan.Additions);
        Assert.Equal("1 new word, 1 you already have, 1 you already correct differently, 3 EnviousWispr can't store.", WordImportMessages.ReviewSummary(plan));
        Assert.Equal(
            "1 added. 1 you already had. 1 left alone because you already correct them differently. 3 could not be stored.",
            WordImportMessages.Result(plan));
    }

    [Fact]
    public void AListHoldingOneSpokenFormTwiceIsPlannedAgainstTheRowTheCorrectorUses()
    {
        // The settings validator does not deduplicate spoken forms, so a profile can arrive like this.
        var existing = new[] { new CustomWordEntry("zed", "Old"), new CustomWordEntry("ZED", "Zed") };

        var fromApp = CustomWordImport.Plan([new CustomWordEntry("zed", "Zed"), new CustomWordEntry("zed2", "Old")], existing);
        var pasted = CustomWordImport.Read("zed,Old", existing);

        Assert.Equal([ImportedWordOutcome.AlreadyPresent, ImportedWordOutcome.Added], fromApp.Lines.Select(line => line.Outcome));
        Assert.Equal([ImportedWordOutcome.Conflict], pasted.Lines.Select(line => line.Outcome));
    }

    // ---- The commit ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ACommitAddsTheNewPairsInOneWriteAndLeavesConflictsForThePerson()
    {
        var existing = new[] { new CustomWordEntry("anthropic", "Anthropic Inc") };
        var (controller, store) = Build(existing);

        var result = await controller.CommitFromAppAsync(
            existing,
            [new CustomWordEntry("anthropic", "Anthropic"), new CustomWordEntry("zed", "Zed")]).WaitAsync(Patience);

        Assert.True(result.Saved);
        Assert.Equal(WordImportCommitKind.Committed, result.Value.Kind);
        Assert.Equal(
            [new CustomWordEntry("anthropic", "Anthropic Inc"), new CustomWordEntry("zed", "Zed")],
            store.Saved!.UserData.CustomWords);
        Assert.Equal([new CustomWordEntry("anthropic", "Anthropic")], result.Value.Plan.Conflicts);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task AListThatChangedDuringTheReviewIsRefusedAndNothingIsWritten()
    {
        var reviewed = new[] { new CustomWordEntry("a", "A") };
        var now = new[] { new CustomWordEntry("a", "A"), new CustomWordEntry("zed", "Zed") };
        var (controller, store) = Build(now);

        var result = await controller.CommitFromAppAsync(reviewed, [new CustomWordEntry("zed", "Zed"), new CustomWordEntry("b", "B")]).WaitAsync(Patience);

        Assert.Equal(WordImportCommitKind.Stale, result.Value.Kind);
        Assert.Equal(now, result.Value.Current);
        // The next review is drawn against the list as it now stands.
        Assert.Equal([ImportedWordOutcome.AlreadyPresent, ImportedWordOutcome.Added], result.Value.Plan.Lines.Select(line => line.Outcome));
        Assert.Equal(0, store.Saves);
        Assert.Equal("Your word list changed while you were reviewing. Nothing was imported. Here is the updated list.", WordImportMessages.StaleNotice);
    }

    [Fact]
    public async Task AChangedStrictnessIsAChangedList()
    {
        var reviewed = new[] { new CustomWordEntry("a", "A") };
        var (controller, store) = Build([new CustomWordEntry("a", "A", MatchStrictness.Strict)]);

        var result = await controller.CommitFromAppAsync(reviewed, [new CustomWordEntry("b", "B")]).WaitAsync(Patience);

        Assert.Equal(WordImportCommitKind.Stale, result.Value.Kind);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task NothingNewWritesNothing()
    {
        var existing = new[] { new CustomWordEntry("a", "A") };
        var (controller, store) = Build(existing);

        var result = await controller.CommitFromAppAsync(existing, [new CustomWordEntry("a", "A")]).WaitAsync(Patience);

        Assert.Equal(WordImportCommitKind.NothingNew, result.Value.Kind);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task AnImportThatWouldOverfillTheListIsRefused()
    {
        var existing = Enumerable.Range(0, AppSettingsValidator.MaximumCustomWords).Select(index => new CustomWordEntry($"w{index}", "x")).ToArray();
        var (controller, store) = Build(existing);

        var result = await controller.CommitFromAppAsync(existing, [new CustomWordEntry("one more", "x")]).WaitAsync(Patience);

        Assert.Equal(WordImportCommitKind.WouldExceedStore, result.Value.Kind);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>ATOMICITY IS RACED, NOT ASSUMED: many reviews of one list commit at once, and exactly one wins.</summary>
    [Fact]
    public async Task ConcurrentCommitsAgainstOneReviewHaveExactlyOneWinner()
    {
        var baseline = new[] { new CustomWordEntry("base", "B") };
        var (controller, store) = Build(baseline);

        var attempts = Enumerable.Range(0, 24)
            .Select(index => Task.Run(() => controller.CommitFromAppAsync(baseline, [new CustomWordEntry($"t{index}", $"x{index}")])))
            .ToArray();
        var results = await Task.WhenAll(attempts).WaitAsync(Patience);

        Assert.Single(results, result => result.Value.Kind == WordImportCommitKind.Committed);
        Assert.Equal(23, results.Count(result => result.Value.Kind == WordImportCommitKind.Stale));
        Assert.Equal(1, store.Saves);
        Assert.Equal(2, store.Saved!.UserData.CustomWords.Count);
    }

    // ---- The report ---------------------------------------------------------------------------------------

    [Fact]
    public void AReportIsCountedFromThePlanAndReachesTheDiskAsCountsAndNothingElse()
    {
        var plan = CustomWordImport.Plan(
            [new CustomWordEntry("anthropic", "Anthropic"), new CustomWordEntry("zed", "Zed"), new CustomWordEntry("a", "A"), new CustomWordEntry("x,y", "Z")],
            [new CustomWordEntry("anthropic", "Anthropic Inc"), new CustomWordEntry("a", "A")]);
        var report = DiagnosticWordImport.ForPlan(DiagnosticWordImportSource.WisprFlow, DiagnosticWordImportOutcome.Completed, excluded: 4, plan, added: 1);
        Assert.Equal(new DiagnosticWordImport(DiagnosticWordImportSource.WisprFlow, DiagnosticWordImportOutcome.Completed, null, 4, 1, 1, 1, 1, 4), report);

        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UnixEpoch, AppEventCode.WordsImportedFromApp, WordImport: report));
        var line = JsonLineFileLogger.Serialize(LocalDiagnosticLine.From(record, null));

        Assert.Contains(
            "\"wordImport\":{\"source\":\"WisprFlow\",\"outcome\":\"Completed\",\"candidates\":4,\"added\":1,\"alreadyHad\":1,\"conflicts\":1,\"unstorable\":1,\"excluded\":4}",
            line,
            StringComparison.Ordinal);
        Assert.DoesNotContain("anthropic", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zed", line, StringComparison.OrdinalIgnoreCase);
        Assert.True(JsonLineFileLogger.TryParseRecord(line, out var parsed));
        Assert.Equal(report, parsed!.WordImport);
    }

    [Fact]
    public void AnOutOfRangeReportIsDroppedWholeAndASmuggledFieldIsNotRead()
    {
        foreach (var bad in new[]
        {
            new DiagnosticWordImport(DiagnosticWordImportSource.Handy, DiagnosticWordImportOutcome.Completed, Added: -1),
            new DiagnosticWordImport(DiagnosticWordImportSource.Handy, DiagnosticWordImportOutcome.Completed, Excluded: 100_001),
            new DiagnosticWordImport((DiagnosticWordImportSource)97, DiagnosticWordImportOutcome.Completed),
            new DiagnosticWordImport(DiagnosticWordImportSource.Handy, (DiagnosticWordImportOutcome)97),
            new DiagnosticWordImport(DiagnosticWordImportSource.Handy, DiagnosticWordImportOutcome.Failed, (WordImportFailure)97),
        })
        {
            Assert.Null(PrivacySafeDiagnosticRecord.From(new AppLogEntry(
                DateTimeOffset.UnixEpoch, AppEventCode.WordsImportedFromApp, WordImport: bad)).WordImport);
        }

        const string Smuggled =
            "{\"timestamp\":\"2026-09-26T00:00:00+00:00\",\"event\":\"WordsImportedFromApp\",\"failure\":\"None\",\"wordImport\":{\"source\":\"Handy\",\"outcome\":\"Completed\",\"word\":\"Kubernetes\"}}";
        const string Negative =
            "{\"timestamp\":\"2026-09-26T00:00:00+00:00\",\"event\":\"WordsImportedFromApp\",\"failure\":\"None\",\"wordImport\":{\"source\":\"Handy\",\"outcome\":\"Completed\",\"added\":-4}}";
        Assert.False(JsonLineFileLogger.TryParseRecord(Smuggled, out _));
        Assert.False(JsonLineFileLogger.TryParseRecord(Negative, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticWordImport.SourceFor("superwhisper"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_folder))
        {
            File.Delete(file);
        }

        Directory.Delete(_folder);
    }

    // ---- Fixtures -----------------------------------------------------------------------------------------

    private string WisprFixture(params string[] rows)
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

    private string HandyFixture(string json)
    {
        var path = Path.Combine(_folder, "settings_store.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private SortedDictionary<string, string> Snapshot() => new(
        Directory.GetFiles(_folder).ToDictionary(
            file => Path.GetFileName(file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + "@" + File.GetLastWriteTimeUtc(file).Ticks),
        StringComparer.Ordinal);

    private static (VocabularyImportController Controller, RecordingStore Store) Build(IReadOnlyList<CustomWordEntry> words)
    {
        var store = new RecordingStore();
        var settings = AppSettings.Default with { UserData = new ReusableUserData(words, []) };
        return (new VocabularyImportController(new VocabularyPresenter(new SettingsPresenter(store, settings))), store);
    }

    private sealed class RecordingStore : ISettingsStore
    {
        private int _saves;

        public AppSettings? Saved { get; private set; }

        public int Saves => Volatile.Read(ref _saves);

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            Interlocked.Increment(ref _saves);
            return Task.CompletedTask;
        }

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
