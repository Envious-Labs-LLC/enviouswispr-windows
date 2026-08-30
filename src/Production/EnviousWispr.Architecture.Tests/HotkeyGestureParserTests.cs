using EnviousWispr.Services.Input;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class HotkeyGestureParserTests
{
    [Theory]
    [InlineData("F8", HotkeyModifiers.None, "F8")]
    [InlineData("ctrl+shift+f12", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F12")]
    [InlineData("F9 + Windows + Alt", HotkeyModifiers.Windows | HotkeyModifiers.Alt, "F9")]
    [InlineData("control + space", HotkeyModifiers.Control, "Space")]
    [InlineData("esc", HotkeyModifiers.None, "Escape")]
    public void ValidGesturesAreCanonicalized(
        string value,
        HotkeyModifiers expectedModifiers,
        string expectedKey)
    {
        var result = HotkeyGestureParser.Parse(value);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedModifiers, result.Gesture?.Modifiers);
        Assert.Equal(expectedKey, result.Gesture?.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Ctrl+F8")]
    [InlineData("F8+F9")]
    [InlineData("F25")]
    [InlineData("Ctrl++F8")]
    public void InvalidGesturesReturnContentFreeTypedFailure(string? value)
    {
        var result = HotkeyGestureParser.Parse(value);

        Assert.False(result.Succeeded);
        Assert.Null(result.Gesture);
        Assert.Equal(AppErrorCode.HotkeyInvalid, result.Error?.Code);
        Assert.Equal(AppErrorStage.HotkeyConfiguration, result.Error?.Stage);
    }

    [Fact]
    public void SettingsValidationRejectsUninstallableGesture()
    {
        var settings = AppSettings.Default with
        {
            Preferences = UserPreferences.Default with
            {
                Dictation = DictationPreferences.Default with
                {
                    CancelGesture = "F8",
                },
            },
        };

        var error = AppSettingsValidator.Validate(settings, AppErrorStage.SettingsSave);

        Assert.Equal(AppErrorCode.InvalidData, error?.Code);
    }

    /// <summary>
    /// Every key the hook can listen for is a key the parser will accept, and the reverse.
    /// </summary>
    /// <remarks>
    /// THE FEATURE THIS EXISTS FOR WAS SHIPPED UNREACHABLE. The hotkey engine was taught to take a
    /// modifier as the dictation key, with twenty-five passing tests, and the parser refused every
    /// gesture that had no ordinary key in it. So the setting could not be expressed, could not be
    /// saved, and could not be chosen - a working engine wired to nothing. That is the second dead
    /// gesture on this project, after the hands-free lock.
    ///
    /// NEITHER HALF'S OWN TESTS CAN CATCH IT. The parser's tests are about parsing and passed. The
    /// engine's tests are about the engine and passed. The defect is that the two sets DISAGREE,
    /// which lives in neither file and shows up in neither suite.
    ///
    /// It is a closed question with a finite answer on both sides, so it is checked as a set
    /// comparison rather than as a list of examples somebody remembers to extend.
    /// </remarks>
    [Fact]
    public void TheParserAndTheHookAgreeOnWhichKeysExist()
    {
        var root = FindRepositoryRoot();
        var hookSource = File.ReadAllText(Path.Combine(
            root, "src", "Production", "EnviousWispr.Services", "Input", "WindowsPushToTalkHook.cs"));
        var parserSource = File.ReadAllText(Path.Combine(
            root, "src", "Production", "EnviousWispr.Core", "Input", "HotkeyContracts.cs"));

        var hookKeys = Regex.Matches(hookSource, @"""(\w+)"" => 0x[0-9A-Fa-f]{2},")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(hookKeys.Length >= 10, $"Expected the hook's key map, found {hookKeys.Length}.");

        // One direction, through the REAL parser rather than a reading of it: everything the hook
        // can listen for must be something a user can express.
        var unexpressible = hookKeys
            .Where(key => !HotkeyGestureParser.Parse(key).Succeeded)
            .ToArray();

        Assert.True(
            unexpressible.Length == 0,
            "The hook listens for these keys and no gesture can name them, so they can never be "
                + "chosen: " + string.Join(", ", unexpressible));

        // The other direction: everything the parser can produce must be something the hook can
        // listen for, or a user can save a binding that silently never fires.
        var parserKeys = Regex.Matches(parserSource, @"=> ""(\w+)"",")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(parserKeys.Length >= 10, $"Expected the parser's key names, found {parserKeys.Length}.");

        var unheard = parserKeys
            .Where(key => !hookSource.Contains($"\"{key}\" => 0x", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            unheard.Length == 0,
            "A user can name these keys and the hook never listens for them, so the binding would "
                + "save and never fire: " + string.Join(", ", unheard));
    }

    /// <summary>
    /// The three places that must agree about which keys are modifiers, do.
    /// </summary>
    /// <remarks>
    /// THERE IS A THIRD SET AND THE FIRST VERSION OF THE CHECK ABOVE MISSED IT. The parser decides
    /// what a user can TYPE, the hook decides what the app LISTENS for, and the tracker decides
    /// which keys take the TAP route instead of hold-to-talk. A key present in the first two and
    /// absent from the third saves happily and then goes down the ordinary path - where a modifier
    /// can never match, because pressing Control reports Control as active while the binding
    /// requires no modifiers. It would save, show correctly on screen, and silently never fire.
    ///
    /// Two agreeing sets is not the same as a closed set. Checking the pair I happened to be
    /// editing would have been a complete-looking check on two thirds of the question.
    /// </remarks>
    [Fact]
    public void TheTrackerAgreesWithTheHookAboutWhichKeysAreModifiers()
    {
        var hookSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "Production", "EnviousWispr.Services", "Input", "WindowsPushToTalkHook.cs"));

        var sided = Regex.Matches(hookSource, @"""(Left|Right)(Ctrl|Shift|Win)"" => 0x([0-9A-Fa-f]{2}),")
            .Select(match => (Name: match.Groups[1].Value + match.Groups[2].Value,
                              Key: Convert.ToUInt32(match.Groups[3].Value, 16)))
            .ToArray();

        Assert.True(sided.Length >= 6, $"Expected the sided modifier keys, found {sided.Length}.");

        var unrouted = sided
            .Where(entry => !HotkeyEdgeTracker.IsModifierKey(entry.Key))
            .Select(entry => entry.Name)
            .ToArray();

        Assert.True(
            unrouted.Length == 0,
            "A user can bind these and the tracker does not treat them as modifiers, so they would "
                + "take the hold-to-talk path and never fire: " + string.Join(", ", unrouted));

        // The other direction. A key the tracker routes as a modifier that no gesture can name is a
        // branch nothing reaches - dead code that reads as coverage.
        var everyOrdinaryKey = Regex.Matches(hookSource, @"""(\w+)"" => 0x([0-9A-Fa-f]{2}),")
            .Where(match => !HotkeyEdgeTracker.IsModifierKey(Convert.ToUInt32(match.Groups[2].Value, 16)))
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.All(
            everyOrdinaryKey,
            key => Assert.DoesNotContain("Ctrl", key, StringComparison.Ordinal));
    }

    /// <summary>
    /// The screen can produce every binding the rest of the app accepts.
    /// </summary>
    /// <remarks>
    /// THIS IS THE SET THAT WAS ACTUALLY BROKEN, and it is the fourth. The parser decides what can
    /// be SAVED, the hook what is LISTENED for, the tracker which keys take the TAP route - and the
    /// keybind field decides what a user can PRODUCE. The first three agreed. The field swallowed a
    /// lone modifier and returned, so the binding existed everywhere except the one place a person
    /// could reach, which is the only place that decides whether a feature exists for them.
    ///
    /// A FEATURE IS REACHABLE ONLY IF EVERY SET AGREES, and each set I checked made the next one
    /// feel less likely. Three agreeing sets is the most convincing possible argument for not
    /// looking at the fourth.
    ///
    /// Checked against the field's OWN vocabulary in source, because the field cannot be driven
    /// from a test. It proves the names line up; it cannot prove a keystroke reaches the handler,
    /// and that half still belongs to the session driving the real app.
    /// </remarks>
    [Fact]
    public void TheKeybindFieldCanProduceEveryBindingTheAppAccepts()
    {
        var window = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        var parserSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.Core", "Input", "HotkeyContracts.cs"));

        // What the field can put in the box, taken from the method that names the sided keys.
        var offered = Regex.Matches(window, @"return ""(\w+)"";|\? ""(\w+)"" : null")
            .SelectMany(match => new[] { match.Groups[1].Value, match.Groups[2].Value })
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offered.Length >= 6,
            $"Expected the field to offer the sided modifiers, found {offered.Length}.");

        // Everything the field can produce must parse, or a user assembles a binding that fails at
        // save with a field that looked perfectly valid.
        var unparseable = offered
            .Where(name => !HotkeyGestureParser.Parse(name).Succeeded)
            .ToArray();

        Assert.True(
            unparseable.Length == 0,
            "The keybind field can produce these and the parser refuses them: "
                + string.Join(", ", unparseable));

        // And everything the parser accepts as a standalone modifier must be producible, or the
        // binding exists everywhere except where a person could choose it.
        var accepted = Regex.Matches(parserSource, @"=> ""((?:Left|Right)(?:Ctrl|Shift|Win))"",")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(accepted.Length >= 6, $"Expected six sided modifiers, found {accepted.Length}.");

        var unreachable = accepted.Except(offered, StringComparer.Ordinal).ToArray();

        Assert.True(
            unreachable.Length == 0,
            "The app accepts these bindings and nothing on screen can produce them, so they exist "
                + "for nobody: " + string.Join(", ", unreachable));
    }

    /// <summary>A modifier on its own is a binding a user can actually type.</summary>
    /// <remarks>
    /// The control for the pair above. Without it both set comparisons would pass on a parser that
    /// still refused the one gesture this whole feature needs.
    /// </remarks>
    [Theory]
    [InlineData("RCtrl")]
    [InlineData("rightctrl")]
    [InlineData("LShift")]
    [InlineData("RWin")]
    public void AModifierOnItsOwnIsAValidBinding(string typed)
    {
        var result = HotkeyGestureParser.Parse(typed);

        Assert.True(result.Succeeded, $"{typed} was refused as a binding.");
        Assert.Equal(HotkeyModifiers.None, result.Gesture!.Value.Modifiers);
    }

    /// <summary>
    /// Alt stays out, matching the engine: a lone Alt tap already opens a window's menu bar.
    /// </summary>
    [Theory]
    [InlineData("RAlt")]
    [InlineData("LAlt")]
    public void AltIsStillNotABindingOnItsOwn(string typed)
    {
        Assert.False(HotkeyGestureParser.Parse(typed).Succeeded);
    }

    /// <summary>Ordinary combinations are untouched by any of this.</summary>
    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+Alt+W")]
    [InlineData("F8")]
    public void OrdinaryGesturesStillParse(string typed)
    {
        Assert.True(HotkeyGestureParser.Parse(typed).Succeeded);
    }

    /// <summary>
    /// ONE unsided modifier is still not a binding - it qualifies, it does not stand.
    /// </summary>
    /// <remarks>
    /// "Ctrl" names two physical keys and a binding has to name one, so a single modifier is bound
    /// by its side instead: RCtrl, LShift. A PAIR is different and is accepted below.
    /// </remarks>
    [Theory]
    [InlineData("Ctrl")]
    [InlineData("Shift")]
    [InlineData("Win")]
    public void OneUnsidedModifierIsStillNotABinding(string typed)
    {
        Assert.False(HotkeyGestureParser.Parse(typed).Succeeded);
    }

    /// <summary>
    /// Two modifiers together ARE a binding, and Ctrl+Win is the new default.
    /// </summary>
    /// <remarks>
    /// Holding two modifiers together is not how any common shortcut begins, while holding one is
    /// how most of them begin. That is what makes a pair safe to hold as a record key where a single
    /// modifier needs the hold threshold to be safe at all.
    /// </remarks>
    [Theory]
    [InlineData("Ctrl+Win")]
    [InlineData("Win+Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Shift+Win")]
    public void TwoModifiersTogetherAreABinding(string typed)
    {
        var result = HotkeyGestureParser.Parse(typed);

        Assert.True(result.Succeeded, $"{typed} was refused.");
        Assert.Equal(string.Empty, result.Gesture!.Value.Key);
    }

    /// <summary>
    /// Alt is excluded from a pair as well as on its own. A lone Alt tap opens a window's menu bar
    /// and Alt+Shift cycles the keyboard layout; both are shell gestures the user would lose.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+Alt")]
    [InlineData("Alt+Shift")]
    [InlineData("Alt+Win")]
    public void AnyPairContainingAltIsRefused(string typed)
    {
        Assert.False(HotkeyGestureParser.Parse(typed).Succeeded);
    }

    /// <summary>
    /// A modifier-only binding must survive being written down and read back, or it saves as one
    /// thing and loads as another.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+Win")]
    [InlineData("RCtrl")]
    [InlineData("Ctrl+Shift+D")]
    [InlineData("F8")]
    public void EveryBindingRoundTripsThroughItsOwnText(string typed)
    {
        var first = HotkeyGestureParser.Parse(typed);
        Assert.True(first.Succeeded, $"{typed} was refused.");

        var written = first.Gesture!.Value.ToString();
        var second = HotkeyGestureParser.Parse(written);

        Assert.True(second.Succeeded, $"{typed} wrote itself as '{written}', which does not parse.");
        Assert.Equal(first.Gesture!.Value, second.Gesture!.Value);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnviousWispr.Windows.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
