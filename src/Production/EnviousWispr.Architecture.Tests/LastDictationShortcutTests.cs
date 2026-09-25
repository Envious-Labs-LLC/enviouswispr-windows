using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The Paste and Copy Last Dictation shortcuts: what can be set, what is stored, what the hook does. Ref: #206.</summary>
public sealed class LastDictationShortcutTests
{
    private const uint F8 = 0x77;
    private const uint Escape = 0x1B;
    private const uint LetterZ = 0x5A;
    private const uint F9 = 0x78;
    private static readonly HotkeyModifiers AltShift = HotkeyModifiers.Alt | HotkeyModifiers.Shift;
    private static readonly HotkeyModifiers CtrlShift = HotkeyModifiers.Control | HotkeyModifiers.Shift;

    [Theory]
    [InlineData("", true, "")]
    [InlineData("   ", true, "")]
    [InlineData("Alt+Shift+Z", true, "Alt+Shift+Z")]
    [InlineData("ctrl+shift+f9", true, "Ctrl+Shift+F9")]
    [InlineData("Ctrl+Win", false, null)]
    [InlineData("RightCtrl", false, null)]
    [InlineData("Alt+Shift+Nonsense", false, null)]
    public void AOneShotShortcutIsBlankOrHasAnOrdinaryKey(string value, bool accepted, string? normalized)
    {
        var parsed = HotkeyGestureParser.ParseOneShot(value);

        Assert.Equal(accepted, parsed.Succeeded);
        if (accepted)
        {
            Assert.Equal(normalized, parsed.Gesture?.ToString() ?? string.Empty);
        }
    }

    [Fact]
    public void TheDefaultsAreAltShiftZForPasteAndNothingForCopy()
    {
        Assert.Equal("Alt+Shift+Z", DictationPreferences.Default.PasteLastGesture);
        Assert.Equal(string.Empty, DictationPreferences.Default.CopyLastGesture);
        Assert.Null(AppSettingsValidator.Validate(AppSettings.Default, EnviousWispr.Core.Errors.AppErrorStage.SettingsLoad));
    }

    [Theory]
    [InlineData("F8", "")]             // the recording key
    [InlineData("Ctrl+Alt+W", "")]     // Add-a-word
    [InlineData("Ctrl+Shift+F9", "Ctrl+Shift+F9")] // the two of them
    [InlineData("RightCtrl", "")]      // not a one-shot shape
    public void AClashingOrUnusableShortcutIsRefused(string paste, string copy)
    {
        var settings = WithShortcuts(paste, copy);

        Assert.NotNull(AppSettingsValidator.Validate(settings, EnviousWispr.Core.Errors.AppErrorStage.SettingsSave));
    }

    [Fact]
    public void AnUnsetShortcutClashesWithNothing()
    {
        Assert.Null(AppSettingsValidator.Validate(WithShortcuts("", ""), EnviousWispr.Core.Errors.AppErrorStage.SettingsSave));
    }

    /// <summary>A file from before these shortcuts keeps its own binding of Alt+Shift+Z and is not reset.</summary>
    [Fact]
    public void AnOlderBindingOfAltShiftZWinsOverTheNewDefault()
    {
        var older = DictationPreferences.Default with { QuickAddGesture = "Alt+Shift+Z" };

        var settled = older.WithoutClashingLastDictationShortcuts();

        Assert.Equal("Alt+Shift+Z", settled.QuickAddGesture);
        Assert.Equal(string.Empty, settled.PasteLastGesture);
    }

