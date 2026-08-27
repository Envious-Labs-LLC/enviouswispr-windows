using System.Text.RegularExpressions;
using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A history row tells a person which engine produced a dictation. It must use the name that
/// person chose it by, and the two sites must not be able to drift apart silently.
/// </summary>
public sealed partial class DesignSystemTokenTests
{
    /// <summary>
    /// Every engine id shape the product can write into history, as a literal - written the way
    /// a real history.json contains it, NOT by calling the code that builds them, so the row and
    /// the expectation cannot be wrong together.
    /// </summary>
    private static readonly string[] EngineIdsTheProductWrites =
    [
        "parakeet-tdt-0.6b-v3:cpu",
        "parakeet-tdt-0.6b-v3:cuda",
        "parakeet-tdt-0.6b-v3:cuda:isolated",
        "whisper-large-v3-turbo:cpu",
        "whisper-large-v3-turbo:directml",
        "whisper-large-v3-turbo:cuda:isolated",
        "whisper-small:cpu",
    ];

    [Fact]
    public void EveryEngineIdTheProductWritesNamesAnEngineThePickerOffers()
    {
        var offered = new[] { TranscriptionEngineNames.Parakeet, TranscriptionEngineNames.Whisper };

        var unnamed = EngineIdsTheProductWrites
            .Select(id => (id, name: TranscriptionEngineNames.DisplayName(id)))
            .Where(pair => !offered.Contains(pair.name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            unnamed.Length == 0,
            "A history row would show a raw engine id to a person for: " +
            string.Join(", ", unnamed.Select(pair => $"'{pair.id}' -> '{pair.name}'")));
    }

    /// <summary>
    /// The positive control for the test above: prove DisplayName can fail to name something.
    /// Without this, a DisplayName that returned "Parakeet" for every input would pass.
    /// </summary>
    [Fact]
    public void AnEngineWeDoNotShipReturnsItsRawIdRatherThanAName()
    {
        const string unknown = "some-future-model-v9:cpu";

        Assert.Equal(unknown, TranscriptionEngineNames.DisplayName(unknown));
    }

    /// <summary>
    /// Both producers must build their ids from the Core constants, so the mapping above cannot
    /// go stale when a model id changes. A second copy of the literal is how that happens: the
    /// isolated worker carried one, and its id already differs from the in-process one.
    /// </summary>
    [Fact]
    public void NoEngineIdIsBuiltFromItsOwnCopyOfAModelName()
    {
        var owner = Path.Combine(
            "src", "Production", "EnviousWispr.Core", "Runtime", "HardwareContracts.cs");

        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "src", "Production"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith(owner, StringComparison.Ordinal))
            .Where(path => !path.EndsWith("TranscriptionEngineNameTests.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllLines(path).Any(IsModelNameCode))
            .Select(path => Path.GetRelativePath(FindRepositoryRoot(), path))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A model name is spelled out away from its constant in: " + string.Join(", ", offenders));

        // The control: prove the matcher can see a model name at all, and that it correctly
        // ignores one written in prose. Without this pair, a matcher that found nothing anywhere
        // would report the same clean result as a codebase that is genuinely clean.
        Assert.True(IsModelNameCode("    EngineId = \"parakeet-tdt-0.6b-v3:cpu\";"));
        Assert.False(IsModelNameCode("/// an id looks like \"parakeet-tdt-0.6b-v3:cuda:isolated\"."));
    }

    /// <summary>
    /// A model name in a COMMENT is an example, not a producer, so only code lines count.
    /// </summary>
    private static bool IsModelNameCode(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith('*') ||
            trimmed.StartsWith("/*", StringComparison.Ordinal))
        {
            return false;
        }

        return ModelNameLiteral().IsMatch(line);
    }

    /// <summary>
    /// The engine picker and the history row name the same two engines. They live in different
    /// files, so nothing but this stops one being renamed and the other left behind - which is
    /// exactly the defect this whole change removes, one level up.
    /// </summary>
    [Fact]
    public void TheHistoryRowNamesEnginesTheWayTheTranscriptionPageDoes()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var block = FinalEngineChoicesBlock().Match(source);
        Assert.True(block.Success, "Could not find the FinalEngineChoices list in MainWindow.xaml.cs.");

        var offered = ChoiceLabel()
            .Matches(block.Groups[1].Value)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(3, offered.Length);
        Assert.Equal("Automatic", offered[0]);
        Assert.Equal(TranscriptionEngineNames.Parakeet, offered[1]);
        Assert.Equal(TranscriptionEngineNames.Whisper, offered[2]);

        // "Automatic" is a preference, never an engine, so it is not a name a history row can use.
        Assert.Equal(3, Enum.GetValues<FinalAsrEngine>().Length);
    }

