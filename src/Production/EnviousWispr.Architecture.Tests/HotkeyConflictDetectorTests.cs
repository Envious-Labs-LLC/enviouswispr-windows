using System.Text.RegularExpressions;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Two keybind fields asking for the same key, and the sentence that says so.
/// </summary>
public sealed class HotkeyConflictDetectorTests
{
    private static IReadOnlyList<(string Role, string Text)> Fields(string recording, string cancel, string quickAdd) =>
    [
        ("Recording", recording),
        ("Cancel", cancel),
        ("Add-a-word", quickAdd),
    ];

    [Fact]
    public void ThreeDifferentShortcutsDoNotClash()
    {
        Assert.Empty(HotkeyConflictDetector.Find(Fields("F8", "Escape", "Ctrl+Alt+W")));
    }

    /// <summary>The measured defect: the same shortcut sitting in two fields with nothing said.</summary>
    [Fact]
    public void TheSameShortcutInTwoFieldsIsNamedWithBothRoles()
    {
        var clashes = HotkeyConflictDetector.Find(Fields("Ctrl+Alt+W", "Escape", "Ctrl+Alt+W"));

        var clash = Assert.Single(clashes);
        Assert.Equal("Recording", clash.FirstRole);
        Assert.Equal("Add-a-word", clash.SecondRole);
        Assert.Equal("Ctrl+Alt+W", clash.Gesture);
    }

    /// <summary>
    /// THE REASON THIS PARSES INSTEAD OF COMPARING TEXT. Ctrl+Win and Win+Ctrl are two spellings of
    /// one key combination, so a string comparison would call them different and let both be saved.
    /// </summary>
    [Fact]
    public void TwoSpellingsOfOneCombinationStillClash()
    {
        var clash = Assert.Single(HotkeyConflictDetector.Find(Fields("Ctrl+Win", "Win+Ctrl", "F8")));

        Assert.Equal("Recording", clash.FirstRole);
        Assert.Equal("Cancel", clash.SecondRole);
    }

    [Fact]
    public void AllThreeTheSameNamesEveryPair()
    {
        var clashes = HotkeyConflictDetector.Find(Fields("F8", "F8", "F8"));

        Assert.Equal(3, clashes.Count);
        Assert.Contains(clashes, clash => clash is { FirstRole: "Recording", SecondRole: "Cancel" });
        Assert.Contains(clashes, clash => clash is { FirstRole: "Recording", SecondRole: "Add-a-word" });
        Assert.Contains(clashes, clash => clash is { FirstRole: "Cancel", SecondRole: "Add-a-word" });
    }

    /// <summary>
    /// THE STATE EVERY FRESH FIELD STARTS IN. Comparing the parse results directly would make two
    /// empty fields collide with each other, so the warning would be showing before anyone had
    /// typed anything.
    /// </summary>
    [Fact]
    public void EmptyFieldsDoNotClashWithEachOther()
    {
        Assert.Empty(HotkeyConflictDetector.Find(Fields("", "", "")));
        Assert.Empty(HotkeyConflictDetector.Find(Fields("   ", "F8", "")));
    }

    /// <summary>Text that means nothing is not a shortcut, so it cannot collide with one.</summary>
    [Fact]
    public void UnreadableTextDoesNotClash()
    {
        Assert.Empty(HotkeyConflictDetector.Find(Fields("Ctrl+Ctrl", "Ctrl+Ctrl", "F8")));
    }

    [Fact]
    public void NothingIsSaidWhenNothingClashes()
    {
        Assert.Equal(string.Empty, HotkeyConflictDetector.Describe(HotkeyConflictDetector.Find(Fields("F8", "Escape", "Ctrl+Alt+W"))));
    }

    [Fact]
    public void TheSentenceNamesTheFieldsAndTheKeyAndSaysWhatToDo()
    {
        var sentence = HotkeyConflictDetector.Describe(HotkeyConflictDetector.Find(Fields("Ctrl+Alt+W", "Escape", "Ctrl+Alt+W")));

        Assert.Equal("Recording and Add-a-word are both set to Ctrl+Alt+W. Give each one its own shortcut.", sentence);
    }

    /// <summary>A two-key clash reads as two sentences, not one run-on line.</summary>
    [Fact]
    public void EveryClashGetsItsOwnSentence()
    {
        var sentence = HotkeyConflictDetector.Describe(HotkeyConflictDetector.Find(Fields("F8", "F8", "F8")));

        Assert.Equal(3, sentence.Split("are both set to").Length - 1);
        Assert.EndsWith("Give each one its own shortcut.", sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// A detector nothing calls is a detector nobody sees.
    /// </summary>
    /// <remarks>
    /// The rule was already correct in the save path and the person typing still got no warning,
    /// so the thing worth pinning is not the logic but the WIRING. Every field must report a
    /// change, there must be somewhere for the sentence to appear, and both the live warning and
    /// the save refusal must ask the same detector rather than growing a second opinion.
    /// </remarks>
    [Fact]
    public void EveryKeybindFieldIsWiredToTheWarningAndSaveAsksTheSameDetector()
    {
        var markup = File.ReadAllText(AppSourcePath("MainWindow.xaml"));
        var code = File.ReadAllText(AppSourcePath("MainWindow.xaml.cs"));

        foreach (var field in new[] { "HotkeyTextBox", "CancelHotkeyTextBox", "QuickAddHotkeyTextBox" })
        {
            var declaration = Regex.Match(markup, $"<TextBox x:Name=\"{field}\"[^>]*/>");
            Assert.True(declaration.Success, $"{field} was not found in the markup, so this check verified nothing.");
            Assert.Contains("TextChanged=\"HotkeyBoxTextChanged\"", declaration.Value, StringComparison.Ordinal);
        }

        Assert.Contains("x:Name=\"KeybindConflictText\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"KeybindErrorProbe\"", markup, StringComparison.Ordinal);

        var save = code[code.IndexOf("private async void SaveSettingsButton_Click", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("AutoStopSecondsBox", StringComparison.Ordinal)];
        Assert.Contains("HotkeyConflictDetector.Find(", save, StringComparison.Ordinal);
        Assert.DoesNotContain("parsedHotkey.Gesture == parsedCancelHotkey.Gesture", save, StringComparison.Ordinal);
    }

    private static string AppSourcePath(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnviousWispr.Windows.slnx")))
            {
                return Path.Combine(current.FullName, "src", "Production", "EnviousWispr.App", fileName);
            }

            current = current.Parent;
        }

        throw new InvalidOperationException($"Could not find the repository root above '{AppContext.BaseDirectory}'.");
    }
}
