using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;
using EnviousWispr.PostProcessing.Learning;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class VocabularyPackOverridesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ── 1. Core override resolution & sparse pruning ─────────────────────────

    [Fact]
    public void DisableAndReenableWordPrunesSparseOverrideAndUpdatesWordState()
    {
        var pack = VocabularyPacks.Tech;
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
        {
            [pack.Id] = new Dictionary<string, VocabularyPackWordOverride>
            {
                [VocabularyPacks.OverrideKey("accept")] = new VocabularyPackWordOverride(isEnabled: false),
            },
        };

        var states = VocabularyPacks.GetPackWordStates(pack, overrides);
        var acceptState = Assert.Single(states, s => s.Canonical == "accept");
        Assert.False(acceptState.IsEnabled);
        Assert.True(acceptState.IsEdited);
        Assert.Equal(1, VocabularyPacks.CountEditedWords(pack, overrides));

        var resolved = VocabularyPacks.ResolveEntries([pack.Id], overrides);
        Assert.Equal(pack.FixCount - 1, resolved.Count);
        Assert.DoesNotContain(resolved, e => string.Equals(e.Replacement, "accept", StringComparison.Ordinal));

        // Re-enabling produces an empty override that prunes cleanly.
        var reenabled = new VocabularyPackWordOverride(isEnabled: true);
        Assert.True(reenabled.IsEmpty);
        Assert.Null(reenabled.IsEnabled);
        var pruned = VocabularyPacks.PruneEmptyOverrides(
            new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
            {
                [pack.Id] = new Dictionary<string, VocabularyPackWordOverride>
                {
                    [VocabularyPacks.OverrideKey("accept")] = reenabled,
                },
            });
        Assert.Empty(pruned);
    }

    [Fact]
    public void AliasEditsRoundTripOrderInsensitivelyAndPreserveCaseSensitivity()
    {
        var shipped = VocabularyPacks.Tech.FindTerm("accept")!.Aliases;
        Assert.True(shipped.Count >= 2);

        // Reordered identical aliases match shipped defaults.
        var reversed = shipped.Reverse().ToArray();
        Assert.True(VocabularyPacks.AliasesEqualShipped(reversed, shipped));

        // Different casing counts as edited (exact-case parity).
        var upperFirst = new[] { shipped[0].ToUpperInvariant() }.Concat(shipped.Skip(1)).ToArray();
        Assert.False(VocabularyPacks.AliasesEqualShipped(upperFirst, shipped));

        // Empty aliases override keeps word enabled with no spoken aliases (canonical fuzzy still possible).
        var emptyAliasesOverrides = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
        {
            ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
            {
                [VocabularyPacks.OverrideKey("Dockerfile")] = new VocabularyPackWordOverride(aliases: []),
            },
        };
        var state = Assert.Single(
            VocabularyPacks.GetPackWordStates(VocabularyPacks.Tech, emptyAliasesOverrides),
            s => s.Canonical == "Dockerfile");
        Assert.True(state.IsEnabled);
        Assert.True(state.IsEdited);
        Assert.Empty(state.Aliases);
        Assert.Empty(state.DisplayAliases);

        var resolved = VocabularyPacks.ResolveEntries(["tech"], emptyAliasesOverrides);
        var dockerfileEntry = Assert.Single(resolved, e => e.Replacement == "Dockerfile");
        Assert.Empty(dockerfileEntry.Aliases);
    }

    [Fact]
    public void FormatSummaryHandlesSingularAndPluralCounts()
    {
        var single = new VocabularyPack("single", "Single", "One word", "foo,Foo");
        Assert.Equal("1 fix \u00B7 e.g. Foo", VocabularyPacks.FormatSummary(single));
        Assert.Equal(
            "76 fixes \u00B7 e.g. AngularJS, Bazel, Blazor",
            VocabularyPacks.FormatSummary(VocabularyPacks.Tech));
    }

    // ── 2. Presentation mutations & atomic persistence ───────────────────────

    [Fact]
    public async Task PresenterMutationsPersistAtomicallySkipNoOpsAndRestoreCleanly()
    {
        var store = new RecordingStore();
        var initial = AppSettings.Default with
        {
            UserData = new ReusableUserData([], [], enabledVocabularyPackIds: ["tech"]),
        };
        var presenter = new VocabularyPresenter(new SettingsPresenter(store, initial));

        // No-op enable of an already enabled word writes nothing.
        var noop = await presenter.SetVocabularyPackWordEnabledAsync("tech", "accept", true).WaitAsync(Patience);
        Assert.True(noop.Saved);
        Assert.Equal(0, store.Saves);

        // Disable word.
        var disable = await presenter.SetVocabularyPackWordEnabledAsync("tech", "accept", false).WaitAsync(Patience);
        Assert.True(disable.Saved);
        Assert.Equal(1, store.Saves);
        Assert.Equal(1, VocabularyPacks.CountEditedWords(VocabularyPacks.Tech, store.Saved!.UserData.VocabularyPackOverrides));

        // Add custom alias on another word.
        var shipped = VocabularyPacks.Tech.FindTerm("Dockerfile")!.Aliases;
        var withExtra = shipped.Append("doc file").ToArray();
        var addAlias = await presenter.SetVocabularyPackWordAliasesAsync("tech", "Dockerfile", withExtra).WaitAsync(Patience);
        Assert.True(addAlias.Saved);
        Assert.Equal(2, store.Saves);
        Assert.Equal(2, VocabularyPacks.CountEditedWords(VocabularyPacks.Tech, store.Saved!.UserData.VocabularyPackOverrides));

        // Remove custom alias + reorder shipped -> returns to unedited automatically!
        var restoreByEdit = await presenter.SetVocabularyPackWordAliasesAsync("tech", "Dockerfile", shipped.Reverse().ToArray()).WaitAsync(Patience);
        Assert.True(restoreByEdit.Saved);
        Assert.Equal(1, VocabularyPacks.CountEditedWords(VocabularyPacks.Tech, store.Saved!.UserData.VocabularyPackOverrides));

        // Edit a word in Medical pack to verify RestorePack is scoped per pack.
        var medicalEdit = await presenter.SetVocabularyPackWordEnabledAsync("medical", "Humira", false).WaitAsync(Patience);
        Assert.True(medicalEdit.Saved);
        Assert.Equal(1, VocabularyPacks.CountEditedWords(VocabularyPacks.Tech, store.Saved!.UserData.VocabularyPackOverrides));
        Assert.Equal(1, VocabularyPacks.CountEditedWords(VocabularyPacks.Medical, store.Saved!.UserData.VocabularyPackOverrides));

        // Restore Tech pack leaves Medical untouched.
        var restoreTech = await presenter.RestoreVocabularyPackAsync("tech").WaitAsync(Patience);
        Assert.True(restoreTech.Saved);
        Assert.Equal(0, VocabularyPacks.CountEditedWords(VocabularyPacks.Tech, store.Saved!.UserData.VocabularyPackOverrides));
        Assert.Equal(1, VocabularyPacks.CountEditedWords(VocabularyPacks.Medical, store.Saved!.UserData.VocabularyPackOverrides));

        // Restore single Medical word clears all overrides cleanly.
        var restoreMedicalWord = await presenter.RestoreVocabularyPackWordAsync("medical", "Humira").WaitAsync(Patience);
        Assert.True(restoreMedicalWord.Saved);
        Assert.Empty(store.Saved!.UserData.VocabularyPackOverrides);
    }

    [Fact]
    public async Task PresenterRefusedPackOverrideSaveLeavesPreviousStateUnchanged()
    {
        var store = new RecordingStore();
        var initial = AppSettings.Default with
        {
            UserData = new ReusableUserData([], [], enabledVocabularyPackIds: ["tech"]),
        };
        var presenter = new VocabularyPresenter(new SettingsPresenter(store, initial));
        store.FailNext = new IOException("disk full");

        var failed = await presenter.SetVocabularyPackWordEnabledAsync("tech", "accept", false).WaitAsync(Patience);

        Assert.False(failed.Saved);
        Assert.Equal(SettingsSaveRefusal.StorageUnavailable, failed.Refusal);
        Assert.Null(store.Saved);
    }

    // ── 3. Storage split, schema migration, portable profile, & validator ────

    [Fact]
    public async Task SplitStorePersistsPackOverridesInUserDataWithoutTouchingSettingsJson()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var userDataPath = Path.Combine(directory, "user-data.json");
            var store = new JsonSettingsStore(settingsPath, userDataPath);

            var baseline = AppSettings.Default with
            {
                UserData = new ReusableUserData([], [], enabledVocabularyPackIds: ["tech"], hasSeededStarterSnippets: true),
            };
            await store.SaveAsync(baseline);

            var settingsWriteBefore = File.GetLastWriteTimeUtc(settingsPath);
            await Task.Delay(35);

            var overrides = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
            {
                ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
                {
                    ["accept"] = new VocabularyPackWordOverride(isEnabled: false),
                    ["dockerfile"] = new VocabularyPackWordOverride(aliases: ["doc file", "dickerfile"]),
                },
            };
            var updated = baseline with
            {
                UserData = baseline.UserData.WithVocabularyPackOverrides(overrides),
            };
            await store.SaveAsync(updated);

            // settings.json stays untouched when only user-data pack overrides change.
            Assert.Equal(settingsWriteBefore, File.GetLastWriteTimeUtc(settingsPath));

            var reloaded = await new JsonSettingsStore(settingsPath, userDataPath).LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, reloaded.Status);
            Assert.Equal(updated.UserData, reloaded.Settings.UserData);
            Assert.Equal(2, VocabularyPacks.CountEditedWords(VocabularyPacks.Tech, reloaded.Settings.UserData.VocabularyPackOverrides));
        });
    }

    [Fact]
    public async Task LegacyV29SettingsAndV22PortableProfileMigrateCleanlyAndRoundTripOverrides()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var legacyV29 = JsonSettingsStoreTests.CreatePopulatedSettings() with { SchemaVersion = 29 };
            var v29Node = JsonNode.Parse(JsonSerializer.Serialize(legacyV29, JsonSettingsStore.SerializerOptions))!.AsObject();
            v29Node["userData"]!.AsObject().Remove("vocabularyPackOverrides");
            await File.WriteAllTextAsync(settingsPath, v29Node.ToJsonString(JsonSettingsStore.SerializerOptions));

            var loaded = await new JsonSettingsStore(settingsPath).LoadAsync();
            Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
            Assert.Equal(29, loaded.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
            Assert.Empty(loaded.Settings.UserData.VocabularyPackOverrides);

            // Portable profile v22 migrates to v23 with empty overrides.
            var profileService = new JsonPortableProfileService();
            var legacyProfilePath = Path.Combine(directory, "legacy-v22.json");
            var legacyProfile = new PortableProfile(
                22,
                UserPreferences.Default,
                new ReusableUserData([], [], enabledVocabularyPackIds: ["tech"]));
            var v22ProfileNode = JsonNode.Parse(JsonSerializer.Serialize(legacyProfile, JsonSettingsStore.SerializerOptions))!.AsObject();
            v22ProfileNode["userData"]!.AsObject().Remove("vocabularyPackOverrides");
            await File.WriteAllTextAsync(legacyProfilePath, v22ProfileNode.ToJsonString(JsonSettingsStore.SerializerOptions));

            var migratedProfile = await profileService.ImportAsync(legacyProfilePath);
            Assert.Equal(PortableProfileImportStatus.Imported, migratedProfile.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, migratedProfile.Profile!.SchemaVersion);
            Assert.Empty(migratedProfile.Profile.UserData.VocabularyPackOverrides);

            // Export + Import round-trip preserves VocabularyPackOverrides.
            var overrides = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
            {
                ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
                {
                    ["accept"] = new VocabularyPackWordOverride(isEnabled: false, aliases: ["custom accept"]),
                },
            };
            var profileWithOverrides = new PortableProfile(
                PortableProfile.CurrentSchemaVersion,
                UserPreferences.Default,
                new ReusableUserData([], [], enabledVocabularyPackIds: ["tech"], vocabularyPackOverrides: overrides));
            var exportedPath = Path.Combine(directory, "with-overrides.enviouswispr.json");
            Assert.True((await profileService.ExportAsync(profileWithOverrides, exportedPath)).Succeeded);

            var reimported = await profileService.ImportAsync(exportedPath);
            Assert.Equal(PortableProfileImportStatus.Imported, reimported.Status);
            Assert.Equal(profileWithOverrides, reimported.Profile);

            // Unmapped field inside vocabularyPackOverrides is rejected.
            var badPath = Path.Combine(directory, "bad-override.json");
            var badNode = JsonNode.Parse(await File.ReadAllTextAsync(exportedPath))!.AsObject();
            badNode["userData"]!["vocabularyPackOverrides"]!["tech"]!["accept"]!.AsObject()["unknownField"] = true;
            await File.WriteAllTextAsync(badPath, badNode.ToJsonString(JsonSettingsStore.SerializerOptions));

            var badImport = await profileService.ImportAsync(badPath);
            Assert.Equal(PortableProfileImportStatus.Invalid, badImport.Status);
            Assert.Equal(AppErrorCode.InvalidData, badImport.Error?.Code);
        });
    }

    [Fact]
    public void AppSettingsValidatorRejectsOutOfBoundsPackOverrides()
    {
        var tooManyAliases = Enumerable.Range(0, CustomWordEntry.MaximumAliasesPerEntry + 1)
            .Select(i => $"alias{i}")
            .ToArray();
        var tooManySettings = AppSettings.Default with
        {
            UserData = new ReusableUserData(
                [],
                [],
                vocabularyPackOverrides: new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
                {
                    ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
                    {
                        ["accept"] = new VocabularyPackWordOverride(aliases: tooManyAliases),
                    },
                }),
        };
        Assert.NotNull(AppSettingsValidator.Validate(tooManySettings, AppErrorStage.SettingsSave));

        // Whitespace-only aliases normalize cleanly to an empty list in the model.
        var normalizedWhitespace = new VocabularyPackWordOverride(aliases: ["   "]);
        Assert.NotNull(normalizedWhitespace.Aliases);
        Assert.Empty(normalizedWhitespace.Aliases);

        var overlongAliasSettings = AppSettings.Default with
        {
            UserData = new ReusableUserData(
                [],
                [],
                vocabularyPackOverrides: new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
                {
                    ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
                    {
                        ["accept"] = new VocabularyPackWordOverride(aliases: [new string('a', 257)]),
                    },
                }),
        };
        Assert.NotNull(AppSettingsValidator.Validate(overlongAliasSettings, AppErrorStage.SettingsSave));

        var overlongWordKeySettings = AppSettings.Default with
        {
            UserData = new ReusableUserData(
                [],
                [],
                vocabularyPackOverrides: new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
                {
                    ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
                    {
                        [new string('w', 257)] = new VocabularyPackWordOverride(isEnabled: false),
                    },
                }),
        };
        Assert.NotNull(AppSettingsValidator.Validate(overlongWordKeySettings, AppErrorStage.SettingsSave));

        var longPackKeySettings = AppSettings.Default with
        {
            UserData = new ReusableUserData(
                [],
                [],
                vocabularyPackOverrides: new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
                {
                    [new string('p', 65)] = new Dictionary<string, VocabularyPackWordOverride>
                    {
                        ["accept"] = new VocabularyPackWordOverride(isEnabled: false),
                    },
                }),
        };
        Assert.NotNull(AppSettingsValidator.Validate(longPackKeySettings, AppErrorStage.SettingsSave));
    }

    // ── 4. Lower-authority pack correction tier (macOS #992 parity) ──────────

    [Fact]
    public void PackCanonicalNeverCaseFlattensCapitalizedText()
    {
        // Legal pack ships lowercase canonical "accord" with alias "archord".
        var legalPack = VocabularyPacks.ResolveEntries(["legal"]);

        var capitalized = CustomWordCorrector.Correct("Accord was reached today.", [], legalPack);
        Assert.Equal("Accord was reached today.", capitalized.Text);
        Assert.Equal(0, capitalized.ReplacementCount);

        var misheard = CustomWordCorrector.Correct("The archord was signed.", [], legalPack);
        Assert.Equal("The accord was signed.", misheard.Text);
        Assert.Equal(1, misheard.ReplacementCount);
    }

    [Fact]
    public void ShortPackTermsUnderSevenCharsAreExactOnlyNeverFuzzy()
    {
        var pack = new[]
        {
            new CustomWordEntry(["ekipt"], "accept"),
            new CustomWordEntry(["blizzr"], "Blazor"),
        };

        // Exact alias fires.
        Assert.Equal("please accept this", CustomWordCorrector.Correct("please ekipt this", [], pack).Text);

        // Near-miss of <7 char pack alias or canonical never fuzzy-matches.
        var nearAlias = CustomWordCorrector.Correct("please blizzrs this", [], pack);
        Assert.Equal("please blizzrs this", nearAlias.Text);
        Assert.Equal(0, nearAlias.ReplacementCount);

        var nearShortCanonical = CustomWordCorrector.Correct("try blazox today", [], pack);
        Assert.Equal("try blazox today", nearShortCanonical.Text);
        Assert.Equal(0, nearShortCanonical.ReplacementCount);
    }

    [Fact]
    public void PackFuzzyRequiresSevenPlusCharsAndHigherThresholdAndSuppressesCaseOnlyChanges()
    {
        var pack = new[]
        {
            new CustomWordEntry(["backpapagine"], "backpropagation"),
        };

        // 15-char canonical with 1 edit: similarity 14/15 = 0.933 >= 0.87 -> fires!
        var fuzzyHit = CustomWordCorrector.Correct("we studied backpropagasion today", [], pack);
        Assert.Equal("we studied backpropagation today", fuzzyHit.Text);
        Assert.Equal(1, fuzzyHit.ReplacementCount);

        // Case-only difference on a pack canonical is suppressed (never flattens "Backpropagation").
        var caseOnly = CustomWordCorrector.Correct("Backpropagation works well.", [], pack);
        Assert.Equal("Backpropagation works well.", caseOnly.Text);
        Assert.Equal(0, caseOnly.ReplacementCount);

        // 3 edits on 15-char word: similarity = 12/15 = 0.800.
        // User threshold for 15 chars is 0.82 - 0.035 = 0.785 (matches),
        // while pack threshold adds +0.05 -> 0.835 (refuses).
        var userHit = CustomWordCorrector.Correct("we studied backpropogaxxon today", pack);
        Assert.Equal("we studied backpropagation today", userHit.Text);

        var tooFarForPack = CustomWordCorrector.Correct("we studied backpropogaxxon today", [], pack);
        Assert.Equal("we studied backpropogaxxon today", tooFarForPack.Text);
        Assert.Equal(0, tooFarForPack.ReplacementCount);
    }

    [Fact]
    public void UserWordsAlwaysBeatPackWordsAndAreUnpenalizedByEnablingAllFivePacks()
    {
        var allFivePacks = VocabularyPacks.ResolveEntries(["tech", "medical", "legal", "brands", "names"]);
        // "kortax" vs "Kortex" is 5/6 = 0.8333 (>= 0.82 user threshold when pool <= 10,
        // but < 0.86 if 391 pack words had inflated the user fuzzy pool!).
        var userWords = new[]
        {
            new CustomWordEntry("Kortex", "Kortex"),
            new CustomWordEntry("ekipt", "CustomEquipped"),
            new CustomWordEntry("backpropagasion", "UserBackprop"),
        };

        var result = CustomWordCorrector.Correct(
            "use kortax and ekipt with backpropagasion",
            userWords,
            allFivePacks);

        Assert.Equal("use Kortex and CustomEquipped with UserBackprop", result.Text);
        Assert.Equal(3, result.ReplacementCount);
    }

    [Fact]
    public void MultiWordPackAliasesAreExactOnlyNeverFuzzy()
    {
        var pack = new[]
        {
            new CustomWordEntry(["cue burnett"], "Kubernetes"),
        };

        var exact = CustomWordCorrector.Correct("deploy cue burnett now", [], pack);
        Assert.Equal("deploy Kubernetes now", exact.Text);

        var fuzzyPhrase = CustomWordCorrector.Correct("deploy cue burnet now", [], pack);
        Assert.Equal("deploy cue burnet now", fuzzyPhrase.Text);
        Assert.Equal(0, fuzzyPhrase.ReplacementCount);
    }

    [Fact]
    public void PackWordWithAllAliasesRemovedStillRunsCanonicalFuzzyWhileDisabledWordDoesNothing()
    {
        var clearedAliases = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
        {
            ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
            {
                [VocabularyPacks.OverrideKey("backpropagation")] = new VocabularyPackWordOverride(aliases: []),
            },
        };
        var resolvedCleared = VocabularyPacks.ResolveEntries(["tech"], clearedAliases);

        // Shipped alias "backpapagine" is removed and no longer matches.
        Assert.Equal(
            "check backpapagine now",
            CustomWordCorrector.Correct("check backpapagine now", [], resolvedCleared).Text);

        // Canonical fuzzy (>=7 chars) still works when enabled with empty aliases.
        Assert.Equal(
            "check backpropagation now",
            CustomWordCorrector.Correct("check backpropagasion now", [], resolvedCleared).Text);

        // Disabling the word stops both exact and canonical fuzzy.
        var disabled = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
        {
            ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
            {
                [VocabularyPacks.OverrideKey("backpropagation")] = new VocabularyPackWordOverride(isEnabled: false),
            },
        };
        var resolvedDisabled = VocabularyPacks.ResolveEntries(["tech"], disabled);
        Assert.Equal(
            "check backpapagine and backpropagasion now",
            CustomWordCorrector.Correct("check backpapagine and backpropagasion now", [], resolvedDisabled).Text);
    }

    // ── 5. End-to-end Pipeline, Preview, Finalizer, British Spelling, Repolish, & Self-Learning ──

    [Fact]
    public async Task PipelinePreviewFinalizerBritishSpellingRepolishAndLearningHonorPackOverrides()
    {
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
        {
            ["tech"] = new Dictionary<string, VocabularyPackWordOverride>
            {
                [VocabularyPacks.OverrideKey("accept")] = new VocabularyPackWordOverride(isEnabled: false),
                [VocabularyPacks.OverrideKey("Dockerfile")] = new VocabularyPackWordOverride(aliases: ["doc file"]),
            },
        };

        var pipeline = new DeterministicTextPipeline();
        var options = new DeterministicTextOptions(
            WordCorrectionEnabled: true,
            FillerRemovalEnabled: false,
            EmojiFormatterEnabled: false,
            SpokenPunctuationEnabled: false);

        // 1. DeterministicTextPipeline: disabled "accept" ("ekipt") does not fire;
        //    edited "Dockerfile" fires custom alias "doc file" and no longer fires removed shipped alias "decaphile".
        var pipelineResult = await pipeline.ProcessAsync(new DeterministicTextRequest(
            new Transcript(DictationSessionId.Create(), "check ekipt and decaphile and doc file", "whisper", []),
            CustomWords: [],
            options,
            EnabledVocabularyPackIds: ["tech"],
            VocabularyPackOverrides: overrides));
        Assert.Equal("check ekipt and decaphile and Dockerfile", pipelineResult.Output.Text);

        // 2. LivePreviewTextFormatter honors the exact same overrides.
        var preview = LivePreviewTextFormatter.Format(
            "check ekipt and doc file",
            wordCorrectionEnabled: true,
            customWords: [],
            EnglishSpelling.American,
            enabledVocabularyPackIds: ["tech"],
            vocabularyPackOverrides: overrides);
        Assert.Equal("check ekipt and Dockerfile", preview);

        // 3. TranscriptFinalizer honors the exact same overrides.
        var effects = new NoOpFinalizationEffects();
        var executor = new PolishExecutor(new AllowAllRuntimeAdmission(), effects, () => []);
        var finalizer = new TranscriptFinalizer(
            pipeline,
            executor,
            effects,
            new SnippetExpansionStage(
                new SnippetExpander(),
                TimeProvider.System,
                () => System.Globalization.CultureInfo.InvariantCulture,
                _ => Task.FromResult<string?>(null),
                TimeSpan.FromSeconds(1)));
        var finalized = await finalizer.FinalizeAsync(
            new Transcript(DictationSessionId.Create(), "check ekipt and doc file", "whisper", []),
            customWords: [],
            options,
            SnippetVocabulary.Empty,
            polish: null,
            targetAppName: null,
            enabledVocabularyPackIds: ["tech"],
            vocabularyPackOverrides: overrides,
            cancellationToken: CancellationToken.None);
        Assert.Equal("check ekipt and Dockerfile", finalized.Processed.Output.Text);

        // 4. CorrectionCandidateFilter respects active overrides:
        //    - Disabled "accept" ("ekipt") is NO LONGER pack-owned, so self-learning sees it as learnable.
        //    - Custom alias "doc file" -> "Dockerfile" IS pack-owned, so self-learning skips it.
        var activePackEntries = VocabularyPacks.ResolveEntries(["tech"], overrides);
        var filtered = CorrectionCandidateFilter.Filter(
            [
                new EditAlignment.Run("ekipt", "accept", 0, 1, 0, 1, [EditAlignment.Label.Substitute]),
                new EditAlignment.Run("doc file", "Dockerfile", 2, 4, 2, 3, [EditAlignment.Label.Substitute]),
            ],
            new CorrectionCandidateFilter.Inputs([], activePackEntries, new HashSet<string>(StringComparer.Ordinal)));

        Assert.IsType<CorrectionCandidateFilter.Disposition.Candidate>(filtered[0].Disposition);
        Assert.IsType<CorrectionCandidateFilter.Disposition.AlreadyCovered>(filtered[1].Disposition);

        // 5. HistoryRepolishService + British spelling protection honor pack overrides:
        //    Legal pack ships "offense"; while enabled, British spelling protects "offense",
        //    and once disabled via VocabularyPackOverrides, "offense" converts to "offence".
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var britishOptions = options with { EnglishSpelling = EnglishSpelling.British };

        var protectedStore = new InMemoryHistoryStore(now);
        var protectedEntry = new DictationHistoryEntry(
            Guid.NewGuid(),
            now,
            "old text",
            "whisper",
            WasPolished: false,
            WasDelivered: true)
        {
            OriginalText = "the color of the offense",
            LanguageCode = "en",
        };
        protectedStore.Entries.Add(protectedEntry);

        var polishSetup = new PolishSetup(
            new StubPolishProvider("stub", text => text),
            UsesLocalRuntime: false,
            RuntimeResourceKind.Cpu,
            "stub-model");
        var repolishEnabled = new HistoryRepolishService(
            protectedStore,
            executor,
            pipeline,
            () => polishSetup,
            textOptions: () => britishOptions,
            customWords: () => [],
            enabledVocabularyPackIds: () => ["legal"],
            vocabularyPackOverrides: () => VocabularyPacks.EmptyOverrides);
        var protectedOutcome = await repolishEnabled.RepolishAsync(protectedEntry.Id);
        Assert.True(protectedOutcome.Succeeded);
        Assert.Equal("the colour of the offense", protectedOutcome.Entry!.Text);

        var disabledOffenseOverrides = new Dictionary<string, IReadOnlyDictionary<string, VocabularyPackWordOverride>>
        {
            ["legal"] = new Dictionary<string, VocabularyPackWordOverride>
            {
                [VocabularyPacks.OverrideKey("offense")] = new VocabularyPackWordOverride(isEnabled: false),
            },
        };
        var disabledStore = new InMemoryHistoryStore(now);
        var disabledEntry = protectedEntry with { Id = Guid.NewGuid() };
        disabledStore.Entries.Add(disabledEntry);

        var repolishDisabled = new HistoryRepolishService(
            disabledStore,
            executor,
            pipeline,
            () => polishSetup,
            textOptions: () => britishOptions,
            customWords: () => [],
            enabledVocabularyPackIds: () => ["legal"],
            vocabularyPackOverrides: () => disabledOffenseOverrides);
        var disabledOutcome = await repolishDisabled.RepolishAsync(disabledEntry.Id);
        Assert.True(disabledOutcome.Succeeded);
        Assert.Equal("the colour of the offence", disabledOutcome.Entry!.Text);
    }

    private sealed class RecordingStore : ISettingsStore
    {
        public AppSettings? Saved { get; private set; }

        public int Saves { get; private set; }

        public Exception? FailNext { get; set; }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            Saved = settings;
            Saves++;
            return Task.CompletedTask;
        }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpFinalizationEffects : ITranscriptFinalizationEffects
    {
        public void RecordDeterministicProcessingStarted() { }
        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly) { }
        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken) => Task.CompletedTask;
        public void RecordPolishRefused() { }
        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds, bool empty = false) { }
        public void RecordPolishStarted(string providerId) { }
        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) { }
    }

    private sealed class AllowAllRuntimeAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new DummyLease()));

        private sealed class DummyLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryHistoryStore(DateTimeOffset now) : IHistoryStore
    {
        public List<DictationHistoryEntry> Entries { get; } = [];

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset currentNow, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult([.. Entries], HistoryLoadStatus.Loaded));

        public Task<DictationHistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset currentNow, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryRepolishUpdateResult> UpdateRepolishedAsync(
            Guid id,
            string expectedCurrentText,
            string newText,
            bool wasPolished,
            string? providerId,
            string? modelId,
            CancellationToken cancellationToken = default)
        {
            var index = Entries.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return Task.FromResult(HistoryRepolishUpdateResult.NotFound());
            }

            var existing = Entries[index];
            if (existing.ExpiresAt is { } exp && exp <= now)
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
            Entries[index] = updated;
            return Task.FromResult(HistoryRepolishUpdateResult.Updated(updated));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Entries.RemoveAll(e => e.Id == id);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default)
        {
            Entries.Clear();
            return Task.FromResult(new HistoryOperationResult(true));
        }
    }

    private sealed class StubPolishProvider(string providerId, Func<string, string> transform) : IPolishProvider
    {
        public string ProviderId => providerId;

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolishResult(
                request.Input with { Text = transform(request.Input.Text) },
                PolishAttemptStatus.Polished));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