    /// <summary>
    /// The in-process Parakeet engine's public constant is the same value as the Core owner, so
    /// its two existing consumers keep working without knowing the constant moved.
    /// </summary>
    [Fact]
    public void TheParakeetEngineConstantIsTheCoreOne()
    {
        Assert.Equal(ParakeetModelIds.Final, ParakeetTranscriptionEngine.ModelId);
    }

    [GeneratedRegex(@"parakeet-tdt-[0-9.]+b-v[0-9]+|whisper-large-v[0-9]+-turbo|""whisper-small""")]
    private static partial Regex ModelNameLiteral();

    [GeneratedRegex(@"FinalEngineChoices\s*=\s*\[(.*?)\];", RegexOptions.Singleline)]
    private static partial Regex FinalEngineChoicesBlock();

    [GeneratedRegex(@"new\(""([^""]+)""")]
    private static partial Regex ChoiceLabel();

    /// <summary>
    /// EVERY keybind field must tell the hook it has focus. Miss one and pressing the recording
    /// key inside THAT field starts a recording, which is the whole defect - and it would be
    /// invisible, because the other two fields would behave correctly.
    /// </summary>
    /// <remarks>
    /// Enumerated from the markup, not from the three names anyone happens to remember: the
    /// markup is what produces the fields, so a fourth added later is caught by construction.
    /// </remarks>
    [Fact]
    public void EveryKeybindFieldTellsTheHookWhenItHasFocus()
    {
        var markup = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var code = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var fields = CaptureField()
            .Matches(markup)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.True(fields.Length >= 3, $"Expected the keybind fields in the markup, found {fields.Length}.");

        var loop = CaptureFocusLoop().Match(code);
        Assert.True(loop.Success, "Could not find the loop that reports keybind-field focus.");

        var missing = fields.Where(name => !loop.Groups[1].Value.Contains(name, StringComparison.Ordinal)).ToArray();

        Assert.True(
            missing.Length == 0,
            "Pressing the recording key inside these fields would start a recording: " +
            string.Join(", ", missing));
    }

    /// <summary>
    /// The day fields keep their rounder.
    /// </summary>
    /// <remarks>
    /// STATED LIMIT, because this check is weaker than it looks and the weakness is the point:
    /// it proves the rounder is written down, NOT that a value gets rounded. The previous
    /// formatter was attached exactly as intended and 12.7 still came through untouched, so a
    /// check of this shape would have passed the defect. Only the running app answers the real
    /// question. What this stops is the rounder being deleted by someone tidying the formatter.
    /// </remarks>
    [Fact]
    public void TheDayFieldsKeepTheRounderThatMakesThemWholeNumbers()
    {
        var code = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var formatter = DayFieldFormatter().Match(code);
        Assert.True(formatter.Success, "Could not find the day fields' formatter.");

        Assert.Contains("IncrementNumberRounder", formatter.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("Increment = 1", formatter.Groups[1].Value, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"x:Name=""([A-Za-z]+)""[^>]*KeyDown=""HotkeyBoxKeyDown""")]
    private static partial Regex CaptureField();

    [GeneratedRegex(@"foreach \(var box in new\[\] \{([^}]*)\}\)\s*\{\s*box\.GotFocus", RegexOptions.Singleline)]
    private static partial Regex CaptureFocusLoop();

    [GeneratedRegex(@"box\.NumberFormatter = new DecimalFormatter\s*\{(.*?)\};", RegexOptions.Singleline)]
    private static partial Regex DayFieldFormatter();
}