    [Fact]
    public async Task AVersionSixteenFileGainsTheDefaults()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, VersionSixteen(quickAdd: null));

            var result = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
            Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
            Assert.Equal("Alt+Shift+Z", result.Settings.Preferences.Dictation.PasteLastGesture);
            Assert.Equal(string.Empty, result.Settings.Preferences.Dictation.CopyLastGesture);
        });
    }

    /// <summary>THE RESET THIS PREVENTS: a version-16 file whose Add-a-word key was Alt+Shift+Z loads, keeping it.</summary>
    [Fact]
    public async Task AVersionSixteenFileThatAlreadyUsedAltShiftZLoadsInsteadOfResetting()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, VersionSixteen(quickAdd: "Alt+Shift+Z"));

            var result = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
            Assert.Equal("Alt+Shift+Z", result.Settings.Preferences.Dictation.QuickAddGesture);
            Assert.Equal(string.Empty, result.Settings.Preferences.Dictation.PasteLastGesture);
        });
    }

    [Fact]
    public void PasteLastSignalsOncePerPressAndMasksTheAlt()
    {
        var tracker = Tracker();

        var down = tracker.Process(LetterZ, isKeyDown: true, AltShift);
        var repeat = tracker.Process(LetterZ, isKeyDown: true, AltShift);
        var up = tracker.Process(LetterZ, isKeyDown: false, AltShift);

        Assert.Equal(PushToTalkSignal.PasteLast, down.Signal);
        Assert.True(down.Consume);
        Assert.True(down.MaskMenuKey);
        Assert.Null(repeat.Signal);
        Assert.True(repeat.Consume);
        Assert.False(repeat.MaskMenuKey);
        Assert.True(up.Consume);
    }

    [Fact]
    public void CopyLastSignalsAndNeedsNoMaskWithoutAltOrWin()
    {
        var tracker = Tracker();

        var down = tracker.Process(F9, isKeyDown: true, CtrlShift);

        Assert.Equal(PushToTalkSignal.CopyLast, down.Signal);
        Assert.True(down.Consume);
        Assert.False(down.MaskMenuKey);
    }

    /// <summary>While a take records, the shortcut is the person's application's key, untouched.</summary>
    [Fact]
    public void TheShortcutsPassThroughWhileRecording()
    {
        var tracker = Tracker();
        tracker.SetRecordingActive(active: true);

        var down = tracker.Process(LetterZ, isKeyDown: true, AltShift);
        var up = tracker.Process(LetterZ, isKeyDown: false, AltShift);

        Assert.Null(down.Signal);
        Assert.False(down.Consume);
        Assert.False(up.Consume);
    }

    [Fact]
    public void OtherModifiersOnTheSameKeyAreNotTheShortcut()
    {
        var tracker = Tracker();

        var plain = tracker.Process(LetterZ, isKeyDown: true, HotkeyModifiers.Shift);

        Assert.Null(plain.Signal);
        Assert.False(plain.Consume);
    }

    /// <summary>Letting go of Alt and Shift while Z still repeats: the press stays the shortcut's to the end.</summary>
    [Fact]
    public void ReleasingTheModifiersMidPressDoesNotLeakRepeats()
    {
        var tracker = Tracker();

        var down = tracker.Process(LetterZ, isKeyDown: true, AltShift);
        var repeat = tracker.Process(LetterZ, isKeyDown: true, HotkeyModifiers.None);
        var up = tracker.Process(LetterZ, isKeyDown: false, HotkeyModifiers.None);

        Assert.True(down.Consume);
        Assert.True(repeat.Consume);
        Assert.Null(repeat.Signal);
        Assert.True(up.Consume);
    }

    /// <summary>Pressed during a recording, it passed through; the recording ending mid-press does not fire it.</summary>
    [Fact]
    public void APressThatBeganDuringARecordingNeverFires()
    {
        var tracker = Tracker();
        tracker.SetRecordingActive(active: true);

        var down = tracker.Process(LetterZ, isKeyDown: true, AltShift);
        tracker.SetRecordingActive(active: false);
        var repeat = tracker.Process(LetterZ, isKeyDown: true, AltShift);
        var up = tracker.Process(LetterZ, isKeyDown: false, AltShift);

        Assert.False(down.Consume);
        Assert.False(repeat.Consume);
        Assert.Null(repeat.Signal);
        Assert.False(up.Consume);
    }

    /// <summary>Recording on Z and Paste on Alt+Shift+Z share a key: a recording's press does not strand the paste.</summary>
    [Fact]
    public void ARecordingKeySharedWithThePasteKeyLeavesThePasteUsable()
    {
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(LetterZ, HotkeyModifiers.None),
            new HotkeyBinding(Escape, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk,
            typesCharacter: (_, _) => false,
            pasteLast: new HotkeyBinding(LetterZ, AltShift));

        var pasteDown = tracker.Process(LetterZ, isKeyDown: true, AltShift);
        tracker.Process(LetterZ, isKeyDown: true, HotkeyModifiers.None);
        tracker.Process(LetterZ, isKeyDown: false, HotkeyModifiers.None);
        var again = tracker.Process(LetterZ, isKeyDown: true, AltShift);

        Assert.Equal(PushToTalkSignal.PasteLast, pasteDown.Signal);
        Assert.Equal(PushToTalkSignal.PasteLast, again.Signal);
    }

    /// <summary>A damaged version-16 file is refused as invalid, not thrown on by the migration.</summary>
    [Fact]
    public async Task ADamagedVersionSixteenFileIsInvalidNotAnException()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var json = JsonSerializer.Serialize(AppSettings.Default with { SchemaVersion = 16 }, JsonSettingsStore.SerializerOptions);
            var root = JsonNode.Parse(json)!.AsObject();
            root["preferences"]!.AsObject()["dictation"] = null;
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var result = await new JsonSettingsStore(path).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Invalid, result.Status);
        });
    }

    /// <summary>An optional shortcut that cannot listen is switched off on its own; the hook is still built.</summary>
    [Theory]
    [InlineData("", LastDictationShortcutState.Unset)]
    [InlineData("F8", LastDictationShortcutState.Clashes)]
    [InlineData("RightCtrl", LastDictationShortcutState.Invalid)]
    [InlineData("Ctrl+Win", LastDictationShortcutState.Invalid)]
    public void AShortcutThatCannotListenSaysWhy(string configured, LastDictationShortcutState expected)
    {
        var taken = new[] { new HotkeyGesture(HotkeyModifiers.None, "F8") };

        Assert.Equal(expected, WindowsPushToTalkHook.ResolveOptional(configured, taken).State);
    }

    [Fact]
    public async Task TheSettingsPageRefusesAShortcutWithNoOrdinaryKeyAndNamesTheField()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            using var presenter = new SettingsPresenter(new JsonSettingsStore(Path.Combine(directory, "settings.json")), AppSettings.Default);

            var outcome = await presenter.SaveGeneralAsync(SettingsPresenterTestsInput() with { CopyLastShortcut = "RightCtrl" });

            Assert.False(outcome.Saved);
            Assert.Equal(GeneralSaveStatus.InvalidShortcut, outcome.Status);
            Assert.Equal(GeneralShortcutField.CopyLast, outcome.InvalidField);
        });
    }

    [Fact]
    public async Task TheSettingsPageRefusesAShortcutTheRecordingKeyAlreadyHas()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            using var presenter = new SettingsPresenter(new JsonSettingsStore(Path.Combine(directory, "settings.json")), AppSettings.Default);

            var outcome = await presenter.SaveGeneralAsync(SettingsPresenterTestsInput() with { PasteLastShortcut = "F8" });

            Assert.False(outcome.Saved);
            Assert.Equal(GeneralSaveStatus.OverlappingShortcuts, outcome.Status);
        });
    }

    private static HotkeyEdgeTracker Tracker() => new(
        new HotkeyBinding(F8, HotkeyModifiers.None),
        new HotkeyBinding(Escape, HotkeyModifiers.None),
        new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
        DictationRecordingMode.PushToTalk,
        typesCharacter: (_, _) => false,
        pasteLast: new HotkeyBinding(LetterZ, AltShift),
        copyLast: new HotkeyBinding(F9, CtrlShift));

    private static AppSettings WithShortcuts(string paste, string copy) => AppSettings.Default with
    {
        Preferences = AppSettings.Default.Preferences with
        {
            Dictation = AppSettings.Default.Preferences.Dictation with { PasteLastGesture = paste, CopyLastGesture = copy },
        },
    };

    /// <summary>A version-16 file: today's shape without the two fields, and optionally an Add-a-word key.</summary>
    private static string VersionSixteen(string? quickAdd)
    {
        var json = JsonSerializer.Serialize(AppSettings.Default with { SchemaVersion = 16 }, JsonSettingsStore.SerializerOptions);
        var root = JsonNode.Parse(json)!.AsObject();
        var dictation = root["preferences"]!["dictation"]!.AsObject();
        Assert.True(dictation.Remove("pasteLastGesture"));
        Assert.True(dictation.Remove("copyLastGesture"));
        if (quickAdd is not null)
        {
            dictation["quickAddGesture"] = quickAdd;
        }

        return root.ToJsonString(JsonSettingsStore.SerializerOptions);
    }

    private static GeneralSettingsInput SettingsPresenterTestsInput() => new(
        "F8", "Escape", "Ctrl+Alt+W", 1, true, false, true, false, 2, 0, 1, true, true, 3.5, 2, "llama3", "http://localhost:11434",
        true, 45, 2, true, 1, true, true, RecordingSoundPairing.AirGlint, true, true, 30, true, true, "mic-2", "Alt+Shift+Z", "");
}
