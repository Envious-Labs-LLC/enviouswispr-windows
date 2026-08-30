using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Xml.Linq;
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

    /// <summary>
    /// No em dash or en dash reaches a person. Brand rule, and it applies to in-app strings, not
    /// only to marketing: use a full stop, a comma, a colon, a semicolon, brackets, or rewrite.
    /// </summary>
    /// <remarks>
    /// This is worth a gate where a wording rule usually is not, because the thing being banned
    /// is two CHARACTERS. There is no next counterexample to argue about and no judgement to
    /// apply, so the check is exactly as complete as the rule.
    ///
    /// It found 24 on the day it was written, across status lines, paste-fallback messages, the
    /// tray tooltip and two choice cards - every one of them shipped and read by users. Two of
    /// the strings appeared TWICE in different branches of the same file, which is the ordinary
    /// way half a fix like this gets made.
    ///
    /// Test sources are excluded on purpose: their prose is read by the next author, not by a
    /// user, and this file's own explanation contains the characters it bans.
    /// </remarks>
    [Fact]
    public void NoDashReachesAPersonInAnyShippedString()
    {
        var production = Path.Combine(FindRepositoryRoot(), "src", "Production");

        var offenders = Directory
            .EnumerateFiles(production, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".xaml", StringComparison.Ordinal)
                || path.EndsWith(".resw", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains(".Tests", StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (path, line, number: index + 1))
                .Where(row => ShippedStringWithADash(row.path, row.line))
                .Select(row => $"{Path.GetRelativePath(production, row.path)}:{row.number}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "An em dash or en dash reaches a person at: " + string.Join(", ", offenders));

        // Controls, both directions. Without the first, a matcher that had stopped matching would
        // report the same clean result; without the second, one that matched everything would too.
        Assert.True(ShippedStringWithADash("Any.xaml", "<x:String>Toggle \u2014 press once</x:String>"));
        Assert.False(ShippedStringWithADash("Any.xaml", "<x:String>Toggle: press once</x:String>"));
    }

    /// <summary>
    /// A dash in a C# COMMENT is prose for the next author, not a shipped string. In XAML and
    /// resource files every dash is shipped, because those files have no code to comment.
    /// </summary>
    private static bool ShippedStringWithADash(string path, string line)
    {
        if (!line.Contains('\u2014') && !line.Contains('\u2013'))
        {
            return false;
        }

        if (!path.EndsWith(".cs", StringComparison.Ordinal))
        {
            return true;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
        {
            return false;
        }

        return QuotedDash().IsMatch(line);
    }

    [GeneratedRegex("\"[^\"]*[\u2014\u2013][^\"]*\"")]
    private static partial Regex QuotedDash();

    /// <summary>
    /// The four notification severities are the app's whole vocabulary for "how bad is this", and
    /// each one has to be recognisable BEFORE the words are read. Two of them were painted the
    /// same colour as the card behind them.
    /// </summary>
    /// <remarks>
    /// Error and Success both pointed at BrandCardBgColor, so the most important message the app
    /// can show - an error - arrived with no colour behind it at all, distinguishable from an
    /// ordinary card only by a small icon. Warning and Informational had tinted backgrounds.
    /// Nobody chose that: BrandWarningSoftColor was the only soft tint that existed, so the two
    /// severities with no token of their own fell back to the card.
    ///
    /// This is why the check is over the whole SET rather than over the two that were wrong. Four
    /// severities times two properties is eight cells, and fixing the two that someone noticed
    /// leaves the same hole open for whichever cell is added next.
    ///
    /// High Contrast is exempt and that is deliberate: it sets every soft tint to Transparent so
    /// the system's own colours decide, which is what High Contrast is for.
    /// </remarks>
    [Fact]
    public void EverySeverityIsPaintedDifferentlyFromThePlainCard()
    {
        var markup = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        string[] severities = ["Error", "Warning", "Success", "Informational"];

        var indistinguishable = new List<string>();
        foreach (var severity in severities)
        {
            foreach (var property in new[] { "BackgroundBrush", "IconBackground" })
            {
                var key = $"InfoBar{severity}Severity{property}";
                var declaration = SeverityBrush(key).Match(markup);
                Assert.True(declaration.Success, $"No brand colour is declared for {key}.");

                if (declaration.Groups[1].Value is "BrandCardBgColor" or "BrandPageBgColor")
                {
                    indistinguishable.Add($"{key} -> {declaration.Groups[1].Value}");
                }
            }
        }

        Assert.True(
            indistinguishable.Count == 0,
            "These notifications are painted the same colour as the surface behind them: " +
            string.Join(", ", indistinguishable));

        // Every soft tint the markup asks for must exist in all three themes, or the severity
        // silently renders with no background at all - which looks exactly like the defect above.
        var tokens = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "Theme", "DesignTokens.xaml"));

        foreach (var soft in new[] { "BrandErrorSoftColor", "BrandWarningSoftColor", "BrandSuccessSoftColor" })
        {
            var declared = ColorToken(soft).Count(tokens);
            Assert.True(declared == 3, $"{soft} is declared {declared} times; Light, Dark and HighContrast all need it.");
        }
    }

    private static Regex SeverityBrush(string key) =>
        new(@"x:Key=""" + Regex.Escape(key) + @""" Color=""\{ThemeResource (Brand[A-Za-z]+)\}""");

    private static Regex ColorToken(string key) =>
        new(@"<Color x:Key=""" + Regex.Escape(key) + @""">");

    /// <summary>
    /// The first screen now promises "Your voice is transcribed here and never leaves it." This
    /// is the check that fails when that stops being true.
    /// </summary>
    /// <remarks>
    /// TWO STRUCTURAL FACTS, both printed rather than read:
    ///
    /// The project holding the transcription engines has no network client of any kind, so
    /// transcription is local by construction rather than by policy. And no project that DOES
    /// hold a network client mentions any audio-carrying type, so there is no route by which
    /// captured samples reach one.
    ///
    /// STATED LIMIT, because the obvious stronger gate does not work and it is worth saying why
    /// rather than leaving the next reader to try it. A project-reference gate - "network
    /// projects must not reference the audio project" - would be a false claim: the captured
    /// audio type lives in Core, which every project references, so the reference graph cannot
    /// separate them. This is a tripwire on the routes a person would actually take, not a proof.
    /// The proof is watching the network, which no unit test can do.
    /// </remarks>
    [Fact]
    public void NothingThatCanReachTheNetworkCanReachTheAudio()
    {
        var production = Path.Combine(FindRepositoryRoot(), "src", "Production");

        string[] networkClients = ["HttpClient", "WebSocket", "PostAsync", "WebRequest"];
        string[] audioCarriers = ["CapturedAudio", "AudioSample", "float[]", "PcmFrame"];

        var asr = SourceFilesIn(Path.Combine(production, "EnviousWispr.ASR"));
        Assert.NotEmpty(asr);

        var asrTalksToTheNetwork = asr
            .Where(file => networkClients.Any(client => file.text.Contains(client, StringComparison.Ordinal)))
            .Select(file => file.name)
            .ToArray();

        Assert.True(
            asrTalksToTheNetwork.Length == 0,
            "Transcription is no longer local by construction; a network client appeared in: "
            + string.Join(", ", asrTalksToTheNetwork));

        foreach (var project in new[] { "EnviousWispr.LLM", "EnviousWispr.ModelDelivery" })
        {
            var reachesAudio = SourceFilesIn(Path.Combine(production, project))
                .Where(file => audioCarriers.Any(carrier => file.text.Contains(carrier, StringComparison.Ordinal)))
                .Select(file => file.name)
                .ToArray();

            Assert.True(
                reachesAudio.Length == 0,
                $"{project} can reach the network AND now names an audio type in: "
                + string.Join(", ", reachesAudio));
        }

        // The control, and it is the half that matters: prove both halves of the matcher can
        // actually see what they are looking for, somewhere they are allowed to be.
        var app = SourceFilesIn(Path.Combine(production, "EnviousWispr.App"));
        Assert.Contains(app, file => audioCarriers.Any(c => file.text.Contains(c, StringComparison.Ordinal)));

        var llm = SourceFilesIn(Path.Combine(production, "EnviousWispr.LLM"));
        Assert.Contains(llm, file => networkClients.Any(c => file.text.Contains(c, StringComparison.Ordinal)));
    }

    private static (string name, string text)[] SourceFilesIn(string directory) =>
        Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();

    /// <summary>
    /// Every page pins its content to the left edge, so the left edge cannot move with content.
    /// </summary>
    /// <remarks>
    /// Measured on the running app: the settings page title is a CONSTANT 1392 wide and sat at nine
    /// different left edges across the nine settings sub-pages, a 452px spread. A centred panel's
    /// left edge is a function of how wide the visible section happens to be, so the same title
    /// moved depending on which section was showing.
    ///
    /// Left alignment fixes it BY CONSTRUCTION rather than by tuning: the left edge becomes the
    /// container's, which no content width can move. It is also what Windows 11 Settings does.
    ///
    /// The gate is over the SET, because a page added later that centres itself reproduces the
    /// original defect on that page alone, which is the version nobody notices.
    /// </remarks>
    [Fact]
    public void EveryPagePinsItsContentToTheLeftEdge()
    {
        var markup = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        var columns = PageContentColumn().Count(markup);

        Assert.True(
            columns >= 7,
            $"Expected at least seven capped content columns, found {columns}. Every page's content "
                + "must live in a column that is both star-sized and capped, or its edges move.");

        // The old construction, which fixed the left edge and left the right one loose. A panel
        // that carries the page width itself is sized by its CONTENT, so every page's cards ended
        // wherever that page happened to end - measured at a 424px spread across twenty pages, and
        // the primary Save button swung 391px with them.
        var selfSized = PageContentPanel().Matches(markup)
            .Select(match => match.Value)
            .Where(panel => panel.StartsWith("<StackPanel", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            selfSized.Length == 0,
            $"{selfSized.Length} page panel(s) still carry the page width themselves, so their right "
                + "edge is wherever their content ends: " + string.Join(" | ", selfSized));

        // Control, both directions. The matcher must recognise the old shape - otherwise a matcher
        // that had stopped matching anything would report the same clean result - and the new shape
        // must not be mistaken for it.
        const string selfSizedPanel =
            "<StackPanel HorizontalAlignment=\"Left\" MaxWidth=\"{StaticResource BrandPageContentMaxWidth}\" Spacing=\"18\">";
        const string cappedColumn =
            "<ColumnDefinition Width=\"*\" MaxWidth=\"{StaticResource BrandPageContentMaxWidth}\" />";
        Assert.Matches(PageContentPanel(), selfSizedPanel);
        Assert.Matches(PageContentColumn(), cappedColumn);
        Assert.DoesNotMatch(PageContentPanel(), cappedColumn);
    }

    /// <summary>
    /// Selecting a choice card must not move anything on the page.
    /// </summary>
    /// <remarks>
    /// Measured: a selected card was 125 tall where its siblings were 123, because the Checked
    /// state thickened the card's OWN border from 1 to 2. A border's thickness is layout, so
    /// choosing a different option shifted every card below it by two pixels. Two pixels reads as
    /// the page twitching under the cursor.
    ///
    /// The ring is now a dedicated zero-layout overlay. It has its OWN border rather than reusing
    /// the hover one, because hover lives in a different VisualStateGroup and two groups writing
    /// one property have no defined order between them - the shape that made a badge vanish
    /// earlier in this branch.
    ///
    /// So the gate is not "is there a ring" but "does any check state touch a LAYOUT property".
    /// </remarks>
    [Fact]
    public void SelectingAChoiceCardChangesNoLayoutProperty()
    {
        var controls = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "Theme", "Controls.xaml"));

        string[] layoutProperties = ["BorderThickness", "Margin", "Padding", "Width", "Height"];

        var checkStates = CheckStateBlock().Matches(controls).Select(match => match.Groups[1].Value).ToArray();
        Assert.True(checkStates.Length >= 2, $"Expected the Checked and Unchecked states, found {checkStates.Length}.");

        var offenders = checkStates
            .SelectMany(block => SetterTarget().Matches(block).Select(setter => setter.Groups[1].Value))
            .Where(target => layoutProperties.Any(property => target.EndsWith("." + property, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Selecting a card would move the page, because these check-state setters are layout: "
            + string.Join(", ", offenders));

        // Control: the setter matcher must actually find setters in those blocks, or "no layout
        // setters" would be true of an empty read.
        var allTargets = checkStates
            .SelectMany(block => SetterTarget().Matches(block).Select(setter => setter.Groups[1].Value))
            .ToArray();
        Assert.NotEmpty(allTargets);

        Assert.Contains("SelectionBorder", controls, StringComparison.Ordinal);
    }

    /// <summary>The superseded shape: a panel that carries the page width on itself.</summary>
    [GeneratedRegex(@"<StackPanel[^>]*BrandPageContentMaxWidth[^>]*>")]
    private static partial Regex PageContentPanel();

    /// <summary>
    /// The content column: star-sized so it fills, capped so it stops.
    /// </summary>
    /// <remarks>
    /// BOTH HALVES OR IT IS THE OLD BUG WEARING NEW CLOTHES. Star alone lets the content run the
    /// full width of any monitor. A cap alone on a stretched panel makes the framework CENTRE it,
    /// which puts the left edge back at the mercy of the content width - the exact defect the
    /// original left-pin fix was made for, reintroduced by the fix for the right edge.
    /// A star column with a maximum is the one construction that pins both edges at once.
    /// </remarks>
    [GeneratedRegex(@"<ColumnDefinition Width=""\*"" MaxWidth=""\{StaticResource BrandPageContentMaxWidth\}"" />")]
    private static partial Regex PageContentColumn();

    [GeneratedRegex(@"<VisualState x:Name=""(?:Checked|Unchecked)"">(.*?)</VisualState>", RegexOptions.Singleline)]
    private static partial Regex CheckStateBlock();

    [GeneratedRegex(@"<Setter Target=""([^""]+)""")]
    private static partial Regex SetterTarget();

    /// <summary>
    /// The wait a user sat through is reported however the dictation ended.
    /// </summary>
    /// <remarks>
    /// This method leaves by several paths: text delivered, text held for recovery, and a
    /// transcription failure. A report written beside each return would have missed one, and the
    /// one it would have missed is the failure path - which is the SLOWEST, so the number would be
    /// systematically optimistic exactly where it matters.
    ///
    /// So the gate is structural rather than a count of call sites: the event must be written
    /// inside a finally. A finally covers every exit including ones added later and ones that
    /// throw, which no enumeration of returns can promise.
    /// </remarks>
    [Fact]
    public void TheWaitIsReportedHoweverTheDictationEnds()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "App.xaml.cs"));

        var writes = source.Split("AppEventCode.DictationCompleted").Length - 1;
        Assert.True(writes == 1, $"Expected exactly one place to report the wait, found {writes}.");

        var block = FinallyBlock().Matches(source)
            .Select(match => match.Value)
            .Where(body => body.Contains("AppEventCode.DictationCompleted", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            block.Length == 1,
            "The wait is not reported from a finally, so an exit path can leave without reporting it.");

        // Control: the matcher must find finally blocks that do NOT report the wait, or a matcher
        // that matched nothing would fail this test for the wrong reason and one that matched
        // everything would pass it for the wrong reason.
        var allFinallys = FinallyBlock().Count(source);
        Assert.True(allFinallys > 1, $"Expected several finally blocks in this file, found {allFinallys}.");
    }

    [GeneratedRegex(@"finally\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.Singleline)]
    private static partial Regex FinallyBlock();

    /// <summary>
    /// The auto-stop watcher is torn down everywhere the live preview is.
    /// </summary>
    /// <remarks>
    /// Both are loops that exist only while a recording is running, and there are FIVE places a
    /// recording stops: finalize, cancel, fail, the watchdog, and app shutdown. Miss one and a
    /// watcher outlives its recording, then ends the NEXT one early - which would present as the
    /// app randomly cutting people off and would be nearly impossible to attribute.
    ///
    /// Gated by pairing rather than by counting call sites, so a sixth stop path added later is
    /// covered by construction: the preview teardown is long-established and every stop path
    /// already has one, so requiring the two to appear together makes the existing owner the
    /// enumeration.
    /// </remarks>
    [Fact]
    public void TheAutoStopWatcherIsTornDownWhereverTheLivePreviewIs()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "App.xaml.cs"));

        var previewStops = source.Split("StopLivePreviewAsync").Length - 1;
        var watcherStops = source.Split("StopAutoStopWatchAsync").Length - 1;

        // One extra mention each for the method's own declaration.
        Assert.True(previewStops >= 6, $"Expected the preview teardown call sites, found {previewStops}.");

        Assert.True(
            watcherStops >= previewStops,
            $"The live preview is torn down in {previewStops} places and the auto-stop watcher in "
            + $"{watcherStops}. A watcher that outlives its recording ends the NEXT one early.");
    }

    /// <summary>
    /// Auto-stop ends a recording through the same door a key release uses.
    /// </summary>
    /// <remarks>
    /// A parallel finish path would be a second implementation of ending a dictation - session
    /// state machine, the hook's own recording flag, transcription, delivery, history - and the
    /// two would drift. The drift would show up as auto-stopped recordings behaving subtly
    /// differently from released ones, which is the hardest kind of bug to attribute.
    /// </remarks>
    [Fact]
    public void AutoStopEndsTheRecordingThroughTheKeyReleasePath()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "App.xaml.cs"));

        var watcher = AutoStopWatcherBody().Match(source);
        Assert.True(watcher.Success, "Could not find the auto-stop watcher.");

        Assert.Contains(
            "HandlePushToTalkAsync(PushToTalkSignal.Released)",
            watcher.Value,
            StringComparison.Ordinal);

        // Control: the matcher must have captured a real body rather than an empty match, or the
        // assertion above would be about nothing.
        Assert.True(
            watcher.Value.Length > 400,
            $"The watcher body matched only {watcher.Value.Length} characters; the matcher is wrong.");
    }

    [GeneratedRegex(@"private async Task RunAutoStopWatchAsync.*?\n    \}", RegexOptions.Singleline)]
    private static partial Regex AutoStopWatcherBody();

    /// <summary>
    /// Any animation of a LAYOUT property must ask to be allowed to run.
    /// </summary>
    /// <remarks>
    /// WinUI refuses to run "dependent" animations - ones that change layout - unless
    /// EnableDependentAnimation is set. It refuses SILENTLY: no error, no exception, no log line.
    /// The animation is constructed correctly, added to a storyboard, begun, and does nothing.
    ///
    /// Measured on the running app. The notification bar's grow targeted MaxHeight without it, so
    /// MaxHeight was pinned to 0, the accompanying opacity fade ran perfectly on an element clamped
    /// to zero height, and 220ms later the completion handler released the clamp and the bar
    /// appeared in ONE frame. A dead pause followed by exactly the snap the animation existed to
    /// remove - and every visible symptom pointed at the animation not being reached rather than at
    /// it being refused.
    ///
    /// What ruled out every other explanation was a control inside the same binary: the page
    /// entrance animated correctly, through the same guard, the same API and the same file. It
    /// targets opacity and a transform, which are independent. Same everything, different property
    /// class.
    ///
    /// So the gate is the class, not the instance. It costs nothing and the failure it catches has
    /// no other signal.
    /// </remarks>
    [Fact]
    public void EveryAnimationOfALayoutPropertyAsksToBeAllowedToRun()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        // Properties that change LAYOUT. A transform's X and Y are deliberately absent: those are
        // render transforms, which are independent and run without asking.
        string[] layoutProperties =
        [
            "Width", "Height", "MaxWidth", "MaxHeight", "MinWidth", "MinHeight", "Margin", "Padding",
        ];

        var targets = AnimationTarget().Matches(source)
            .Select(match => (Variable: match.Groups[1].Value, Property: match.Groups[2].Value))
            .ToArray();

        Assert.NotEmpty(targets);

        var unasked = targets
            .Where(target => layoutProperties.Contains(target.Property, StringComparer.Ordinal))
            .Where(target => !AnimationDeclaration(target.Variable).IsMatch(source)
                || !AnimationDeclaration(target.Variable).Match(source).Value
                    .Contains("EnableDependentAnimation", StringComparison.Ordinal))
            .Select(target => $"{target.Variable} -> {target.Property}")
            .ToArray();

        Assert.True(
            unasked.Length == 0,
            "These animations change layout and will be silently skipped: " + string.Join(", ", unasked));

        // Control: the matcher must actually be finding a layout animation, or "none unasked" is
        // true of a file it failed to read.
        Assert.Contains(
            targets,
            target => layoutProperties.Contains(target.Property, StringComparer.Ordinal));
    }

    /// <summary>
    /// One animation's object initializer, including a nested one.
    /// </summary>
    /// <remarks>
    /// The nesting is not optional. A flat [^}]* stops at the first closing brace, and these
    /// initializers contain an easing function with its own - so the pattern ended before reaching
    /// EnableDependentAnimation and the gate reported a correct animation as unasked. It failed
    /// loudly on its first run, which is the only reason it is right now.
    /// </remarks>
    private static Regex AnimationDeclaration(string variable) =>
        new(
            @"var " + Regex.Escape(variable) + @" = new DoubleAnimation\s*\{(?:[^{}]|\{[^{}]*\})*\}",
            RegexOptions.Singleline);

    [GeneratedRegex(@"Storyboard\.SetTargetProperty\(\s*(\w+)\s*,\s*""([^""]+)""")]
    private static partial Regex AnimationTarget();

    /// <summary>
    /// A button that only moves focus is not a primary action.
    /// </summary>
    /// <remarks>
    /// Measured on the running app: Your Words showed TWO filled primary buttons at once - "Add
    /// word", which adds a word, and "Add your first word", which moves the cursor into the form's
    /// first field. Two equally loud buttons doing unequal things, on a page where one of them
    /// does nothing but point at the other. Snippets had the identical pair, which is the usual
    /// shape: a defect on one page is a defect on its twin.
    ///
    /// The empty-state button is still a button and still does its job. It just stops competing.
    ///
    /// GATED BY WHAT THE BUTTON DOES rather than by counting primaries per page. A count needs a
    /// notion of "page" that flat markup does not have, and it would pass a page that genuinely
    /// needs two primaries while failing one that legitimately has none. The handler name is the
    /// honest signal: a Focus-prefixed handler moves focus, and moving focus is never the main
    /// thing a person came to a page to do.
    /// </remarks>
    [Fact]
    public void NoButtonThatOnlyMovesFocusIsAPrimaryAction()
    {
        var markup = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        var focusButtons = FocusButton().Matches(markup).Select(match => match.Value).ToArray();

        Assert.True(focusButtons.Length >= 2, $"Expected the empty-state buttons, found {focusButtons.Length}.");

        var shouting = focusButtons
            .Where(button => button.Contains("BrandPrimaryButtonStyle", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            shouting.Length == 0,
            $"{shouting.Length} button(s) that only move focus are styled as the page's main action.");

        // Control: a real primary button must still exist somewhere, or "no primaries" would be
        // true of a page that had lost its main action entirely - which this gate would otherwise
        // read as a pass.
        Assert.Contains("BrandPrimaryButtonStyle", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A handler name contains an underscore. The first version of this pattern did not allow one,
    /// so it matched nothing and the gate failed for its own reason rather than the app's - the
    /// fourth instrument today to fail loudly on its first run, and every one of them accused
    /// something healthy rather than passing something broken.
    /// </summary>
    [GeneratedRegex(@"<Button[^>]*Click=""Focus[A-Za-z_]*""[^>]*>")]
    private static partial Regex FocusButton();

    /// <summary>
    /// An import says what happened whether or not anything was added.
    /// </summary>
    /// <remarks>
    /// The first version itemised only when NOTHING was added. One word importing successfully
    /// replaced the whole description with a generic save message, so a hundred-line file with
    /// sixty good rows and forty unreadable ones said "the change was saved locally" and the user
    /// never learned about the forty.
    ///
    /// That is the INVERSE of the failure the description was written for, and it survived because
    /// only the zero-added path had ever been looked at. It was found by a fixture built as a
    /// CONTROL for the zero case - same shapes, non-zero additions - which is the pairing that
    /// makes a one-sided result mean something.
    ///
    /// Gated structurally: both call sites must pass the description. A gate on the text itself
    /// would pin wording that should be free to change.
    /// </remarks>
    [Fact]
    public void AnImportSaysWhatHappenedOnBothPaths()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var uses = source.Split("DescribeImport(plan)").Length - 1;

        Assert.True(
            uses >= 2,
            $"The import description is used {uses} time(s). Both the added and the nothing-added "
            + "path must report it, or one of them silently drops every problem outcome.");

        // Control: the helper must exist and be more than a stub, or "used twice" would be true of
        // two calls to something that returns nothing.
        Assert.Contains("private static string DescribeImport", source, StringComparison.Ordinal);
        Assert.Contains("could not be read", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every gap in the product comes from the spacing scale.
    /// </summary>
    /// <remarks>
    /// A measured audit of all eighteen pages found gaps that shared no common step, and the markup
    /// behind it used THIRTEEN different values in one window - 3, 4, 5, 6, 7, 8, 9, 10, 12, 14,
    /// 16, 18 and 20. That is not a scale, it is noise, and it is what "inconsistent padding" looks
    /// like from the outside.
    ///
    /// Four-point grid, five steps, because that is what Fluent is built on and what every
    /// first-party Windows surface lines up to.
    ///
    /// GATED ON THE LITERAL RATHER THAN ON THE VALUE, deliberately. A gate checking that each
    /// number is a multiple of four would pass 24, 28, 32 and every other plausible-looking value
    /// somebody reaches for, and the scale would grow back one defensible step at a time. Requiring
    /// a token means adding a step is a decision made in one place, where it can be argued with.
    /// </remarks>
    [Fact]
    public void EveryGapComesFromTheSpacingScale()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");

        var offenders = Directory
            .EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (path, line, number: index + 1))
                .Where(row => LiteralSpacing().IsMatch(row.line))
                .Select(row => $"{Path.GetFileName(row.path)}:{row.number}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These gaps are literals rather than steps on the scale: " + string.Join(", ", offenders));

        // Control, both directions: the matcher must see a literal and must not see a token, or a
        // matcher that had stopped matching would report the same clean result.
        Assert.Matches(LiteralSpacing(), "<StackPanel Spacing=\"10\" />");
        Assert.DoesNotMatch(LiteralSpacing(), "<StackPanel Spacing=\"{StaticResource BrandSpacingS}\" />");
    }

    [GeneratedRegex(@"Spacing=""[0-9]")]
    private static partial Regex LiteralSpacing();

    /// <summary>
    /// Hiding a row hides its icon with it.
    /// </summary>
    /// <remarks>
    /// Rows in this window are a Grid with an icon in column 0 and a control in column 1. Hiding
    /// the CONTROL left the icon behind - on AI Polish with no provider selected, a lone circular
    /// arrow floated between two unrelated fields, attached to nothing.
    ///
    /// Reported by eye on ONE row. Sweeping the class found THREE: every control whose visibility
    /// the code toggles, checked for a FontIcon sibling in the same Grid. Fixing the reported
    /// instance would have left two, and both would have been found later by somebody else looking
    /// at a different page.
    ///
    /// The gate enumerates from the CODE - whatever the file toggles today - rather than from a
    /// list of three names, so a fourth control collapsed next month is covered without anyone
    /// remembering this existed.
    /// </remarks>
    [Fact]
    public void HidingARowHidesItsIconWithIt()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var document = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        var toggled = ToggledElement().Matches(code)
            .Select(match => match.Groups[1].Value)
            .Where(name => char.IsUpper(name[0]))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(toggled);

        // PARSED, NOT SEARCHED. The first version of this used "the nearest preceding <Grid and
        // <FontIcon in the file text", which is not a question about the element's parent at all -
        // for a whole page it found a Grid buried inside the PREVIOUS page and reported twelve
        // false positives including the navigation pane. It failed loudly on its first run, which
        // is the only reason it is right now.
        var named = document
            .Descendants()
            .Where(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace)) is { } name
                && toggled.Contains(name));

        var orphaning = named
            .Where(element => element.Parent?.Name.LocalName == "Grid")
            .Where(element => element.ElementsBeforeSelf()
                .Any(sibling => sibling.Name.LocalName == "FontIcon"))
            .Select(element => (string)element.Attribute(XName.Get("Name", XamlNamespace))!)
            .ToArray();

        Assert.True(
            orphaning.Length == 0,
            "Hiding these would leave their row icon behind: " + string.Join(", ", orphaning));

        // Control: the sweep must be finding real elements, or "none orphaning" would be true of a
        // search that matched nothing in the tree.
        Assert.NotEmpty(named);
    }

    [GeneratedRegex(@"(\w+)\.Visibility\s*=")]
    private static partial Regex ToggledElement();

    /// <summary>
    /// Every section the window declares has a nav row that reaches it.
    /// </summary>
    /// <remarks>
    /// THIS IS THE CHECK THAT WOULD HAVE CAUGHT A REAL REGRESSION. Removing the "All Settings"
    /// aggregate page orphaned THREE sections and nine controls, four of them privacy or retention:
    /// a user could not turn local diagnostics off, could not withdraw telemetry consent, could not
    /// change how long diagnostics were kept, and could not stop dictation history being saved. The
    /// app's own Help page still described a retention control that could no longer be reached.
    ///
    /// The verification after that removal asked whether anything still SHOWED the aggregate. It
    /// did not ask what the aggregate had CARRIED. Those are different properties and only the
    /// first was checked - by both of us - so the sections were orphaned for several builds while
    /// the removal was reported clean.
    ///
    /// Seventeen sections and seventeen nav rows looked like a clean mapping and was not one: three
    /// sections had no row and three rows carried more than one section. A count would have agreed
    /// with itself and been wrong, which is why this pairs them by NAME.
    /// </remarks>
    [Fact]
    public void EverySectionHasANavRowThatReachesIt()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var markup = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        var sections = SectionName().Matches(markup)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(sections.Length >= 15, $"Expected the window's sections, found {sections.Length}.");

        // THERE ARE THREE MECHANISMS AND I FOUND THEM ONE FAILING RUN AT A TIME. A settings tag
        // arm; a help tag arm, plus a show-all row that displays every help section at once; and a
        // COMPANION line that shows the deterministic-cleanup section beside Transcription.
        // Enumerating them was the wrong approach - each fix passed until the next section proved
        // there was another way in.
        //
        // ONE RULE COVERS ALL THREE, and it comes from the structure rather than from the list of
        // mechanisms I happened to find. To SHOW a section the code must NAME it. The only place a
        // name appears without showing anything is the array the settings page iterates to hide
        // them - which is exactly why the orphaned sections were invisible: they sat in that array
        // and nowhere else.
        //
        // So: strip that one array, and ask whether the name survives anywhere in the file.
        var reachableCode = SettingsSectionsArray().Replace(code, string.Empty);

        var unreachable = sections
            .Where(section => !reachableCode.Contains(section, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            unreachable.Length == 0,
            "These sections exist and no nav row reaches them: " + string.Join(", ", unreachable));

        // Control: the matcher must be finding sections that ARE reachable too, or "none
        // unreachable" would be true of a search that matched every section by accident.
        Assert.Contains(sections, section => code.Contains($"?){section})", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every control the window greys out, the window can also hand back.
    /// </summary>
    /// <remarks>
    /// THE TWIN OF THE REACHABILITY GATE, FOR THE OTHER WAY A CONTROL DIES. That one asks whether a
    /// section can be reached at all. This asks whether a control the user CAN reach is still
    /// usable, because a control disabled on some path and never re-enabled on any is permanently
    /// dead while remaining perfectly visible - and it looks identical to one that is merely busy.
    ///
    /// NOTHING ELSE WOULD CATCH IT. The build is happy, the layout is right, the control is on the
    /// screen with a label, and the only symptom is a user clicking something that does not respond.
    /// A screen sweep counts it as present, which is how it survives the checks that exist.
    ///
    /// The rule is structural rather than a list of controls: a control turned off must have at
    /// least one assignment somewhere that is not the constant false. That covers being handed back
    /// directly, through a helper, or by a condition, without this test needing to know which.
    /// </remarks>
    [Fact]
    public void EveryControlTheWindowGreysOutItCanAlsoHandBack()
    {
        var code = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        // READ THE VALUE, DO NOT LOOK AHEAD PAST IT. The first version of this asked whether a
        // control had any assignment NOT followed by "false", using a negative lookahead after
        // \s* - and it could never fail. The engine backtracks \s* to zero width, the lookahead
        // then examines " false;" with its leading space, that does not begin with "false", and the
        // check passes on the exact text it exists to reject. Proven by injecting a control that is
        // disabled and never handed back: the suite stayed green.
        var assignments = EnabledAssignment().Matches(code)
            .GroupBy(
                match => match.Groups[1].Value,
                match => match.Groups[2].Value.Trim(),
                StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            assignments.Length >= 3,
            $"Expected the window to set IsEnabled somewhere, found {assignments.Length} controls.");

        var stuck = assignments
            .Where(control => control.All(value => value == "false"))
            .Select(control => control.Key)
            .ToArray();

        Assert.True(
            stuck.Length == 0,
            "These controls are greyed out and never handed back: " + string.Join(", ", stuck));

        // Control, two ways. At least one control must be turned off somewhere, or "none stuck"
        // would be true of a window that never greys anything out; and at least one must be turned
        // back on, or the value capture is reading something other than what it thinks.
        Assert.Contains(assignments, control => control.Any(value => value == "false"));
        Assert.Contains(assignments, control => control.Any(value => value != "false"));
    }

    /// <summary>
    /// No member in the app is handed a sentence and hands back a pill.
    /// </summary>
    /// <remarks>
    /// INFERRING A VISUAL FROM A STRING IS HOW A COPY EDIT SILENTLY CHANGES AN ICON. The macOS app
    /// carries that sentence in its own source. This side used to do exactly that: a
    /// <c>OverlayStateFor(string)</c> matched the status text - <c>StartsWith("Recording")</c>,
    /// <c>Contains("copied only")</c> - and picked the pill from the words, so rewording a sentence
    /// changed what appeared on screen with no code change and nothing able to report it. Two
    /// sentences were measurably wrong when it was removed: a paused recording and a timed-out one
    /// both matched "Recording" and wore the live listening pill, timer running.
    ///
    /// THIS GATE WAS BYPASSED TWICE, THE SAME WAY BOTH TIMES, AND THE SECOND TIME IS THE LESSON.
    /// Draft one keyed on the return type alone, so a helper returning <c>DictationStatus</c>
    /// instead of <c>DictationOverlayState</c> walked past it. Draft two also read the body and
    /// refused <c>.StartsWith(</c> and <c>.Contains(</c> - and <c>.IndexOf(</c> walked past that.
    ///
    /// THE SET OF WAYS TO READ A STRING IS NOT ENUMERABLE. IndexOf, a regex, an equality, a
    /// Substring, a Split, a span comparison, a switch on a prefix. Every draft that lists forbidden
    /// operations is a draft that is one operation short, and it fails GREEN, which is
    /// indistinguishable from the property holding.
    ///
    /// So this no longer asks what a member DOES. It asks what a member IS: handed a string, hands
    /// back a pill or a status. There is no legitimate member of that shape in the app. A status is
    /// built by a named factory in Core - <c>DictationStatus.Warning</c> and its siblings - and Core
    /// is not in the tree this walks. The signature is the whole assertion, and a signature cannot
    /// be smuggled past by a method name nobody thought of.
    ///
    /// Same correction as the repository's own rule about a lookahead after a quantifier: capture
    /// the value and compare it, never look past a value to ask what it is not.
    /// </remarks>
    [Fact]
    public void NoMemberTurnsAStatusSentenceIntoAPillAppearance()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var offenders = new List<string>();
        foreach (var file in ProductionSourceFiles(app))
        {
            offenders.AddRange(
                SentenceToAppearanceOffenders(Path.GetFileName(file), File.ReadAllText(file)));
        }

        Assert.True(
            offenders.Count == 0,
            "These members are handed a sentence and hand back a pill, which is the shape the "
                + "appearance-from-text defect always takes. Build the status where the outcome is "
                + "known and pass DictationStatus through instead: " + string.Join(", ", offenders));
    }

    /// <summary>The two type names this gate is about, however they are spelled at a use site.</summary>
    private static readonly string[] AppearanceTypeNames = ["DictationStatus", "DictationOverlayState"];

    /// <summary>Finds members in one file that turn a sentence into a pill appearance.</summary>
    /// <remarks>
    /// THE COMPILER IS ASKED, BECAUSE FOUR ROUNDS OF REVIEW PROVED GUESSING CANNOT BE FINISHED.
    /// Every regex draft decided a member's return type from the characters near its name, and every
    /// draft was one shape short: a <c>using</c> alias, a return type wrapped onto its own line, a
    /// <c>Func</c> field, an indexer, an attribute inside the parameter list. Reading the line above
    /// to catch the wrapped case then began accusing a member whose NEIGHBOUR returned a status. The
    /// shapes are not enumerable and each miss fails GREEN, which is indistinguishable from the
    /// property holding.
    ///
    /// A SYNTAX TREE KNOWS WHERE A RETURN TYPE ENDS AND A NAME BEGINS, so comments, pragmas,
    /// attributes, line breaks and whitespace stop mattering at all. Methods, indexers, operators,
    /// conversion operators, properties, delegate declarations and fields or locals of a delegate
    /// type are all covered, because each of them can be handed a string and hand back a status.
    ///
    /// IT IS STILL A SYNTAX TREE AND NOT A COMPILATION, so a type is matched by the NAME written at
    /// the declaration, plus any <c>using</c> alias in the same file that resolves to one of these
    /// types.
    ///
    /// WHAT THAT LEAVES OPEN, ENUMERATED, BECAUSE AN UNSTATED LIMIT READS AS COVERAGE: a field whose
    /// type is a named delegate IMPORTED from another file; a lambda assigned to <c>var</c>, whose
    /// type the compiler infers and the text does not state; a function pointer; and a third-party
    /// type that happens to share one of these names. Closing those needs a semantic model over a
    /// real compilation, or better, an analyser that refuses the code at build time rather than a
    /// test that reports it afterwards. That is tracked as its own work.
    ///
    /// The defect this guards is already gone - <c>OverlayStateFor</c> is deleted and every status
    /// is built where its outcome is known. This stops the shape coming back in the forms it has
    /// actually taken, and says plainly which forms it would miss.
    /// </remarks>
    private static List<string> SentenceToAppearanceOffenders(string fileName, string text)
    {
        var offenders = new List<string>();
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();

        // A file-local rename of one of these types counts as one of these types.
        var names = new List<string>(AppearanceTypeNames);
        names.AddRange(root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(directive => directive is { Alias: not null, Name: not null } &&
                AppearanceTypeNames.Any(name => MentionsName(directive.Name!, name)))
            .Select(directive => directive.Alias!.Name.Identifier.ValueText));

        // The null check is a STATEMENT rather than part of the expression, because a lambda
        // capturing `type` defeats the compiler's flow analysis and the null check stops counting.
        bool IsAppearance(TypeSyntax? type)
        {
            if (type is null)
            {
                return false;
            }

            return names.Any(name => MentionsName(type, name));
        }

        // Handed a string, hands back an appearance. Every declaration kind that can be either,
        // including a LOCAL FUNCTION, which is not a member declaration and is the easiest of these
        // to reach for once the others are refused.
        foreach (var node in root.DescendantNodes())
        {
            var (returnType, parameters) = node switch
            {
                MethodDeclarationSyntax method => (method.ReturnType, method.ParameterList.Parameters),
                LocalFunctionStatementSyntax local => (local.ReturnType, local.ParameterList.Parameters),
                IndexerDeclarationSyntax indexer => (indexer.Type, indexer.ParameterList.Parameters),
                OperatorDeclarationSyntax op => (op.ReturnType, op.ParameterList.Parameters),
                ConversionOperatorDeclarationSyntax conversion =>
                    (conversion.Type, conversion.ParameterList.Parameters),
                DelegateDeclarationSyntax nominal => (nominal.ReturnType, nominal.ParameterList.Parameters),
                _ => (null, default),
            };

            if (IsAppearance(returnType) &&
                parameters.Any(parameter => IsStringType(parameter.Type)))
            {
                offenders.Add($"{fileName}: {Describe(node)}");
            }
        }

        // A VARIABLE of a delegate type is not a declaration of any kind above, and it does exactly
        // the refused thing.
        //
        // TWO NARROWINGS, BOTH BOUGHT BY A FALSE POSITIVE THIS CODE ACTUALLY HAD. Scanning every
        // generic type anywhere flagged `Dictionary<string, DictationStatus>`, which is legitimate
        // and is arguably the CLEAREST way to map a known outcome onto a status - so the scan reads
        // only the type written in a DECLARATION, never a type used in an expression. And it
        // requires the name Func, because a syntax tree cannot tell an imported delegate type from
        // any other generic type without asking the compiler what the name resolves to.
        //
        // WHAT THAT LEAVES OPEN, STATED RATHER THAN IMPLIED: a field whose type is an imported
        // named delegate, a lambda assigned to `var`, and a function pointer. Closing those needs a
        // semantic model over a real compilation, which is tracked as its own work.
        foreach (var declared in DeclaredTypes(root).OfType<GenericNameSyntax>())
        {
            var arguments = declared.TypeArgumentList.Arguments;
            if (declared.Identifier.ValueText == "Func" &&
                arguments.Count >= 2 &&
                IsAppearance(arguments[^1]) &&
                arguments.Take(arguments.Count - 1).Any(IsStringType))
            {
                offenders.Add($"{fileName}: {declared}");
            }
        }

        return offenders;
    }

    /// <summary>The types written in a declaration, as opposed to used in an expression.</summary>
    private static IEnumerable<TypeSyntax> DeclaredTypes(SyntaxNode root) =>
        root.DescendantNodes().Select(node => node switch
        {
            VariableDeclarationSyntax variable => variable.Type,
            PropertyDeclarationSyntax property => property.Type,
            ParameterSyntax parameter => parameter.Type,
            _ => null,
        }).OfType<TypeSyntax>();

    /// <summary>Whether a written type is <c>string</c>, however it is spelled.</summary>
    /// <remarks>
    /// FOUR SPELLINGS, AND THE LAST TWO WERE REVIEW FINDINGS. <c>string</c> and <c>String</c> are
    /// the obvious pair. <c>string?</c> wraps either in a nullable node, and
    /// <c>System.String</c> wraps it in a qualified name, so a parameter written either way slipped
    /// past a check that only looked at the outermost node.
    /// </remarks>
    private static bool IsStringType(TypeSyntax? type) => type switch
    {
        NullableTypeSyntax nullable => IsStringType(nullable.ElementType),
        QualifiedNameSyntax qualified => IsStringType(qualified.Right),
        PredefinedTypeSyntax predefined => predefined.Keyword.IsKind(SyntaxKind.StringKeyword),
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "String",
        _ => false,
    };

    /// <summary>Whether a written type names the given type, qualified or not, bare or wrapped.</summary>
    /// <remarks>
    /// A RETURN TYPE IS OFTEN NOT THE TYPE ITSELF. <c>Task&lt;DictationStatus&gt;</c>,
    /// <c>DictationStatus?</c> and <c>IReadOnlyList&lt;DictationOverlayState&gt;</c> all hand back an
    /// appearance, so the whole written type is searched rather than only its outermost name.
    /// </remarks>
    private static bool MentionsName(SyntaxNode type, string name) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()
            .Any(simple => simple.Identifier.ValueText == name);

    /// <summary>One line naming the offending member, without its body.</summary>
    private static string Describe(SyntaxNode member)
    {
        var text = member.ToString();
        var bodyStart = text.IndexOfAny(['{', '=', ';']);
        return string.Join(' ', (bodyStart > 0 ? text[..bodyStart] : text)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Runs the gate over one fixture, as a real file rather than a fragment.</summary>
    /// <remarks>
    /// A MEMBER OUTSIDE A TYPE IS NOT A MEMBER DECLARATION, and the first draft of this proof wrote
    /// its fixtures as bare members. The parser read them as something else entirely and the gate
    /// found nothing, which is the failure the proof exists to catch, arriving from the fixture
    /// rather than from the gate. Each fixture is wrapped in a host type here, and any leading
    /// <c>using</c> stays outside it where the compiler requires it.
    /// </remarks>
    private static List<string> OffendersIn(string fileName, string body)
    {
        var lines = body.Split('\n');
        var usings = lines.Where(line => line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
        var rest = lines.Where(line => !line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
        var source = string.Join('\n', usings)
            + "\nnamespace Sample;\n\ninternal sealed class Host\n{\n"
            + string.Join('\n', rest)
            + "\n}\n";
        return SentenceToAppearanceOffenders(fileName, source);
    }

    /// <summary>
    /// The gate above can see every shape that has so far walked past one of its drafts.
    /// </summary>
    /// <remarks>
    /// A GATE THAT PASSES ON ITS FIRST RUN IS UNPROVEN, and this one passes by finding nothing in a
    /// clean tree, which is exactly what a gate that can find nothing at all also does.
    ///
    /// EVERY POSITIVE CASE HERE IS A REAL BYPASS THAT SHIPPED GREEN, in the order they were found:
    /// the plain method; the return type reached through a <c>using</c> alias; the return type
    /// wrapped onto its own line; the <c>Func</c> field; the indexer; an attribute inside the
    /// parameter list; a comment between the return type and the name; and a named delegate type.
    /// The last three were found by review AFTER the previous draft called itself complete.
    ///
    /// THE NEGATIVE CASES ARE NOT DECORATION. A gate that accuses ordinary code gets switched off,
    /// so each one is a false alarm a draft actually produced: a member returning what it was
    /// handed, a member whose NEIGHBOUR returns a status, and a statement that USES an indexer
    /// rather than declaring one.
    /// </remarks>
    [Fact]
    public void DetectsKnownShapesOfSentenceToAppearance()
    {
        Assert.NotEmpty(OffendersIn(
            "Plain.cs", "    private static DictationStatus Read(string sentence) => default;"));

        Assert.NotEmpty(OffendersIn(
            "Aliased.cs",
            "using Pill = EnviousWispr.Core.Presentation.DictationStatus;\n\n"
                + "    private static Pill Read(string sentence) => default;"));

        Assert.NotEmpty(OffendersIn(
            "Wrapped.cs",
            "    private static IReadOnlyList<DictationOverlayState>\n"
                + "        Read(string sentence) => [];"));

        Assert.NotEmpty(OffendersIn(
            "Delegated.cs", "    private Func<string, DictationStatus> _read = _ => default;"));

        Assert.NotEmpty(OffendersIn(
            "Indexed.cs",
            "    private DictationStatus this[string sentence] =>\n"
                + "        DictationStatus.Warning(sentence);"));

        Assert.NotEmpty(OffendersIn(
            "Attributed.cs",
            "    private DictationStatus Read([Tag(\"input\")] string sentence) => default;"));

        Assert.NotEmpty(OffendersIn(
            "Documented.cs",
            "    private static DictationStatus\n"
                + "        // reads it\n"
                + "        Read(string sentence) => default;"));

        Assert.NotEmpty(OffendersIn(
            "NamedDelegate.cs",
            "    private delegate DictationStatus Reader(string sentence);"));

        // A local function is not a member, and it is the easiest shape to reach for once every
        // member kind is refused.
        Assert.NotEmpty(OffendersIn(
            "LocalFunction.cs",
            "    private void Show()\n"
                + "    {\n"
                + "        DictationStatus Read(string sentence) => default;\n"
                + "    }"));

        // A nullable or fully qualified string is still a string.
        Assert.NotEmpty(OffendersIn(
            "NullableString.cs",
            "    private DictationStatus Read(string? sentence) => default;"));

        Assert.NotEmpty(OffendersIn(
            "QualifiedString.cs",
            "    private DictationStatus Read(System.String sentence) => default;"));

        Assert.Empty(OffendersIn(
            "Innocent.cs", "    private static string Trim(string sentence) => sentence.Trim();"));

        Assert.Empty(OffendersIn(
            "Neighbours.cs",
            "    private static DictationStatus ExistingStatus() =>\n"
                + "        DictationStatus.Quiet(\"Ready\");\n"
                + "    private static string Normalize(string sentence) => sentence.Trim();"));

        Assert.Empty(OffendersIn(
            "IndexerUse.cs",
            "    private void Show() { var current = statuses[this[string.Empty]]; }"));

        // A LOOKUP IS NOT THE DEFECT. Mapping a known outcome onto a status through a dictionary is
        // legitimate and is arguably the clearest way to do it. An earlier draft flagged this, which
        // would have pushed people away from the good pattern.
        Assert.Empty(OffendersIn(
            "Lookup.cs",
            "    private readonly Dictionary<string, DictationStatus> _statuses = [];"));

        Assert.Empty(OffendersIn(
            "GenericCall.cs",
            "    private void Show() { var status = cache.Get<string, DictationStatus>(); }"));
    }

    /// <summary>
    /// Every status handed to the window names the pill it wants.
    /// </summary>
    /// <remarks>
    /// THE GATE ABOVE REFUSES THE OLD SHAPE; THIS ONE REFUSES A NEW STATUS FROM SKIPPING THE
    /// CHOICE. Every call site must name a <c>DictationStatus</c>, including the quiet ones, so a
    /// status that shows no pill says so on purpose rather than by falling through.
    ///
    /// THE CARRIER LIST IS READ OUT OF THE SOURCE, NOT KEPT HERE. The first draft carried a
    /// hand-written list that included the bare word "status", and a reviewer showed that a helper
    /// named <c>statusFromSentence</c> would satisfy it - the allowlist was wide enough to admit
    /// the very defect the pair exists to refuse. Now a carrier is a member this file declares as
    /// returning <c>DictationStatus</c>, or a parameter or local of that type, so a name only
    /// counts once the compiler agrees what it is.
    ///
    /// IT COUNTS WHAT IT SKIPPED. A regex that walks nested parentheses has a depth, and a call
    /// nested deeper than that depth is not reported as suspicious - it is not seen at all, which
    /// is the silent direction. The extracted count is compared against a plain count of the call
    /// token, so a call this cannot parse fails the gate rather than escaping it.
    /// </remarks>
    [Fact]
    public void EveryStatusHandedToTheWindowNamesItsPill()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var shell = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));

        const string call = "SetSessionStatus(";
        var arguments = new List<string>();
        for (var index = shell.IndexOf(call, StringComparison.Ordinal);
             index >= 0;
             index = shell.IndexOf(call, index + call.Length, StringComparison.Ordinal))
        {
            var argument = BalancedArgument(shell, index + call.Length);
            if (argument is not null)
            {
                arguments.Add(argument);
            }
        }

        var written = CountOccurrences(shell, call);
        Assert.True(written >= 20, $"Expected the app's status call sites, found {written}.");
        Assert.True(
            arguments.Count == written,
            $"{written - arguments.Count} of {written} SetSessionStatus calls could not be read, so "
                + "this gate is silently not checking them.");

        // A carrier is a name the compiler already agrees is a DictationStatus: a member declared
        // to return one, or a parameter or local of that type.
        var carriers = DictationStatusCarrier().Matches(shell)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var unnamed = arguments
            .Where(argument => !argument.Contains("DictationStatus.", StringComparison.Ordinal))
            .Where(argument => !carriers.Any(carrier =>
                IdentifierRegexFor(carrier).IsMatch(argument)))
            .ToArray();

        Assert.True(
            unnamed.Length == 0,
            "These statuses reach the window without naming the pill they want, so the appearance "
                + "is decided somewhere this suite cannot see: " + string.Join(" | ", unnamed));
    }

    private static IEnumerable<string> ProductionSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The argument between one call's parentheses, or null if they never balance.</summary>
    private static string? BalancedArgument(string text, int start)
    {
        var depth = 1;
        for (var scan = start; scan < text.Length; scan++)
        {
            if (text[scan] == '(')
            {
                depth++;
            }
            else if (text[scan] == ')' && --depth == 0)
            {
                return text[start..scan].Trim();
            }
        }

        return null;
    }

    private static int CountOccurrences(string text, string token)
    {
        var total = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            total++;
        }

        return total;
    }

    private static Regex IdentifierRegexFor(string name) =>
        new(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);

    /// <summary>A name the compiler agrees is a DictationStatus: member, parameter, or local.</summary>
    [GeneratedRegex(@"\bDictationStatus\??\s+(\w+)\s*(?:\(|=|\)|,|;)")]
    private static partial Regex DictationStatusCarrier();

    /// <summary>
    /// No page arrives with a heading and nothing under it.
    /// </summary>
    /// <remarks>
    /// AN EMPTY DESCRIPTION IS NOT A BLANK LINE, IT IS A SHORTER CARD. Every other page's header is
    /// a title plus a sentence, so the one page with an empty string had a header card of a
    /// different height and the rhythm broke as the user clicked past it. Nothing renders wrong;
    /// the page is simply a different shape from its siblings for no reason a reader can see.
    ///
    /// It is also the cheapest thing in the window to get wrong, because an empty string is a
    /// perfectly valid value and no compiler, no test and no screen sweep objects to it.
    /// </remarks>
    [Fact]
    public void NoPageHeaderIsMissingItsDescription()
    {
        var code = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var empty = PageHeaderEntry().Matches(code)
            .Where(match => match.Groups["description"].Value.Trim() is "string.Empty" or "\"\"")
            .Select(match => match.Groups["title"].Value)
            .ToArray();

        Assert.True(
            empty.Length == 0,
            "These pages arrive with a title and nothing under it, so their header card is a "
                + "different height from every other page's: " + string.Join(", ", empty));

        // Control. The matcher must be finding real header entries, or "none empty" would be true
        // of a search that matched nothing at all.
        Assert.True(
            PageHeaderEntry().Count(code) >= 6,
            $"Expected the page header table, found {PageHeaderEntry().Count(code)} entries.");
    }

    /// <summary>A page header entry: a title and the sentence under it.</summary>
    /// <remarks>
    /// The third element used to be a glyph and this pattern required one. It is now the page's
    /// section, or nothing at all on the help pages, because the icon is read off the sidebar row
    /// instead of written here. The arm therefore ends in either a comma or a bracket, and matching
    /// only the comma form silently skipped every help page.
    /// </remarks>
    [GeneratedRegex(
        @"=>\s*\(\s*""(?<title>[^""]+)"",\s*(?<description>string\.Empty|""[^""]*"")\s*[,)]",
        RegexOptions.Singleline)]
    private static partial Regex PageHeaderEntry();

    /// <summary>
    /// Nothing on the Appearance page can change the window without also being kept.
    /// </summary>
    /// <remarks>
    /// THE DEFECT THIS EXISTS FOR APPLIED INSTANTLY AND SAVED NOTHING. Choosing Light repainted the
    /// window, which is the strongest signal a user can get that a choice took, and the app came
    /// back as System on the next launch. Both halves worked in isolation - saving a theme
    /// round-tripped, choosing one repainted - and nothing joined them. A gap between two passing
    /// tests is not a failure inside either, which is why no existing test saw it.
    ///
    /// APPEARANCE IS THE ONLY SETTINGS PAGE WITH NO SAVE BUTTON, so its controls are the only ones
    /// in the window that must carry their own persistence. That is what makes this checkable at
    /// all: the rule is not "every control saves", it is "these controls have nowhere else to save
    /// from".
    ///
    /// LIMIT, STATED RATHER THAN IMPLIED: this checks the WIRING, not the write. It proves each
    /// choice reaches the persist method and that the method writes through the settings store. It
    /// cannot prove the file on disk changed - that needs the running app, and it is the check the
    /// session driving the real app should keep making.
    /// </remarks>
    [Fact]
    public void EveryAppearanceChoiceIsKeptWithoutASaveButton()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var markup = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        // Both choice controls must report a selection at all. An unwired RadioButton changes the
        // screen through its binding and tells no one, which is precisely the original defect.
        foreach (var group in new[] { "PillTheme", "PillOverlayPosition" })
        {
            var button = RadioButtonInGroup(group).Match(markup);
            Assert.True(button.Success, $"No radio button found in the {group} group.");
            // NOT Contains("Checked=\"") - every one of these carries IsChecked="{Binding ...}",
            // and that substring satisfies a naive search, so the check passed with the wiring
            // deliberately removed. The handler attribute has to be matched as its own attribute.
            Assert.True(
                CheckedHandler().IsMatch(button.Value),
                $"The {group} choice changes the window and reports it to nothing, so it cannot be "
                    + "kept. Appearance has no Save button to fall back on.");
        }

        // Every handler those controls name must reach the one method that writes.
        var handlers = RadioButtonInGroup("Pill(Theme|OverlayPosition)").Matches(markup)
            .Select(match => CheckedHandler().Match(match.Value).Groups[1].Value)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(handlers.Length >= 1, "No Checked handlers found on the Appearance choices.");

        foreach (var handler in handlers)
        {
            var body = HandlerBody(handler).Match(code);
            Assert.True(body.Success, $"{handler} is named in the markup and does not exist.");
            Assert.True(
                body.Value.Contains("PersistAppearanceChoicesAsync", StringComparison.Ordinal),
                $"{handler} changes the window without keeping the choice.");
        }

        // And the method they reach must actually write, rather than merely existing.
        var persist = HandlerBody("PersistAppearanceChoicesAsync").Match(code);
        Assert.True(persist.Success, "PersistAppearanceChoicesAsync does not exist.");
        Assert.Contains("_settingsStore.SaveAsync", persist.Value, StringComparison.Ordinal);
    }

    private static Regex RadioButtonInGroup(string group) =>
        new($@"<RadioButton GroupName=""{group}""[^>]*>");

    /// <summary>A Checked handler attribute, and not the IsChecked binding beside it.</summary>
    /// <remarks>
    /// The lookbehind is the whole point: IsChecked="{Binding IsSelected}" ends in the same eight
    /// characters, so without it this matches the binding and reports a handler that is not there.
    /// </remarks>
    [GeneratedRegex(@"(?<![A-Za-z])Checked=""(\w+)""")]
    private static partial Regex CheckedHandler();

    /// <summary>A method and everything up to the next one at the same indentation.</summary>
    private static Regex HandlerBody(string name) =>
        new($@"\b{Regex.Escape(name)}\([^)]*\)\s*(?::[^{{]*)?{{.*?\n    }}", RegexOptions.Singleline);

    /// <summary>
    /// Every navigation row has an icon, and no two rows wear the same one.
    /// </summary>
    /// <remarks>
    /// A ROW WITH NO ICON DOES NOT LOSE A DECORATION, IT LOSES ITS INDENT. Its label starts in the
    /// icon column, so it sits about thirty points left of every other label and the sidebar reads
    /// as broken. Three rows shipped that way - the three added to re-home the orphaned controls -
    /// and it was the most visible thing in the whole window.
    ///
    /// THE DUPLICATE HALF MATTERS AS MUCH AS THE MISSING HALF, and is harder to see. Two rows
    /// wearing one icon is worse than a plain row, because the icon column stops distinguishing
    /// anything, which is its entire job. This found What's New and Snippets both drawing the
    /// document symbol.
    ///
    /// IT CANNOT BE CHECKED ON SCREEN. FontIcons do not appear in the accessibility tree in this
    /// build, so the session driving the real app can only see icons by eye, one page at a time.
    /// That makes the markup the only place this is mechanically answerable, and this test the only
    /// thing standing between a new row and a broken indent.
    ///
    /// THE FIRST VERSION OF THE DUPLICATE CHECK WAS BLIND TO HALF THE PAIRS, AND THAT IS THE LESSON.
    /// A row can name a built-in Symbol or a Fluent glyph, and the two forms can render the IDENTICAL
    /// picture while reading as different declarations - Symbol "Character" and glyph E8C1 are the
    /// same drawing. Comparing declarations therefore reported a genuinely duplicated pair as
    /// distinct, and it reported it confidently, because the strings really are different.
    ///
    /// The fix is not a lookup table translating one form into the other - that table would be a
    /// premise nobody executed, and wrong entries in it would be invisible. Every nav row now
    /// declares a glyph, so there is one axis and the comparison is exact by construction. The ban
    /// below is what keeps it that way.
    /// </remarks>
    [Fact]
    public void EveryNavigationRowHasItsOwnIcon()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        var rows = NavigationRow().Matches(markup)
            .Select(match => new
            {
                Label = match.Groups["label"].Value,
                Icon = match.Groups["glyph"].Success
                    ? "glyph:" + match.Groups["glyph"].Value.ToUpperInvariant()
                    : SymbolIcon().Match(match.Groups["head"].Value) is { Success: true } symbol
                        ? "symbol:" + symbol.Groups[1].Value
                        : null,
            })
            .ToArray();

        Assert.True(rows.Length >= 15, $"Expected the navigation rows, found {rows.Length}.");

        var plain = rows.Where(row => row.Icon is null).Select(row => row.Label).ToArray();
        Assert.True(
            plain.Length == 0,
            "These navigation rows have no icon, so their labels sit in the icon column: "
                + string.Join(", ", plain));

        // ONE FORM ONLY, OR THE COMPARISON BELOW CANNOT BE TRUSTED. This is the clause that makes
        // "no two rows share an icon" a real claim rather than a claim about spelling.
        var builtIn = rows
            .Where(row => row.Icon?.StartsWith("symbol:", StringComparison.Ordinal) == true)
            .Select(row => row.Label)
            .ToArray();

        Assert.True(
            builtIn.Length == 0,
            "These navigation rows use a built-in symbol rather than a glyph, so a duplicate against "
                + "a glyph row cannot be detected: " + string.Join(", ", builtIn));

        var shared = rows
            .GroupBy(row => row.Icon, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} on {string.Join(" and ", group.Select(row => row.Label))}")
            .ToArray();

        Assert.True(
            shared.Length == 0,
            "These navigation rows wear the same icon as another row: " + string.Join("; ", shared));

        // Control. Every row reaches its icon as a glyph now, so the matcher must be finding them
        // that way - a regex that had stopped matching would otherwise report a clean sidebar while
        // seeing nothing at all. The symbol branch is still read, above, so the ban can fire.
        Assert.All(rows, row =>
            Assert.StartsWith("glyph:", row.Icon, StringComparison.Ordinal));
    }

    /// <summary>
    /// A navigation row, with the Fluent glyph that follows it when it uses one.
    /// </summary>
    /// <remarks>
    /// BOTH FORMS OR THE TEST IS A LIE. A built-in symbol is an attribute on the element; a Fluent
    /// glyph is a child element after it. Matching only one form would report every row using the
    /// other as having no icon, and this window uses both.
    /// </remarks>
    [GeneratedRegex(
        @"<NavigationViewItem\b(?![\w.])(?<head>[^>]*?Content=""(?<label>[^""]*)""[^>]*?)>"
            + @"(?:\s*<NavigationViewItem\.Icon>\s*<FontIcon Glyph=""&\#x(?<glyph>[0-9A-Fa-f]+);"")?",
        RegexOptions.Singleline)]
    private static partial Regex NavigationRow();

    /// <summary>The built-in symbol form, read out of a row's own attributes.</summary>
    [GeneratedRegex(@"\sIcon=""(\w+)""")]
    private static partial Regex SymbolIcon();

    /// <summary>Every assignment to a control's IsEnabled, with the value it assigns.</summary>
    [GeneratedRegex(@"(\w+)\.IsEnabled\s*=\s*([^;]+);")]
    private static partial Regex EnabledAssignment();

    [GeneratedRegex(@"x:Name=""(\w+Section)""")]
    private static partial Regex SectionName();

    /// <summary>
    /// The array the settings page iterates in order to HIDE sections.
    /// </summary>
    /// <remarks>
    /// The one place a section name appears without showing anything, which is why an orphaned
    /// section is invisible to a plain search: it sits here and nowhere else.
    /// </remarks>
    [GeneratedRegex(@"SettingsSections\(\) =>\s*\[[^\]]*\]", RegexOptions.Singleline)]
    private static partial Regex SettingsSectionsArray();
}
