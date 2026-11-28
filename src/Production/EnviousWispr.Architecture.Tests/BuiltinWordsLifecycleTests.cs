using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Presentation.Learning;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Verifies the two-tier built-in word lifecycle: fresh install &amp; upgrade merge, tombstones on
/// deletion, promotion to user override on edit/rename, tombstone restore on re-add/import,
/// self-learning + undo, user-only CSV export, PortableProfile round-trip, and validation caps.
/// </summary>
public sealed class BuiltinWordsLifecycleTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FreshInstallAndLegacyProfileBothMergeAllEighteenBuiltinsWithoutPersistingThem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ew-builtin-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settingsPath = Path.Combine(dir, "settings.json");
            var userDataPath = Path.Combine(dir, "user-data.json");

            var store = new JsonSettingsStore(settingsPath, userDataPath);
            var fresh = await store.LoadAsync().WaitAsync(Patience);
            Assert.Equal(SettingsLoadStatus.Missing, fresh.Status);
            Assert.Empty(fresh.Settings.UserData.CustomWords);
            Assert.Equal(18, fresh.Settings.UserData.EffectiveCustomWords().Count);

            // Simulate an upgraded user-data.json written before DeletedBuiltinWordIds existed,
            // carrying one personal word that overrides the built-in GitHub canonical.
            var userGitHub = new CustomWordEntry(["get hub", "git hub custom"], "GitHub", MatchStrictness.Strict);
            var personal = new CustomWordEntry(["coobernetes"], "Kubernetes", MatchStrictness.Default, WordCategory.Domain);
            var legacyData = new ReusableUserData([userGitHub, personal], []);
            await store.SaveAsync(fresh.Settings with { UserData = legacyData }).WaitAsync(Patience);

            var reloaded = await store.LoadAsync().WaitAsync(Patience);
            var effective = reloaded.Settings.UserData.EffectiveCustomWords();
            Assert.Equal(19, effective.Count); // 17 non-overridden built-ins + 2 user words
            var resolvedGitHub = Assert.Single(effective, e => string.Equals(e.Replacement, "GitHub", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(CustomWordOrigin.User, resolvedGitHub.Origin);
            Assert.Equal(MatchStrictness.Strict, resolvedGitHub.Strictness);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeletingBuiltinPersistsTombstoneAcrossReloadAndDoesNotAffectSiblings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ew-builtin-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new JsonSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "user-data.json"));
            var loaded = await store.LoadAsync().WaitAsync(Patience);
            var presenter = new SettingsPresenter(store, loaded.Settings);
            var vocab = new VocabularyPresenter(presenter);

            var github = Assert.Single(loaded.Settings.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub");
            var removal = await vocab.RemoveWordsAndReportAsync([github]).WaitAsync(Patience);

            Assert.True(removal.Saved);
            Assert.Single(removal.Value);
            Assert.Equal(["github"], presenter.Current.UserData.DeletedBuiltinWordIds);
            Assert.DoesNotContain(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub");
            Assert.Equal(17, presenter.Current.UserData.EffectiveCustomWords().Count);

            var reloadedStore = new JsonSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "user-data.json"));
            var reloaded = await reloadedStore.LoadAsync().WaitAsync(Patience);
            Assert.Equal(["github"], reloaded.Settings.UserData.DeletedBuiltinWordIds);
            Assert.DoesNotContain(reloaded.Settings.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub");
            Assert.Equal(17, reloaded.Settings.UserData.EffectiveCustomWords().Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task EditingBuiltinCreatesUserOverrideAndRenamingRetiresOldBuiltin()
    {
        var (vocab, presenter, _) = Build();
        var github = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub");

        // Same-canonical edit: saves user override without tombstoning and leaves no duplicate.
        var sameCanonicalEdit = await vocab.UpdateMultiAliasWordAsync(
            github,
            ["git hub", "get hub", "gh"],
            "GitHub",
            MatchStrictness.Strict,
            WordCategory.Brand).WaitAsync(Patience);
        Assert.True(sameCanonicalEdit.Saved);
        Assert.Null(sameCanonicalEdit.Value.Refusal);
        Assert.Empty(presenter.Current.UserData.DeletedBuiltinWordIds);
        var overrideRow = Assert.Single(presenter.Current.UserData.CustomWords);
        Assert.Equal(CustomWordOrigin.User, overrideRow.Origin);
        Assert.Equal(github.Id, overrideRow.Id);
        Assert.Equal(18, presenter.Current.UserData.EffectiveCustomWords().Count);

        // Rename away from built-in canonical: retires old built-in via tombstone so it doesn't duplicate.
        var renameEdit = await vocab.UpdateMultiAliasWordAsync(
            overrideRow,
            ["git lab", "get lab"],
            "GitLab",
            MatchStrictness.Default,
            WordCategory.Brand).WaitAsync(Patience);
        Assert.True(renameEdit.Saved);
        Assert.Null(renameEdit.Value.Refusal);
        Assert.Contains("github", presenter.Current.UserData.DeletedBuiltinWordIds);
        Assert.DoesNotContain(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub");
        Assert.Contains(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "GitLab");
        Assert.Equal(18, presenter.Current.UserData.EffectiveCustomWords().Count);
    }

    [Fact]
    public async Task DeletingUserOverrideOfBuiltinTombstonesBuiltinSoItStaysDeleted()
    {
        var (vocab, presenter, _) = Build();
        var openai = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "OpenAI");

        var edit = await vocab.UpdateMultiAliasWordAsync(
            openai,
            ["open AI", "open A I", "open eye"],
            "OpenAI",
            MatchStrictness.Default,
            WordCategory.Brand).WaitAsync(Patience);
        Assert.True(edit.Saved);

        var overrideEntry = Assert.Single(presenter.Current.UserData.CustomWords);
        var removal = await vocab.RemoveWordsAndReportAsync([overrideEntry]).WaitAsync(Patience);
        Assert.True(removal.Saved);
        Assert.Empty(presenter.Current.UserData.CustomWords);
        Assert.Contains("openai", presenter.Current.UserData.DeletedBuiltinWordIds);
        Assert.DoesNotContain(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "OpenAI");
    }

    [Fact]
    public async Task ReAddingOrImportingDeletedBuiltinClearsTombstoneCleanly()
    {
        var (vocab, presenter, _) = Build();
        var importer = new VocabularyImportController(vocab);
        var airpods = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "AirPods");
        var gmail = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "Gmail");
        var wifi = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "Wi-Fi");

        await vocab.RemoveWordsAsync([airpods, gmail, wifi]).WaitAsync(Patience);
        Assert.Equal(3, presenter.Current.UserData.DeletedBuiltinWordIds.Count);

        // 1. Re-add via AddMultiAliasWordAsync with no extra aliases -> restores shipped built-in cleanly.
        var restoreAirPods = await vocab.AddMultiAliasWordAsync(
            [],
            "AirPods",
            MatchStrictness.Default,
            WordCategory.Brand).WaitAsync(Patience);
        Assert.True(restoreAirPods.Saved);
        Assert.Null(restoreAirPods.Value.Refusal);
        Assert.DoesNotContain("airpods", presenter.Current.UserData.DeletedBuiltinWordIds);
        Assert.Contains(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "AirPods" && e.Origin == CustomWordOrigin.Builtin);

        // 2. Re-add via AddWordAsync -> clears tombstone and preserves shipped aliases.
        await vocab.AddWordAsync("gee mail", "Gmail").WaitAsync(Patience);
        Assert.DoesNotContain("gmail", presenter.Current.UserData.DeletedBuiltinWordIds);
        var restoredGmail = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "Gmail");
        Assert.Contains("gmail", restoredGmail.Aliases);
        Assert.Contains("gee mail", restoredGmail.Aliases);

        // 3. Re-add via ImportAsync -> clears tombstone.
        var importResult = await importer.ImportAsync("why fi,Wi-Fi").WaitAsync(Patience);
        Assert.True(importResult.Saved);
        Assert.DoesNotContain("wifi", presenter.Current.UserData.DeletedBuiltinWordIds);
        Assert.Contains(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "Wi-Fi");
    }

    [Fact]
    public async Task SelfLearningOverActiveAndDeletedBuiltinAndUndoFollowsTwoTierRules()
    {
        var (vocab, presenter, _) = Build();
        var github = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub");

        // 1. Learn onto active built-in -> creates user override; Undo removes override WITHOUT tombstoning.
        var activeLearn = await vocab.SaveLearnedWordAsync(
            "guitab",
            "GitHub",
            new LearnTargetState.ExistingWord(github.Id),
            packTerms: [],
            learnedAt: DateTimeOffset.UtcNow).WaitAsync(Patience);
        Assert.True(activeLearn.Saved);
        Assert.Equal(LearnedSaveStatus.Saved, activeLearn.Value.Status);
        Assert.False(activeLearn.Value.RedeleteBuiltinOnUndo);
        Assert.Equal(github, activeLearn.Value.Before);
        Assert.NotNull(activeLearn.Value.After);
        Assert.Equal(CustomWordOrigin.User, activeLearn.Value.After!.Origin);
        Assert.True(activeLearn.Value.After.IsAutoLearned);

        var undoActive = await vocab.UndoLearnedWordAsync(
            activeLearn.Value.After,
            activeLearn.Value.Before,
            activeLearn.Value.RedeleteBuiltinOnUndo).WaitAsync(Patience);
        Assert.True(undoActive.Saved);
        Assert.Equal(LearnedUndoWriteStatus.Applied, undoActive.Value);
        Assert.Empty(presenter.Current.UserData.CustomWords);
        Assert.Empty(presenter.Current.UserData.DeletedBuiltinWordIds);
        Assert.Contains(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub" && e.Origin == CustomWordOrigin.Builtin);

        // 2. Delete built-in, learn over it -> clears tombstone; Undo removes override AND re-tombstones built-in.
        await vocab.RemoveWordsAsync([github]).WaitAsync(Patience);
        Assert.Contains("github", presenter.Current.UserData.DeletedBuiltinWordIds);

        var deletedLearn = await vocab.SaveLearnedWordAsync(
            "guitab",
            "GitHub",
            new LearnTargetState.NewWord(),
            packTerms: [],
            learnedAt: DateTimeOffset.UtcNow).WaitAsync(Patience);
        Assert.True(deletedLearn.Saved);
        Assert.Equal(LearnedSaveStatus.Saved, deletedLearn.Value.Status);
        Assert.True(deletedLearn.Value.RedeleteBuiltinOnUndo);
        Assert.DoesNotContain("github", presenter.Current.UserData.DeletedBuiltinWordIds);

        var undoDeleted = await vocab.UndoLearnedWordAsync(
            deletedLearn.Value.After!,
            deletedLearn.Value.Before,
            deletedLearn.Value.RedeleteBuiltinOnUndo).WaitAsync(Patience);
        Assert.True(undoDeleted.Saved);
        Assert.Equal(LearnedUndoWriteStatus.Applied, undoDeleted.Value);
        Assert.Empty(presenter.Current.UserData.CustomWords);
        Assert.Contains("github", presenter.Current.UserData.DeletedBuiltinWordIds);
        Assert.DoesNotContain(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "GitHub");
    }

    [Fact]
    public async Task CsvExportExcludesUntouchedBuiltinsWhilePortableProfilePreservesTombstones()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ew-builtin-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (vocab, presenter, _) = Build();
            Assert.Empty(presenter.Current.UserData.CustomWords);
            Assert.Equal(CustomWordImport.Header, CustomWordImport.Write(presenter.Current.UserData.CustomWords));

            // Edit one built-in and delete another.
            var openai = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "OpenAI");
            var chatgpt = Assert.Single(presenter.Current.UserData.EffectiveCustomWords(), e => e.Replacement == "ChatGPT");
            await vocab.UpdateMultiAliasWordAsync(openai, ["open AI", "open eye"], "OpenAI", MatchStrictness.Default, WordCategory.Brand).WaitAsync(Patience);
            await vocab.RemoveWordsAsync([chatgpt]).WaitAsync(Patience);

            var csv = CustomWordImport.Write(presenter.Current.UserData.CustomWords);
            Assert.Contains("open eye,OpenAI", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("EnviousWispr", csv, StringComparison.Ordinal);

            var profilePath = Path.Combine(dir, "profile.json");
            var profileService = new JsonPortableProfileService();
            var exported = await profileService.ExportAsync(presenter.Current.ToPortableProfile(), profilePath).WaitAsync(Patience);
            Assert.True(exported.Succeeded);

            var imported = await profileService.ImportAsync(profilePath).WaitAsync(Patience);
            Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
            Assert.NotNull(imported.Profile);
            Assert.Equal(["chatgpt"], imported.Profile!.UserData.DeletedBuiltinWordIds);
            Assert.DoesNotContain(imported.Profile.UserData.EffectiveCustomWords(), e => e.Replacement == "ChatGPT");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ValidatorRejectsOversizedOrBlankDeletedBuiltinIds()
    {
        var tooMany = Enumerable.Range(0, BuiltinWords.MaximumDeletedBuiltinIds + 1)
            .Select(i => $"id-{i}")
            .ToArray();
        var badCount = AppSettings.Default with
        {
            UserData = new ReusableUserData([], [], deletedBuiltinWordIds: tooMany),
        };
        Assert.NotNull(AppSettingsValidator.Validate(badCount, Core.Errors.AppErrorStage.SettingsLoad));

        var tooLong = AppSettings.Default with
        {
            UserData = new ReusableUserData([], [], deletedBuiltinWordIds: [new string('x', 129)]),
        };
        Assert.NotNull(AppSettingsValidator.Validate(tooLong, Core.Errors.AppErrorStage.SettingsLoad));
    }

    [Fact]
    public void BuiltinClaudeIsExcludedFromAutoLearnedFilterChip()
    {
        var effective = AppSettings.FreshInstall.UserData.EffectiveCustomWords();
        var autoLearned = VocabularyPresenter.FilterWords(effective, "auto-learned");
        Assert.Empty(autoLearned);
    }

    private static (VocabularyPresenter Vocab, SettingsPresenter Settings, RecordingStore Store) Build()
    {
        var store = new RecordingStore();
        var settings = new SettingsPresenter(store, AppSettings.FreshInstall);
        return (new VocabularyPresenter(settings), settings, store);
    }

    private sealed class RecordingStore : ISettingsStore
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
