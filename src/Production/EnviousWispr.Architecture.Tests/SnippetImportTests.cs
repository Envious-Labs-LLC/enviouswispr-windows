using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Snippet export, import, review and commit, and search, against literal expectations: the file macOS ships,
/// the list and CSV grammars, the skip-only review rows, the one write that refuses a list that moved, and a
/// keyword no import ever changes.
/// </summary>
public sealed class SnippetImportTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ---- The export file ----------------------------------------------------------------------------------

    /// <summary>The bytes macOS v1 wrote for one snippet, copied from its own shipped-bytes test.</summary>
    private const string ShippedMacV1 = """
        {
          "keyword" : "backslash",
          "snippets" : [
            {
              "createdAt" : 778000000,
              "expansion" : "hello@example.com",
              "id" : "5F3A2C1E-0B7D-4E8A-9C21-3D4E5F6A7B8C",
              "trigger" : "my email address"
            }
          ],
          "version" : 1
        }
        """;

    [Fact]
    public void TheFileTheMacShipsIsRead()
    {
        var document = SnippetsTransferDocument.Read(ShippedMacV1);

        Assert.Equal(1, document.Version);
        Assert.Equal("backslash", document.Keyword);
        Assert.Equal([new SnippetImportCandidate("my email address", "hello@example.com")], document.Snippets);
    }

    [Fact]
    public void ExportWritesTheMacShapeAndReadsBackWhole()
    {
        var snippets = new[]
        {
            new SnippetEntry("my email", "john.doe@example.com"),
            new SnippetEntry("sign off", "Best,\nSam \u00E9 \"quoted\" {{date}}"),
        };
        var ids = new Queue<Guid>([Guid.Parse("5f3a2c1e-0b7d-4e8a-9c21-3d4e5f6a7b8c"), Guid.Parse("00000000-0000-0000-0000-000000000001")]);
        var exportedAt = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var text = SnippetsTransferDocument.Write(snippets, "hey", exportedAt, ids.Dequeue);

        // THE SHAPE, READ AS TEXT: sorted keys, upper-case ids, seconds since 2001-01-01.
        Assert.Contains("\"id\": \"5F3A2C1E-0B7D-4E8A-9C21-3D4E5F6A7B8C\"", text, StringComparison.Ordinal);
        Assert.Contains("\"createdAt\": 778377600", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("\"keyword\"", StringComparison.Ordinal) < text.IndexOf("\"snippets\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"snippets\"", StringComparison.Ordinal) < text.IndexOf("\"version\": 1", StringComparison.Ordinal));
        Assert.Contains("\u00E9", text, StringComparison.Ordinal);

        var read = SnippetsTransferDocument.Read(text);
        Assert.Equal("hey", read.Keyword);
        Assert.Equal(
            [
                new SnippetImportCandidate("my email", "john.doe@example.com"),
                new SnippetImportCandidate("sign off", "Best,\nSam \u00E9 \"quoted\" {{date}}"),
            ],
            read.Snippets);
    }

    [Fact]
    public async Task AnExportImportedIntoAnEmptyProfileRoundTripsAndLeavesTheKeywordAlone()
    {
        var original = new[]
        {
            new SnippetEntry("my address", "1 Example Way\nSpringfield"),
            new SnippetEntry("my email", "john.doe@example.com"),
        };
        var file = SnippetsTransferDocument.Write(original, "zebra", DateTimeOffset.UnixEpoch, Guid.NewGuid);
        var batch = SnippetFileImport.Read(".json", System.Text.Encoding.UTF8.GetBytes(file));
        var (controller, store) = Build(snippets: [], keyword: "backslash");
        var rows = SnippetImportReview.BuildRows(batch.Candidates, []);

        var result = await controller.CommitAsync([], rows.Where(row => row.Add).Select(row => row.Candidate).ToArray()).WaitAsync(Patience);

        Assert.Equal(SnippetImportCommitKind.Committed, result.Value.Kind);
        Assert.Equal(2, result.Value.Added);
        Assert.Equal(original, store.Saved!.UserData.Snippets);
        Assert.Equal("backslash", store.Saved.UserData.SnippetKeyword);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"name\":\"a\",\"text\":\"b\"}]")]
    [InlineData("{\"format\":\"custom-words\",\"version\":1}")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    [InlineData("{\"version\":1}")]
    public void JsonThatIsNotOursSaysSo(string text)
    {
        var refusal = Assert.Throws<SnippetImportException>(() => SnippetsTransferDocument.Read(text));
        Assert.Equal("That file isn't an EnviousWispr snippets file.", refusal.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":1,\"snippets\":\"nope\"}")]
    [InlineData("{\"version\":1,\"keyword\":\"k\",\"snippets\":[{\"trigger\":1}]}")]
    [InlineData("{\"version\":0,\"snippets\":[]}")]
    [InlineData("{\"snippets\":[]}")]
    [InlineData("{\"version\":\"one\",\"snippets\":[]}")]
    [InlineData("{\"version\":1,\"keyword\":\"k\",\"snippets\":[{\"trigger\":\"a\",\"expansion\":\"b\",\"id\":\"nope\",\"createdAt\":1}]}")]
    public void BrokenBytesOrABrokenPayloadAreDamaged(string text)
    {
        var refusal = Assert.Throws<SnippetImportException>(() => SnippetsTransferDocument.Read(text));
        Assert.Equal("That file is damaged and can't be read.", refusal.Message);
    }

    [Fact]
    public void ANewerFormatSaysUpdateNotDamaged()
    {
        var refusal = Assert.Throws<SnippetImportException>(() =>
            SnippetsTransferDocument.Read("{\"version\":2,\"keyword\":\"k\",\"snippets\":[{\"future\":true}]}"));
        Assert.Equal(
            "That file was exported by a newer version of EnviousWispr (format 2). Update the app, then try again.",
            refusal.Message);
    }

    // ---- The list grammar ---------------------------------------------------------------------------------

    [Fact]
    public void AListReadsEverySeparatorAndCountsWhatItCannotRead()
    {
        const string text =
            "my email = john@example.com\n" +
            "\n" +
            "   \n" +
            "sig\tBest, Sam\n" +
            "addr -> 1 Main St\n" +
            "cal => https://cal.example.com\n" +
            "arrow \u2192 right\n" +
            "signature = Hello, world\n" +
            "site: https://example.com\n" +
            "https://example.com: homepage\n" +
            "no separator here\n" +
            " = empty trigger\n" +
            "empty text =\n" +
            "multi = line one\\nline two\n";

        var result = SnippetLineListParser.Parse(text, limit: 100);

        Assert.Equal(
            [
                new SnippetImportCandidate("my email", "john@example.com"),
                new SnippetImportCandidate("sig", "Best, Sam"),
                new SnippetImportCandidate("addr", "1 Main St"),
                new SnippetImportCandidate("cal", "https://cal.example.com"),
                new SnippetImportCandidate("arrow", "right"),
                new SnippetImportCandidate("signature", "Hello, world"),
                new SnippetImportCandidate("site", "https://example.com"),
                new SnippetImportCandidate("https://example.com", "homepage"),
                new SnippetImportCandidate("multi", "line one\nline two"),
            ],
            result.Candidates);
        Assert.Equal(3, result.SkippedLines);
    }

    [Fact]
    public void AQuotedTriggerSplitsByWhatFollowsItsClosingQuote()
    {
        const string text =
            "\"sig\": \"Use x = y\"\n" +
            "\"a, b\" = c\n" +
            "\u201Csmart quotes\u201D = done\n" +
            "\"say \"\"hi\"\"\" = hello\n" +
            "'hello = world\n";

        var result = SnippetLineListParser.Parse(text, limit: 100);

        Assert.Equal(
            [
                new SnippetImportCandidate("sig", "Use x = y"),
                new SnippetImportCandidate("a, b", "c"),
                new SnippetImportCandidate("smart quotes", "done"),
                new SnippetImportCandidate("say \"\"hi\"\"", "hello"),
                new SnippetImportCandidate("'hello", "world"),
            ],
            result.Candidates);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void AListHeaderIsSkippedOnlyOnTheFirstLine()
    {
        var result = SnippetLineListParser.Parse("trigger = text\na = b\ntrigger = text\n", limit: 100);

        Assert.Equal(
            [new SnippetImportCandidate("a", "b"), new SnippetImportCandidate("trigger", "text")],
            result.Candidates);
    }

    [Fact]
    public void AListPastTheCeilingIsRefusedWhole()
    {
        var text = string.Join("\n", Enumerable.Range(0, 4).Select(index => $"t{index} = x"));

        var refusal = Assert.Throws<SnippetImportException>(() => SnippetLineListParser.Parse(text, limit: 3));
        Assert.Equal(
            "That has more than 3 snippets, which is more than EnviousWispr can import at once. Nothing was imported.",
            refusal.Message);
    }

    // ---- CSV ----------------------------------------------------------------------------------------------

    [Fact]
    public void ACsvKeepsQuotedCommasDoubledQuotesAndLineBreaks()
    {
        const string text =
            "trigger,text\r\n" +
            "sig,\"Best,\r\nSam\"\r\n" +
            "\"say \"\"hi\"\"\",hello\n" +
            "\n" +
            "\"\",\n" +
            ",only text\n" +
            "screen,5\" wide\n" +
            "last,no newline";

        var result = SnippetCsvParser.Parse(text, limit: 100);

        Assert.Equal(
            [
                new SnippetImportCandidate("sig", "Best,\r\nSam"),
                new SnippetImportCandidate("say \"hi\"", "hello"),
                new SnippetImportCandidate("screen", "5\" wide"),
                new SnippetImportCandidate("last", "no newline"),
            ],
            result.Candidates);
        Assert.Equal(2, result.SkippedLines);
    }

    [Theory]
    [InlineData("a,b\n\"hello\"x,c\n", 2)]
    [InlineData("a,b\nc,d\n\"never closed,e\n", 3)]
    [InlineData("\"one\ntwo\nthree\"x,y", 1)]
    public void ACsvQuotingProblemNamesTheLineItStartedOn(string text, int line)
    {
        var refusal = Assert.Throws<SnippetImportException>(() => SnippetCsvParser.Parse(text, limit: 100));
        Assert.Equal($"That CSV has a quoting problem on line {line}. Nothing was imported.", refusal.Message);
    }

    // ---- Paste sniff and the whole preview -----------------------------------------------------------------

    [Theory]
    [InlineData("a = b\nc = d", SnippetPasteSniff.List)]
    [InlineData("sig = Hello, world", SnippetPasteSniff.Ambiguous)]
    [InlineData("trigger,text\na,b", SnippetPasteSniff.Csv)]
    [InlineData("a,b\n\"say \"\"hi\"\"\",hello", SnippetPasteSniff.Csv)]
    [InlineData("\"my email\" = \"x@y\"", SnippetPasteSniff.List)]
    [InlineData("{date} = September 16", SnippetPasteSniff.List)]
    [InlineData("{\"version\":1,\"keyword\":\"k\",\"snippets\":[]}", SnippetPasteSniff.TransferDocument)]
    public void APasteIsSniffedAsOneGrammar(string text, SnippetPasteSniff expected) =>
        Assert.Equal(expected, SnippetPasteImport.Sniff(text));

    [Fact]
    public void AnAmbiguousPasteReadsAsAListUnlessCsvIsChosen()
    {
        const string text = "sig = Hello, world";

        Assert.Equal([new SnippetImportCandidate("sig", "Hello, world")], SnippetPasteImport.Preview(text, SnippetPasteFormat.Auto).Batch.Candidates);
        Assert.Equal([new SnippetImportCandidate("sig = Hello", " world")], SnippetPasteImport.Preview(text, SnippetPasteFormat.Csv).Batch.Candidates);
    }

    [Fact]
    public void APastedExportIsReadAsTheFileAndItsKeywordIgnored()
    {
        var (_, batch) = SnippetPasteImport.Preview(ShippedMacV1, SnippetPasteFormat.Auto);

        Assert.Equal("Pasted export", batch.SourceDisplayName);
        Assert.Equal([new SnippetImportCandidate("my email address", "hello@example.com")], batch.Candidates);
    }

    [Fact]
    public void TheKeywordRowOfAnExportIsNeverACandidate()
    {
        // A keyword that is itself trigger-shaped must not turn into a snippet, and nothing in the batch carries it.
        var file = SnippetsTransferDocument.Write([new SnippetEntry("a", "b")], "my keyword", DateTimeOffset.UnixEpoch, Guid.NewGuid);

        var batch = SnippetFileImport.Read(".json", System.Text.Encoding.UTF8.GetBytes(file));

        Assert.Equal([new SnippetImportCandidate("a", "b")], batch.Candidates);
        Assert.DoesNotContain(batch.Candidates, candidate => candidate.Trigger.Contains("keyword", StringComparison.Ordinal) ||
            candidate.Expansion.Contains("keyword", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnsayableOrInvisibleTriggerRefusesTheWholeBatch()
    {
        var bidi = Assert.Throws<SnippetImportException>(() => SnippetPasteImport.Preview("my\u202Eemail = x\nok = y", SnippetPasteFormat.Auto));
        Assert.Equal("That contains a trigger EnviousWispr can't use (\"my<U+202E>email\"). Nothing was imported.", bidi.Message);

        var punctuation = Assert.Throws<SnippetImportException>(() => SnippetPasteImport.Preview("... = x", SnippetPasteFormat.Auto));
        Assert.Equal("That contains a trigger EnviousWispr can't use (\"...\"). Nothing was imported.", punctuation.Message);

        var longTrigger = new string('a', 129);
        var tooLong = Assert.Throws<SnippetImportException>(() => SnippetPasteImport.Preview($"{longTrigger} = x", SnippetPasteFormat.Auto));
        Assert.Equal("That contains a trigger longer than 128 characters, which is too long to say. Nothing was imported.", tooLong.Message);
    }

    [Fact]
    public void AFileIsDecodedByItsMarkAndRefusedWhenItIsNotText()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("a = b")).ToArray();
        Assert.Equal([new SnippetImportCandidate("a", "b")], SnippetFileImport.Read(".txt", utf16).Candidates);

        var truncated = new byte[] { 0xFF, 0xFE, 0x61 };
        Assert.Equal("That couldn't be read.", Assert.Throws<SnippetImportException>(() => SnippetFileImport.Read(".txt", truncated)).Message);

        var latin1 = new byte[] { 0x61, 0x20, 0x3D, 0x20, 0xE9 };
        Assert.Equal("That couldn't be read.", Assert.Throws<SnippetImportException>(() => SnippetFileImport.Read(".csv", latin1)).Message);

        Assert.Equal(
            "EnviousWispr can't read \".docx\" files yet. Try the EnviousWispr Snippets.json you exported, a CSV, or a plain list.",
            Assert.Throws<SnippetImportException>(() => SnippetFileImport.Read(".docx", [0x61])).Message);
    }

    // ---- Review ------------------------------------------------------------------------------------------

    [Fact]
    public void TheReviewTicksNewRowsAndMakesEverythingElseSkipOnly()
    {
        var existing = new[] { new SnippetEntry("My Email", "mine@example.com") };
        var candidates = new[]
        {
            new SnippetImportCandidate("my email.", "theirs@example.com"),
            new SnippetImportCandidate("sig", "Best"),
            new SnippetImportCandidate("SIG", "Cheers"),
            new SnippetImportCandidate("phone", "555"),
        };

        var rows = SnippetImportReview.BuildRows(candidates, existing);

        Assert.Equal(
            [SnippetImportRowStatus.Existing, SnippetImportRowStatus.New, SnippetImportRowStatus.DuplicateInBatch, SnippetImportRowStatus.New],
            rows.Select(row => row.Status));
        Assert.Equal([false, true, false, true], rows.Select(row => row.Add));
        Assert.Equal("You have this, as \u201CMy Email\u201D.", rows[0].StatusNote);
        Assert.Equal("You have this", rows[0].SkipLabel);
        Assert.Equal("Already listed above.", rows[2].StatusNote);
        Assert.Equal("Skipped", rows[2].SkipLabel);

        // THE MODEL IS THE AUTHORITY: a skip-only row cannot be ticked, a new row can be unticked.
        Assert.False(rows[0].WithAdd(true).Add);
        Assert.False(rows[2].WithAdd(true).Add);
        Assert.False(rows[1].WithAdd(false).Add);
        Assert.Equal("2 new snippets, 1 you already have, 1 listed twice.", SnippetImportCopy.ReviewSummary(2, 1, 1));
        Assert.Equal("Add 2 snippets", SnippetImportCopy.ConfirmTitle(2));
        Assert.Equal("Add 1 snippet", SnippetImportCopy.ConfirmTitle(1));
        Assert.Equal("Add nothing", SnippetImportCopy.ConfirmTitle(0));
    }

    [Fact]
    public async Task OnlyTheTickedRowsAreAddedSortedWithTheKeywordUnchanged()
    {
        var existing = new[] { new SnippetEntry("zed", "Z") };
        var (controller, store) = Build(existing, keyword: "hey");
        var rows = SnippetImportReview.BuildRows(
            [new SnippetImportCandidate("beta", "B"), new SnippetImportCandidate("alpha", "A"), new SnippetImportCandidate("zed", "other")],
            existing).ToArray();
        rows[0] = rows[0].WithAdd(false);

        var result = await controller.CommitAsync(existing, rows.Where(row => row.Add).Select(row => row.Candidate).ToArray()).WaitAsync(Patience);

        Assert.Equal(SnippetImportCommitKind.Committed, result.Value.Kind);
        Assert.Equal(1, result.Value.Added);
        Assert.Equal([new SnippetEntry("alpha", "A"), new SnippetEntry("zed", "Z")], store.Saved!.UserData.Snippets);
        Assert.Equal("hey", store.Saved.UserData.SnippetKeyword);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task AListThatChangedDuringTheReviewIsRefusedAndNothingIsWritten()
    {
        var reviewed = new[] { new SnippetEntry("a", "A") };
        var (controller, store) = Build([new SnippetEntry("a", "A"), new SnippetEntry("b", "B")], keyword: "backslash");

        var result = await controller.CommitAsync(reviewed, [new SnippetImportCandidate("c", "C")]).WaitAsync(Patience);

        Assert.Equal(SnippetImportCommitKind.Stale, result.Value.Kind);
        Assert.Equal("Your snippets changed while you were reviewing. Nothing was imported. Here is the updated list.", result.Value.Message);
        Assert.Equal([new SnippetEntry("a", "A"), new SnippetEntry("b", "B")], result.Value.Current);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void TheStaleCheckCountsDuplicatesRatherThanComparingSets()
    {
        var one = new SnippetEntry("a", "A");

        Assert.False(SnippetImportReview.SameList([one, one], [one]));
        Assert.True(SnippetImportReview.SameList([new SnippetEntry("b", "B"), one], [one, new SnippetEntry("b", "B")]));
        Assert.False(SnippetImportReview.SameList([new SnippetEntry("a", "a")], [one]));
    }

    [Fact]
    public async Task NothingTickedWritesNothing()
    {
        var (controller, store) = Build([], keyword: "backslash");

        var result = await controller.CommitAsync([], []).WaitAsync(Patience);

        Assert.Equal(SnippetImportCommitKind.NothingApproved, result.Value.Kind);
        Assert.Equal(0, store.Saves);
        Assert.Equal("You skipped everything, so nothing was changed.", SnippetImportCopy.NothingApproved);
    }

    [Fact]
    public async Task AnApprovedRowTheListAlreadyAnswersRefusesTheWholeBatch()
    {
        var existing = new[] { new SnippetEntry("my email", "mine") };
        var (controller, store) = Build(existing, keyword: "backslash");

        var result = await controller.CommitAsync(
            existing,
            [new SnippetImportCandidate("new one", "x"), new SnippetImportCandidate("My Email!", "theirs")]).WaitAsync(Patience);

        Assert.Equal(SnippetImportCommitKind.Refused, result.Value.Kind);
        Assert.Equal("You already have a snippet for those words: \"my email\". Nothing was imported.", result.Value.Message);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task AnImportThatWouldOverfillTheStoreIsRefused()
    {
        var existing = Enumerable.Range(0, AppSettingsValidator.MaximumSnippets).Select(index => new SnippetEntry($"s{index}", "x")).ToArray();
        var (controller, store) = Build(existing, keyword: "backslash");

        var result = await controller.CommitAsync(existing, [new SnippetImportCandidate("one more", "x")]).WaitAsync(Patience);

        Assert.Equal(SnippetImportCommitKind.Refused, result.Value.Kind);
        Assert.Equal("That would make more than 1000 snippets, which is more than EnviousWispr can keep. Nothing was imported.", result.Value.Message);
        Assert.Equal(0, store.Saves);
    }

    /// <summary>ATOMICITY IS RACED, NOT ASSUMED: many reviews of one list commit at once, and exactly one wins.</summary>
    [Fact]
    public async Task ConcurrentCommitsAgainstOneReviewHaveExactlyOneWinner()
    {
        var baseline = new[] { new SnippetEntry("base", "B") };
        var (controller, store) = Build(baseline, keyword: "backslash");

        var attempts = Enumerable.Range(0, 24)
            .Select(index => Task.Run(() => controller.CommitAsync(baseline, [new SnippetImportCandidate($"t{index}", $"x{index}")])))
            .ToArray();
        var results = await Task.WhenAll(attempts).WaitAsync(Patience);

        var winners = results.Where(result => result.Value.Kind == SnippetImportCommitKind.Committed).ToArray();
        Assert.Single(winners);
        Assert.Equal(23, results.Count(result => result.Value.Kind == SnippetImportCommitKind.Stale));
        Assert.Equal(1, store.Saves);
        Assert.Equal(2, store.Saved!.UserData.Snippets.Count);
        Assert.Equal(winners[0].Value.Current, store.Saved.UserData.Snippets);
    }

    // ---- Search ------------------------------------------------------------------------------------------

    [Fact]
    public void SearchMatchesTriggerOrTextIgnoringCaseAndABlankQueryShowsAll()
    {
        var snippets = new[]
        {
            new SnippetEntry("my email", "john.doe@example.com"),
            new SnippetEntry("my phone", "(555) 010-4477"),
            new SnippetEntry("sign off", "Best, EMAIL team"),
        };

        Assert.Equal([snippets[0], snippets[2]], SnippetSearch.Filter(snippets, "  Email "));
        Assert.Equal([snippets[1]], SnippetSearch.Filter(snippets, "555"));
        Assert.Empty(SnippetSearch.Filter(snippets, "nothing like this"));
        Assert.Same(snippets, SnippetSearch.Filter(snippets, "   "));
        Assert.Equal("2 of 3", SnippetImportCopy.CountLabel(2, 3, searching: true));
        Assert.Equal("1 snippet", SnippetImportCopy.CountLabel(1, 1, searching: false));
        Assert.Equal("3 snippets", SnippetImportCopy.CountLabel(3, 3, searching: false));
    }

    // ---- Starters ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshProfileStartsWithTheSixExamplesAndOnlyAFreshOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ew-starters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "settings.json");
            var store = new JsonSettingsStore(path);

            var fresh = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Missing, fresh.Status);
            Assert.Equal(
                ["my address", "my calendar", "my email", "my intro", "my phone", "my signature"],
                fresh.Settings.UserData.Snippets.Select(snippet => snippet.Name));
            Assert.Equal("john.doe@example.com", fresh.Settings.UserData.Snippets.Single(snippet => snippet.Name == "my email").Body);
            Assert.All(fresh.Settings.UserData.Snippets, snippet => Assert.True(SnippetStarters.IsUneditedExample(snippet)));

            // Somebody who deleted every example has a file with none, and gets none back.
            await store.SaveAsync(fresh.Settings with { UserData = fresh.Settings.UserData.WithSnippets([]) });
            var existing = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, existing.Status);
            Assert.Empty(existing.Settings.UserData.Snippets);

            // An unreadable file is not a fresh install either.
            await File.WriteAllTextAsync(path, "{ not json");
            var invalid = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Invalid, invalid.Status);
            Assert.Empty(invalid.Settings.UserData.Snippets);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder))
            {
                File.Delete(file);
            }

            Directory.Delete(folder);
        }
    }

    [Fact]
    public void TheDefaultsCarryNoExamplesAndTheStartersAreValidAndDistinct()
    {
        Assert.Empty(AppSettings.Default.UserData.Snippets);
        Assert.Equal(6, SnippetStarters.All.Count);
        Assert.Null(AppSettingsValidator.Validate(AppSettings.FreshInstall, EnviousWispr.Core.Errors.AppErrorStage.SettingsSave));
        foreach (var starter in SnippetStarters.All)
        {
            Assert.Null(SnippetRules.Validate(starter, SnippetStarters.All, out _));
        }
    }

    [Fact]
    public void TheExampleBadgeGoesWithTheFirstEditToEitherHalf()
    {
        Assert.True(SnippetStarters.IsUneditedExample(new SnippetEntry("my email", "john.doe@example.com")));
        Assert.False(SnippetStarters.IsUneditedExample(new SnippetEntry("my email", "me@real.example")));
        Assert.False(SnippetStarters.IsUneditedExample(new SnippetEntry("my mail", "john.doe@example.com")));
        Assert.False(SnippetStarters.IsUneditedExample(new SnippetEntry("My Email", "john.doe@example.com")));
    }

    // ---- The writer does not save a value it already holds -----------------------------------------------

    [Fact]
    public async Task TheWriterDoesNotSaveTheSettingsItAlreadyHolds()
    {
        var store = new RecordingStore();
        using var writer = new SerialSettingsWriter(store, AppSettings.Default);

        Assert.Null(await writer.UpdateAsync(current => current).WaitAsync(Patience));
        Assert.Equal(0, store.Saves);

        Assert.Null(await writer.UpdateAsync(current => current with { LaunchCount = 3 }).WaitAsync(Patience));
        Assert.Equal(1, store.Saves);
    }

    private static (SnippetImportController Controller, RecordingStore Store) Build(IReadOnlyList<SnippetEntry> snippets, string keyword)
    {
        var store = new RecordingStore();
        var settings = AppSettings.Default with { UserData = new ReusableUserData([], snippets, keyword) };
        return (new SnippetImportController(new VocabularyPresenter(new SettingsPresenter(store, settings))), store);
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
