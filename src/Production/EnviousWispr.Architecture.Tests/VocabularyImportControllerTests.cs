using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// What a word list does to the dictionary, without the page: additions land, conflicts are offered
/// rather than decided, the plan is computed against the words that are current when it is applied
/// - even when an edit lands while the import waits - and a refused save keeps every entry.
/// </summary>
public sealed class VocabularyImportControllerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AnImportAddsWhatIsNewAndReportsTheRest()
    {
        var (controller, store) = Build(words: [new CustomWordEntry("envy", "EnviousWispr"), new CustomWordEntry("same", "Same")]);

        var result = await controller.ImportAsync("envy, Envy Corp\nsame, Same\nnew, New\nnot a pair").WaitAsync(Patience);

        Assert.True(result.Saved);
        var plan = result.Value;
        Assert.Equal(["new"], plan.Additions.Select(word => word.SpokenForm));
        Assert.Equal(["envy"], plan.Conflicts.Select(word => word.SpokenForm));
        Assert.Equal(1, plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.AlreadyPresent));
        var stored = store.Saved!.UserData.CustomWords;
        Assert.Equal(3, stored.Count);
        Assert.Equal("EnviousWispr", stored.Single(word => word.SpokenForm == "envy").Replacement);
    }

    [Fact]
    public async Task APackTakesExactlyThePathAFileTakes()
    {
        var (controller, store) = Build();
        var pack = new VocabularyPack("test", "Test pack", "for the test", "alpha, A\nbeta, B");

        var result = await controller.ApplyPackAsync(pack).WaitAsync(Patience);

        Assert.True(result.Saved);
        Assert.Equal(2, result.Value.Additions.Count);
        Assert.Equal(["alpha", "beta"], store.Saved!.UserData.CustomWords.Select(word => word.SpokenForm));

        // Applied twice, the second time has nothing to add and says so.
        var again = await controller.ApplyPackAsync(pack).WaitAsync(Patience);
        Assert.Empty(again.Value.Additions);
    }

    [Fact]
    public async Task ConflictsAreComputedAgainstTheWordsCurrentWhenTheImportIsApplied()
    {
        // AN EDIT LANDS WHILE THE IMPORT WAITS. The person adds their own correction for "envy"
        // while the import is queued behind another save; the import then sees that correction and
        // offers the conflict instead of overwriting it or adding a duplicate.
        var (controller, store) = Build();
        var vocabulary = new VocabularyPresenter(controller.Settings);
        store.Hold = true;
        var edit = vocabulary.AddWordAsync(new CustomWordEntry("envy", "Mine"));
        await store.SaveStarted.Task.WaitAsync(Patience);
        var import = controller.ImportAsync("envy, Theirs\nother, O");

        store.Release.SetResult();
        Assert.True((await edit.WaitAsync(Patience)).Saved);
        var result = await import.WaitAsync(Patience);

        Assert.True(result.Saved);
        Assert.Equal(["envy"], result.Value.Conflicts.Select(word => word.SpokenForm));
        Assert.Equal(["other"], result.Value.Additions.Select(word => word.SpokenForm));
        var stored = store.Saved!.UserData.CustomWords;
        Assert.Equal("Mine", stored.Single(word => word.SpokenForm == "envy").Replacement);
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task ReplacingConflictsMergesTheListsVersionInAgainstTheCurrentWords()
    {
        var (controller, store) = Build(words: [new CustomWordEntry("envy", "Mine", MatchStrictness.Strict)]);
        var theirs = new CustomWordEntry("envy", "Theirs");

        var result = await controller.ReplaceConflictsAsync([theirs]).WaitAsync(Patience);

        Assert.True(result.Saved);
        var stored = Assert.Single(store.Saved!.UserData.CustomWords);
        Assert.Equal("Theirs", stored.Replacement);
        Assert.Equal(MatchStrictness.Default, stored.Strictness);
    }

    [Fact]
    public async Task ARefusedSaveKeepsEveryExistingEntryAndNamesTheCause()
    {
        var (controller, store) = Build(words: [new CustomWordEntry("keep", "K")]);
        store.FailNext = new ArgumentException("ceiling");

        var result = await controller.ImportAsync("new, N").WaitAsync(Patience);

        Assert.False(result.Saved);
        Assert.Equal(SettingsSaveRefusal.InvalidValues, result.Refusal);
        Assert.Null(store.Saved);
        Assert.Equal(["keep"], controller.Settings.Current.UserData.CustomWords.Select(word => word.SpokenForm));
    }

    private static (TestController Controller, RecordingStore Store) Build(IReadOnlyList<CustomWordEntry>? words = null)
    {
        var store = new RecordingStore();
        var settings = new SettingsPresenter(store, AppSettings.Default with { UserData = new ReusableUserData(words ?? [], []) });
        return (new TestController(settings), store);
    }

    /// <summary>The controller with the settings presenter it was built over, for the tests that edit beside it.</summary>
    private sealed class TestController
    {
        private readonly VocabularyImportController _inner;

        public TestController(SettingsPresenter settings)
        {
            Settings = settings;
            _inner = new VocabularyImportController(new VocabularyPresenter(settings));
        }

        public SettingsPresenter Settings { get; }

        public Task<SettingsSaveResult<CustomWordImportPlan>> ImportAsync(string text) => _inner.ImportAsync(text);

        public Task<SettingsSaveResult<CustomWordImportPlan>> ApplyPackAsync(VocabularyPack pack) => _inner.ApplyPackAsync(pack);

        public Task<SettingsSaveResult> ReplaceConflictsAsync(IReadOnlyList<CustomWordEntry> replacements) => _inner.ReplaceConflictsAsync(replacements);
    }

    private sealed class RecordingStore : ISettingsStore
    {
        public AppSettings? Saved { get; private set; }

        public Exception? FailNext { get; set; }

        public bool Hold { get; set; }

        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            if (Hold)
            {
                await Release.Task;
            }

            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            Saved = settings;
        }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
