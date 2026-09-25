using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// What a word or a snippet does to the list, without the page: a twin is replaced rather than
/// duplicated, the strictness given is the strictness stored, a removal matches rows by identity
/// and says how many really went, two edits landing together both survive, and a refused save
/// leaves the list as it was.
/// </summary>
public sealed class VocabularyPresenterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AWordIsAddedUnderItsStrictnessAndTheListStaysSorted()
    {
        var (presenter, store) = Build(words: [new CustomWordEntry("zed", "Z")]);

        var result = await presenter.AddWordAsync(new CustomWordEntry("alpha", "A", MatchStrictness.Strict)).WaitAsync(Patience);

        Assert.True(result.Saved);
        var words = store.Saved!.UserData.CustomWords;
        Assert.Equal(["alpha", "zed"], words.Select(word => word.SpokenForm));
        Assert.Equal(MatchStrictness.Strict, words[0].Strictness);
    }

    [Fact]
    public async Task AWordSpokenTheSameWayReplacesItsTwinWhateverTheCase()
    {
        var (presenter, store) = Build(words: [new CustomWordEntry("Envy", "Old"), new CustomWordEntry("other", "O")]);

        await presenter.AddWordAsync(new CustomWordEntry("envy", "EnviousWispr")).WaitAsync(Patience);

        var words = store.Saved!.UserData.CustomWords;
        Assert.Equal(2, words.Count);
        var envy = Assert.Single(words, word => string.Equals(word.SpokenForm, "envy", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("EnviousWispr", envy.Replacement);
    }

    [Fact]
    public async Task ARemovalMatchesRowsByIdentityAndSaysHowManyReallyWent()
    {
        // The selection carries the row objects the page showed. A value-equal twin that is a
        // different row stays, and a selected row that another change replaced meanwhile is left
        // alone - the count says what was really removed.
        var selected = new CustomWordEntry("one", "1");
        var twin = new CustomWordEntry("one", "1");
        var replaced = new CustomWordEntry("two", "2");
        var replacement = new CustomWordEntry("two", "2");
        var (presenter, store) = Build(words: [selected, twin, replacement]);

        var result = await presenter.RemoveWordsAsync([selected, replaced]).WaitAsync(Patience);

        Assert.True(result.Saved);
        Assert.Equal(1, result.Value);
        var survivors = store.Saved!.UserData.CustomWords;
        Assert.Equal(2, survivors.Count);
        // THE VERY ROWS, NOT VALUE-EQUAL ONES: the twin and the replacement survive as themselves.
        Assert.Same(twin, survivors[0]);
        Assert.Same(replacement, survivors[1]);
    }

    [Fact]
    public async Task ASnippetReplacesOneOfTheSameNameAndARemovalMatchesByValue()
    {
        var (presenter, store) = Build(snippets: [new SnippetEntry("Sig", "old"), new SnippetEntry("Address", "x")]);

        await presenter.AddSnippetAsync(new SnippetEntry("sig", "new")).WaitAsync(Patience);
        var snippets = store.Saved!.UserData.Snippets;
        Assert.Equal(["Address", "sig"], snippets.Select(snippet => snippet.Name));
        Assert.Equal("new", snippets[1].Body);

        await presenter.RemoveSnippetAsync(new SnippetEntry("Address", "x")).WaitAsync(Patience);
        Assert.Equal(["sig"], store.Saved!.UserData.Snippets.Select(snippet => snippet.Name));
    }

    /// <summary>
    /// A TRIGGER ANOTHER SNIPPET ALREADY ANSWERS TO IS REFUSED, named, and nothing is stored: "My Email."
    /// is the same spoken words as "my email", so saving both would make which one fires a tie nobody
    /// chose. The same name, ignoring case, is still the edit it always was.
    /// </summary>
    [Fact]
    public async Task ADuplicateTriggerIsRefusedAndNamedAndTheSameNameStillReplaces()
    {
        var existing = new SnippetEntry("my email", "sam@example.com");
        var (presenter, store) = Build(snippets: [existing]);

        var duplicate = await presenter.AddSnippetAsync(new SnippetEntry("My Email.", "other@example.com")).WaitAsync(Patience);

        Assert.True(duplicate.Saved);
        Assert.Equal(new SnippetSaveOutcome(SnippetRefusal.DuplicateTrigger, existing), duplicate.Value);
        Assert.True(store.Saved is null || store.Saved.UserData.Snippets.SequenceEqual([existing]), "a refused snippet changed the list");

        var edit = await presenter.AddSnippetAsync(new SnippetEntry("MY EMAIL", "new@example.com")).WaitAsync(Patience);

        Assert.Null(edit.Value.Refusal);
        Assert.Equal([new SnippetEntry("MY EMAIL", "new@example.com")], store.Saved!.UserData.Snippets);
    }

    [Theory]
    [InlineData("...", "some text", SnippetRefusal.TriggerEmpty)]
    [InlineData("   ", "some text", SnippetRefusal.TriggerEmpty)]
    [InlineData("my email", "   \n ", SnippetRefusal.ExpansionEmpty)]
    public async Task ASnippetNobodyCanSayOrThatPastesNothingIsRefused(string trigger, string body, SnippetRefusal expected)
    {
        var (presenter, store) = Build();

        var result = await presenter.AddSnippetAsync(new SnippetEntry(trigger, body)).WaitAsync(Patience);

        Assert.Equal(expected, result.Value.Refusal);
        Assert.Empty((store.Saved?.UserData ?? ReusableUserData.Empty).Snippets);
    }

    /// <summary>
    /// The keyword is stored in its comparison form, a cleared box stores the default, two words are
    /// refused before anything is written, and a later word or snippet change keeps the keyword.
    /// </summary>
    [Fact]
    public async Task TheKeywordIsCleanedStoredAndKeptByEveryLaterChange()
    {
        var (presenter, store) = Build(snippets: [new SnippetEntry("my email", "sam@example.com")]);

        var set = await presenter.SetSnippetKeywordAsync("  Insert. ").WaitAsync(Patience);
        Assert.Equal(new SnippetKeywordOutcome(null, "insert"), set.Value);
        Assert.Equal("insert", store.Saved!.UserData.SnippetKeyword);

        await presenter.AddWordAsync(new CustomWordEntry("envy", "EnviousWispr")).WaitAsync(Patience);
        await presenter.AddSnippetAsync(new SnippetEntry("my cell", "555-0100")).WaitAsync(Patience);
        await presenter.RemoveSnippetAsync(new SnippetEntry("my cell", "555-0100")).WaitAsync(Patience);
        Assert.Equal("insert", store.Saved!.UserData.SnippetKeyword);

        var saves = store.Saves;
        var twoWords = await presenter.SetSnippetKeywordAsync("hey wispr").WaitAsync(Patience);
        Assert.Equal(new SnippetKeywordOutcome(SnippetRefusal.KeywordNotOneWord, null), twoWords.Value);
        Assert.Equal(saves, store.Saves);
        Assert.Equal("insert", store.Saved!.UserData.SnippetKeyword);

        var cleared = await presenter.SetSnippetKeywordAsync("   ").WaitAsync(Patience);
        Assert.Equal(new SnippetKeywordOutcome(null, "backslash"), cleared.Value);
        Assert.Equal("backslash", store.Saved!.UserData.SnippetKeyword);
    }

    [Fact]
    public async Task TwoEditsLandingTogetherBothSurvive()
    {
        var (presenter, store) = Build();
        store.Hold = true;
        var word = presenter.AddWordAsync(new CustomWordEntry("one", "1"));
        await store.SaveStarted.Task.WaitAsync(Patience);
        var snippet = presenter.AddSnippetAsync(new SnippetEntry("s", "body"));

        store.Release.SetResult();
        Assert.True((await word.WaitAsync(Patience)).Saved);
        Assert.True((await snippet.WaitAsync(Patience)).Saved);

        Assert.Single(store.Saved!.UserData.CustomWords);
        Assert.Single(store.Saved.UserData.Snippets);
    }

    [Fact]
    public async Task ARefusedSaveLeavesTheListAsItWasAndNamesTheCause()
    {
        var (presenter, store) = Build(words: [new CustomWordEntry("keep", "K")]);
        store.FailNext = new ArgumentException("too many words");

        var result = await presenter.AddWordAsync(new CustomWordEntry("more", "M")).WaitAsync(Patience);

        Assert.False(result.Saved);
        Assert.Equal(SettingsSaveRefusal.InvalidValues, result.Refusal);
        Assert.Null(store.Saved);

        // The next change still derives from the list that was really kept.
        await presenter.AddSnippetAsync(new SnippetEntry("s", "b")).WaitAsync(Patience);
        Assert.Equal(["keep"], store.Saved!.UserData.CustomWords.Select(word => word.SpokenForm));
    }

    private static (VocabularyPresenter Presenter, RecordingStore Store) Build(
        IReadOnlyList<CustomWordEntry>? words = null,
        IReadOnlyList<SnippetEntry>? snippets = null)
    {
        var store = new RecordingStore();
        var settings = AppSettings.Default with { UserData = new ReusableUserData(words ?? [], snippets ?? []) };
        return (new VocabularyPresenter(new SettingsPresenter(store, settings)), store);
    }

    private sealed class RecordingStore : ISettingsStore
    {
        public AppSettings? Saved { get; private set; }

        /// <summary>How many writes landed, so a refusal can be shown to have written nothing.</summary>
        public int Saves { get; private set; }

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
            Saves++;
        }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
