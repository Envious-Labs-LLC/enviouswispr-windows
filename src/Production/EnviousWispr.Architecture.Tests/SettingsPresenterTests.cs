using System.Security;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The settings window's writing decisions, without the window: overlapping saves both survive, a
/// refused save is named for what stopped it and moves nothing, an Appearance click writes its three
/// fields and no more, two windows share nothing, and a change after the drain is refused quietly.
/// </summary>
/// <remarks>
/// EVERY ONE OF THESE USED TO BE UNPROVABLE, because it only happens while two saves overlap or
/// while the window is going away, and a test that drives a window one call at a time never puts
/// two writers in the same moment. The store here can be held open mid-save, which is what does.
/// </remarks>
public sealed class SettingsPresenterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TwoOverlappingSavesBothSurvive()
    {
        var store = new BlockingStore();
        using var presenter = new SettingsPresenter(store, AppSettings.Default);

        var first = presenter.SaveAsync(current => current with { LaunchCount = 7 });
        await store.SaveStarted.Task.WaitAsync(Patience);
        var second = presenter.SaveAsync(current => current with { HasCompletedOnboarding = true });

        store.LetSavesFinish();
        Assert.True((await first.WaitAsync(Patience)).Saved);
        Assert.True((await second.WaitAsync(Patience)).Saved);

        Assert.Equal(7, presenter.Current.LaunchCount);
        Assert.True(presenter.Current.HasCompletedOnboarding);
        Assert.Equal(7, store.LastSaved!.LaunchCount);
        Assert.True(store.LastSaved.HasCompletedOnboarding);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), SettingsSaveRefusal.InvalidValues)]
    [InlineData(typeof(UnauthorizedAccessException), SettingsSaveRefusal.StorageBlocked)]
    [InlineData(typeof(SecurityException), SettingsSaveRefusal.StorageBlocked)]
    [InlineData(typeof(IOException), SettingsSaveRefusal.StorageUnavailable)]
    public async Task AStorageFailureIsNamedForItsCauseAndMovesNothing(Type failure, SettingsSaveRefusal expected)
    {
        // THREE CAUSES, THREE ANSWERS. A value out of range and a disk that cannot be written send
        // somebody to different places, and the answer has to say which.
        var store = new BlockingStore { FailNext = (Exception)Activator.CreateInstance(failure)! };
        using var presenter = new SettingsPresenter(store, AppSettings.Default);
        store.LetSavesFinish();

        var result = await presenter.SaveAsync(current => current with { LaunchCount = 3 }).WaitAsync(Patience);

        Assert.False(result.Saved);
        Assert.Equal(expected, result.Refusal);
        Assert.IsType(failure, result.Failure);
        Assert.Equal(AppSettings.Default.LaunchCount, presenter.Current.LaunchCount);
        Assert.Null(store.LastSaved);

        // The next change still derives from what is really stored, not from the one that failed.
        var next = await presenter.SaveAsync(current => current with { HasCompletedOnboarding = true }).WaitAsync(Patience);
        Assert.True(next.Saved);
        Assert.Equal(AppSettings.Default.LaunchCount, store.LastSaved!.LaunchCount);
    }

    [Fact]
    public async Task AValuedSaveThatFailsStillReportsItsCause()
    {
        var store = new BlockingStore { FailNext = new ArgumentException("out of range") };
        using var presenter = new SettingsPresenter(store, AppSettings.Default);
        store.LetSavesFinish();

        var result = await presenter
            .SaveAsync(current => (current with { LaunchCount = 3 }, "planned"))
            .WaitAsync(Patience);

        Assert.False(result.Saved);
        Assert.Equal(SettingsSaveRefusal.InvalidValues, result.Refusal);
    }

    [Fact]
    public async Task AValuedSaveAnswersWithWhatItWorkedOutInsideTheGate()
    {
        // THE DECISION AND THE ANSWER BOTH BELONG INSIDE THE GATE. The plan is made against the words
        // that are current once the gate is held - here, after another change has added one.
        var store = new BlockingStore();
        using var presenter = new SettingsPresenter(store, AppSettings.Default);
        var first = presenter.SaveAsync(current => current with
        {
            UserData = new ReusableUserData([new CustomWordEntry("envy", "EnviousWispr")], current.UserData.Snippets),
        });
        await store.SaveStarted.Task.WaitAsync(Patience);
        var second = presenter.SaveAsync(current => (current, current.UserData.CustomWords.Count));

        store.LetSavesFinish();
        await first.WaitAsync(Patience);
        var result = await second.WaitAsync(Patience);

        Assert.True(result.Saved);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task AnAppearanceSaveWritesItsThreeFieldsAndNothingElse()
    {
        // An Appearance click lands while the Save button's whole-record write is inside the store,
        // and while a bookkeeping write has moved something on another page. The click keeps its
        // three fields; everything else stays what the other writers made it.
        var store = new BlockingStore();
        using var presenter = new SettingsPresenter(store, AppSettings.Default);
        var elsewhere = presenter.SaveAsync(current => current with
        {
            LaunchCount = 9,
            Preferences = current.Preferences with { LivePreviewEnabled = true, CopyInsteadOfPaste = true },
        });
        await store.SaveStarted.Task.WaitAsync(Patience);

        var appearance = presenter.SaveAppearanceAsync(
            new AppearanceChoices(AppTheme.Dark, OverlayPillPosition.Bottom, RecordingPillDesign.LevelRail));

        store.LetSavesFinish();
        Assert.True((await elsewhere.WaitAsync(Patience)).Saved);
        Assert.True((await appearance.WaitAsync(Patience)).Saved);

        var stored = store.LastSaved!;
        Assert.Equal(AppTheme.Dark, stored.Preferences.Theme);
        Assert.Equal(OverlayPillPosition.Bottom, stored.Preferences.OverlayPosition);
        Assert.Equal(RecordingPillDesign.LevelRail, stored.Preferences.PillDesignWithoutWords);
        Assert.Equal(9, stored.LaunchCount);
        Assert.True(stored.Preferences.LivePreviewEnabled);
        Assert.True(stored.Preferences.CopyInsteadOfPaste);
        Assert.Equal(AppSettings.Default.Preferences.PillDesignWithWords, stored.Preferences.PillDesignWithWords);
    }

    [Fact]
    public async Task TwoPresentersShareNothing()
    {
        // Two windows, two presenters, two stores: a change in one is not a change in the other.
        var first = new BlockingStore();
        var second = new BlockingStore();
        using var one = new SettingsPresenter(first, AppSettings.Default);
        using var two = new SettingsPresenter(second, AppSettings.Default with { LaunchCount = 1 });
        first.LetSavesFinish();
        second.LetSavesFinish();

        Assert.True((await one.SaveAsync(current => current with { LaunchCount = 5 }).WaitAsync(Patience)).Saved);

        Assert.Equal(5, one.Current.LaunchCount);
        Assert.Equal(1, two.Current.LaunchCount);
        Assert.Null(second.LastSaved);
    }

    [Fact]
    public async Task TheDrainWaitsForTheSaveInFlightAndRefusesTheNextQuietly()
    {
        var store = new BlockingStore();
        using var presenter = new SettingsPresenter(store, AppSettings.Default);
        var inFlight = presenter.SaveAsync(current => current with { LaunchCount = 4 });
        await store.SaveStarted.Task.WaitAsync(Patience);

        var drain = presenter.DrainAsync();
        Assert.False(drain.IsCompleted, "the drain waits for the save inside the store");
        store.LetSavesFinish();
        await drain.WaitAsync(Patience);
        Assert.True((await inFlight.WaitAsync(Patience)).Saved);
        Assert.Equal(4, store.LastSaved!.LaunchCount);

        // AFTER THE DRAIN, A CLICK IS REFUSED AS CLOSING - not as a storage failure, because it is
        // not one, and the window says nothing for it.
        var late = await presenter.SaveAsync(current => current with { LaunchCount = 5 }).WaitAsync(Patience);
        Assert.False(late.Saved);
        Assert.Equal(SettingsSaveRefusal.Closing, late.Refusal);
        Assert.Equal(4, presenter.Current.LaunchCount);

        var lateAppearance = await presenter
            .SaveAppearanceAsync(new AppearanceChoices(AppTheme.Light, OverlayPillPosition.Top, RecordingPillDesign.Classic))
            .WaitAsync(Patience);
        Assert.Equal(SettingsSaveRefusal.Closing, lateAppearance.Refusal);
    }

    private sealed class BlockingStore : ISettingsStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
