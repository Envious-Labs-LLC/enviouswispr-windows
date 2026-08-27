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

        var panels = PageContentPanel().Matches(markup).Select(match => match.Value).ToArray();

        Assert.True(panels.Length >= 7, $"Expected at least the seven page panels, found {panels.Length}.");

        var unpinned = panels
            .Where(panel => !panel.Contains("HorizontalAlignment=\"Left\"", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            unpinned.Length == 0,
            $"{unpinned.Length} page panel(s) still centre their content, so their left edge moves with it.");

        // Control, both directions. The matcher must MATCH an unpinned panel - otherwise a matcher
        // that had stopped matching would report the same clean result - and the unpinned one must
        // be recognised as unpinned.
        const string centredPanel = "<StackPanel MaxWidth=\"{StaticResource BrandPageContentMaxWidth}\" Spacing=\"18\">";
        Assert.Matches(PageContentPanel(), centredPanel);
        Assert.DoesNotContain("HorizontalAlignment=\"Left\"", centredPanel, StringComparison.Ordinal);
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

    [GeneratedRegex(@"<StackPanel[^>]*BrandPageContentMaxWidth[^>]*>")]
    private static partial Regex PageContentPanel();

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
}
