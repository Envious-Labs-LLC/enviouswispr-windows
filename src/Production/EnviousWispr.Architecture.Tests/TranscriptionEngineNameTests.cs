using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Xml.Linq;
using EnviousWispr.Analyzers;
using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Presentation;
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
        "parakeet-ultra:cpu",
        "parakeet-ultra:cuda",
        "parakeet-ultra:cuda:isolated",
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
        var xaml = AppXamlSources.ReadMainWindowAndPagesText(FindRepositoryRoot());

        Assert.Contains("Content=\"Parakeet\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Whisper\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Automatic\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Automatic\"", xaml, StringComparison.Ordinal);

        // Only actual engines (Parakeet and Whisper) remain in FinalAsrEngine; legacy "Automatic" maps to Parakeet.
        Assert.Equal(2, Enum.GetValues<FinalAsrEngine>().Length);
    }

    /// <summary>
    /// The in-process Parakeet engine's public constant is the same value as the Core owner, so
    /// its two existing consumers keep working without knowing the constant moved.
    /// </summary>
    [Fact]
    public void TheParakeetEngineConstantIsTheCoreOne()
    {
        Assert.Equal("parakeet-ultra", ParakeetModelIds.Final);
        Assert.Equal("parakeet-tdt-0.6b-v3", ParakeetModelIds.LegacyV3);
        Assert.Equal(ParakeetModelIds.Final, ParakeetTranscriptionEngine.ModelId);
    }

    [GeneratedRegex(@"parakeet-ultra|parakeet-tdt-[0-9.]+b-v[0-9]+|whisper-large-v[0-9]+-turbo|""whisper-small""")]
    private static partial Regex ModelNameLiteral();

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
        var markup = AppXamlSources.ReadMainWindowAndPagesText(FindRepositoryRoot());
        var code = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App"),
                    "MainWindow*.cs")
                .Order()
                .Select(File.ReadAllText));

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
        var code = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App"),
                    "MainWindow*.cs")
                .Order()
                .Select(File.ReadAllText));

        var formatter = DayFieldFormatter().Match(code);
        Assert.True(formatter.Success, "Could not find the day fields' formatter.");

        Assert.Contains("IncrementNumberRounder", formatter.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("Increment = 1", formatter.Groups[1].Value, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"x:Name=""([A-Za-z]+)""[^>]*KeyDown=""HotkeyBoxKeyDown""")]
    private static partial Regex CaptureField();

    [GeneratedRegex(@"foreach \(var box in new\[\] \{([^}]*)\}\)\s*\{\s*box\.GotFocus", RegexOptions.Singleline)]
    private static partial Regex CaptureFocusLoop();

    [GeneratedRegex(@"(?:box|DiagnosticRetentionDaysBox)\.NumberFormatter = new DecimalFormatter\s*\{(.*?)\};", RegexOptions.Singleline)]
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
                "src", "Production", "EnviousWispr.App", "Theme", "ControlBrushes.xaml"));

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
        var document = LoadMainWindow();

        // Fluent pages pin their left edge beside the sidebar and stretch cleanly up to BrandFluentPageMaxWidth
        // using a single capped star column inside a Grid, so wide windows neither center a narrow column nor
        // shrink short-copy pages to text width.
        foreach (var name in new[]
                 {
                     "WhatsNewPage",
                     "DictionaryPage",
                     "SnippetsPage",
                     "SettingsPage",
                 })
        {
            var page = FindNamedElement(document, name);
            var grid = page.Elements().Single();
            Assert.Equal("Grid", grid.Name.LocalName);
            var columnDefinitions = grid.Elements()
                .Single(e => e.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements()
                .ToArray();
            var col = Assert.Single(columnDefinitions);
            Assert.Equal("*", (string?)col.Attribute("Width"));
            Assert.Equal("{StaticResource BrandFluentPageMaxWidth}", (string?)col.Attribute("MaxWidth"));
            var container = grid.Elements().Single(e =>
                e.Name.LocalName is "StackPanel" or "Grid");
            Assert.Null(container.Attribute("MaxWidth"));
        }

        // Wave 3 HistoryPage uses a full-height two-column split instead of a capped content column.
        var historyPage = FindNamedElement(document, "HistoryPage");
        Assert.Equal("Grid", historyPage.Name.LocalName);
        Assert.Equal(0, PageContentColumn().Count(historyPage.ToString()));

        // Control, both directions.
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
        // THE WRITE LIVES IN THE SHELL AND THE FINALLY LIVES IN PIPELINE, since step 6 on #148 moved
        // the finalisation out of the shell behind an effects port. So the gate has to prove a CHAIN,
        // not two facts: the runner's finally calls the port unconditionally, and the shell's only
        // write of the event is the port's implementation, using the number it was handed. Two
        // disconnected token searches let the write wander anywhere - a reviewer showed the event
        // could be logged from "Transcribing..." with both searches still green.
        var root = FindRepositoryRoot();
        // The shell's session sources: the window's file and the WinUI-free composition beside it,
        // where the effects adapters live since plan-2 step 4. One tree, so a write that moved
        // between them is still one write.
        var shell = CSharpSyntaxTree.ParseText(string.Join(
            Environment.NewLine,
            ShellSessionSources(root).Select(File.ReadAllText))).GetRoot();
        var runner = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(
            root, "src", "Production", "EnviousWispr.Pipeline", "SessionFinalizationRunner.cs"))).GetRoot();

        // The runner: RunAsync has exactly one finally, and it invokes the port there and nowhere else.
        // (The file also declares the executor's seam, whose RunAsync has no body.)
        var run = runner.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "RunAsync" && method.Body is not null);
        var finallys = run.DescendantNodes().OfType<FinallyClauseSyntax>().ToArray();
        Assert.True(finallys.Length == 1, $"Expected one finally in RunAsync, found {finallys.Length}.");
        var reportsInFinally = finallys[0].DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(call => call.Expression.ToString().EndsWith("RecordDictationCompleted", StringComparison.Ordinal));
        var reportsAnywhere = run.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(call => call.Expression.ToString().EndsWith("RecordDictationCompleted", StringComparison.Ordinal));
        Assert.True(
            reportsInFinally == 1 && reportsAnywhere == 1,
            "The wait is not reported from the finally, or is reported elsewhere too, so an exit path can leave "
                + $"without reporting it or report it twice (finally={reportsInFinally}, total={reportsAnywhere}).");

        // The shell: the only write of the event is the port implementation, and it uses its argument.
        var writers = shell.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.ToString().Contains("AppEventCode.DictationCompleted", StringComparison.Ordinal))
            .ToArray();
        var writer = Assert.Single(writers);
        Assert.Equal("RecordDictationCompleted", writer.Identifier.ValueText);
        var parameter = Assert.Single(writer.ParameterList.Parameters).Identifier.ValueText;
        Assert.Contains(
            writer.DescendantNodes().OfType<IdentifierNameSyntax>(),
            name => name.Identifier.ValueText == parameter);
        var writesInShell = shell.ToFullString().Split("AppEventCode.DictationCompleted").Length - 1;
        Assert.True(writesInShell == 1, $"Expected exactly one place to write the event, found {writesInShell}.");
    }

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
    ///
    /// PAIRED PER METHOD, NOT AS TWO TOTALS. Two totals let a watcher stop deleted from one path and
    /// duplicated in another pass as equal, and let a mention in a comment stand in for a call. The
    /// parser finds each stop as an invocation or as a method group handed to the cleanup helper,
    /// and every method that stops the preview must stop the watcher inside the same body.
    ///
    /// SYNTACTIC CO-LOCATION, AND THE LIMITS ARE STATED. Both stops in one body is what is checked,
    /// not that both run on every branch or in any order; a local function's body counts for its
    /// enclosing method whether or not it is called; a nested class's method is its own method; a
    /// null-conditional call (`_livePreview?.StopAsync()`) loses its receiver in the syntax and would
    /// not be seen as a preview stop at all. The floor is a count of METHODS that stop the preview.
    /// </remarks>
    [Fact]
    public void TheAutoStopWatcherIsTornDownWhereverTheLivePreviewIs()
    {
        // ONE OWNER SINCE PLAN-2 STEP 8: the order around a recording, the recovery's stops and the
        // teardown's are all the background owner's (SessionBackgroundWork), whose unbounded and
        // bounded stops are the two methods counted; the shell stops nothing itself any more. Both
        // files are still read so a stop that reappeared in the shell would be checked too.
        var root = FindRepositoryRoot();
        var methods = Directory.EnumerateFiles(
                Path.Combine(root, "src", "Production", "EnviousWispr.App"),
                "App*.cs")
            .Order()
            .Append(Path.Combine(root, "src", "Production", "EnviousWispr.Pipeline", "SessionBackgroundWork.cs"))
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>());

        var previewStops = 0;
        var unpaired = new List<string>();
        foreach (var method in methods)
        {
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body is null)
            {
                continue;
            }

            // A call (`await app._livePreview.StopAsync()`) or a method group handed to the cleanup
            // helper (`TryCleanupAsync(_autoStop.StopAsync)`); a comment is neither.
            var references = body.DescendantNodes()
                .Select(node => node switch
                {
                    InvocationExpressionSyntax invocation => invocation.Expression.ToString(),
                    ArgumentSyntax argument => argument.Expression.ToString(),
                    _ => null,
                })
                .OfType<string>()
                .ToArray();
            var stopsPreview = references.Any(text =>
                text.EndsWith("_livePreview.StopAsync", StringComparison.Ordinal) ||
                text.EndsWith("_preview.StopAsync", StringComparison.Ordinal));
            var stopsWatcher = references.Any(text => text.EndsWith("_autoStop.StopAsync", StringComparison.Ordinal));
            if (!stopsPreview)
            {
                continue;
            }

            previewStops++;
            if (!stopsWatcher)
            {
                unpaired.Add(method.Identifier.ValueText);
            }
        }

        // THE FLOOR IS TODAY'S COUNT of methods that stop the preview, so a path deleted or renamed
        // out of the pattern is noticed rather than silently narrowing the gate. Six until step 11 of
        // #148 moved the watchdog's and Windows' recoveries behind the executor; four while the
        // executor reached the shell's stops through its effects; three while the order around a
        // recording was Pipeline's but the recovery still the shell's; two since the recovery moved
        // into the executor too, which stops through the same Pipeline owner (one method there, the
        // teardown here).
        Assert.True(previewStops >= 2, $"Expected the preview teardown call sites, found {previewStops}.");
        Assert.True(
            unpaired.Count == 0,
            "These methods stop the live preview without stopping the auto-stop watcher; a watcher that "
                + "outlives its recording ends the NEXT one early: " + string.Join(", ", unpaired));
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
        var root = FindRepositoryRoot();
        var timers = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.Pipeline", "RecordingTimers.cs"));
        var effects = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "Composition", "RuntimeEffects.cs"));
        var composition = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "Composition", "RuntimeComposition.cs"));
        var shell = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.DictationRuntime.cs"));

        // THE LENGTH CONTROL IS GONE BECAUSE WHAT IT GUARDED IS GONE. The pattern this replaces
        // ended at the first line reading exactly four spaces and a brace, so it could capture a
        // FRAGMENT of the method and the assertion below would then be about part of a body; the
        // character count stood in for "did you get the whole thing". The parser returns the method
        // or nothing, so there is no fragment to guard against. Ref: #82.
        //
        // THREE LINKS SINCE PLAN-2 STEP 5: the monitor in the pipeline posts a Released signal through
        // its port; the composed adapter for that port hands the signal to the runtime's queue; and
        // the hook's handler in the shell hands a key to that same queue. Any link alone could be
        // rewired to a parallel finish; all three are checked.
        Assert.Contains(
            "Post(PushToTalkSignal.Released, sessionId)",
            DeclarationTextOf(timers, "RunAsync"),
            StringComparison.Ordinal);
        Assert.Contains(
            "queue.HandAsync(signal, forSession)",
            DeclarationTextOf(effects, "Post"),
            StringComparison.Ordinal);
        Assert.Contains(
            "coordinator.SubmitAsync(signal, forSession, isHandsFree)",
            DeclarationTextOf(composition, "HandAsync"),
            StringComparison.Ordinal);
        Assert.Contains(
            "_queue.HandAsync(signal, forSession: null, isHandsFree)",
            DeclarationTextOf(composition, "SubmitAsync"),
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtime.SubmitAsync(args.Signal, args.IsHandsFree)",
            DeclarationTextOf(shell, "OnPushToTalkSignalled"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The shell hands what a session uses to the lifetime under the lists it runs only behind a
    /// quiescent session - and disposes it nowhere else on the way out.
    /// </summary>
    /// <remarks>
    /// TEARDOWN NEVER RUNS BESIDE A RESOURCE USER (plan-2 steps 8 and 9). The coordinator's report
    /// says whether anything is still using the session; the engines, the polish provider, the worker
    /// arbiter, the background owners and the stores are what such a user is inside. The lifetime
    /// (ApplicationLifetime, proved in ApplicationLifetimeTests) runs DisposeSessionDependencies only
    /// behind a quiescent session with nothing outstanding, and CloseRunState only once nothing that
    /// writes to the store is outstanding; what this gate checks is the shell's side of that contract: every
    /// disposal of one of those on the way out is under one of those named arguments of
    /// LifetimeParts(), the shell's DisposeAsync disposes nothing itself, and the shell leaves through
    /// the lifetime and not around it. The App itself cannot be run here, so this is read from its
    /// source.
    /// </remarks>
    /// <summary>
    /// The Quick Add borrows the delivery adapter, which the session's teardown disposes, so it may
    /// touch the adapter only under a presentation lease - the one thing the exit joins before it asks
    /// the session to shut down. The App cannot be run here; this is read from its source.
    /// </summary>
    [Fact]
    public void TheQuickAddBorrowsTheAdapterOnlyUnderAPresentationLease()
    {
        var root = FindRepositoryRoot();
        var methods = Directory.EnumerateFiles(
                Path.Combine(root, "src", "Production", "EnviousWispr.App"),
                "App*.cs")
            .Order()
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>())
            .ToArray();

        // THE HANDLER TAKES THE LEASE BEFORE ANYTHING ELSE and runs the borrow inside `using (lease)`.
        var handler = methods.Single(method => method.Identifier.ValueText == "HandleQuickAddAsync");
        Assert.Contains("presentation.TryEnter(out var lease)", handler.ToString(), StringComparison.Ordinal);
        var usingLease = handler.DescendantNodes().OfType<UsingStatementSyntax>()
            .Single(statement => statement.Expression?.ToString() == "lease");
        var borrow = usingLease.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(call => call.Expression.ToString().EndsWith("QuickAddUnderLeaseAsync", StringComparison.Ordinal));
        Assert.Contains("lease", borrow.ArgumentList.Arguments.Select(argument => argument.ToString()));

        // EVERY TOUCH OF THE ADAPTER IN THE BORROW RUNS UNDER THE LEASE'S TOKEN, and nothing of the
        // handler outside the lease touches it.
        var underLease = methods.Single(method => method.Identifier.ValueText == "QuickAddUnderLeaseAsync");
        static string Compact(SyntaxNode node) => string.Concat(node.ToString().Where(character => !char.IsWhiteSpace(character)));
        var adapterCalls = underLease.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => Compact(call.Expression) is "adapter.CaptureContextAsync" or "WindowsTextTargetAdapter.TryReadSelectionWithCopyAsync")
            .ToArray();
        Assert.Equal(2, adapterCalls.Length);
        Assert.All(adapterCalls, call => Assert.Contains("lease.Closing", call.ArgumentList.Arguments.Select(argument => argument.ToString())));
        Assert.DoesNotContain(
            handler.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            access => access.Name.Identifier.ValueText is "CaptureContextAsync" or "TryReadSelectionWithCopyAsync");

        // THE DIALOG IS QUEUED THROUGH ONE METHOD, AND THAT METHOD READS THE CLOSURE WHEN IT RUNS:
        // the borrow never enqueues to the window itself, and the one place that does checks Leaving
        // - the flags or the presentation's durable closure - first inside the queued callback, as
        // does the show it calls.
        Assert.DoesNotContain(
            underLease.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            access => access.Name.Identifier.ValueText == "TryEnqueue");
        Assert.Equal(2, underLease.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Count(call => call.Expression.ToString() == "OpenQuickAddUnlessLeaving"));
        foreach (var name in new[] { "OpenQuickAddUnlessLeaving", "ShowMainWindow" })
        {
            var queued = methods.Single(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count > 0)
                .DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(call => call.Expression.ToString().EndsWith("TryEnqueue", StringComparison.Ordinal));
            var callback = (LambdaExpressionSyntax)queued.ArgumentList.Arguments.Single().Expression;
            var firstStatement = Assert.IsType<IfStatementSyntax>(((BlockSyntax)callback.Body).Statements.First());
            Assert.Contains("Leaving", firstStatement.Condition.ToString(), StringComparison.Ordinal);
            Assert.IsType<ReturnStatementSyntax>(((BlockSyntax)firstStatement.Statement).Statements.Single());
        }

        // THE OTHER PROVIDER BORROWER - the mishearing suggestion - is admitted the same way and
        // hands a linked token (caller cancellation + lease.Closing) to the provider.
        var advisorSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "Composition", "AdmittedMishearingAdvisor.cs"));
        var advisorTree = CSharpSyntaxTree.ParseText(advisorSource).GetRoot();
        var suggestion = advisorTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "SuggestAsync");
        Assert.Contains("_presentation.TryEnter(out var lease)", suggestion.ToString(), StringComparison.Ordinal);
        Assert.Contains("CreateLinkedTokenSource(cancellationToken, lease.Closing)", suggestion.ToString(), StringComparison.Ordinal);
        var suggest = suggestion.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(call => call.Expression.ToString() == "advisor.SuggestAsync");
        Assert.Contains("linked.Token", suggest.ArgumentList.Arguments.Select(argument => argument.ToString()));
        Assert.NotNull(suggest.Ancestors().OfType<UsingStatementSyntax>().SingleOrDefault(statement => statement.Expression?.ToString() == "lease"));
    }

    /// <summary>
    /// The launch after the tray and a model delivery borrow what the exit disposes - the recovery
    /// store, the engines they build or replace. Each is one tracked task the exit policy cancels
    /// and Quiesce joins, and each reads Leaving after every await outside its own cancellation, so
    /// nothing is built once the exit has begun. Read from the App's source.
    /// </summary>
    [Fact]
    public void TheLaunchAndAModelDeliveryAreTrackedCancelledAndJoinedByTheLifetime()
    {
        var root = FindRepositoryRoot();
        var appDir = Path.Combine(root, "src", "Production", "EnviousWispr.App");
        var shell = File.ReadAllText(Path.Combine(appDir, "App.xaml.cs"));
        var delivery = AppXamlSources.ReadAppModelDeliveryCodeText(root);
        var methods = Directory.EnumerateFiles(appDir, "App*.cs")
            .Order()
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>())
            .ToArray();

        // TRACKED: the launch's tail is the _startup task, awaited by OnLaunched; the delivery is the
        // _modelDelivery task, started by the request and cleared when it ends.
        Assert.Contains("_startup = CompleteStartupAsync(", DeclarationTextOf(shell, "OnLaunched"), StringComparison.Ordinal);
        var request = DeclarationTextOf(delivery, "OnModelDownloadRequested");
        Assert.Contains("_modelDelivery = DeliverModelsAsync();", request, StringComparison.Ordinal);
        // A SECOND REQUEST IS REFUSED BY A RUNNING TASK, NOT A STORED ONE: the guard reads the task's
        // completion, so a delivery that refused synchronously does not block the retry it asked for,
        // and nothing clears the field behind the assignment.
        Assert.Contains("_modelDelivery is { IsCompleted: false }", request, StringComparison.Ordinal);
        Assert.DoesNotContain("_modelDelivery = null", delivery, StringComparison.Ordinal);

        // CANCELLED AND JOINED: the exit policy cancels both; Quiesce joins both, taking each once.
        var parts = methods.Single(method => method.Identifier.ValueText == "LifetimeParts");
        var policy = parts.DescendantNodes().OfType<ArgumentSyntax>().Single(argument => argument.NameColon?.Name.Identifier.ValueText == "CancelProcessing").ToString();
        Assert.Contains("_startupCancellation.Cancel();", policy, StringComparison.Ordinal);
        Assert.Contains("_modelDownload?.Cancel();", policy, StringComparison.Ordinal);
        var quiesce = parts.DescendantNodes().OfType<ArgumentSyntax>().Single(argument => argument.NameColon?.Name.Identifier.ValueText == "Quiesce").ToString();
        Assert.Contains("new LifetimeStep(\"startup\", () => Join(Interlocked.Exchange(ref _startup, null)))", quiesce, StringComparison.Ordinal);
        Assert.Contains("new LifetimeStep(\"model delivery\", () => Join(Interlocked.Exchange(ref _modelDelivery, null)))", quiesce, StringComparison.Ordinal);

        // NOTHING BUILT AFTER THE EXIT HAS BEGUN: in the launch's tail, every await - the last
        // included, since the session is built after it - is followed by the Leaving check; the
        // recovery read carries the launch's own token.
        static void EveryAwaitIsFollowedByTheLeavingCheck(MethodDeclarationSyntax method, int expectedAwaits)
        {
            var statements = method.Body!.Statements;
            var awaits = statements.Where(statement => statement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any()).ToArray();
            Assert.Equal(expectedAwaits, awaits.Length);
            foreach (var awaited in awaits)
            {
                var next = statements[statements.IndexOf(awaited) + 1];
                var check = Assert.IsType<IfStatementSyntax>(next);
                Assert.Equal("Leaving", check.Condition.ToString());
                Assert.IsType<ReturnStatementSyntax>(((BlockSyntax)check.Statement).Statements.Single());
            }
        }

        // THE EXIT'S CANCELLATION OF THE LAUNCH IS CAUGHT INSIDE THE TRACKED TASK, filtered to the
        // launch's own token, so the launch entry point - an async void - never sees it.
        var wrapper = methods.Single(method => method.Identifier.ValueText == "CompleteStartupAsync");
        var handler = Assert.Single(wrapper.DescendantNodes().OfType<CatchClauseSyntax>());
        Assert.Equal("OperationCanceledException", handler.Declaration?.Type.ToString());
        Assert.Equal("_startupCancellation.IsCancellationRequested", handler.Filter?.FilterExpression.ToString());
        Assert.Contains("await CompleteStartupCoreAsync(settings, runStart, window)", wrapper.ToString(), StringComparison.Ordinal);

        var startup = methods.Single(method => method.Identifier.ValueText == "CompleteStartupCoreAsync");
        EveryAwaitIsFollowedByTheLeavingCheck(startup, expectedAwaits: 4);
        Assert.Contains("LoadStartupRecoveryAsync(_startupCancellation.Token)", startup.ToString(), StringComparison.Ordinal);
        Assert.Contains("_recoveryTextStore.LoadAsync(cancellationToken)", DeclarationTextOf(shell, "LoadStartupRecoveryAsync"), StringComparison.Ordinal);

        // The delivery's tail - after its own download loop - checks Leaving before it tears an
        // engine down and after every await that builds one.
        var deliveryTree = CSharpSyntaxTree.ParseText(delivery).GetRoot();
        var core = deliveryTree.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "DeliverModelsAsync");
        var tail = core.Body!.Statements.SkipWhile(statement => statement is not TryStatementSyntax).Skip(1).ToArray();
        var afterTry = string.Join("\n", tail.Select(statement => statement.ToString().Split('\n')[0].Trim()));
        Assert.Equal(
            "if (Leaving)\n_window?.SetModelDelivery(new(\"Download verified. Starting local transcription…\", Stage: SpeechModelDelivery.Activating));\nawait TryReconfigureTranscriptionUnderHoldAsync().ConfigureAwait(true);\nif (Leaving)\nPresentGraphicsRuntime();\nawait PresentModelDeliveryAsync().ConfigureAwait(true);",
            afterTry);
        var heldReconfigure = DeclarationTextOf(delivery, "TryReconfigureTranscriptionUnderHoldAsync");
        Assert.Contains("SessionHolder.SpeechEngineSwitch", heldReconfigure, StringComparison.Ordinal);
        Assert.Contains("ReconfigureTranscriptionAsync()", heldReconfigure, StringComparison.Ordinal);

        // THE FIRST-RUN RETRY IS THE SAME TRACKED TASK: one slot, refused while one runs, refused while a dictation holds
        // the session, and the same Leaving check after every await that is not a delivery or a reconfiguration
        // already carrying its own.
        var retryRequest = DeclarationTextOf(delivery, "OnTranscriptionRetryRequested");
        Assert.Contains("_modelDelivery = RetryTranscriptionAsync();", retryRequest, StringComparison.Ordinal);
        Assert.Contains("_modelDelivery is { IsCompleted: false }", retryRequest, StringComparison.Ordinal);
        var retry = deliveryTree.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "RetryTranscriptionAsync");
        Assert.Equal(
            "if (_sessionController?.CurrentSession is not null || _sessionCoordinator?.IsProcessing == true)\n_missingModelIds = await MissingModelIdsAsync(_settings.Preferences).ConfigureAwait(true);\nif (Leaving)\nif (_missingModelIds.Count > 0)\nawait ReconfigureTranscriptionAsync().ConfigureAwait(true);\nif (Leaving)\nPresentGraphicsRuntime();\nawait PresentModelDeliveryAsync().ConfigureAwait(true);",
            string.Join("\n", retry.Body!.Statements.Select(statement => statement.ToString().Split('\n')[0].Trim())));

        // THE RECONFIGURATION BOTH DELIVERIES SHARE (a speech model, the graphics runtime) checks Leaving
        // before it tears an engine down and after the teardown, before it builds one.
        var graphics = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "App.GraphicsRuntime.cs"));
        var graphicsTree = CSharpSyntaxTree.ParseText(graphics).GetRoot();
        var reconfigure = graphicsTree.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "ReconfigureTranscriptionAsync");
        var guarded = Assert.IsType<TryStatementSyntax>(reconfigure.Body!.Statements[1]).Block.Statements;
        Assert.Equal(
            "if (Leaving)\nawait TeardownTranscriptionAsync().ConfigureAwait(true);\nif (Leaving)\nawait ConfigureTranscriptionAsync(_settings.Preferences.Dictation.FinalEngine).ConfigureAwait(true);",
            string.Join("\n", guarded.Select(statement => statement.ToString().Split('\n')[0].Trim())));

        // THE GRAPHICS RUNTIME DELIVERY IS CANCELLED AND JOINED LIKE THE MODEL DELIVERY.
        Assert.Contains("_graphicsRuntimeDownload?.Cancel();", policy, StringComparison.Ordinal);
        Assert.Contains("new LifetimeStep(\"graphics runtime delivery\", () => Join(Interlocked.Exchange(ref _graphicsRuntimeDelivery, null)))", quiesce, StringComparison.Ordinal);
        Assert.Contains("_graphicsRuntimeDelivery is { IsCompleted: false }", graphics, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheSessionUsesIsHandedToTheLifetimeUnderItsGuardedListsOnly()
    {
        var root = FindRepositoryRoot();
        var appDir = Path.Combine(root, "src", "Production", "EnviousWispr.App");
        var lifecycle = File.ReadAllText(Path.Combine(appDir, "App.TrayAndLifecycle.cs"));
        var trees = Directory.EnumerateFiles(appDir, "App*.cs")
            .Order()
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot())
            .ToArray();
        var parts = trees
            .SelectMany(tree => tree.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Single(method => method.Identifier.ValueText == "LifetimeParts");
        string[] dependencies =
        [
            "_previewEngine", "_transcriptionEngine", "_polishProvider", "_resourceArbiter", "_polishLifetime",
            "_livePreview", "_watchdog", "_autoStop", "_historyStore", "_recoveryTextStore", "_runStateStore",
        ];
        string[] disposals = ["Dispose", "DisposeAsync"];
        // The list the lifetime runs only behind a quiescent session, and the run-state store's
        // closing, which the lifetime runs only once nothing that writes to it is outstanding.
        string[] guardedLists = ["DisposeSessionDependencies", "CloseRunState"];

        // A DISPOSAL OF A SESSION DEPENDENCY: `_field.Dispose(...)`, `_field.DisposeAsync(...)`, the
        // method group `_field.Dispose` handed to a step, or `_field is { } local` - the pattern the
        // steps use to take the field and dispose the local.
        static bool IsDisposal(IdentifierNameSyntax identifier, string[] fields, string[] names) =>
            fields.Contains(identifier.Identifier.ValueText) &&
            identifier.Parent switch
            {
                MemberAccessExpressionSyntax access => names.Contains(access.Name.Identifier.ValueText),
                IsPatternExpressionSyntax => true,
                _ => false,
            };
        static bool UnderGuardedList(SyntaxNode node, string[] lists) =>
            node.Ancestors().OfType<ArgumentSyntax>().Any(argument => argument.NameColon is { } name && lists.Contains(name.Name.Identifier.ValueText));

        // ON THE WAY OUT, EVERY ONE OF THEM IS UNDER A GUARDED LIST: the shell's DisposeAsync disposes
        // none itself, and inside LifetimeParts a disposal sits under DisposeSessionDependencies or
        // DisposeLast and nowhere else. (An engine that failed to start is disposed where it failed,
        // before any session exists; that path is outside both.)
        var exit = trees
            .SelectMany(tree => tree.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Single(method => method.Identifier.ValueText == "DisposeAsync" && method.Body is not null);
        Assert.DoesNotContain(exit.DescendantNodes().OfType<IdentifierNameSyntax>(), identifier => IsDisposal(identifier, dependencies, disposals));
        var inParts = parts.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => IsDisposal(identifier, dependencies, disposals))
            .ToArray();
        Assert.All(
            inParts,
            identifier => Assert.True(
                UnderGuardedList(identifier, guardedLists),
                $"{identifier.Parent} is handed to the lifetime outside DisposeSessionDependencies/CloseRunState."));

        // EVERY DEPENDENCY IS LISTED: each field's disposal is found inside one of the two lists.
        var listed = inParts
            .Select(identifier => identifier.Identifier.ValueText)
            .Distinct()
            .ToArray();
        Assert.Equal(dependencies.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));

        // THE SHELL LEAVES THROUGH THE LIFETIME: every path out reaches PrepareForExitAsync, which
        // returns the lifetime's one exit task; DisposeAsync awaits the same; nothing else calls it.
        Assert.Contains("return Lifetime().ExitAsync();", DeclarationTextOf(lifecycle, "PrepareForExitAsync"), StringComparison.Ordinal);
        Assert.Contains("await PrepareForExitAsync()", exit.ToString(), StringComparison.Ordinal);
        Assert.Single(
            trees.SelectMany(tree => tree.DescendantNodes().OfType<InvocationExpressionSyntax>()),
            call => call.Expression.ToString() == "Lifetime().ExitAsync");
    }

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
        var source = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App"),
                    "MainWindow*.cs")
                .Order()
                .Select(File.ReadAllText));

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
        var markup = AppXamlSources.ReadMainWindowAndPagesText(FindRepositoryRoot());

        var focusButtons = FocusButton().Matches(markup).Select(match => match.Value).ToArray();

        Assert.True(focusButtons.Length >= 1, $"Expected the focus empty-state buttons, found {focusButtons.Length}.");

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
        var root = FindRepositoryRoot();
        var mainWindowSource = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.Dictionary.cs"));
        var fromAppsSource = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.WordsFromApps.cs"));

        // File and paste word imports share DescribeImport in MainWindow.Dictionary.cs, while importing
        // from another dictation app reports through WordImportMessages.Result in MainWindow.WordsFromApps.cs.
        var fileAndPasteUses = Regex.Count(mainWindowSource, @"DescribeImport\((?!IReadOnly|CustomWordImportPlan)\w+\)");
        Assert.True(fileAndPasteUses >= 1, "File/paste word import path must report DescribeImport.");
        Assert.Contains("WordImportMessages.Result(", fromAppsSource, StringComparison.Ordinal);

        // Control: the helper must exist and be more than a stub.
        Assert.Contains("private static string DescribeImport", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("could not be read", mainWindowSource, StringComparison.Ordinal);
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
        // Exclude MainWindow.Onboarding.Rendering.cs, whose overlaid checklist status icons
        // toggle mutually inside the same Grid cell rather than sitting beside a row label.
        var code = string.Join(
            "\n",
            Directory.EnumerateFiles(root, "MainWindow*.cs")
                .Where(path => !Path.GetFileName(path).Equals("MainWindow.Onboarding.Rendering.cs", StringComparison.OrdinalIgnoreCase))
                .Order()
                .Select(File.ReadAllText));

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
        var markup = AppXamlSources.ReadMainWindowAndPagesText(FindRepositoryRoot());
        var mainWindowSources = Directory.EnumerateFiles(root, "MainWindow*.cs")
            .Where(path => !path.EndsWith("MainWindow.PageControls.cs", StringComparison.OrdinalIgnoreCase))
            .Order()
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();
        var code = string.Join("\n", mainWindowSources.Select(source => source.Text));

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
        // So: strip that one array, and ask whether the name survives as a real code identifier
        // anywhere in the file (never merely inside a comment or string literal).
        var reachableIdentifiers = StripHideLists(mainWindowSources)
            .SelectMany(stripped => CSharpSyntaxTree.ParseText(stripped)
                .GetRoot()
                .DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Select(identifier => identifier.Identifier.ValueText))
            .ToHashSet(StringComparer.Ordinal);

        var unreachable = sections
            .Where(section => !reachableIdentifiers.Contains(section))
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
        var code = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App"),
                    "MainWindow*.cs")
                .Order()
                .Select(File.ReadAllText));

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
        var root = FindRepositoryRoot();
        var directories = new[]
        {
            Path.Combine(root, "src", "Production", "EnviousWispr.Core"),
            Path.Combine(root, "src", "Production", "EnviousWispr.Presentation"),
            Path.Combine(root, "src", "Production", "EnviousWispr.Pipeline"),
            Path.Combine(root, "src", "Production", "EnviousWispr.App"),
        };
        var offenders = new List<string>();
        foreach (var directory in directories)
        {
            var trees = ProductionSourceFiles(directory)
                .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file))
                .ToArray();
            var compilation = CreateSemanticCompilation(trees);
            offenders.AddRange(
                RunDeclarationShapeAnalyzer(
                        compilation,
                        trees,
                        DeclarationShapeAnalyzer.SentenceToAppearanceDiagnosticId)
                    .Select(diagnostic =>
                        $"{Path.GetFileName(diagnostic.Location.SourceTree?.FilePath ?? string.Empty)}: "
                        + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.True(
            offenders.Count == 0,
            "These members are handed a sentence and hand back a pill, which is the shape the "
                + "appearance-from-text defect always takes. Build the status where the outcome is "
                + "known and pass DictationStatus through instead: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Runs the Roslyn semantic analyser (<see cref="DeclarationShapeAnalyzer"/>) over one fixture,
    /// compiled against <c>EnviousWispr.Core</c> and an external assembly declaring a delegate type.
    /// </summary>
    /// <remarks>
    /// THE COMPILER'S SEMANTIC MODEL IS ASKED, CLOSING WHAT THE SYNTAX-ONLY PARSER COULD NOT SEE.
    /// The analyser (<c>EW0001</c>) runs during every build of <c>EnviousWispr.App</c> and
    /// <c>EnviousWispr.Pipeline</c>, and is exercised directly here against every historical and
    /// semantic shape: an imported named delegate declared in another assembly, a lambda assigned
    /// to <c>var</c> that leaves its return type to inference, a function pointer, and a
    /// third-party type in another namespace that happens to share the name <c>DictationStatus</c>.
    /// Ref: #82.
    /// </remarks>
    private static List<string> OffendersIn(string fileName, string body)
    {
        var lines = body.Split('\n');
        var usings = lines.Where(line => line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
        var rest = lines.Where(line => !line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
        var source = "namespace Sample;\n\n"
            + string.Join('\n', usings)
            + "\n\ninternal sealed class Host\n{\n"
            + string.Join('\n', rest)
            + "\n}\n";
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var compilation = CreateSemanticCompilation([tree], includeExternalDelegateAssembly: true);
        return RunDeclarationShapeAnalyzer(
                compilation,
                [tree],
                DeclarationShapeAnalyzer.SentenceToAppearanceDiagnosticId)
            .Select(diagnostic => $"{fileName}: {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToList();
    }

    /// <summary>
    /// The Roslyn semantic analyser refuses every shape that has walked past earlier drafts, plus
    /// the four semantic shapes a syntax tree alone could not resolve.
    /// </summary>
    /// <remarks>
    /// A GATE THAT PASSES ON ITS FIRST RUN IS UNPROVEN, and this one passes by finding nothing in a
    /// clean tree, which is exactly what a gate that can find nothing at all also does.
    ///
    /// EVERY POSITIVE CASE HERE IS A REAL BYPASS: the seventeen syntax shapes found across five
    /// review rounds, plus the three semantic shapes that required a compilation and analyser
    /// (<c>ImportedNamedDelegate.cs</c> from a separate compiled assembly,
    /// <c>InferredVarLambda.cs</c> / <c>InferredParameterLambda.cs</c> where the lambda states
    /// neither return type nor left-hand delegate type, and <c>FunctionPointer.cs</c>).
    ///
    /// THE NEGATIVE CASES ARE NOT DECORATION. Each is a false alarm a draft produced or a semantic
    /// control (<c>ThirdPartySameName.cs</c>, where a type in another namespace shares the name
    /// <c>DictationStatus</c>). Ref: #82.
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

        // A rename of STRING is the same bypass aimed at the other half of the shape.
        Assert.NotEmpty(OffendersIn(
            "AliasedString.cs",
            "using Str = System.String;\n\n"
                + "    private DictationStatus Read(Str sentence) => default;"));

        // The Func can be qualified, nested, returned, or hidden behind its own rename. Narrowing to
        // the outermost written name lost every one of these.
        Assert.NotEmpty(OffendersIn(
            "QualifiedFunc.cs",
            "    private System.Func<string, DictationStatus> _read = _ => default;"));

        Assert.NotEmpty(OffendersIn(
            "NestedFunc.cs",
            "    private List<Func<string, DictationStatus>> _readers = [];"));

        Assert.NotEmpty(OffendersIn(
            "ReturnedFunc.cs",
            "    private Func<string, DictationStatus> Reader() => _ => default;"));

        // C# 10 lets a lambda state its return type, so `var` on the left proves nothing.
        Assert.NotEmpty(OffendersIn(
            "TypedLambda.cs",
            "    private void Show()\n"
                + "    {\n"
                + "        var read = DictationStatus (string sentence) => default;\n"
                + "    }"));

        Assert.NotEmpty(OffendersIn(
            "AliasedFunc.cs",
            "using Mapper = System.Func<string, DictationStatus>;\n\n"
                + "    private Mapper _read = _ => default;"));

        // SEMANTIC SHAPES THAT SYNTAX ALONE COULD NOT SEE (#82):
        // 1. A field whose type is a named delegate declared in an external assembly outside the scanned tree.
        Assert.NotEmpty(OffendersIn(
            "ImportedNamedDelegate.cs",
            "    private External.SentenceToStatus _read = null!;"));

        // 2. A lambda assigned to `var` that states neither its return type nor a left-hand delegate type.
        Assert.NotEmpty(OffendersIn(
            "InferredVarLambda.cs",
            "    private void Show()\n"
                + "    {\n"
                + "        var read = (string sentence) => DictationStatus.Quiet(sentence);\n"
                + "    }"));

        // 3. A function pointer taking a string and returning a DictationStatus.
        Assert.NotEmpty(OffendersIn(
            "FunctionPointer.cs",
            "    private unsafe delegate*<string, DictationStatus> _read = null;"));

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

        // 4. A third-party type in another namespace that happens to share the name DictationStatus,
        // both when resolved in the compilation and when coming from an unreferenced assembly.
        Assert.Empty(OffendersIn(
            "ThirdPartySameName.cs",
            "    private static OtherNamespace.DictationStatus Read(string sentence) => default;"));

        Assert.Empty(OffendersIn(
            "UnresolvedThirdPartySameName.cs",
            "    private static UnresolvedAssembly.DictationStatus Read(string sentence) => default;"));
    }

    /// <summary>
    /// Both production projects that own these contracts wire <c>EnviousWispr.Analyzers</c> as a
    /// build-time Roslyn analyser, so a violation is refused by the compiler before tests run.
    /// </summary>
    [Fact]
    public void TheProductionAppAndPipelineWireTheDeclarationShapeAnalyzerAtBuildTime()
    {
        var root = FindRepositoryRoot();
        foreach (var project in new[]
        {
            Path.Combine(root, "src", "Production", "EnviousWispr.Core", "EnviousWispr.Core.csproj"),
            Path.Combine(root, "src", "Production", "EnviousWispr.Presentation", "EnviousWispr.Presentation.csproj"),
            Path.Combine(root, "src", "Production", "EnviousWispr.Pipeline", "EnviousWispr.Pipeline.csproj"),
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "EnviousWispr.App.csproj"),
        })
        {
            var document = XDocument.Load(project);
            var reference = document.Descendants("ProjectReference")
                .FirstOrDefault(element =>
                    ((string?)element.Attribute("Include"))?.EndsWith(
                        "EnviousWispr.Analyzers.csproj",
                        StringComparison.Ordinal) == true);
            Assert.True(
                reference is not null &&
                (string?)reference.Attribute("OutputItemType") == "Analyzer" &&
                (string?)reference.Attribute("ReferenceOutputAssembly") == "false",
                $"{Path.GetFileName(project)} does not wire EnviousWispr.Analyzers as OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\".");
        }
    }

    /// <summary>
    /// Every status handed to the window names the pill it wants.
    /// </summary>
    /// <remarks>
    /// THE GATE ABOVE REFUSES THE OLD SHAPE; THIS ONE REFUSES A NEW STATUS FROM SKIPPING THE
    /// CHOICE. Every call site must name a <c>DictationStatus</c> factory or pass a carrier the
    /// compiler resolves to <c>EnviousWispr.Core.Presentation.DictationStatus</c>, including the
    /// quiet ones, so a status that shows no pill says so on purpose rather than by falling through.
    ///
    /// CHECKED BY THE ROSLYN SEMANTIC ANALYSER (<c>EW0003</c>) AT BUILD TIME AND HERE. A <c>var</c>
    /// local, a pattern-bound variable, or a <c>using</c> alias for <c>DictationStatus</c> is
    /// resolved by the compiler, while a same-named type from another namespace, a <c>default</c>
    /// value, a raw <c>new DictationStatus(...)</c>, or a sentence-to-status helper is refused.
    /// Ref: #82.
    /// </remarks>
    [Fact]
    public void EveryStatusHandedToTheWindowNamesItsPill()
    {
        // TWO ROUTES TO THE WINDOW, ONE GATE. The shell's own code calls the window's
        // SetSessionStatus; the session's effects, WinUI-free under App/Composition since plan-2
        // step 4, call the session view's ShowStatus, which the shell forwards to the same window
        // method with the status untouched. Every argument on either route must name its pill.
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "src", "Production", "EnviousWispr.App");
        var paths = ShellSessionSources(root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allAppTrees = ProductionSourceFiles(app)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
            .ToArray();
        var trees = allAppTrees
            .Where(tree => paths.Contains(tree.FilePath))
            .ToArray();
        var compilation = CreateSemanticCompilation(allAppTrees);
        var symbols = DeclarationShapeAnalyzer.KnownSymbols.From(compilation);
        Assert.True(
            symbols.StatusType is { TypeKind: not TypeKind.Error },
            "DictationStatus did not resolve in the semantic compilation.");

        var calls = new List<(InvocationExpressionSyntax Invocation, SyntaxTree Tree)>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (DeclarationShapeAnalyzer.IsStatusSinkCall(invocation, model, symbols.StatusType!))
                {
                    calls.Add((invocation, tree));
                }
            }
        }

        Assert.True(calls.Count >= 20, $"Expected the app's status call sites, found {calls.Count}.");

        // The shell forwards the view's status to the window untouched: the view's forwarder is read
        // as syntax - one expression, the parameter handed on as it arrived, nothing assigned to it -
        // and it is the only call whose argument is a bare parameter, or a status could be rewritten
        // between the composition and the window.
        var appPartPaths = Directory.EnumerateFiles(app, "App*.cs")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shellPath = Path.Combine(app, "App.xaml.cs");
        var shell = File.ReadAllText(shellPath);
        Assert.Null(StatusForwarderComplaint(shell));
        var forwarders = calls.Count(entry =>
            appPartPaths.Contains(entry.Tree.FilePath) &&
            entry.Invocation.ArgumentList.Arguments.Count == 1 &&
            entry.Invocation.ArgumentList.Arguments[0].Expression.ToString() == "status");
        Assert.True(forwarders == 1, $"Expected exactly one bare forwarder of a status to the window, found {forwarders}.");

        var unnamed = RunDeclarationShapeAnalyzer(
                compilation,
                trees,
                DeclarationShapeAnalyzer.StatusPillNamingDiagnosticId)
            .Select(diagnostic => diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        Assert.True(
            unnamed.Length == 0,
            "These statuses reach the window without naming the pill they want, so the appearance "
                + "is decided somewhere this suite cannot see: " + string.Join(" | ", unnamed));
    }

    /// <summary>The forwarder check refuses a forwarder that touches the status on its way to the window.</summary>
    /// <remarks>
    /// PROVING THE CHECK CAN FAIL. A forwarder that reassigns its parameter before handing it on
    /// leaves exactly one bare call site and passes a count; the syntax check is what refuses it,
    /// and both directions are asserted here because one of them alone is not a control.
    /// </remarks>
    [Fact]
    public void TheStatusForwarderCheckRefusesARewrittenStatusAndAcceptsTheBareOne()
    {
        const string bare = """
            private sealed class WindowSessionView(App app) : ISessionView
            {
                public void ShowStatus(DictationStatus status) =>
                    app.PostToLiveWindow(window => window.SetSessionStatus(status));
            }
            """;
        const string rewritten = """
            private sealed class WindowSessionView(App app) : ISessionView
            {
                public void ShowStatus(DictationStatus status)
                {
                    status = DictationStatus.Quiet("replacement");
                    app.PostToLiveWindow(window => window.SetSessionStatus(status));
                }
            }
            """;
        const string transformed = """
            private sealed class WindowSessionView(App app) : ISessionView
            {
                public void ShowStatus(DictationStatus status) =>
                    app.PostToLiveWindow(window => window.SetSessionStatus(status with { Text = "x" }));
            }
            """;
        const string tupleReassigned = """
            private sealed class WindowSessionView(App app) : ISessionView
            {
                public void ShowStatus(DictationStatus status) =>
                    app.PostToLiveWindow(window =>
                    {
                        (status, _) = (status with { Text = "x" }, 0);
                        window.SetSessionStatus(status);
                    });
            }
            """;
        const string helperNamedLikeTheWindow = """
            private sealed class WindowSessionView(App app) : ISessionView
            {
                public void ShowStatus(DictationStatus status) =>
                    app.PostToLiveWindow(window => app.RewriteAndSetSessionStatus(status));
            }
            """;
        const string notDispatched = """
            private sealed class WindowSessionView(App app) : ISessionView
            {
                public void ShowStatus(DictationStatus status) =>
                    app._window?.SetSessionStatus(Rewrite(status));
            }
            """;

        Assert.Null(StatusForwarderComplaint(bare));
        Assert.NotNull(StatusForwarderComplaint(rewritten));
        Assert.NotNull(StatusForwarderComplaint(transformed));
        Assert.NotNull(StatusForwarderComplaint(tupleReassigned));
        Assert.NotNull(StatusForwarderComplaint(helperNamedLikeTheWindow));
        Assert.NotNull(StatusForwarderComplaint(notDispatched));
        Assert.NotNull(StatusForwarderComplaint("private sealed class Elsewhere { }"));
    }

    /// <summary>Why the shell's status forwarder is not a bare forward, or null when it is.</summary>
    private static string? StatusForwarderComplaint(string shell)
    {
        var root = CSharpSyntaxTree.ParseText(shell).GetRoot();
        var view = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.ValueText == "WindowSessionView");
        if (view is null)
        {
            return "The shell has no WindowSessionView.";
        }

        var forwarder = view.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(method => method.Identifier.ValueText == "ShowStatus");
        if (forwarder is null)
        {
            return "The view has no ShowStatus.";
        }

        // THE WHOLE PERMITTED SHAPE, NOT A LIST OF FORBIDDEN ONES: one expression, which is one
        // dispatch through the lifetime bridge, whose one argument names the live window and whose body
        // is one expression, which is the window's SetSessionStatus handed the parameter as it
        // arrived. Anything else - a block anywhere, a helper named like the window's method, a
        // transformed argument, a second statement - is not that shape.
        if (forwarder.ExpressionBody is null)
        {
            return "The forwarder is a block, so it can do more than forward.";
        }

        var parameter = forwarder.ParameterList.Parameters.Single().Identifier.ValueText;
        var (dispatch, dispatcher) = Call(forwarder.ExpressionBody.Expression);
        if (dispatch is null || dispatcher != "app.PostToLiveWindow")
        {
            return $"The forwarder is not one dispatch through the window lifetime bridge ('{dispatcher}').";
        }

        if (dispatch.ArgumentList.Arguments.Count != 1 ||
            dispatch.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda ||
            lambda.ExpressionBody is null)
        {
            return "The dispatched work is not one expression.";
        }

        var (handOn, target) = Call(lambda.ExpressionBody);
        if (handOn is null || target != lambda.Parameter.Identifier.ValueText + ".SetSessionStatus")
        {
            return $"The dispatched work calls '{target}' rather than the window's SetSessionStatus.";
        }

        if (handOn.ArgumentList.Arguments.Count != 1 ||
            handOn.ArgumentList.Arguments[0].Expression is not IdentifierNameSyntax name ||
            name.Identifier.ValueText != parameter)
        {
            return $"The forwarder hands on '{handOn.ArgumentList.Arguments}' rather than its parameter.";
        }

        return null;

        // `a?.b.c(d)` parses as a conditional access whose not-null branch is the invocation; the
        // callee is read across that seam so the whole receiver chain is compared.
        static (InvocationExpressionSyntax? Invocation, string Callee) Call(ExpressionSyntax expression) =>
            expression switch
            {
                InvocationExpressionSyntax invocation => (invocation, Compact(invocation.Expression)),
                ConditionalAccessExpressionSyntax { WhenNotNull: InvocationExpressionSyntax invocation } access =>
                    (invocation, Compact(access.Expression) + "?" + Compact(invocation.Expression)),
                _ => (null, Compact(expression)),
            };

        static string Compact(SyntaxNode node) =>
            string.Concat(node.ToString().Where(character => !char.IsWhiteSpace(character)));
    }

    /// <summary>
    /// The Roslyn semantic analyser accepts wrapped factory calls, <c>var</c> carriers, and
    /// <c>using</c> aliases while refusing nameless helpers, <c>default</c>, and raw construction.
    /// </summary>
    /// <remarks>
    /// PROVING THE ANALYSER (<c>EW0003</c>) CLOSES BOTH DIRECTIONS. Both positive and negative
    /// calls are compiled and checked through the real semantic model, including a <c>var</c>
    /// local bound to a <c>DictationStatus</c> and a <c>using</c> alias for the type. Ref: #82.
    /// </remarks>
    [Fact]
    public void TheStatusFactoryMatchAcceptsAWrappedCallAndStillRefusesANamelessOne()
    {
        Assert.Empty(StatusSinkDiagnosticsIn("SetSessionStatus(DictationStatus.Quiet(\"Ready\"));"));
        Assert.Empty(StatusSinkDiagnosticsIn(
            "SetSessionStatus(" + Environment.NewLine +
            "    DictationStatus" + Environment.NewLine +
            "        .Quiet(\"Ready\")" + Environment.NewLine +
            "        .AboutTheTranscriptionEngine());"));
        Assert.Empty(StatusSinkDiagnosticsIn(
            "var ready = DictationStatus.Quiet(\"Ready\");" + Environment.NewLine +
            "SetSessionStatus(ready);"));
        Assert.Empty(StatusSinkDiagnosticsIn(
            "using Pill = EnviousWispr.Core.Presentation.DictationStatus;" + Environment.NewLine +
            "Pill ready = DictationStatus.Quiet(\"Ready\");" + Environment.NewLine +
            "SetSessionStatus(ready);"));

        Assert.NotEmpty(StatusSinkDiagnosticsIn("SetSessionStatus(helper.BuildTheStatus(sentence));"));
        Assert.NotEmpty(StatusSinkDiagnosticsIn("SetSessionStatus(statusFromSentence(sentence));"));
        Assert.NotEmpty(StatusSinkDiagnosticsIn("SetSessionStatus(DictationStatusHelper(sentence));"));
        Assert.NotEmpty(StatusSinkDiagnosticsIn("SetSessionStatus(default);"));
        Assert.NotEmpty(StatusSinkDiagnosticsIn(
            "DictationStatus status = default!;" + Environment.NewLine +
            "SetSessionStatus(status);"));
        Assert.NotEmpty(StatusSinkDiagnosticsIn(
            "var status = DictationStatus.Quiet(\"Ready\");" + Environment.NewLine +
            "status = default;" + Environment.NewLine +
            "SetSessionStatus(status);"));
        Assert.NotEmpty(StatusSinkDiagnosticsIn(
            "if (default(DictationStatus) is var status) { SetSessionStatus(status); }"));
        Assert.NotEmpty(StatusSinkDiagnosticsIn(
            "SetSessionStatus(new DictationStatus(\"Ready\", DictationOverlayState.Hidden));"));

        // Cross-file partial-class status carriers are checked across trees: a field or property
        // initialized or assigned to default in another file is refused, while a factory-backed
        // carrier in another file is accepted.
        Assert.Empty(StatusSinkDiagnosticsAcrossFiles(
            "internal partial class CrossFileHost { private readonly DictationStatus _status = DictationStatus.Quiet(\"Ready\"); }",
            "internal partial class CrossFileHost { private void SetSessionStatus(DictationStatus status) { } private void Show() => SetSessionStatus(_status); }"));
        Assert.NotEmpty(StatusSinkDiagnosticsAcrossFiles(
            "internal partial class CrossFileHost { private readonly DictationStatus _status = default; }",
            "internal partial class CrossFileHost { private void SetSessionStatus(DictationStatus status) { } private void Show() => SetSessionStatus(_status); }"));
        Assert.NotEmpty(StatusSinkDiagnosticsAcrossFiles(
            "internal partial class CrossFileHost { private DictationStatus _status = DictationStatus.Quiet(\"Ready\"); private void Corrupt() => _status = default; }",
            "internal partial class CrossFileHost { private void SetSessionStatus(DictationStatus status) { } private void Show() => SetSessionStatus(_status); }"));

        // Reassigned parameters, defaulted parameters, uninitialized/self-assigned fields, and
        // helper calls handed default are all refused.
        Assert.NotEmpty(StatusSinkDiagnosticsAcrossFiles(
            "internal sealed class ParamHost { private void SetSessionStatus(DictationStatus status) { } private void Forward(DictationStatus status) { status = default; SetSessionStatus(status); } }",
            "internal sealed class EmptyPart { }"));
        Assert.NotEmpty(StatusSinkDiagnosticsAcrossFiles(
            "internal sealed class DefaultParamHost { private void SetSessionStatus(DictationStatus status) { } private void Forward(DictationStatus status = default) => SetSessionStatus(status); }",
            "internal sealed class EmptyPart { }"));
        Assert.NotEmpty(StatusSinkDiagnosticsAcrossFiles(
            "internal sealed class CycleHost { private DictationStatus _status; private void SetSessionStatus(DictationStatus status) { } private void Show() { _status = _status; SetSessionStatus(_status); } }",
            "internal sealed class EmptyPart { }"));
        Assert.NotEmpty(StatusSinkDiagnosticsAcrossFiles(
            "internal sealed class HelperCallHost { private void SetSessionStatus(DictationStatus status) { } private void Forward(DictationStatus status) => SetSessionStatus(status); private void Caller() => Forward(default); }",
            "internal sealed class EmptyPart { }"));
    }

    private static Diagnostic[] StatusSinkDiagnosticsAcrossFiles(string firstFile, string secondFile)
    {
        var firstTree = CSharpSyntaxTree.ParseText(firstFile, path: "Part1.cs");
        var secondTree = CSharpSyntaxTree.ParseText(secondFile, path: "Part2.cs");
        var compilation = CreateSemanticCompilation([firstTree, secondTree]);
        return RunDeclarationShapeAnalyzer(
            compilation,
            [firstTree, secondTree],
            DeclarationShapeAnalyzer.StatusPillNamingDiagnosticId);
    }

    private static Diagnostic[] StatusSinkDiagnosticsIn(string statements)
    {
        var lines = statements.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var usings = lines.Where(line => line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
        var body = lines.Where(line => !line.TrimStart().StartsWith("using ", StringComparison.Ordinal));
        var source = string.Join('\n', usings) + "\n" + """
            internal sealed class StatusSinkFixture
            {
                private void SetSessionStatus(DictationStatus status) { }
                private void Run(string sentence, dynamic helper, System.Func<string, DictationStatus> statusFromSentence, System.Func<string, DictationStatus> DictationStatusHelper)
                {
            """ + "\n" + string.Join('\n', body) + "\n    }\n}\n";
        return AnalyzeSnippet(source, DeclarationShapeAnalyzer.StatusPillNamingDiagnosticId);
    }

    /// <summary>The shell's session sources: the window's file and the WinUI-free composition beside it.</summary>
    private static IReadOnlyList<string> ShellSessionSources(string root)
    {
        var app = Path.Combine(root, "src", "Production", "EnviousWispr.App");
        var appPartials = Directory.EnumerateFiles(app, "App*.cs").Order().ToArray();
        var composition = Directory.EnumerateFiles(Path.Combine(app, "Composition"), "*.cs").Order().ToArray();
        Assert.True(composition.Length >= 3, $"Expected the composition files under App/Composition, found {composition.Length}.");
        return [.. appPartials, .. composition];
    }

    private static IEnumerable<string> ProductionSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
        // Exclude pre-existing MainWindow.Onboarding.cs, whose DrawFooter switches use 4-tuples
        // with empty HelpText for hidden buttons rather than page header entries.
        var code = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App"),
                    "MainWindow*.cs")
                .Where(path => !Path.GetFileName(path).Equals("MainWindow.Onboarding.Rendering.cs", StringComparison.OrdinalIgnoreCase))
                .Order()
                .Select(File.ReadAllText));

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
        var markup = AppXamlSources.ReadMainWindowAndPagesText(FindRepositoryRoot());
        var mainWindowSources = Directory.EnumerateFiles(root, "MainWindow*.cs")
            .Where(path => !path.EndsWith("MainWindow.PageControls.cs", StringComparison.OrdinalIgnoreCase))
            .Order()
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();
        var code = string.Join("\n", mainWindowSources.Select(source => source.Text));

        foreach (var buttonName in new[]
                 {
                     "SystemThemeSegmentButton",
                     "LightThemeSegmentButton",
                     "DarkThemeSegmentButton",
                     "TopPositionSegmentButton",
                     "BottomPositionSegmentButton",
                 })
        {
            Assert.True(
                Regex.IsMatch(markup, $@"x:Name=""{buttonName}""[^>]*Click=""\w+"""),
                $"{buttonName} has no Click handler, so choosing it would not be saved.");
        }

        foreach (var cardName in new[] { "CapsulePillButton", "LevelRailPillButton", "ReadingWellPillButton" })
        {
            Assert.True(
                Regex.IsMatch(markup, $@"x:Name=""{cardName}""[^>]*Checked=""PillStyleCardChecked"""),
                $"{cardName} does not report its check to PillStyleCardChecked.");
        }

        var setTheme = HandlerBody("SetThemeSelection").Match(code);
        Assert.True(setTheme.Success && setTheme.Value.Contains("PersistAppearanceChoicesAsync", StringComparison.Ordinal));
        var setPosition = HandlerBody("SetOverlayPositionSelection").Match(code);
        Assert.True(setPosition.Success && setPosition.Value.Contains("PersistAppearanceChoicesAsync", StringComparison.Ordinal));
        var pillStyle = HandlerBody("PillStyleCardChecked").Match(code);
        Assert.True(pillStyle.Success && pillStyle.Value.Contains("PersistRecordingPillSelectionAsync", StringComparison.Ordinal));

        var persist = HandlerBody("PersistAppearanceChoicesAsync").Match(code);
        Assert.True(persist.Success, "PersistAppearanceChoicesAsync does not exist.");
        Assert.True(
            persist.Value.Contains("SaveAppearanceAsync", StringComparison.Ordinal),
            "PersistAppearanceChoicesAsync does not hand the choice to the presenter.");

        foreach (var reader in new[] { "_selectedThemeIndex", "_selectedOverlayPositionIndex", "PillDesignWithoutWordsFromControls" })
        {
            Assert.True(
                persist.Value.Contains(reader, StringComparison.Ordinal),
                $"PersistAppearanceChoicesAsync does not read {reader}, so that choice on screen is not the one saved.");
        }

        var persistPill = HandlerBody("PersistRecordingPillSelectionAsync").Match(code);
        Assert.True(persistPill.Success && persistPill.Value.Contains("SaveRecordingPillSelectionAsync", StringComparison.Ordinal));
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

        Assert.True(rows.Length >= 9, $"Expected the navigation rows, found {rows.Length}.");

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
    ///
    /// AND IN ANY ATTRIBUTE ORDER. This required Glyph to be the FIRST attribute on the FontIcon,
    /// so giving those icons an x:Name - which is how a section header borrows one - reported all
    /// twenty rows as having no icon at once. Attribute order carries no meaning in XAML, and a rule
    /// that depends on it fails on a change that means nothing.
    /// </remarks>
    [GeneratedRegex(
        @"<NavigationViewItem\b(?![\w.])(?<head>[^>]*?Content=""(?<label>[^""]*)""[^>]*?)>"
            + @"(?:\s*<NavigationViewItem\.Icon>\s*<FontIcon\b[^>]*?Glyph=""&\#x(?<glyph>[0-9A-Fa-f]+);"")?",
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

    /// <summary>The hide list is removed however its declaration is written.</summary>
    /// <remarks>
    /// THREE OF THESE FIVE WERE INVISIBLE YESTERDAY, checked by running the old pattern against
    /// them rather than assumed: a comment between the parameter list and the arrow, a block body
    /// instead of an expression body, and the property form with no parameter list. The pattern
    /// required `SettingsSections() =>` with nothing but whitespace between. When it missed, the
    /// hide list stayed in the text, every section name inside it read as reached - including an
    /// orphaned one - and the gate passed. Ref: #82.
    ///
    /// THE OTHER TWO ALREADY MATCHED AND ARE HERE AS THE CONTROL, so a rewrite that traded one
    /// blind spot for another could not pass as a fix.
    /// </remarks>
    [Theory]
    [InlineData("private Border[] SettingsSections() /* inventory */ => [ PrivacySection ];")]
    [InlineData("private Border[] SettingsSections() => [ PrivacySection ];")]
    [InlineData("private Border[] SettingsSections()|    {|        return [ PrivacySection ];|    }")]
    [InlineData("private Border[] SettingsSections => [ PrivacySection ];")]
    [InlineData("private System.Collections.Generic.List<Border> SettingsSections() => [ PrivacySection ];")]
    public void TheHideListIsRemovedHoweverItIsWritten(string declaration)
    {
        var code = "class C { " + declaration.Replace('|', '\n') + " void Other() { } }";

        var remaining = CodeWithoutTheHideList(code);

        Assert.DoesNotContain("PrivacySection", remaining, StringComparison.Ordinal);
        // The control: it removed the hide list and not the whole file.
        Assert.Contains("void Other()", remaining, StringComparison.Ordinal);
    }

    /// <summary>
    /// The semantic model resolves the hide list across partial class declarations, ignores a
    /// same-named member on an unrelated class, and finds a hide list built under another member
    /// name when iterated by the section-hiding loop.
    /// </summary>
    [Fact]
    public void TheHideListIsResolvedSemanticallyAcrossPartialClassesAndHideLoops()
    {
        const string partialDeclarations = """
            public partial class MainWindow
            {
                private object[] SettingsSections() => [ FirstSection ];
                private void Keep() { _ = ReachedSection; }
            }
            public partial class MainWindow
            {
                private object[] SettingsSections(int overload) => [ SecondSection ];
            }
            """;
        var withoutPartials = CodeWithoutTheHideList(partialDeclarations);
        Assert.DoesNotContain("FirstSection", withoutPartials, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondSection", withoutPartials, StringComparison.Ordinal);
        Assert.Contains("ReachedSection", withoutPartials, StringComparison.Ordinal);

        const string hideListUnderAnotherName = """
            public partial class MainWindow
            {
                private object[] HiddenInventory() => [ OrphanedSection ];
                private void Apply(object section)
                {
                    foreach (var candidate in HiddenInventory())
                    {
                        candidate.Visibility = ReferenceEquals(candidate, section)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                    _ = ReachedSection;
                }
            }
            """;
        var withoutCustomHideList = CodeWithoutTheHideList(hideListUnderAnotherName);
        Assert.DoesNotContain("OrphanedSection", withoutCustomHideList, StringComparison.Ordinal);
        Assert.Contains("ReachedSection", withoutCustomHideList, StringComparison.Ordinal);

        const string strangerClassOnly = """
            internal sealed class Stranger
            {
                private object[] SettingsSections() => [ PrivacySection ];
            }
            public partial class MainWindow
            {
                private void Other() { }
            }
            """;
        Assert.ThrowsAny<Exception>(() => CodeWithoutTheHideList(strangerClassOnly));
    }

    /// <summary>A hide list that is not there stops the gate rather than emptying it.</summary>
    /// <remarks>
    /// UNDER THE PATTERN THIS REPLACES, NOT FINDING IT REMOVED NOTHING AND THE TEST STILL PASSED.
    /// A check whose own blindness is indistinguishable from the property holding is worse than no
    /// check, because it is counted as coverage.
    /// </remarks>
    [Fact]
    public void AMissingHideListFailsLoudly()
    {
        Assert.ThrowsAny<Exception>(() => CodeWithoutTheHideList("class C { void Other() { } }"));
    }

    /// <summary>A body is read whole, however braces appear inside it.</summary>
    /// <remarks>
    /// THE PATTERN THIS REPLACES STOPPED AT THE FIRST LINE THAT WAS FOUR SPACES AND A BRACE, and it
    /// did not care whether that brace was code. Verified against the old pattern rather than
    /// assumed: with a verbatim string in the body holding such a line, it captured a FRAGMENT - the
    /// marker below was outside it - and the caller then asserted about part of a method. Ref: #82.
    /// </remarks>
    [Fact]
    public void AMethodBodyIsReadWholeWhenAStringInsideItHoldsAClosingBrace()
    {
        var code = string.Join("\n", [
            "class C",
            "{",
            "    private async Task RunAutoStopWatchAsync()",
            "    {",
            "        var fixture = @\"",
            "    }\";",
            "        Marker();",
            "    }",
            "}",
        ]);

        Assert.Contains("Marker();", DeclarationTextOf(code, "RunAutoStopWatchAsync"), StringComparison.Ordinal);
    }

    [Fact]
    public void DeclarationNamedIgnoresLocalVariablesAndInterfaceSignaturesAndRefusesDuplicates()
    {
        const string withInterfaceAndLocal = """
            internal interface IRunner
            {
                Task RunAsync();
            }
            internal sealed class Runner : IRunner
            {
                private void Before()
                {
                    var RunAsync = "not a member";
                    _ = RunAsync;
                }
                public async Task RunAsync()
                {
                    RealBodyMarker();
                    await Task.Yield();
                }
            }
            """;
        var body = DeclarationTextOf(withInterfaceAndLocal, "RunAsync");
        Assert.Contains("RealBodyMarker();", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not a member", body, StringComparison.Ordinal);

        const string ambiguous = """
            internal sealed class First { public void RunAsync() { } }
            internal sealed class Second { public void RunAsync() { } }
            """;
        Assert.ThrowsAny<Exception>(() => DeclarationTextOf(ambiguous, "RunAsync"));
    }

    /// <summary>An initializer is found however the declaration is written, and constant labels resolve semantically.</summary>
    /// <remarks>
    /// The pattern this replaces required the name, then `=`, then `[`, with only whitespace between,
    /// and stopped at the first `];`. A comment before the bracket hid the list entirely; a nested
    /// collection ended it early, and the caller read a truncated list as the whole set of choices.
    /// The semantic model also resolves constant identifiers such as
    /// <see cref="TranscriptionEngineNames.Parakeet"/> and ignores a local variable of the same
    /// name. Ref: #82.
    /// </remarks>
    [Theory]
    [InlineData("private static readonly object[] FinalEngineChoices = [ new(\"Automatic\") ];")]
    [InlineData("private static readonly object[] FinalEngineChoices /* three */ = [ new(\"Automatic\") ];")]
    [InlineData("private static object[] FinalEngineChoices => [ new(\"Automatic\") ];")]
    public void AnInitializerIsFoundHoweverItIsWritten(string declaration)
    {
        var text = InitializerOf("class C { " + declaration + " }", "FinalEngineChoices");

        Assert.Contains("Automatic", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoiceLabelsResolveConstantSymbolsAndIgnoreCommentsAndLocals()
    {
        const string source = """
            internal sealed class ChoiceHost
            {
                private void IgnoreLocal()
                {
                    var FinalEngineChoices = new[] { "LocalShadow" };
                    _ = FinalEngineChoices;
                }

                private readonly object[] FinalEngineChoices =
                [
                    // new("CommentedOut")
                    new("Automatic", "First"),
                    new(TranscriptionEngineNames.Parakeet, "Second"),
                    new(TranscriptionEngineNames.Whisper, "Third"),
                ];
            }
            """;

        Assert.Equal(
            ["Automatic", TranscriptionEngineNames.Parakeet, TranscriptionEngineNames.Whisper],
            ChoiceLabelsOf(source, "FinalEngineChoices"));
    }

    /// <summary>A carrier is a declaration the compiler resolves to DictationStatus, not a mention.</summary>
    /// <remarks>
    /// THE PATTERN THIS REPLACES INVENTED CARRIERS, and the two rows below are the ones it actually
    /// invents - checked by running it, not taken from the issue. It matched the type name, a word,
    /// and any of `( = ) , ;`, so a COMMENT ending in a semicolon and a STRING containing one both
    /// produced a name. Every name in an argument then counts as already being a status, which is
    /// the check quietly not checking.
    ///
    /// ASKING THE COMPILER'S SEMANTIC MODEL ALSO CLOSES THE THREE GAPS SYNTAX ALONE LEFT OPEN:
    /// a <c>var</c> local bound to a <c>DictationStatus</c>, a pattern-bound variable, a
    /// <c>using</c> alias for the type, and an imposter type of the same name from another
    /// namespace. Ref: #82.
    /// </remarks>
    [Fact]
    public void OnlyDeclarationsCountAsCarriers()
    {
        var code = string.Join("\n", [
            "using Pill = EnviousWispr.Core.Presentation.DictationStatus;",
            "class C",
            "{",
            "    // Returns a DictationStatus value; nothing is declared here.",
            "    private const string Note = \"DictationStatus invented;\";",
            "    private DictationStatus Declared() => default;",
            "    private void Takes(DictationStatus parameter) { }",
            "    private void Local()",
            "    {",
            "        DictationStatus local = default;",
            "        var inferred = DictationStatus.Quiet(\"Ready\");",
            "        Pill aliased = DictationStatus.Quiet(\"Ready\");",
            "        if (Declared() is { } patterned) { _ = patterned; }",
            "        OtherNamespace.DictationStatus imposter = default;",
            "        _ = (local, inferred, aliased, imposter);",
            "    }",
            "}",
        ]);

        var carriers = DictationStatusCarriers(code);

        Assert.Contains("Declared", carriers);
        Assert.Contains("parameter", carriers);
        Assert.Contains("local", carriers);
        Assert.Contains("inferred", carriers);
        Assert.Contains("aliased", carriers);
        Assert.Contains("patterned", carriers);
        // The two the old pattern invented, plus the same-named type from another namespace.
        Assert.DoesNotContain("value", carriers);
        Assert.DoesNotContain("invented", carriers);
        Assert.DoesNotContain("imposter", carriers);
    }

    /// <summary>A member that is not there stops the gate rather than emptying it.</summary>
    [Fact]
    public void AMissingMemberFailsLoudly()
    {
        Assert.ThrowsAny<Exception>(() => DeclarationTextOf("class C { }", "NotThere"));
    }

    /// <summary>The whole text of the concrete member with this name.</summary>
    /// <remarks>
    /// ASKS THE COMPILER'S SEMANTIC MODEL WHICH DECLARATION IS THE CONCRETE MEMBER. Neither an
    /// interface method signature without a body nor a local variable sharing the name counts, and
    /// an ambiguous duplicate member name fails loudly instead of silently checking the first. Ref: #82.
    /// </remarks>
    private static string DeclarationTextOf(string code, string name) =>
        DeclarationNamed(code, name).Node.ToString();

    /// <summary>The text of the initializer of the member with this name.</summary>
    /// <remarks>
    /// The pattern this replaces required `Name = [` with only whitespace between and stopped at the
    /// first `];`, so a comment before the bracket hid the list and a nested collection ended it early.
    /// </remarks>
    private static string InitializerOf(string code, string name) =>
        InitializerNodeOf(code, name).Node.ToString();

    private static (SyntaxNode Node, SemanticModel Model) InitializerNodeOf(string code, string name)
    {
        var (declaration, model) = DeclarationNamed(code, name);
        var initializer = declaration switch
        {
            VariableDeclaratorSyntax variable => (SyntaxNode?)variable.Initializer?.Value,
            PropertyDeclarationSyntax property =>
                (SyntaxNode?)property.Initializer?.Value ?? property.ExpressionBody?.Expression,
            _ => null,
        };

        Assert.True(initializer is not null, $"{name} has no initializer to read.");
        return (initializer!, model);
    }

    /// <summary>
    /// Evaluates the first constructor argument of each choice item in a member's initializer
    /// through the compiler's semantic constant evaluator.
    /// </summary>
    private static string[] ChoiceLabelsOf(string code, string name)
    {
        var (initializer, model) = InitializerNodeOf(code, name);
        var creations = initializer.DescendantNodes()
            .OfType<BaseObjectCreationExpressionSyntax>()
            .Where(creation => creation.ArgumentList is { Arguments.Count: >= 1 })
            .ToArray();

        var labels = new List<string>(creations.Length);
        foreach (var creation in creations)
        {
            var firstArgument = creation.ArgumentList!.Arguments[0].Expression;
            var constant = model.GetConstantValue(firstArgument);
            Assert.True(
                constant is { HasValue: true, Value: string },
                $"Choice argument '{firstArgument}' in {name} did not evaluate to a constant string.");
            labels.Add((string)constant.Value!);
        }

        return labels.ToArray();
    }

    /// <summary>Every name the compiler resolves as a DictationStatus: member, parameter or local.</summary>
    /// <remarks>
    /// ASKS THE COMPILER'S SEMANTIC MODEL RATHER THAN THE WRITTEN TYPE NAME. A <c>var</c> local
    /// bound to a <c>DictationStatus</c>, a pattern-bound variable, and a <c>using</c> alias all
    /// resolve to <c>EnviousWispr.Core.Presentation.DictationStatus</c>, while a comment, a string,
    /// or a type of the same name from another namespace does not. Ref: #82.
    /// </remarks>
    private static HashSet<string> DictationStatusCarriers(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, path: "Carriers.cs");
        var compilation = CreateSemanticCompilation([tree]);
        var statusType = compilation.GetTypeByMetadataName("EnviousWispr.Core.Presentation.DictationStatus");
        Assert.True(
            statusType is { TypeKind: not TypeKind.Error },
            "DictationStatus did not resolve in the semantic compilation.");

        var model = compilation.GetSemanticModel(tree);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var (identifier, symbol) = node switch
            {
                MethodDeclarationSyntax method =>
                    (method.Identifier.ValueText, (ISymbol?)model.GetDeclaredSymbol(method)),
                PropertyDeclarationSyntax property =>
                    (property.Identifier.ValueText, model.GetDeclaredSymbol(property)),
                ParameterSyntax parameter =>
                    (parameter.Identifier.ValueText, model.GetDeclaredSymbol(parameter)),
                VariableDeclaratorSyntax variable =>
                    (variable.Identifier.ValueText, model.GetDeclaredSymbol(variable)),
                SingleVariableDesignationSyntax designation =>
                    (designation.Identifier.ValueText, model.GetDeclaredSymbol(designation)),
                _ => (string.Empty, null),
            };

            if (identifier.Length > 0 &&
                DeclarationShapeAnalyzer.IsDictationStatusCarrierSymbol(symbol, statusType!))
            {
                names.Add(identifier);
            }
        }

        return names;
    }

    /// <summary>The one concrete member with this name, resolved through the semantic model.</summary>
    /// <remarks>
    /// A MISSING OR AMBIGUOUS MEMBER STOPS THE TEST. Only concrete class or struct members
    /// (<see cref="IFieldSymbol"/>, <see cref="IPropertySymbol"/>, or an <see cref="IMethodSymbol"/>
    /// with a body) count: local variables (<see cref="ILocalSymbol"/>) and bodyless interface
    /// signatures are excluded by the semantic model. Ref: #82.
    /// </remarks>
    private static (SyntaxNode Node, SemanticModel Model) DeclarationNamed(string code, string name)
    {
        var tree = CSharpSyntaxTree.ParseText(code, path: "DeclarationLookup.cs");
        var compilation = CreateSemanticCompilation([tree]);
        var model = compilation.GetSemanticModel(tree);

        var declarations = tree.GetRoot()
            .DescendantNodes()
            .Where(node =>
            {
                var symbol = node switch
                {
                    MethodDeclarationSyntax method when method.Identifier.ValueText == name &&
                        (method.Body is not null || method.ExpressionBody is not null) =>
                        (ISymbol?)model.GetDeclaredSymbol(method),
                    PropertyDeclarationSyntax property when property.Identifier.ValueText == name =>
                        model.GetDeclaredSymbol(property),
                    VariableDeclaratorSyntax variable when variable.Identifier.ValueText == name =>
                        model.GetDeclaredSymbol(variable),
                    _ => null,
                };

                return symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol &&
                    symbol.ContainingType?.TypeKind is TypeKind.Class or TypeKind.Struct;
            })
            .ToArray();

        Assert.True(
            declarations.Length > 0,
            $"No member called {name}. Either it was renamed, in which case this gate is no longer "
                + "checking what it was written for, or it is gone and the gate should be too.");
        Assert.True(
            declarations.Length == 1,
            $"Found {declarations.Length} concrete members called {name}; disambiguate the target type so the gate cannot read the wrong one.");
        return (declarations[0], model);
    }

    /// <summary>
    /// The window's code with the hide-list member(s) removed, resolved semantically across partial
    /// declarations and hide loops so a name left only there does not count as reached.
    /// </summary>
    /// <remarks>
    /// ASKS THE COMPILER'S SEMANTIC MODEL WHICH MEMBERS ON THE TARGET TYPE FORM THE HIDE LIST.
    /// Every <c>SettingsSections</c> member on the target <see cref="INamedTypeSymbol"/> across all
    /// partial declarations is removed, together with any member on that type iterated by a
    /// section-hiding <c>foreach</c> loop (setting <c>Visibility</c> to <c>Collapsed</c> when
    /// <c>ReferenceEquals</c> does not match), while ignoring any member of the same name on an
    /// unrelated class. Ref: #82.
    ///
    /// STATED LIMIT: a hide list populated dynamically at runtime from reflection or a XAML visual
    /// tree walk rather than naming the section fields in C# would contain no section identifiers to
    /// strip, and is outside static source analysis.
    /// </remarks>
    private static string CodeWithoutTheHideList(string code) =>
        StripHideLists([("HideListLookup.cs", code)]).Single();

    private static string[] StripHideLists((string Path, string Text)[] sources)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source.Text, path: source.Path))
            .ToArray();
        var compilation = CreateSemanticCompilation(trees);

        var allClasses = trees
            .SelectMany(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                return tree.GetRoot()
                    .DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Select(declaration => model.GetDeclaredSymbol(declaration))
                    .OfType<INamedTypeSymbol>();
            })
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToArray();

        var targetType = allClasses.FirstOrDefault(type => type.Name == "MainWindow")
            ?? allClasses.FirstOrDefault();
        Assert.True(targetType is not null, "No class declaration was found to inspect for the hide list.");

        var hideSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var member in targetType!.GetMembers("SettingsSections"))
        {
            if (member is IMethodSymbol or IPropertySymbol or IFieldSymbol)
            {
                hideSymbols.Add(member);
            }
        }

        // Also detect any member on the target class iterated by a section-hiding foreach loop
        // (`foreach (var candidate in <Member>)` setting `.Visibility` to `Collapsed` via `ReferenceEquals`
        // without a `showAll` flag), so a hide list built under another member name is still removed.
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var loop in tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>())
            {
                var bodyText = loop.Statement.ToString();
                if (!bodyText.Contains(".Visibility", StringComparison.Ordinal) ||
                    !bodyText.Contains("Visibility.Collapsed", StringComparison.Ordinal) ||
                    !bodyText.Contains("ReferenceEquals", StringComparison.Ordinal) ||
                    bodyText.Contains("showAll", StringComparison.Ordinal))
                {
                    continue;
                }

                var expression = loop.Expression is InvocationExpressionSyntax invocation
                    ? invocation.Expression
                    : loop.Expression;
                var symbol = model.GetSymbolInfo(expression).Symbol;
                if (symbol is (IMethodSymbol or IPropertySymbol or IFieldSymbol) &&
                    SymbolEqualityComparer.Default.Equals(symbol.ContainingType, targetType))
                {
                    hideSymbols.Add(symbol);
                }
            }
        }

        Assert.True(
            hideSymbols.Count > 0,
            "The window has no SettingsSections or hide-list member. Either it was renamed - in which case this "
                + "test no longer removes the hide list and would call an orphaned section reached - "
                + "or the hide list is gone and this test should be too.");

        var results = new string[sources.Length];
        var anyRemoved = false;
        for (var i = 0; i < sources.Length; i++)
        {
            var tree = trees[i];
            var spans = hideSymbols
                .SelectMany(symbol => symbol.DeclaringSyntaxReferences)
                .Where(reference => reference.SyntaxTree == tree)
                .Select(reference =>
                {
                    var syntax = reference.GetSyntax();
                    return syntax is VariableDeclaratorSyntax
                        {
                            Parent: VariableDeclarationSyntax
                            {
                                Variables.Count: 1,
                                Parent: FieldDeclarationSyntax field,
                            },
                        }
                        ? field.FullSpan
                        : syntax.FullSpan;
                })
                .Distinct()
                .OrderByDescending(span => span.Start)
                .ToArray();

            var updated = sources[i].Text;
            if (spans.Length > 0)
            {
                anyRemoved = true;
                foreach (var span in spans)
                {
                    updated = updated.Remove(span.Start, span.Length);
                }
            }

            results[i] = updated;
        }

        Assert.True(anyRemoved, "No hide-list declaration spans were found to remove.");
        return results;
    }

    private static readonly SyntaxTree ImplicitSemanticUsingsTree = CSharpSyntaxTree.ParseText(
        """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using EnviousWispr.ASR;
        global using EnviousWispr.Core.Audio;
        global using EnviousWispr.Core.Diagnostics;
        global using EnviousWispr.Core.Dictation;
        global using EnviousWispr.Core.Errors;
        global using EnviousWispr.Core.Input;
        global using EnviousWispr.Core.Observability;
        global using EnviousWispr.Core.Presentation;
        global using EnviousWispr.Core.Runtime;
        global using EnviousWispr.Core.Settings;
        global using EnviousWispr.Pipeline;

        namespace OtherNamespace
        {
            public readonly record struct DictationStatus(string Text);
            public enum DictationOverlayState { Hidden }
            public sealed class Task { }
            public readonly record struct DictationSessionId(System.Guid Value);
            public static class DictationScope
            {
                public static System.IDisposable? Begin(System.Guid sessionId) => null;
            }
        }
        """,
        path: "ImplicitSemanticUsings.g.cs");

    private static readonly MetadataReference[] BaseSemanticReferences = BuildBaseSemanticReferences();

    private static readonly PortableExecutableReference ExternalDelegateAssemblyReference = BuildExternalDelegateAssemblyReference();

    private static MetadataReference[] BuildBaseSemanticReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0 && File.Exists(path))
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "EnviousWispr.*.dll"))
            .Append(typeof(DictationStatus).Assembly.Location)
            .Append(typeof(EnviousWispr.Pipeline.SessionCommand).Assembly.Location)
            .Append(typeof(ITranscriptionEngine).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();

    private static PortableExecutableReference BuildExternalDelegateAssemblyReference()
    {
        var tree = CSharpSyntaxTree.ParseText(
            """
            namespace External;
            public delegate EnviousWispr.Core.Presentation.DictationStatus SentenceToStatus(string sentence);
            """,
            path: "ExternalContracts.cs");
        var compilation = CSharpCompilation.Create(
            "EnviousWispr.ExternalTestContracts",
            [tree],
            BaseSemanticReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, "Failed to emit external delegate test assembly: " + string.Join("; ", emit.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static CSharpCompilation CreateSemanticCompilation(
        IReadOnlyList<SyntaxTree> trees,
        bool includeExternalDelegateAssembly = false)
    {
        var references = includeExternalDelegateAssembly
            ? [.. BaseSemanticReferences, ExternalDelegateAssemblyReference]
            : BaseSemanticReferences;
        return CSharpCompilation.Create(
            "EnviousWispr.SemanticGateScan",
            [.. trees, ImplicitSemanticUsingsTree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    private static Diagnostic[] AnalyzeSnippet(string code, params string[] diagnosticIds)
    {
        var tree = CSharpSyntaxTree.ParseText(code, path: "Snippet.cs");
        var compilation = CreateSemanticCompilation([tree], includeExternalDelegateAssembly: true);
        return RunDeclarationShapeAnalyzer(compilation, [tree], diagnosticIds);
    }

    private static Diagnostic[] RunDeclarationShapeAnalyzer(
        CSharpCompilation compilation,
        IReadOnlyCollection<SyntaxTree> targetTrees,
        params string[] diagnosticIds)
    {
        var targetSet = targetTrees.ToHashSet();
        var idSet = diagnosticIds.ToHashSet(StringComparer.Ordinal);
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new DeclarationShapeAnalyzer()));
        var diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        return diagnostics
            .Where(diagnostic =>
                (idSet.Count == 0 || idSet.Contains(diagnostic.Id)) &&
                diagnostic.Location.SourceTree is { } tree &&
                targetSet.Contains(tree))
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .ToArray();
    }
}
