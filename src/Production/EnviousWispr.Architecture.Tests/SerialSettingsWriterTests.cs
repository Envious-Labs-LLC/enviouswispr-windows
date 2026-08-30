using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Two changes made at the same time both survive.
/// </summary>
/// <remarks>
/// THE DEFECT ONLY EXISTS WHILE TWO SAVES OVERLAP, which is why it lived so long: every test drove
/// one call at a time and every one of them passed. The store here can be held open in the middle of
/// a save, which is the only way to put two writers in the same moment on purpose.
/// </remarks>
public sealed class SerialSettingsWriterTests
{
    [Fact]
    public async Task TwoOverlappingChangesBothSurvive()
    {
        var store = new BlockingStore();
        using var writer = new SerialSettingsWriter(store, AppSettings.Default);

        // The first change is caught mid-save and held there.
        var first = writer.UpdateAsync(current => current with { LaunchCount = 7 });
        await store.SaveStarted.Task.ConfigureAwait(true);

        // The second arrives while the first is still inside the store.
        var second = writer.UpdateAsync(current => current with { HasCompletedOnboarding = true });

        store.LetSavesFinish();
        Assert.Null(await first.ConfigureAwait(true));
        Assert.Null(await second.ConfigureAwait(true));

        // BOTH, WHICH IS THE WHOLE POINT. Before the gate, the second wrote a record built from
        // settings that did not have LaunchCount 7 in them, and the count vanished.
        Assert.Equal(7, writer.Current.LaunchCount);
        Assert.True(writer.Current.HasCompletedOnboarding);
        Assert.Equal(7, store.LastSaved!.LaunchCount);
        Assert.True(store.LastSaved.HasCompletedOnboarding);
    }

    [Fact]
    public async Task TwoOverlappingWordChangesBothSurvive()
    {
        // THE SHAPE THAT ACTUALLY HAPPENS. Two edits to the same list, each deriving its new list
        // from what is stored - which is the whole reason the list is built inside the gate rather
        // than handed to it. Built outside, the second would replace a list that never had the
        // first word in it.
        var store = new BlockingStore();
        using var writer = new SerialSettingsWriter(store, AppSettings.Default);

        var first = writer.UpdateAsync(current => current with
        {
            UserData = new ReusableUserData(
                [.. current.UserData.CustomWords, new CustomWordEntry("envy wisper", "EnviousWispr")],
                current.UserData.Snippets),
        });
        await store.SaveStarted.Task.ConfigureAwait(true);

        var second = writer.UpdateAsync(current => current with
        {
            UserData = new ReusableUserData(
                [.. current.UserData.CustomWords, new CustomWordEntry("git hub", "GitHub")],
                current.UserData.Snippets),
        });

        store.LetSavesFinish();
        Assert.Null(await first.ConfigureAwait(true));
        Assert.Null(await second.ConfigureAwait(true));

        var words = writer.Current.UserData.CustomWords.Select(word => word.SpokenForm).ToArray();
        Assert.Contains("envy wisper", words);
        Assert.Contains("git hub", words);
    }

    [Fact]
    public async Task AWordChangeAndASnippetChangeDoNotEraseEachOther()
    {
        // THE HALF EACH ONE WAS NOT TOUCHING WAS THE HALF THAT DISAPPEARED. A word save read the
        // snippets from a snapshot taken before it waited, and put the old ones back.
        var store = new BlockingStore();
        using var writer = new SerialSettingsWriter(store, AppSettings.Default);

        var word = writer.UpdateAsync(current => current with
        {
            UserData = new ReusableUserData(
                [.. current.UserData.CustomWords, new CustomWordEntry("one", "1")],
                current.UserData.Snippets),
        });
        await store.SaveStarted.Task.ConfigureAwait(true);

        var snippet = writer.UpdateAsync(current => current with
        {
            UserData = new ReusableUserData(
                current.UserData.CustomWords,
                [.. current.UserData.Snippets, new SnippetEntry("sign off", "Thanks, Saurabh")]),
        });

        store.LetSavesFinish();
        Assert.Null(await word.ConfigureAwait(true));
        Assert.Null(await snippet.ConfigureAwait(true));

        Assert.Single(writer.Current.UserData.CustomWords);
        Assert.Single(writer.Current.UserData.Snippets);
    }

    [Fact]
    public async Task AFailedSaveLeavesTheStoredValueAlone()
    {
        var store = new BlockingStore { FailNext = new IOException("the disk said no") };
        using var writer = new SerialSettingsWriter(store, AppSettings.Default);
        store.LetSavesFinish();

        var failure = await writer.UpdateAsync(current => current with { LaunchCount = 3 })
            .ConfigureAwait(true);

        Assert.IsType<IOException>(failure);
        // NOT ADVANCED, so the next change still derives from what is really on disk rather than
        // from something that was never written.
        Assert.Equal(AppSettings.Default.LaunchCount, writer.Current.LaunchCount);
    }

    [Fact]
    public async Task AnUnexpectedFailureIsNotSwallowed()
    {
        var store = new BlockingStore { FailNext = new InvalidOperationException("something else") };
        using var writer = new SerialSettingsWriter(store, AppSettings.Default);
        store.LetSavesFinish();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.UpdateAsync(current => current with { LaunchCount = 3 })).ConfigureAwait(true);
    }

    private sealed class BlockingStore : ISettingsStore
    {
        private readonly TaskCompletionSource _release = new();

        public TaskCompletionSource SaveStarted { get; } = new();

        public AppSettings? LastSaved { get; private set; }

        public Exception? FailNext { get; set; }

        public void LetSavesFinish() => _release.TrySetResult();

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            LastSaved = settings;
        }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsResetResult> ResetAsync(
            AppSettings replacement,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
