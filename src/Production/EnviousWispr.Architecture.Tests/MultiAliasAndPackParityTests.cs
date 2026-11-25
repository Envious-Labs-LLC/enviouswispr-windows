using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class MultiAliasAndPackParityTests
{
    [Fact]
    public void MultiAliasEntryNormalizesDeduplicatesPreservesLegacySpokenFormAndLegacyEquality()
    {
        var legacyA = new CustomWordEntry("envy wisper", "EnviousWispr");
        var legacyB = new CustomWordEntry("envy wisper", "EnviousWispr");
        Assert.Equal(legacyA, legacyB);
        Assert.Equal("envy wisper", legacyA.SpokenForm);
        Assert.Equal(["envy wisper"], legacyA.Aliases);
        Assert.Equal(WordCategory.General, legacyA.Category);
        Assert.False(string.IsNullOrWhiteSpace(legacyA.Id));

        var multi = new CustomWordEntry(
            ["  kube  ", "Q burnett's", "KUBE", "", "coobernetes"],
            "Kubernetes",
            MatchStrictness.Loose,
            WordCategory.Domain);
        Assert.Equal(["kube", "Q burnett's", "coobernetes"], multi.Aliases);
        Assert.Equal("kube", multi.SpokenForm);
        Assert.Equal(WordCategory.Domain, multi.Category);
        Assert.Contains("Kubernetes", multi.ToString(), StringComparison.Ordinal);
        Assert.Contains("loose", multi.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeLegacyEntriesMergesDuplicateReplacementsWithMatchingStrictness()
    {
        var merged = CustomWordEntry.NormalizeLegacyEntries(
        [
            new CustomWordEntry("kube", "Kubernetes", MatchStrictness.Default),
            new CustomWordEntry("envy", "EnviousWispr", MatchStrictness.Default),
            new CustomWordEntry("q burnett", "Kubernetes", MatchStrictness.Default),
            new CustomWordEntry("coobernetes", "Kubernetes", MatchStrictness.Strict),
            new CustomWordEntry("case diff", "kubernetes", MatchStrictness.Default),
        ]);

        Assert.Equal(4, merged.Count);
        var kubeDefault = merged[0];
        Assert.Equal("Kubernetes", kubeDefault.Replacement);
        Assert.Equal(["kube", "q burnett"], kubeDefault.Aliases);
        Assert.Equal(MatchStrictness.Default, kubeDefault.Strictness);

        Assert.Equal("EnviousWispr", merged[1].Replacement);
        Assert.Equal(["coobernetes"], merged[2].Aliases);
        Assert.Equal(MatchStrictness.Strict, merged[2].Strictness);
        Assert.Equal("kubernetes", merged[3].Replacement);
    }

    [Fact]
    public void CustomWordCorrectorExpandsAllAliasesOnOneEntry()
    {
        var entry = new CustomWordEntry(
            ["q burnett's", "coobernetes"],
            "Kubernetes",
            MatchStrictness.Default,
            WordCategory.Domain);

        var first = CustomWordCorrector.Correct("we deployed q burnett's yesterday", [entry]);
        var second = CustomWordCorrector.Correct("we scaled coobernetes pods", [entry]);

        Assert.Equal("we deployed Kubernetes yesterday", first.Text);
        Assert.Equal("we scaled Kubernetes pods", second.Text);
    }

    [Fact]
    public async Task LegacyV21JsonMigratesToV22AndMergesDuplicateReplacements()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ew-multialias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var preferencesJson = JsonSerializer.Serialize(UserPreferences.Default, JsonSettingsStore.SerializerOptions);
            var observabilityJson = JsonSerializer.Serialize(ObservabilityPreferences.Default, JsonSettingsStore.SerializerOptions);
            var legacyV21Json = $$"""
            {
              "schemaVersion": 21,
              "launchCount": 4,
              "hasCompletedOnboarding": true,
              "preferences": {{preferencesJson}},
              "observability": {{observabilityJson}},
              "userData": {
                "customWords": [
                  { "spokenForm": "q burnett", "replacement": "Kubernetes" },
                  { "spokenForm": "coobernetes", "replacement": "Kubernetes" },
                  { "spokenForm": "envy wisper", "replacement": "EnviousWispr", "strictness": "Strict" }
                ],
                "snippets": []
              }
            }
            """;
            await File.WriteAllTextAsync(settingsPath, legacyV21Json);

            var loaded = await new JsonSettingsStore(settingsPath).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
            Assert.Equal(21, loaded.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings!.SchemaVersion);
            Assert.Empty(loaded.Settings.UserData.EnabledVocabularyPackIds);
            Assert.Equal(2, loaded.Settings.UserData.CustomWords.Count);
            Assert.Equal(["q burnett", "coobernetes"], loaded.Settings.UserData.CustomWords[0].Aliases);
            Assert.Equal(MatchStrictness.Strict, loaded.Settings.UserData.CustomWords[1].Strictness);

            var updatedSettings = loaded.Settings with
            {
                UserData = loaded.Settings.UserData
                    .WithCustomWords(
                    [
                        new CustomWordEntry(["q burnett", "coobernetes"], "Kubernetes", MatchStrictness.Loose, WordCategory.Domain, "id-kube-1")
                    ])
                    .WithEnabledVocabularyPackIds(["tech", "brands"]),
            };
            await new JsonSettingsStore(settingsPath).SaveAsync(updatedSettings);

            var roundTripped = await new JsonSettingsStore(settingsPath).LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, roundTripped.Status);
            Assert.Equal(AppSettings.CurrentSchemaVersion, roundTripped.Settings!.SchemaVersion);
            Assert.Equal(["tech", "brands"], roundTripped.Settings.UserData.EnabledVocabularyPackIds);
            var stored = Assert.Single(roundTripped.Settings.UserData.CustomWords);
            Assert.Equal("id-kube-1", stored.Id);
            Assert.Equal(WordCategory.Domain, stored.Category);
            Assert.Equal(MatchStrictness.Loose, stored.Strictness);
            Assert.Equal(["q burnett", "coobernetes"], stored.Aliases);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PortableProfileV15MigratesToV16RejectsUnmappedFieldsAndRoundTripsMultiAliasAndPacks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ew-profile-multialias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new JsonPortableProfileService();
            var legacyV15Path = Path.Combine(directory, "legacy-v15.json");
            var preferencesJson = JsonSerializer.Serialize(UserPreferences.Default, JsonSettingsStore.SerializerOptions);
            var legacyV15Json = $$"""
            {
              "schemaVersion": 15,
              "preferences": {{preferencesJson}},
              "userData": {
                "customWords": [
                  { "spokenForm": "kube", "replacement": "Kubernetes" },
                  { "spokenForm": "k8s", "replacement": "Kubernetes" }
                ],
                "snippets": []
              }
            }
            """;
            await File.WriteAllTextAsync(legacyV15Path, legacyV15Json);

            var migrated = await service.ImportAsync(legacyV15Path);
            Assert.Equal(PortableProfileImportStatus.Imported, migrated.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, migrated.Profile!.SchemaVersion);
            Assert.Empty(migrated.Profile.UserData.EnabledVocabularyPackIds);
            var mergedWord = Assert.Single(migrated.Profile.UserData.CustomWords);
            Assert.Equal(["kube", "k8s"], mergedWord.Aliases);

            var badPath = Path.Combine(directory, "bad.json");
            var badJson = $$"""
            {
              "schemaVersion": {{PortableProfile.CurrentSchemaVersion}},
              "preferences": {},
              "userData": {
                "customWords": [
                  { "spokenForm": "kube", "replacement": "Kubernetes", "unexpectedField": true }
                ],
                "snippets": []
              }
            }
            """;
            await File.WriteAllTextAsync(badPath, badJson);

            var badResult = await service.ImportAsync(badPath);
            Assert.Equal(PortableProfileImportStatus.Invalid, badResult.Status);
            Assert.Equal(AppErrorCode.InvalidData, badResult.Error?.Code);

            var profilePath = Path.Combine(directory, "valid.enviouswispr.json");
            var profile = new PortableProfile(
                PortableProfile.CurrentSchemaVersion,
                UserPreferences.Default,
                new ReusableUserData(
                    [new CustomWordEntry(["anthro pick", "antropik"], "Anthropic", MatchStrictness.Default, WordCategory.Brand, "brand-1")],
                    [],
                    enabledVocabularyPackIds: ["medical", "legal"]));
            Assert.True((await service.ExportAsync(profile, profilePath)).Succeeded);

            var imported = await service.ImportAsync(profilePath);
            Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
            Assert.Equal(profile, imported.Profile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PartialAliasConflictSplitPreservesNonConflictingAliasesDuringImportMerge()
    {
        var existing = new[]
        {
            new CustomWordEntry(["foo", "bar"], "X", MatchStrictness.Loose, WordCategory.Domain),
        };

        var merged = CustomWordImport.Merge(existing, [new CustomWordEntry("foo", "Y")]);

        Assert.Equal(2, merged.Count);
        var preserved = Assert.Single(merged, e => e.Replacement == "X");
        Assert.Equal(["bar"], preserved.Aliases);
        Assert.Equal(MatchStrictness.Loose, preserved.Strictness);
        Assert.Equal(WordCategory.Domain, preserved.Category);

        var replaced = Assert.Single(merged, e => e.Replacement == "Y");
        Assert.Equal(["foo"], replaced.Aliases);
    }

    [Fact]
    public async Task QuickAddSplitsConflictingAliasOutOfMultiAliasEntryWithoutCreatingDuplicate()
    {
        var store = new MemorySettingsStore();
        var initial = AppSettings.Default with
        {
            UserData = new ReusableUserData(
                [new CustomWordEntry(["foo", "bar"], "X", MatchStrictness.Default, WordCategory.Brand, "id-1")],
                []),
        };
        var presenter = new VocabularyPresenter(new SettingsPresenter(store, initial));

        var save = await presenter.AddWordAsync("foo", "Y").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(save.Saved);
        var words = store.Saved!.UserData.CustomWords;
        Assert.Equal(2, words.Count);
        var originalSurvivor = Assert.Single(words, w => w.Replacement == "X");
        Assert.Equal(["bar"], originalSurvivor.Aliases);
        Assert.Equal(WordCategory.Brand, originalSurvivor.Category);

        var added = Assert.Single(words, w => w.Replacement == "Y");
        Assert.Equal(["foo"], added.Aliases);
    }

    [Fact]
    public void CsvExportAndImportPreservesCategoriesAndAllAliasesWithoutBreakingLegacyThreeColumnFormat()
    {
        var entries = new[]
        {
            new CustomWordEntry(["kube", "q burnett"], "Kubernetes", MatchStrictness.Loose, WordCategory.Domain),
            new CustomWordEntry(["jane doh"], "Jane Doe", MatchStrictness.Strict, WordCategory.Person),
        };

        var exported = CustomWordImport.Write(entries);
        Assert.StartsWith(CustomWordImport.HeaderWithCategory, exported, StringComparison.Ordinal);

        var plan = CustomWordImport.Read(exported, []);
        Assert.Equal(0, plan.UnreadableCount);
        Assert.Equal(3, plan.Additions.Count);
        Assert.All(plan.Additions.Where(a => a.Replacement == "Kubernetes"), entry =>
        {
            Assert.Equal(WordCategory.Domain, entry.Category);
            Assert.Equal(MatchStrictness.Loose, entry.Strictness);
        });
        var person = Assert.Single(plan.Additions, a => a.Replacement == "Jane Doe");
        Assert.Equal(WordCategory.Person, person.Category);
        Assert.Equal(MatchStrictness.Strict, person.Strictness);

        var mergedImported = CustomWordEntry.NormalizeLegacyEntries(plan.Additions);
        Assert.Equal(2, mergedImported.Count);
        Assert.Equal(["kube", "q burnett"], mergedImported[0].Aliases);
    }

    [Fact]
    public async Task VocabularyPacksOperateAsIsolatedLayerInPipelineAndPreviewWithoutPollutingPolishOrCustomWords()
    {
        var pipeline = new DeterministicTextPipeline();
        var options = new DeterministicTextOptions(
            WordCorrectionEnabled: true,
            FillerRemovalEnabled: false,
            EmojiFormatterEnabled: false,
            SpokenPunctuationEnabled: false);

        var offResult = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "check ekipt and archord", "whisper", []),
            CustomWords: [],
            options,
            EnabledVocabularyPackIds: []));
        Assert.Equal("check ekipt and archord", offResult.Output.Text);

        var onResult = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "check ekipt and archord", "whisper", []),
            CustomWords: [],
            options,
            EnabledVocabularyPackIds: ["tech", "legal"]));
        Assert.Equal("check accept and accord", onResult.Output.Text);

        var overrideResult = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "check ekipt", "whisper", []),
            CustomWords: [new CustomWordEntry("ekipt", "CustomEquipped")],
            options,
            EnabledVocabularyPackIds: ["tech"]));
        Assert.Equal("check CustomEquipped", overrideResult.Output.Text);

        var previewText = LivePreviewTextFormatter.Format(
            "try ekipt",
            wordCorrectionEnabled: true,
            customWords: [],
            EnglishSpelling.American,
            enabledVocabularyPackIds: ["tech"]);
        Assert.Equal("try accept", previewText);

        var polishEligible = PolishVocabulary.Eligible("check accept and CustomEquipped", []);
        Assert.Empty(polishEligible);
    }

    [Fact]
    public void OfflineAliasSuggesterAndCategoryClassifierWorkWithoutAiPolish()
    {
        var suggestions = OfflineAliasSuggester.Suggest("EnviousWispr", existingAliases: ["envy wisper"]);
        Assert.NotEmpty(suggestions);
        Assert.DoesNotContain("envy wisper", suggestions, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(WordCategory.Acronym, WordCategoryFormatting.Classify("FERPA"));
        Assert.Equal(WordCategory.Person, WordCategoryFormatting.Classify("Jane Doe"));
        Assert.Equal(WordCategory.Domain, WordCategoryFormatting.Classify("graphql_schema"));
        Assert.Equal(WordCategory.Brand, WordCategoryFormatting.Classify("Anthropic"));
        Assert.Equal(WordCategory.General, WordCategoryFormatting.Classify("hello"));
    }

    [Fact]
    public void DictionaryUiIncludesBannerCategoriesMassEditAndAllFivePacksWithSeeAllDrillDown()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var editorCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.WordsAndSnippets.cs"));
        var mainCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.Dictionary.cs"));

        Assert.Contains("Switching from another dictation app?", xaml, StringComparison.Ordinal);
        Assert.Contains("WordFilterAllChip", xaml, StringComparison.Ordinal);
        Assert.Contains("WordFilterGeneralChip", xaml, StringComparison.Ordinal);
        Assert.Contains("WordFilterPersonChip", xaml, StringComparison.Ordinal);
        Assert.Contains("WordFilterBrandChip", xaml, StringComparison.Ordinal);
        Assert.Contains("WordFilterAcronymChip", xaml, StringComparison.Ordinal);
        Assert.Contains("WordFilterDomainChip", xaml, StringComparison.Ordinal);
        Assert.Contains("MassEditWordsToggle", xaml, StringComparison.Ordinal);
        Assert.Contains("VocabularyPacksList", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowPackDetailDialogAsync", editorCs, StringComparison.Ordinal);
        Assert.Contains("OfflineAliasSuggester", editorCs, StringComparison.Ordinal);
        Assert.Contains("\"See all\"", mainCs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeLegacyEntriesSplitsGroupsOverMaximumAliasesPerEntrySoSettingsStayValid()
    {
        var entries = Enumerable.Range(1, CustomWordEntry.MaximumAliasesPerEntry + 1)
            .Select(i => new CustomWordEntry($"spoken-{i:D2}", "Kubernetes", MatchStrictness.Default))
            .ToArray();

        var normalized = CustomWordEntry.NormalizeLegacyEntries(entries);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(CustomWordEntry.MaximumAliasesPerEntry, normalized[0].Aliases.Count);
        Assert.Single(normalized[1].Aliases);
        Assert.Equal("spoken-65", normalized[1].Aliases[0]);

        var directory = Path.Combine(Path.GetTempPath(), $"ew-v21-max-aliases-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var populated = JsonSettingsStoreTests.CreatePopulatedSettings() with { SchemaVersion = 21 };
            var root = JsonNode.Parse(JsonSerializer.Serialize(populated, JsonSettingsStore.SerializerOptions))!.AsObject();
            var userData = root["userData"]!.AsObject();
            userData.Remove("enabledVocabularyPackIds");
            userData["customWords"] = JsonNode.Parse(JsonSerializer.Serialize(
                entries.Select(e => new
                {
                    spokenForm = e.SpokenForm,
                    replacement = e.Replacement,
                    strictness = (int)e.Strictness,
                }),
                JsonSettingsStore.SerializerOptions));
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var loaded = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
            Assert.Equal(2, loaded.Settings.UserData.CustomWords.Count);
            Assert.Equal(CustomWordEntry.MaximumAliasesPerEntry, loaded.Settings.UserData.CustomWords[0].Aliases.Count);
            Assert.Equal("spoken-65", Assert.Single(loaded.Settings.UserData.CustomWords[1].Aliases));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportClassifiesAndMergesCategoryChangesFaithfully()
    {
        var existing = new[]
        {
            new CustomWordEntry(["k8s"], "Kubernetes", MatchStrictness.Default, WordCategory.General),
        };

        var categorizedCsv = CustomWordImport.Write(
            [new CustomWordEntry(["k8s"], "Kubernetes", MatchStrictness.Default, WordCategory.Brand)]);

        var readPlan = CustomWordImport.Read(categorizedCsv, existing);
        Assert.Empty(readPlan.Additions);
        var conflict = Assert.Single(readPlan.Conflicts);
        Assert.Equal(WordCategory.Brand, conflict.Category);

        var directPlan = CustomWordImport.Plan(
            [new CustomWordEntry(["k8s"], "Kubernetes", MatchStrictness.Default, WordCategory.Brand)],
            existing);
        Assert.Equal(WordCategory.Brand, Assert.Single(directPlan.Conflicts).Category);

        var merged = CustomWordImport.Merge(existing, readPlan.Conflicts);
        var updated = Assert.Single(merged);
        Assert.Equal(WordCategory.Brand, updated.Category);
    }

    [Fact]
    public async Task UpdateMultiAliasWordAsyncRefusesStaleEditWhenLogicalRowWasModifiedConcurrently()
    {
        var initialWord = new CustomWordEntry(
            ["kwark"],
            "Quark",
            MatchStrictness.Default,
            WordCategory.Brand,
            id: "logical-row-1");
        var store = new MemorySettingsStore();
        var initial = AppSettings.Default with
        {
            UserData = new ReusableUserData([initialWord], []),
        };
        var presenter = new VocabularyPresenter(new SettingsPresenter(store, initial));

        // Simulate another concurrent edit (or Quick Add strip) modifying the logical row with the same ID.
        var concurrentSave = await presenter.UpdateMultiAliasWordAsync(
            initialWord,
            ["kwark", "cork"],
            "Quark",
            MatchStrictness.Default,
            WordCategory.Brand).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(concurrentSave.Value.Refusal);

        // Saving from the stale editor snapshot (still carrying the same Id) must be refused instead of overwriting.
        var staleSave = await presenter.UpdateMultiAliasWordAsync(
            initialWord,
            ["old alias"],
            "QuarkStale",
            MatchStrictness.Strict,
            WordCategory.General).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WordEditRefusal.TargetMissing, staleSave.Value.Refusal);
        var preserved = Assert.Single(store.Saved!.UserData.CustomWords);
        Assert.Equal(["kwark", "cork"], preserved.Aliases);
        Assert.Equal("Quark", preserved.Replacement);
        Assert.Equal(WordCategory.Brand, preserved.Category);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public AppSettings? Saved { get; private set; }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
