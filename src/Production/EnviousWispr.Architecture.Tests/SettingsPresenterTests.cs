using System.Security;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;

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

    /// <summary>A General page as its Save reads it, sound and complete; a test changes one thing at a time.</summary>
    private static GeneralSettingsInput General() => new(
        RecordingShortcut: "F8",
        CancelShortcut: "Escape",
        QuickAddShortcut: "Ctrl+Alt+W",
        FinalEngineIndex: 1,
        WordCorrection: true,
        FillerRemoval: false,
        EmojiFormatter: true,
        SpokenPunctuation: false,
        WhisperLanguageIndex: 2,
        EnglishSpellingIndex: 1,
        RecordingModeIndex: 1,
        EscapeRecovery: true,
        AutoStop: true,
        AutoStopSeconds: 3.5,
        PolishProviderIndex: 2,
        PolishModel: "  llama3  ",
        OllamaEndpoint: "http://localhost:11434",
        HistoryEnabled: true,
        HistoryRetentionDays: 45,
        ThemeIndex: 2,
        LivePreview: true,
        OverlayPositionIndex: 1,
        LevelRailPill: true,
        PlayRecordingSounds: true,
        RecordingSoundPairing: RecordingSoundPairing.AirGlint,
        CopyInsteadOfPaste: true,
        LocalDiagnostics: true,
        DiagnosticRetentionDays: 30,
        ShareTelemetry: true,
        TelemetryAvailable: true,
        MicrophoneId: "mic-2",
        PasteLastShortcut: "Alt+Shift+Z",
        CopyLastShortcut: "Ctrl+Shift+F9");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralSaveStoresEveryFieldItReplacesAsTheOldHandlerDid(bool inverted)
    {
        // THE CHARACTERISATION: every field the Save replaces, from inputs chosen so that no two are
        // alike and none is what the default would give, against a record built independently the way
        // the window's handler used to build it - and once more with every toggle inverted, so a save
        // that hard-coded any toggle either way, ignored the pairing, forgot the microphone, dropped the
        // endpoint or moved a field differs from one of the two records somewhere. Telemetry consent is
        // off while the build could share in the inverted case. The production store on a file, read
        // back as a launch would.
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            var before = AppSettings.Default with { LaunchCount = 4, HasCompletedOnboarding = true, LastSeenReleaseNotes = "0.18.0" };
            using var presenter = new SettingsPresenter(store, before);
            var input = General() with
            {
                WordCorrection = !inverted,
                FillerRemoval = inverted,
                EmojiFormatter = !inverted,
                SpokenPunctuation = inverted,
                EnglishSpellingIndex = inverted ? 0 : 1,
                PasteLastShortcut = inverted ? "" : "Alt+Shift+Z",
                EscapeRecovery = !inverted,
                AutoStop = inverted,
                HistoryEnabled = !inverted,
                LivePreview = !inverted,
                LevelRailPill = !inverted,
                PlayRecordingSounds = !inverted,
                RecordingSoundPairing = inverted ? RecordingSoundPairing.CloudPop : RecordingSoundPairing.AirGlint,
                CopyInsteadOfPaste = !inverted,
                LocalDiagnostics = inverted,
                ShareTelemetry = !inverted,
                TelemetryAvailable = true,
            };

            var outcome = await presenter.SaveGeneralAsync(input);

            Assert.True(outcome.Saved);
            var expected = before with
            {
                PreferredMicrophoneId = "mic-2",
                Preferences = new UserPreferences(
                    new DictationPreferences(
                        FinalAsrEngine.Parakeet,
                        "F8",
                        WordCorrectionEnabled: !inverted,
                        FillerRemovalEnabled: inverted,
                        EmojiFormatterEnabled: !inverted,
                        SpokenPunctuationEnabled: inverted,
                        (WhisperLanguagePreference)2,
                        (DictationRecordingMode)1,
                        "Escape",
                        EscapeRecoveryEnabled: !inverted,
                        "Ctrl+Alt+W",
                        AutoStopEnabled: inverted,
                        AutoStopSilenceSeconds: 3.5,
                        EnglishSpelling: inverted ? EnglishSpelling.American : EnglishSpelling.British,
                        PasteLastGesture: inverted ? "" : "Alt+Shift+Z",
                        CopyLastGesture: "Ctrl+Shift+F9"),
                    new PolishPreferences(PolishProvider.Ollama, "llama3", "http://localhost:11434"),
                    new HistoryPreferences(!inverted, 45),
                    AppTheme.Dark,
                    LivePreviewEnabled: !inverted,
                    OverlayPillPosition.Bottom,
                    inverted ? RecordingPillDesign.Classic : RecordingPillDesign.LevelRail,
                    RecordingPillDesign.ReadingWell,
                    PlayRecordingSounds: !inverted,
                    inverted ? RecordingSoundPairing.CloudPop : RecordingSoundPairing.AirGlint,
                    CopyInsteadOfPaste: !inverted),
                Observability = new ObservabilityPreferences(inverted, 30, !inverted),
            };
            Assert.Equal(expected, (await store.LoadAsync()).Settings);
            Assert.Equal(expected, presenter.Current);
            Assert.Equal(AppTheme.Dark, outcome.Theme);
            Assert.Equal(
                "Theme, Live Preview, pill design, pill position, recording sounds, and local data choices apply now. "
                + "Engine, microphone, shortcut, and polish changes apply safely on the next launch.",
                GeneralSaveOutcome.SavedMessage);
            Assert.Equal("Settings saved", GeneralSaveOutcome.SavedTitle);
        });
    }

    [Fact]
    public async Task GeneralSaveReadsAMissingChoiceAsTheFirstSafeOption()
    {
        // NOTHING SELECTED IS -1 (A NULL PAIRING), AND THE PRESENTER DECIDES WHAT THAT MEANS: the
        // first, safe option of each list - Automatic, the first language, hold-to-talk, no provider,
        // the system theme, the top position, the whisper tick - as the window's own fallback used to.
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            using var presenter = new SettingsPresenter(store, AppSettings.Default);

            var outcome = await presenter.SaveGeneralAsync(General() with
            {
                FinalEngineIndex = -1,
                WhisperLanguageIndex = -1,
                EnglishSpellingIndex = -1,
                RecordingModeIndex = -1,
                PolishProviderIndex = -1,
                ThemeIndex = -1,
                OverlayPositionIndex = -1,
                RecordingSoundPairing = null,
                DiagnosticRetentionDays = double.NaN,
            });

            Assert.True(outcome.Saved);
            Assert.Equal(AppTheme.System, outcome.Theme);
            var stored = (await store.LoadAsync()).Settings;
            Assert.Equal(FinalAsrEngine.Automatic, stored.Preferences.Dictation.FinalEngine);
            Assert.Equal((WhisperLanguagePreference)0, stored.Preferences.Dictation.WhisperLanguage);
            Assert.Equal(EnglishSpelling.American, stored.Preferences.Dictation.EnglishSpelling);
            Assert.Equal((DictationRecordingMode)0, stored.Preferences.Dictation.RecordingMode);
            Assert.Equal(PolishProvider.None, stored.Preferences.Polish.Provider);
            Assert.Equal(AppTheme.System, stored.Preferences.Theme);
            Assert.Equal(OverlayPillPosition.Top, stored.Preferences.OverlayPosition);
            Assert.Equal(RecordingSoundPairing.WhisperTick, stored.Preferences.RecordingSoundPairing);
            Assert.Equal(ObservabilityPreferences.Default.DiagnosticRetentionDays, stored.Observability!.DiagnosticRetentionDays);
        });
    }

    /// <summary>Only the recording key may be a modifier set; Save refuses one in Cancel or Add-a-word. Ref: #66.</summary>
    /// <remarks>
    /// THE HOOK REFUSES A CANCEL OR ADD-A-WORD KEY WITH NO KEY OF ITS OWN, and with it the whole hook - recording
    /// included. Save accepting what the hook refuses would store a profile whose dictation is dead at next launch.
    /// </remarks>
    [Fact]
    public async Task GeneralSaveRefusesAModifierSetOutsideTheRecordingKey()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            using var presenter = new SettingsPresenter(store, AppSettings.Default);

            var cancelSet = await presenter.SaveGeneralAsync(General() with { CancelShortcut = "Ctrl+Shift" });
            Assert.Equal(GeneralShortcutField.Cancel, cancelSet.InvalidField);
            var quickAddSet = await presenter.SaveGeneralAsync(General() with { QuickAddShortcut = "Ctrl+Win" });
            Assert.Equal(GeneralShortcutField.QuickAdd, quickAddSet.InvalidField);
            Assert.False(File.Exists(Path.Combine(directory, "settings.json")), "a keyless cancel or Add-a-word key reached the store");

            var recordingSet = await presenter.SaveGeneralAsync(General() with { RecordingShortcut = "Ctrl+Win" });
            Assert.True(recordingSet.Saved);
            Assert.Equal("Ctrl+Win", (await store.LoadAsync()).Settings.Preferences.Dictation.PushToTalkGesture);
        });
    }

    [Fact]
    public async Task GeneralSaveRejectsInvalidOrOverlappingShortcuts()
    {
        // NOTHING IS STORED UNTIL THE SHORTCUTS ARE SOUND. A shortcut that does not parse names its
        // own field - the first of the three that fails - and the store is never asked; two fields
        // sharing a gesture are described and the store is never asked; the same three sound and
        // distinct, the store is asked once. The production store on a file: the record is what a
        // launch would read.
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            using var presenter = new SettingsPresenter(store, AppSettings.Default);

            var recordingBad = await presenter.SaveGeneralAsync(General() with { RecordingShortcut = "Ctrl+" });
            Assert.Equal(GeneralSaveStatus.InvalidShortcut, recordingBad.Status);
            Assert.Equal(GeneralShortcutField.Recording, recordingBad.InvalidField);
            var cancelBad = await presenter.SaveGeneralAsync(General() with { CancelShortcut = "" });
            Assert.Equal(GeneralShortcutField.Cancel, cancelBad.InvalidField);
            var quickAddBad = await presenter.SaveGeneralAsync(General() with { QuickAddShortcut = "Ctrl+Ctrl+W" });
            Assert.Equal(GeneralShortcutField.QuickAdd, quickAddBad.InvalidField);
            Assert.False(File.Exists(Path.Combine(directory, "settings.json")), "an unreadable shortcut reached the store");

            var overlapping = await presenter.SaveGeneralAsync(General() with { CancelShortcut = "F8" });
            Assert.Equal(GeneralSaveStatus.OverlappingShortcuts, overlapping.Status);
            Assert.Contains("Recording", overlapping.Overlap, StringComparison.Ordinal);
            Assert.Contains("Cancel", overlapping.Overlap, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "settings.json")), "overlapping shortcuts reached the store");
            Assert.Equal(AppSettings.Default, presenter.Current);

            var sound = await presenter.SaveGeneralAsync(General());
            Assert.True(sound.Saved);
            Assert.Equal(AppTheme.Dark, sound.Theme);
            var loaded = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            var dictation = loaded.Settings.Preferences.Dictation;
            Assert.Equal("F8", dictation.PushToTalkGesture);
            Assert.Equal("Escape", dictation.CancelGesture);
            Assert.Equal("Ctrl+Alt+W", dictation.QuickAddGesture);
        });
    }

    [Fact]
    public async Task GeneralSaveUsesExistingDefaults()
    {
        // AN EMPTY NUMBER MEANS THE DEFAULT, AN INDEX OFF THE END MEANS THE NEAREST CHOICE, AND A BUILD
        // THAT CANNOT SHARE TELEMETRY DOES NOT. Cleared number boxes read NaN; the presenter stores the
        // auto-stop default, the history fallback and the diagnostic default rather than zero - and
        // clamps a retention typed beyond its bounds to them. Every choice index is normalised the way
        // the page's own maps do, and the pill design with words is always Reading Well.
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            using var presenter = new SettingsPresenter(store, AppSettings.Default);

            var outcome = await presenter.SaveGeneralAsync(General() with
            {
                AutoStopSeconds = double.NaN,
                HistoryRetentionDays = double.NaN,
                DiagnosticRetentionDays = 9_999,
                FinalEngineIndex = 7,
                WhisperLanguageIndex = -3,
                RecordingModeIndex = 9,
                PolishProviderIndex = 42,
                ThemeIndex = 5,
                OverlayPositionIndex = 3,
                LevelRailPill = false,
                ShareTelemetry = true,
                TelemetryAvailable = false,
                PolishModel = "   ",
            });

            Assert.True(outcome.Saved);
            var stored = (await store.LoadAsync()).Settings;
            var preferences = stored.Preferences;
            Assert.Equal(DictationPreferences.Default.AutoStopSilenceSeconds, preferences.Dictation.AutoStopSilenceSeconds);
            Assert.Equal(30, preferences.History.RetentionDays);
            Assert.Equal(RetentionDays.DiagnosticMaximum, stored.Observability!.DiagnosticRetentionDays);
            Assert.Equal(FinalAsrEngine.Whisper, preferences.Dictation.FinalEngine);
            Assert.Equal((WhisperLanguagePreference)0, preferences.Dictation.WhisperLanguage);
            Assert.Equal((DictationRecordingMode)1, preferences.Dictation.RecordingMode);
            Assert.Equal(PolishProvider.None, preferences.Polish.Provider);
            Assert.Null(preferences.Polish.ModelId);
            Assert.Equal(AppTheme.System, preferences.Theme);
            Assert.Equal(OverlayPillPosition.Top, preferences.OverlayPosition);
            Assert.Equal(RecordingPillDesign.Classic, preferences.PillDesignWithoutWords);
            Assert.Equal(RecordingPillDesign.ReadingWell, preferences.PillDesignWithWords);
            Assert.False(stored.Observability.ShareAnonymousTelemetry, "telemetry was shared by a build that cannot share it");
        });
    }

    [Fact]
    public async Task GeneralSavePreservesConcurrentVocabularyAndAppState()
    {
        // THE SAVE REPLACES THREE FIELDS AND KEEPS THE REST AS THEY ARE WHEN THE GATE OPENS. It waits
        // behind a vocabulary import and an app-state write that landed first (the store is held open
        // under the first of them); when its turn comes it stores the microphone, the preferences and
        // the observability choices it read, and the words, the snippets, the launch count, the
        // release notes seen and the language offers are the ones written meanwhile - not the ones
        // the page was opened with.
        var store = new BlockingStore();
        using var presenter = new SettingsPresenter(store, AppSettings.Default with { HasCompletedOnboarding = true });
        var imported = new ReusableUserData([new CustomWordEntry("envy wisper", "EnviousWispr")], [new SnippetEntry("sig", "Regards")]);

        var import = presenter.SaveAsync(current => current with { UserData = imported });
        await store.SaveStarted.Task.WaitAsync(Patience);
        var appState = presenter.SaveAsync(current => current with { LaunchCount = 9, LastSeenReleaseNotes = "0.19.0", LanguageOfferHistory = "fr:2" });
        var general = presenter.SaveGeneralAsync(General());

        store.LetSavesFinish();
        Assert.True((await import.WaitAsync(Patience)).Saved);
        Assert.True((await appState.WaitAsync(Patience)).Saved);
        var outcome = await general.WaitAsync(Patience);
        Assert.True(outcome.Saved);

        var stored = store.LastSaved!;
        Assert.Equal(imported, stored.UserData);
        Assert.True(stored.HasCompletedOnboarding, "the save replaced the onboarding state it was opened with");
        Assert.Equal(9, stored.LaunchCount);
        Assert.Equal("0.19.0", stored.LastSeenReleaseNotes);
        Assert.Equal("fr:2", stored.LanguageOfferHistory);
        Assert.Equal("mic-2", stored.PreferredMicrophoneId);
        Assert.Equal("llama3", stored.Preferences.Polish.ModelId);
        Assert.Equal(45, stored.Preferences.History.RetentionDays);
        Assert.True(stored.Observability!.ShareAnonymousTelemetry);
        Assert.Equal(stored, presenter.Current);
    }

    [Fact]
    public async Task GeneralSaveFailureLeavesCurrentSettingsUnchanged()
    {
        // THE STORE REFUSES: the values were sound, the write failed, and the presenter's current
        // settings - and the file a launch would read - are what they were. The outcome carries the
        // store's answer for the window to name.
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var before = AppSettings.Default with { LaunchCount = 3 };
            await store.SaveAsync(before);
            using var presenter = new SettingsPresenter(new FailingStore(store), before);

            var outcome = await presenter.SaveGeneralAsync(General());

            Assert.Equal(GeneralSaveStatus.NotSaved, outcome.Status);
            Assert.Equal(SettingsSaveRefusal.StorageBlocked, outcome.Save!.Value.Refusal);
            Assert.Null(outcome.Theme);
            Assert.Equal(before, presenter.Current);
            Assert.Equal(before, (await store.LoadAsync()).Settings);
        });
    }

    /// <summary>The production store behind a write that Windows refuses.</summary>
    private sealed class FailingStore(ISettingsStore inner) : ISettingsStore
    {
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("the settings file is read-only");

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default) =>
            inner.ResetAsync(replacement, cancellationToken);
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
