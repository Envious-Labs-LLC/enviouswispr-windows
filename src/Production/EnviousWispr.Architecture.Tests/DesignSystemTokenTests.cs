using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MacSnapshotFactAttribute : FactAttribute
{
    public MacSnapshotFactAttribute()
    {
        var swiftPath = DesignSystemTokenTests.GetMacSnapshotPath();
        if (!File.Exists(swiftPath))
        {
            Skip = $"macOS snapshot not found at '{swiftPath}'; the macOS-parity half did not run.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MacPillSnapshotFactAttribute : FactAttribute
{
    public MacPillSnapshotFactAttribute()
    {
        var swiftPath = DesignSystemTokenTests.GetMacPillSnapshotPath();
        if (!File.Exists(swiftPath))
        {
            Skip = $"macOS pill snapshot not found at '{swiftPath}'; the macOS-parity half did not run.";
        }
    }
}

public sealed partial class DesignSystemTokenTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The four card-based choice lists, by name.</summary>
    private static readonly string[] ChoiceListNames =
    [
        "EngineComboBox",
        "PolishProviderComboBox",
        "ThemeComboBox",
        "OverlayPositionComboBox",
    ];

    private static readonly Dictionary<string, ThemePair> ExpectedTokenColors = new(StringComparer.Ordinal)
    {
        ["BrandPageBg"] = new("#F8F5FF", "#131019"),
        ["BrandCardBg"] = new("#FFFFFF", "#201B2B"),
        ["BrandSidebarBg"] = new("#E8E2F5", "#1A1623"),
        ["BrandWindowBg"] = new("#DDD5EE", "#0D0B12"),
        ["BrandTextPrimary"] = new("#0F0A1A", "#ECE9F4"),
        ["BrandTextBody"] = new("#332D47", "#D5D1E2"),
        ["BrandTextSecondary"] = new("#4A3D60", "#AAA2BF"),
        ["BrandTextTertiary"] = new("#6B5E86", "#7A7290"),
        ["BrandAccent"] = new("#7C3AED", "#A78BFA"),
        ["BrandAccentSolid"] = new("#7C3AED", "#8B46F0"),
        ["BrandOnAccent"] = new("#FFFFFF", "#FFFFFF"),
        ["BrandAccentLight"] = new("#177C3AED", "#29A78BFA"),
        ["BrandSuccess"] = new("#00A366", "#5CC99A"),
        ["BrandWarning"] = new("#CC7000", "#E6B766"),
        ["BrandWarningSoft"] = new("#1ACC7000", "#24E6B766"),
        ["BrandError"] = new("#C0392B", "#EF7C89"),
        ["BrandToggleOn"] = new("#00A366", "#5CC99A"),
        ["BrandToggleOff"] = new("#9B8EB8", "#4A4360"),
        ["BrandDivider"] = new("#148A2BE2", "#24B8AAD6"),
        ["BrandSpectrumRed"] = new("#FF2A40", "#FF2A40"),
        ["BrandSpectrumOrange"] = new("#FF8C00", "#FF8C00"),
        ["BrandSpectrumGold"] = new("#FFD700", "#FFD700"),
        ["BrandSpectrumLime"] = new("#ADFF2F", "#ADFF2F"),
        ["BrandSpectrumSpring"] = new("#00FA9A", "#00FA9A"),
        ["BrandSpectrumCyan"] = new("#00FFFF", "#00FFFF"),
        ["BrandSpectrumBlue"] = new("#1E90FF", "#1E90FF"),
        ["BrandSpectrumRoyal"] = new("#4169E1", "#4169E1"),
        ["BrandSpectrumViolet"] = new("#8A2BE2", "#8A2BE2"),
    };

    private static readonly Dictionary<string, ThemePair> ExpectedPillTokenColors = new(StringComparer.Ordinal)
    {
        ["PillSurface"] = new("#F0FCFAFF", "#E6110F18"),
        ["PillBorder"] = new("#240F0A1A", "#21FFFFFF"),
        ["PillDivider"] = new("#1A0F0A1A", "#17FFFFFF"),
        ["PillTimer"] = new("#FF221B33", "#F0FFFFFF"),
        ["PillModeQuiet"] = new("#990F0A1A", "#80FFFFFF"),
        ["PillBadgeFill"] = new("#170F0A1A", "#21FFFFFF"),
        ["PillBadgeText"] = new("#B80F0A1A", "#E0FFFFFF"),
        ["PillText"] = new("#FF0F0A1A", "#F7FFFFFF"),
        ["PillTextDimmed"] = new("#990F0A1A", "#80FFFFFF"),
        ["PillNotice"] = new("#E00F0A1A", "#F2FFFFFF"),
    };

    private static readonly (string SwiftName, string BrandName)[] SwiftTokenMap =
    [
        ("stPageBg", "BrandPageBg"),
        ("stSectionBg", "BrandCardBg"),
        ("stSidebarBg", "BrandSidebarBg"),
        ("stWindowBg", "BrandWindowBg"),
        ("stTextPrimary", "BrandTextPrimary"),
        ("stTextBody", "BrandTextBody"),
        ("stTextSecondary", "BrandTextSecondary"),
        ("stTextTertiary", "BrandTextTertiary"),
        ("stAccent", "BrandAccent"),
        ("stAccentSolid", "BrandAccentSolid"),
        ("stAccentLight", "BrandAccentLight"),
        ("stSuccess", "BrandSuccess"),
        ("stWarning", "BrandWarning"),
        ("stWarningSoft", "BrandWarningSoft"),
        ("stError", "BrandError"),
        ("stToggleOn", "BrandToggleOn"),
        ("stToggleOff", "BrandToggleOff"),
        ("stDivider", "BrandDivider"),
    ];

    private static readonly (string SwiftName, string PillName)[] SwiftPillTokenMap =
    [
        ("surface", "PillSurface"),
        ("border", "PillBorder"),
        ("divider", "PillDivider"),
        ("timer", "PillTimer"),
        ("modeQuiet", "PillModeQuiet"),
        ("badgeFill", "PillBadgeFill"),
        ("badgeText", "PillBadgeText"),
        ("text", "PillText"),
        ("textDimmed", "PillTextDimmed"),
        ("notice", "PillNotice"),
    ];

    [Fact]
    public void EveryThemeDeclaresEveryColorAndBrushToken()
    {
        var repositoryRoot = FindRepositoryRoot();
        var themeDictionaries = LoadThemeDictionaries(repositoryRoot);
        var missing = new List<string>();

        foreach (var themeName in new[] { "Light", "Dark", "HighContrast" })
        {
            if (!themeDictionaries.TryGetValue(themeName, out var theme))
            {
                missing.Add($"theme dictionary '{themeName}'");
                continue;
            }

            var keys = theme.Elements()
                .Select(element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)))
                .Where(key => key is not null)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var tokenName in ExpectedTokenColors.Keys)
            {
                foreach (var suffix in new[] { "Color", "Brush" })
                {
                    var resourceKey = tokenName + suffix;
                    if (!keys.Contains(resourceKey))
                    {
                        missing.Add($"{themeName}/{resourceKey}");
                    }
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Design token resources are missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void LightAndDarkTokensMatchExpectedTable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var themeDictionaries = LoadThemeDictionaries(repositoryRoot);
        foreach (var token in ExpectedTokenColors)
        {
            AssertColorMatches(themeDictionaries, "Light", token.Key, token.Value.Light);
            AssertColorMatches(themeDictionaries, "Dark", token.Key, token.Value.Dark);
        }
    }

    [Fact]
    public void EveryThemeDeclaresEveryPillColorAndBrushToken()
    {
        var repositoryRoot = FindRepositoryRoot();
        var themeDictionaries = LoadThemeDictionaries(repositoryRoot, "PillTokens.xaml");
        var missing = new List<string>();

        foreach (var themeName in new[] { "Light", "Dark", "HighContrast" })
        {
            if (!themeDictionaries.TryGetValue(themeName, out var theme))
            {
                missing.Add($"theme dictionary '{themeName}'");
                continue;
            }

            var keys = theme.Elements()
                .Select(element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)))
                .Where(key => key is not null)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var tokenName in ExpectedPillTokenColors.Keys)
            {
                foreach (var suffix in new[] { "Color", "Brush" })
                {
                    var resourceKey = tokenName + suffix;
                    if (!keys.Contains(resourceKey))
                    {
                        missing.Add($"{themeName}/{resourceKey}");
                    }
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Pill token resources are missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void LightAndDarkPillTokensMatchExpectedTable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var themeDictionaries = LoadThemeDictionaries(repositoryRoot, "PillTokens.xaml");
        foreach (var token in ExpectedPillTokenColors)
        {
            AssertColorMatches(themeDictionaries, "Light", token.Key, token.Value.Light);
            AssertColorMatches(themeDictionaries, "Dark", token.Key, token.Value.Dark);
        }
    }

    [MacSnapshotFact]
    public void MacDynamicColorsMatchExpectedTable()
    {
        var swiftPath = GetMacSnapshotPath();
        Assert.True(
            File.Exists(swiftPath),
            $"The macOS snapshot disappeared after test discovery at '{swiftPath}'.");

        var swiftTokens = ParseSwiftTokens(File.ReadAllText(swiftPath));
        foreach (var mapping in SwiftTokenMap)
        {
            if (!swiftTokens.TryGetValue(mapping.SwiftName, out var actual))
            {
                throw new InvalidOperationException(
                    $"Mapped Swift token '{mapping.SwiftName}' for '{mapping.BrandName}' was not found in '{swiftPath}'.");
            }

            if (!ExpectedTokenColors.TryGetValue(mapping.BrandName, out var expected))
            {
                throw new InvalidOperationException(
                    $"Mapped Brand token '{mapping.BrandName}' for '{mapping.SwiftName}' is missing from the expected table.");
            }

            AssertChannelsMatch(
                "macOS Light snapshot",
                mapping.BrandName,
                ParseHexColor(mapping.BrandName, "expected Light table", expected.Light),
                actual.Light,
                mapping.SwiftName);
            AssertChannelsMatch(
                "macOS Dark snapshot",
                mapping.BrandName,
                ParseHexColor(mapping.BrandName, "expected Dark table", expected.Dark),
                actual.Dark,
                mapping.SwiftName);
        }
    }

    [MacPillSnapshotFact]
    public void MacPillDynamicColorsMatchExpectedTableIncludingAlpha()
    {
        var swiftPath = GetMacPillSnapshotPath();
        Assert.True(
            File.Exists(swiftPath),
            $"The macOS pill snapshot disappeared after test discovery at '{swiftPath}'.");

        var swiftTokens = ParseSwiftTokens(File.ReadAllText(swiftPath));
        foreach (var mapping in SwiftPillTokenMap)
        {
            if (!swiftTokens.TryGetValue(mapping.SwiftName, out var actual))
            {
                throw new InvalidOperationException(
                    $"Mapped Swift pill token '{mapping.SwiftName}' for '{mapping.PillName}' was not found in '{swiftPath}'.");
            }

            if (!ExpectedPillTokenColors.TryGetValue(mapping.PillName, out var expected))
            {
                throw new InvalidOperationException(
                    $"Mapped Pill token '{mapping.PillName}' for '{mapping.SwiftName}' is missing from the expected table.");
            }

            AssertChannelsMatch(
                "macOS Light pill snapshot",
                mapping.PillName,
                ParseHexColor(mapping.PillName, "expected Light pill table", expected.Light),
                actual.Light,
                mapping.SwiftName);
            AssertChannelsMatch(
                "macOS Dark pill snapshot",
                mapping.PillName,
                ParseHexColor(mapping.PillName, "expected Dark pill table", expected.Dark),
                actual.Dark,
                mapping.SwiftName);
        }
    }

    [Fact]
    public void OverlayUsesDeclaredPillTokensAndNoUndersizedText()
    {
        var repositoryRoot = FindRepositoryRoot();
        var overlayPath = Path.Combine(
            repositoryRoot,
            "src",
            "Production",
            "EnviousWispr.App",
            "DictationOverlayWindow.xaml");
        var overlayCodeBehindPath = overlayPath + ".cs";
        var overlay = XDocument.Load(overlayPath);
        var declaredPillKeys = LoadThemeDictionaries(repositoryRoot, "PillTokens.xaml")["Light"]
            .Elements()
            .Select(element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        var resourceReferences = overlay.Descendants()
            .Attributes()
            .Select(attribute => ThemeResourceRegex().Match(attribute.Value))
            .Where(match => match.Success)
            .Select(match => match.Groups["key"].Value)
            .Where(key => key.StartsWith("Pill", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(resourceReferences);
        Assert.All(resourceReferences, key => Assert.Contains(key, declaredPillKeys));

        // Resolve a style reference against the overlay's own resources OR anywhere in the
        // pill theme file. The property worth pinning is that every Pill* style the overlay
        // names is AVAILABLE, not which file happens to hold it: the four pill text styles
        // moved from the overlay into PillTokens.xaml and a test asserting their location
        // failed on a change that broke nothing a user could see.
        //
        // Read EVERY x:Key in that file, not just the theme dictionaries' — a Style does not
        // vary by theme (only the brushes it references do), so these correctly sit at the
        // dictionary root, outside <ResourceDictionary.ThemeDictionaries>.
        var pillThemeFile = XDocument.Load(
            Path.Combine(
                repositoryRoot, "src", "Production", "EnviousWispr.App", "Theme", "PillTokens.xaml"));
        var declaredLocalKeys = overlay.Descendants()
            .Concat(pillThemeFile.Descendants())
            .Select(element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);
        var localStyleReferences = overlay.Descendants()
            .Attributes()
            .Select(attribute => StaticResourceRegex().Match(attribute.Value))
            .Where(match => match.Success)
            .Select(match => match.Groups["key"].Value)
            .Where(key => key.StartsWith("Pill", StringComparison.Ordinal))
            .Concat(CodeBehindPillStyleRegex()
                .Matches(File.ReadAllText(overlayCodeBehindPath))
                .Cast<Match>()
                .Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(localStyleReferences);
        Assert.All(localStyleReferences, key => Assert.Contains(key, declaredLocalKeys));

        var undersizedText = overlay.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => new
            {
                Name = (string?)element.Attribute(XName.Get("Name", XamlNamespace)) ?? "unnamed TextBlock",
                FontSize = (double?)element.Attribute("FontSize"),
            })
            .Where(text => text.FontSize is null or < 14)
            .ToArray();
        Assert.True(
            undersizedText.Length == 0,
            $"Overlay text must be at least 14px: {string.Join(", ", undersizedText.Select(text => $"{text.Name}={text.FontSize}"))}");
    }

    [Fact]
    public void AppViewsContainNoLiteralColors()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appDirectory = Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App");
        var themeDirectory = Path.Combine(appDirectory, "Theme") + Path.DirectorySeparatorChar;
        var xamlFiles = Directory.GetFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).StartsWith(themeDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(appDirectory, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // A shrinking scope is how this check quietly stops checking anything, so
        // require the two views by name rather than merely requiring a non-empty
        // set: an exclusion that swallowed everything would still satisfy a count.
        foreach (var required in new[] { "MainWindow.xaml", "DictationOverlayWindow.xaml" })
        {
            Assert.True(
                xamlFiles.Any(path => Path.GetFileName(path).Equals(required, StringComparison.Ordinal)),
                $"'{required}' was not in the scanned set under '{appDirectory}'. The scan's scope is wrong, "
                    + "so a pass would prove nothing.");
        }

        var offenders = new List<string>();
        foreach (var path in xamlFiles)
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in HexColorRegex().Matches(lines[index]))
                {
                    offenders.Add(DescribeOffender(repositoryRoot, path, index + 1, match.Value));
                }

                foreach (Match match in NamedColorAttributeRegex().Matches(lines[index]))
                {
                    offenders.Add(DescribeOffender(repositoryRoot, path, index + 1, match.Value));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Literal colors must use a ThemeResource token:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void SelectableCardStyleUsesRadioButtonInteractionStates()
    {
        var repositoryRoot = FindRepositoryRoot();
        var controlsPath = Path.Combine(
            repositoryRoot,
            "src",
            "Production",
            "EnviousWispr.App",
            "Theme",
            "Controls.xaml");
        var document = XDocument.Load(controlsPath);
        var style = document.Descendants().Single(element =>
            element.Name.LocalName == "Style" &&
            string.Equals(
                (string?)element.Attribute(XName.Get("Key", XamlNamespace)),
                "BrandSelectableCardStyle",
                StringComparison.Ordinal));

        Assert.Equal("RadioButton", (string?)style.Attribute("TargetType"));
        var stateNames = style.Descendants()
            .Where(element => element.Name.LocalName == "VisualState")
            .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace)))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("PointerOver", stateNames);
        Assert.Contains("Pressed", stateNames);
        Assert.Contains("Checked", stateNames);
        Assert.Contains("Focused", stateNames);
        Assert.Equal("3", ReadVisualStateSetter(style, "PointerOver", "InteractionBorder.BorderThickness"));
        Assert.Equal("4", ReadVisualStateSetter(style, "Pressed", "InteractionBorder.BorderThickness"));
        Assert.Equal("2", ReadVisualStateSetter(style, "Checked", "CardBorder.BorderThickness"));
        Assert.Contains(style.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                "CheckBadge",
                StringComparison.Ordinal) &&
            string.Equals((string?)element.Attribute("Background"), "{ThemeResource BrandAccentSolidBrush}", StringComparison.Ordinal));
    }

    private static string? ReadVisualStateSetter(XElement style, string stateName, string target)
    {
        var state = style.Descendants().Single(element =>
            element.Name.LocalName == "VisualState"
            && string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                stateName,
                StringComparison.Ordinal));
        return (string?)state.Descendants().Single(element =>
            element.Name.LocalName == "Setter"
            && string.Equals((string?)element.Attribute("Target"), target, StringComparison.Ordinal))
            .Attribute("Value");
    }

    [Fact]
    public void HistoryDistinguishesLoadingFromLoadedEmptyStates()
    {
        var document = LoadMainWindow();
        Assert.Equal("Collapsed", (string?)FindNamedElement(document, "HistoryList").Attribute("Visibility"));
        Assert.Null(FindNamedElement(document, "HistoryLoadingState").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)FindNamedElement(document, "HistoryEmptyState").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)FindNamedElement(document, "HistorySearchEmptyState").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)FindNamedElement(document, "HistoryUnavailableState").Attribute("Visibility"));

        var repositoryRoot = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Production",
            "EnviousWispr.App",
            "MainWindow.xaml.cs"));
        Assert.Contains("private bool _isHistoryLoading = true;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_isHistoryLoading = false;", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "HistoryLoadingState.Visibility = _isHistoryLoading ? Visibility.Visible : Visibility.Collapsed;",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChoiceSetsKeepPlatformSingleSelectionSemantics()
    {
        var document = LoadMainWindow();
        foreach (var name in ChoiceListNames)
        {
            var element = FindNamedElement(document, name);

            // Deliberately NOT a RadioButtons control. That control arranges each item at the
            // item's own desired size and never consults the item's alignment, so the cards came
            // out at as many different widths as there were descriptions - a visible staircase.
            // Three attempts to make it hand its width down failed, the last one inertly, giving
            // byte-identical measurements across two builds. An ItemsControl over a StackPanel
            // gives every card the panel's full width, which is all that was ever wanted.
            Assert.Equal("ItemsControl", element.Name.LocalName);

            // Single-selection semantics survive the change: the items are still RadioButtons in
            // a named group, so exactly one can be checked. Losing that would trade a layout bug
            // for a behaviour bug.
            var group = element.Descendants()
                .Where(child => child.Name.LocalName == "RadioButton")
                .Select(child => (string?)child.Attribute("GroupName"))
                .ToArray();
            Assert.True(
                group.Length == 1 && !string.IsNullOrWhiteSpace(group[0]),
                $"'{name}' must render exactly one RadioButton template carrying a GroupName; "
                    + $"found {group.Length}. Without a group name the cards stop excluding each "
                    + "other and more than one can appear chosen.");
        }

        // Every CHOICE LIST's group name must be distinct, or two lists share one exclusion set
        // and choosing in one silently clears the other. Scoped to the four lists on purpose: the
        // recording-pill cards below deliberately SHARE a group, because Capsule and Level Rail
        // are two options in one choice, and a blanket distinctness check would call that a bug.
        var groupNames = ChoiceListNames
            .Select(name => FindNamedElement(document, name))
            .SelectMany(list => list.Descendants().Where(child => child.Name.LocalName == "RadioButton"))
            .Select(element => (string?)element.Attribute("GroupName"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        Assert.True(
            groupNames.Length == groupNames.Distinct(StringComparer.Ordinal).Count(),
            "Two choice lists share a RadioButton GroupName: "
                + string.Join(", ", groupNames.GroupBy(v => v, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1).Select(g => g.Key)));

        foreach (var name in new[]
                 {
                     "CapsulePillButton",
                     "LevelRailPillButton",
                     "ReadingWellPillButton",
                 })
        {
            var element = FindNamedElement(document, name);
            Assert.Equal("RadioButton", element.Name.LocalName);
            Assert.Null(element.Attribute("Click"));
        }

        Assert.Equal(
            (string?)FindNamedElement(document, "CapsulePillButton").Attribute("GroupName"),
            (string?)FindNamedElement(document, "LevelRailPillButton").Attribute("GroupName"));
    }

    [Fact]
    public void EveryProductPageStartsWithAHeaderCard()
    {
        var document = LoadMainWindow();
        foreach (var name in new[]
                 {
                     "HomePage",
                     "WhatsNewPage",
                     "HistoryPage",
                     "DictionaryPage",
                     "SnippetsPage",
                     "SettingsPage",
                     "HelpPage",
                 })
        {
            var page = FindNamedElement(document, name);
            var stack = page.Elements().Single(element => element.Name.LocalName == "StackPanel");
            var firstContent = stack.Elements().First();
            Assert.Equal("Border", firstContent.Name.LocalName);
            Assert.Equal(
                "{StaticResource BrandHeaderCardStyle}",
                (string?)firstContent.Attribute("Style"));
        }
    }

    /// <summary>
    /// Every measure in the product window comes from a layout token, never from a number typed
    /// at the site.
    /// </summary>
    /// <remarks>
    /// The defect this exists for is not "a magic number is untidy". Pages that each picked their
    /// own cap rendered at three different widths (900, 820, 440), so the content card visibly
    /// changed width as the user clicked from one nav row to the next — the frame appeared to
    /// twitch rather than hold still. A token cannot disagree with itself.
    ///
    /// Enumerated from the DOCUMENT, not from a list of pages kept here. A page added tomorrow is
    /// covered without anybody remembering to add it, which is the only version of this check that
    /// stays true.
    /// </remarks>
    [Fact]
    public void EveryMeasureInTheProductWindowComesFromALayoutToken()
    {
        var document = LoadMainWindow();
        var offenders = document.Descendants()
            .Select(element => (Element: element, MaxWidth: (string?)element.Attribute("MaxWidth")))
            .Where(pair => pair.MaxWidth is not null)
            .Where(pair => !pair.MaxWidth!.StartsWith("{StaticResource ", StringComparison.Ordinal))
            .Select(pair => $"<{pair.Element.Name.LocalName} MaxWidth=\"{pair.MaxWidth}\">")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These elements set MaxWidth to a literal instead of a layout token:\n  "
                + string.Join("\n  ", offenders)
                + "\nUse {StaticResource BrandPageContentMaxWidth} for a page's own measure, or "
                + "{StaticResource BrandInlineContentMaxWidth} for a column nested inside a card. "
                + "Pages that each choose their own number render at different widths and the frame "
                + "appears to change size as the user navigates.");

        // Two-way control: the check above is satisfied by a window with no MaxWidth at all, which
        // would be a different bug wearing this test's green. At least one page must actually be
        // capped, or the measure is not being applied anywhere.
        Assert.Contains(
            document.Descendants(),
            element => (string?)element.Attribute("MaxWidth")
                == "{StaticResource BrandPageContentMaxWidth}");
    }

    /// <summary>
    /// Every choice list renders as ONE full-width column of equal cards.
    /// </summary>
    /// <remarks>
    /// The PROPERTY is unchanged from when this was written; the mechanism that delivers it is
    /// not. It used to assert a RadioButtons control pinned to one column - that control arranges
    /// each item at the item's own desired size, so the cards came out at six different widths and
    /// three attempts to make it hand its width down all failed. The lists are now ItemsControls
    /// over a vertical StackPanel, which gives every item the panel's full width.
    ///
    /// Re-pointed rather than deleted. A gate whose subject disappears is a gate that passes
    /// vacuously or fails confusingly, and either way the claim it protected stops being checked.
    /// </remarks>
    [Fact]
    public void EveryChoiceListIsOneFullWidthColumn()
    {
        var document = LoadMainWindow();

        foreach (var name in ChoiceListNames)
        {
            var list = FindNamedElement(document, name);
            Assert.Equal("ItemsControl", list.Name.LocalName);
            Assert.Equal("Stretch", (string?)list.Attribute("HorizontalAlignment"));

            // The items panel must be a plain StackPanel. Its default orientation is vertical, and
            // a StackPanel hands each child the full cross-axis width - which is the whole reason
            // the control was changed.
            var panel = list.Descendants()
                .Where(element => element.Name.LocalName == "ItemsPanelTemplate")
                .SelectMany(template => template.Elements())
                .ToArray();
            Assert.True(
                panel.Length == 1,
                $"'{name}' must declare exactly one items panel; found {panel.Length}.");
            Assert.Equal("StackPanel", panel[0].Name.LocalName);

            var orientation = (string?)panel[0].Attribute("Orientation");
            Assert.True(
                orientation is null or "Vertical",
                $"'{name}' lays its cards out {orientation}. A horizontal run of cards is a "
                    + "different design and would reintroduce per-item widths.");
        }
    }

    /// <summary>
    /// Every setting row's leading icon lines up with the row's first line of text.
    /// </summary>
    /// <remarks>
    /// Centred against the whole row, a glyph beside a label-plus-control-plus-helper-text row
    /// floats level with the middle of the control and reads as a stray column of marks in the
    /// gutter rather than as part of the row it belongs to.
    ///
    /// Enumerated from the producing structure: any Grid whose first column is the row-icon column
    /// is a setting row, so a row added later is covered without being listed here.
    /// </remarks>
    [Fact]
    public void EveryRowIconSitsWithItsFirstLineOfText()
    {
        var document = LoadMainWindow();
        var rowGrids = document.Descendants()
            .Where(element => element.Name.LocalName == "Grid")
            // The grid's OWN first column, not any column anywhere beneath it. Descendants() walks
            // into nested grids, so a container wrapping a setting row matched as though it were
            // one — a row with no icon of its own, reported as a row missing its icon.
            // A setting row is a grid whose first column sizes to its leading glyph. The marker is
            // structural rather than a token: the width token now lives on the FontIcon, because
            // ColumnDefinition.Width is a GridLength and a StaticResource handed to it is a
            // load-time parse failure - so keying the selector off that attribute would be keying
            // it off the very thing that crashed the app.
            .Where(grid => grid.Elements()
                .Where(child => child.Name.LocalName == "Grid.ColumnDefinitions")
                .SelectMany(definitions => definitions.Elements())
                .Select(definition => (string?)definition.Attribute("Width"))
                .FirstOrDefault() == "Auto"
                && grid.Elements().Any(child => child.Name.LocalName == "FontIcon"))
            .ToArray();

        Assert.True(
            rowGrids.Length > 0,
            "No setting rows were found. A row is a Grid whose first column is Auto and which carries "
                + "a FontIcon, so zero matches means that shape changed and this test is now checking "
                + "nothing rather than checking something and passing.");

        foreach (var grid in rowGrids)
        {
            var icon = grid.Elements().FirstOrDefault(element => element.Name.LocalName == "FontIcon");
            Assert.True(
                icon is not null,
                "A Grid reserves the row-icon column but has no FontIcon in it, so the row carries "
                    + "an empty gutter where its icon should be.");
            Assert.Equal("Top", (string?)icon!.Attribute("VerticalAlignment"));
            Assert.Equal(
                "{StaticResource BrandRowIconInset}",
                (string?)icon.Attribute("Margin"));
            Assert.Equal(
                "{StaticResource BrandRowIconColumnWidth}",
                (string?)icon.Attribute("Width"));
        }
    }

    /// <summary>
    /// Every sidebar row leads somewhere of its own. No two rows may resolve to the same section.
    /// </summary>
    /// <remarks>
    /// The defect: Transcription, Microphone and Keybinds all resolved to one section, and Live
    /// Preview resolved to Appearance's. Five sidebar rows rendered two distinct views, with only
    /// the header card's text differing — so clicking Keybinds showed a microphone picker and an
    /// engine chooser with "Keybinds" written above them. Nothing failed; each page looked
    /// deliberate on its own, and the duplication was only visible by opening two rows in turn.
    /// The Help page had the same shape one level worse: its dispatch set the header and never
    /// filtered at all, so three rows each showed all five sections.
    ///
    /// Enumerated from the NAV, not from a list kept here. The tags come out of MainWindow.xaml and
    /// the destinations out of the dispatch in MainWindow.xaml.cs, so a sidebar row added next month
    /// is covered on arrival — and a row added with no destination at all is a failure rather than a
    /// silent fall-through to the catch-all.
    /// </remarks>
    [Fact]
    public void NoTwoSidebarRowsLeadToTheSameSection()
    {
        var document = LoadMainWindow();
        var codeBehind = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Production",
            "EnviousWispr.App",
            "MainWindow.xaml.cs"));

        var tags = document.Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(element => (string?)element.Attribute("Tag"))
            .Where(tag => tag is not null)
            .Select(tag => tag!)
            .Where(tag => tag.StartsWith("settings-", StringComparison.Ordinal)
                || tag.StartsWith("help-", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            tags.Length > 0,
            "No settings- or help- nav tags were found in MainWindow.xaml. The sidebar is this test's "
                + "input, so zero tags means it is checking nothing.");

        // Both dispatches spell a destination the same way: the section's field name, ending in
        // "Section", on the line that closes the switch arm.
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            // A tag can appear in MORE THAN ONE dispatch, and the one that names the section is not
            // always the first. Help rows carry their title in ConfigureHelpPage and their
            // destination in HelpSectionFor, so reading only the first arm finds a title, no
            // section, and reports a row that leads nowhere - a false report in the exact shape of
            // a real finding. Every arm for the tag is read, and they must agree.
            var found = new List<string>();
            var cursor = 0;
            while (true)
            {
                var start = codeBehind.IndexOf($"\"{tag}\"", cursor, StringComparison.Ordinal);
                if (start < 0)
                {
                    break;
                }

                cursor = start + tag.Length;

                // Only an occurrence followed by "=>" is a dispatch arm. The tag also appears in
                // ordinary comparisons ( tag == "settings-transcription" ), and slicing from one of
                // those runs on into unrelated code - it picked up SettingsSections() and reported
                // the row as resolving to two different destinations.
                var afterLiteral = start + tag.Length + 2;
                if (afterLiteral >= codeBehind.Length
                    || !codeBehind[afterLiteral..].TrimStart().StartsWith("=>", StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = codeBehind[start..];
                var end = rest.Length;
                foreach (var terminator in new[] { "\"settings-", "\"help-", "_ =>" })
                {
                    // From index 1, so the arm's own opening tag literal is not its own terminator.
                    var next = rest.IndexOf(terminator, 1, StringComparison.Ordinal);
                    if (next >= 0 && next < end)
                    {
                        end = next;
                    }
                }

                var match = Regex.Match(rest[..end], @"(?<name>\w+Section)(?![A-Za-z0-9_])");
                if (match.Success)
                {
                    found.Add(match.Groups["name"].Value);
                }
            }

            Assert.True(
                found.Count > 0,
                $"The nav row tagged '{tag}' resolves to no section in any dispatch. A row that leads "
                    + "nowhere shows whatever the previous row left on screen.");
            Assert.True(
                found.Distinct(StringComparer.Ordinal).Count() == 1,
                $"The nav row tagged '{tag}' resolves to more than one section depending on which "
                    + $"dispatch you read: {string.Join(", ", found.Distinct(StringComparer.Ordinal))}. "
                    + "Two dispatches disagreeing about one row is how a page ends up showing one "
                    + "thing and claiming another.");
            destinations[tag] = found[0];
        }

        var collisions = destinations
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} <- {string.Join(", ", group.Select(pair => pair.Key))}")
            .ToArray();

        Assert.True(
            collisions.Length == 0,
            "These sidebar rows lead to the SAME section, so they render the same page with only the "
                + "header differing:\n  "
                + string.Join("\n  ", collisions)
                + "\nGive each row its own section, or remove the row. A sidebar that promises more "
                + "destinations than it has is the single loudest way an app reads as unfinished.");

        // Every destination must actually exist in the markup. A dispatch naming a section that was
        // renamed or deleted would not compile, but one naming a section that exists only in the
        // dispatch's own text would pass the check above while resolving to nothing.
        var declared = document.Descendants()
            .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace)))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (tag, section) in destinations)
        {
            Assert.True(
                declared.Contains(section),
                $"The row tagged '{tag}' points at '{section}', which is not declared in MainWindow.xaml.");
        }
    }

    /// <summary>
    /// Where markup and the resource file both name a control, they must say the same thing.
    /// </summary>
    /// <remarks>
    /// An <c>x:Uid</c> makes the .resw the winner: whatever <c>Content="..."</c> says in the markup
    /// is dead text that never reaches the screen. So the two can drift apart in total silence, and
    /// the tell is only visible at runtime, on screen, in two places at once.
    ///
    /// Measured: the sidebar read "Dictionary" while the page it opened was headed "Your Words", and
    /// the rest of the product — the page header, the add-a-word helper — said "Your Words" too. One
    /// feature, two names, and the name a reader would find by grepping the markup was the one nobody
    /// ever saw. Editing that dead attribute is the natural repair and changes nothing.
    ///
    /// Enumerated from the .resw, which is the authority. An override added later is covered on
    /// arrival, and this asserts AGREEMENT rather than picking a winner, so it cannot be satisfied by
    /// deleting one side.
    /// </remarks>
    [Fact]
    public void MarkupAndResourcesAgreeOnEveryOverriddenLabel()
    {
        var document = LoadMainWindow();
        var resources = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Production",
            "EnviousWispr.App",
            "Strings",
            "en-US",
            "Resources.resw"));

        var overrides = resources.Root!
            .Elements("data")
            .Select(entry => (
                Name: (string?)entry.Attribute("name") ?? string.Empty,
                Value: entry.Element("value")?.Value ?? string.Empty))
            .Where(entry => entry.Name.EndsWith(".Content", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            overrides.Length > 0,
            "No .Content overrides were found in Resources.resw. They are this test's input, so zero "
                + "means it is checking nothing.");

        var disagreements = new List<string>();
        foreach (var (name, value) in overrides)
        {
            var uid = name[..^".Content".Length];
            var element = document.Descendants()
                .FirstOrDefault(candidate =>
                    (string?)candidate.Attribute(XName.Get("Uid", XamlNamespace)) == uid);
            if (element is null)
            {
                continue;
            }

            var markup = (string?)element.Attribute("Content");
            if (markup is not null && !string.Equals(markup, value, StringComparison.Ordinal))
            {
                disagreements.Add($"{uid}: markup says \"{markup}\", resource says \"{value}\" (resource wins)");
            }
        }

        Assert.True(
            disagreements.Count == 0,
            "Markup and resources disagree about what these controls are called:\n  "
                + string.Join("\n  ", disagreements)
                + "\nThe resource file wins at runtime, so the markup value is what nobody sees. Make "
                + "them match — the user should not meet one feature under two names.");
    }

    /// <summary>
    /// Every layout token is assigned to a property of the type it was declared as.
    /// </summary>
    /// <remarks>
    /// A StaticResource is assigned WITHOUT running a type converter. Hand an
    /// <c>x:Double</c> to a property whose type is <c>GridLength</c> and the build is clean, the
    /// XML is well-formed, every existing gate here is green — and the app exits about two seconds
    /// after launch with E_XAMLPARSEFAILED, no window, and nothing on stdout or stderr.
    ///
    /// Measured, and it shipped through four commits: 41 ColumnDefinition.Width attributes read a
    /// Double token. Every check in this file parses MainWindow.xaml as XML, so all of them passed
    /// on a build that could not start. XML well-formedness and XAML validity are different
    /// questions, and only the first one was ever being asked.
    ///
    /// This asks the second one, for the part of it that is decidable from source: a token's
    /// declared element type against the type its consuming property requires. Enumerated from the
    /// USES in the markup, so a new consumer is checked on arrival, and any attribute this table
    /// does not know about is reported rather than skipped in silence — an unknown property is the
    /// case where a wrong answer looks exactly like a right one.
    /// </remarks>
    [Fact]
    public void EveryLayoutTokenIsAssignedToAPropertyOfItsOwnType()
    {
        var root = FindRepositoryRoot();
        var layout = XDocument.Load(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "Theme", "Layout.xaml"));

        var declaredType = layout.Root!
            .Elements()
            .Where(element => element.Attribute(XName.Get("Key", XamlNamespace)) is not null)
            .ToDictionary(
                element => (string)element.Attribute(XName.Get("Key", XamlNamespace))!,
                element => element.Name.LocalName switch
                {
                    "Double" => "Double",
                    var other => other,
                },
                StringComparer.Ordinal);

        // What each property REQUIRES. Keyed by owning element where the same attribute name means
        // different things: Width on a ColumnDefinition is a GridLength, Width on anything else is
        // a Double. That collision is the whole defect.
        static string? RequiredType(string element, string attribute) => (element, attribute) switch
        {
            ("ColumnDefinition", "Width") or ("RowDefinition", "Height") => "GridLength",
            (_, "Margin") or (_, "Padding") => "Thickness",
            (_, "CornerRadius") => "CornerRadius",
            (_, "Width") or (_, "Height") or (_, "MaxWidth") or (_, "MinWidth")
                or (_, "MaxHeight") or (_, "MinHeight") or (_, "Spacing")
                or (_, "ColumnSpacing") or (_, "RowSpacing")
                or (_, "FontSize") or (_, "OpenPaneLength") => "Double",
            _ => null,
        };

        var mismatches = new List<string>();
        var unknown = new List<string>();
        var checkedCount = 0;

        foreach (var view in new[] { "MainWindow.xaml", "DictationOverlayWindow.xaml" })
        {
            var document = XDocument.Load(Path.Combine(
                root, "src", "Production", "EnviousWispr.App", view));

            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var match = Regex.Match(attribute.Value, @"^\{StaticResource (?<key>\w+)\}$");
                    if (!match.Success
                        || !declaredType.TryGetValue(match.Groups["key"].Value, out var actual))
                    {
                        continue;
                    }

                    var key = match.Groups["key"].Value;

                    // A Setter's Value takes the type of the property it SETS, which is named by
                    // its own Property attribute. Read through to that, otherwise every token
                    // applied through a style is invisible to this check - and a style is exactly
                    // where a token gets applied to many controls at once.
                    var owner = element.Name.LocalName;
                    var property = attribute.Name.LocalName;
                    if (owner == "Setter" && property == "Value")
                    {
                        var target = (string?)element.Attribute("Property");
                        if (target is null)
                        {
                            unknown.Add($"{view}: a Setter sets {key} with no Property attribute");
                            continue;
                        }

                        owner = string.Empty;
                        property = target;
                    }

                    var required = RequiredType(owner, property);
                    if (required is null)
                    {
                        unknown.Add($"{view}: {owner}.{property} <- {key}");
                        continue;
                    }

                    checkedCount++;
                    if (!string.Equals(required, actual, StringComparison.Ordinal))
                    {
                        mismatches.Add(
                            $"{view}: {owner}.{property} needs {required}, "
                                + $"but {key} is declared as {actual}");
                    }
                }
            }
        }

        Assert.True(
            checkedCount > 0,
            "No layout tokens were checked at all, so this test proved nothing. Either the views stopped "
                + "using layout tokens or the resource keys stopped resolving.");

        Assert.True(
            unknown.Count == 0,
            "These properties consume a layout token but are not in this test's type table, so their "
                + "types went unchecked:\n  "
                + string.Join("\n  ", unknown.Distinct(StringComparer.Ordinal))
                + "\nAdd them to RequiredType with the type the property actually takes. Skipping an "
                + "unknown property quietly is how the original defect would have survived this gate.");

        Assert.True(
            mismatches.Count == 0,
            "These tokens are assigned to a property of a different type:\n  "
                + string.Join("\n  ", mismatches)
                + "\nA StaticResource is assigned without a type converter, so this builds clean and "
                + "then fails at LOAD: the app exits seconds after launch with E_XAMLPARSEFAILED and no "
                + "window. Change the token's declared type, or move it to a property that takes it.");
    }

    /// <summary>
    /// No items control declares two attributes that cannot both be honoured.
    /// </summary>
    /// <remarks>
    /// <c>ItemTemplate</c> and <c>DisplayMemberPath</c> are mutually exclusive. Setting both
    /// throws when the item containers are REALIZED rather than when the page is parsed, so the
    /// app launches perfectly and then dies the moment the user opens the page holding that
    /// control. Two of sixteen nav rows were fatal on click for exactly this reason.
    ///
    /// Both crashes shipped this way came from the SAME habit: adding one attribute across
    /// several sites without reading what each site already declared. Forty-one column widths in
    /// one case, four dropdowns in the other. A bulk edit is a per-site edit that only looks
    /// uniform, and neither the compiler nor an XML parse can see the conflict.
    ///
    /// Enumerated from the markup and checked as PAIRS, so a control added later is covered on
    /// arrival and a new mutually-exclusive pair only needs a row in the table below.
    /// </remarks>
    [Fact]
    public void NoControlDeclaresMutuallyExclusiveAttributes()
    {
        // Each row: the pair that cannot coexist, and why it matters.
        var forbidden = new[]
        {
            ("ItemTemplate", "DisplayMemberPath",
                "throws when item containers are realized - the page kills the app when opened"),
        };

        var offenders = new List<string>();
        var checkedControls = 0;

        foreach (var view in new[] { "MainWindow.xaml", "DictationOverlayWindow.xaml" })
        {
            var document = XDocument.Load(Path.Combine(
                FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", view));

            foreach (var element in document.Descendants())
            {
                checkedControls++;
                foreach (var (first, second, why) in forbidden)
                {
                    if (element.Attribute(first) is not null && element.Attribute(second) is not null)
                    {
                        var name = (string?)element.Attribute(XName.Get("Name", XamlNamespace))
                            ?? "(unnamed)";
                        offenders.Add(
                            $"{view}: <{element.Name.LocalName} x:Name=\"{name}\"> sets both "
                                + $"{first} and {second} - {why}");
                    }
                }
            }
        }

        Assert.True(
            checkedControls > 0,
            "No elements were examined, so this test proved nothing about either view.");

        Assert.True(
            offenders.Count == 0,
            "These controls declare attributes that cannot both be honoured:\n  "
                + string.Join("\n  ", offenders)
                + "\nPick one. To keep an ItemTemplate, bind the property inside the template "
                + "({Binding DisplayName}) and drop DisplayMemberPath - binding the item itself "
                + "renders the type name for an object-backed list.");
    }

    private static XDocument LoadMainWindow() =>
        XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Production",
            "EnviousWispr.App",
            "MainWindow.xaml"));

    private static XElement FindNamedElement(XDocument document, string name) =>
        document.Descendants().Single(element =>
            string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                name,
                StringComparison.Ordinal));

    /// <summary>
    /// True when the path sits under a build-output directory.
    /// </summary>
    /// <remarks>
    /// The compiler copies every Page-compiled XAML, <c>Theme/</c> included, into
    /// <c>obj/</c> and <c>bin/</c>. Those copies are the token dictionary itself, so they are
    /// full of the literal colours this check exists to forbid everywhere else. They are also
    /// invisible on a machine that has never built, which is why this was not caught until the
    /// suite ran on Windows.
    /// </remarks>
    private static bool IsBuildOutput(string appDirectory, string path)
    {
        var relative = Path.GetRelativePath(appDirectory, Path.GetFullPath(path));
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, XElement> LoadThemeDictionaries(
        string repositoryRoot,
        string fileName = "DesignTokens.xaml")
    {
        var tokenPath = Path.Combine(
            repositoryRoot,
            "src",
            "Production",
            "EnviousWispr.App",
            "Theme",
            fileName);
        Assert.True(File.Exists(tokenPath), $"{fileName} was not found at '{tokenPath}'.");

        var document = XDocument.Load(tokenPath);
        var container = document.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
        Assert.NotNull(container);
        return container.Elements()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .ToDictionary(
                element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)) ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static Dictionary<string, DynamicColor> ParseSwiftTokens(string source)
    {
        var result = new Dictionary<string, DynamicColor>(StringComparer.Ordinal);
        foreach (Match match in SwiftDynamicColorRegex().Matches(source))
        {
            var name = match.Groups["name"].Value;
            if (!result.TryAdd(
                    name,
                    new DynamicColor(
                        ParseSwiftComponents(name, "light", match.Groups["light"].Value),
                        ParseSwiftComponents(name, "dark", match.Groups["dark"].Value))))
            {
                throw new InvalidOperationException($"Swift token '{name}' is declared more than once.");
            }
        }

        Assert.True(result.Count > 0, "No stDynamic color declarations were parsed from the Swift token file.");
        return result;
    }

    private static Rgba ParseSwiftComponents(string tokenName, string themeName, string value)
    {
        var components = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 4 ||
            !components.All(component => double.TryParse(
                component,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out _)))
        {
            throw new InvalidOperationException(
                $"Swift token '{tokenName}' has an invalid {themeName} RGBA tuple: '{value}'.");
        }

        var parsed = components
            .Select(component => double.Parse(component, CultureInfo.InvariantCulture))
            .ToArray();
        return new Rgba(parsed[0], parsed[1], parsed[2], parsed[3]);
    }

    private static void AssertColorMatches(
        Dictionary<string, XElement> themeDictionaries,
        string themeName,
        string brandName,
        string expectedHex)
    {
        if (!themeDictionaries.TryGetValue(themeName, out var theme))
        {
            throw new InvalidOperationException($"Theme dictionary '{themeName}' is missing.");
        }

        var resourceKey = brandName + "Color";
        var element = theme.Elements().SingleOrDefault(candidate =>
            candidate.Name.LocalName == "Color" &&
            string.Equals(
                (string?)candidate.Attribute(XName.Get("Key", XamlNamespace)),
                resourceKey,
                StringComparison.Ordinal));
        if (element is null)
        {
            throw new InvalidOperationException($"Color resource '{resourceKey}' is missing from theme '{themeName}'.");
        }

        var expected = ParseHexColor(resourceKey, $"expected {themeName} table", expectedHex);
        AssertChannelsMatch(
            themeName,
            resourceKey,
            expected,
            ParseHexColor(resourceKey, themeName, element.Value.Trim()),
            element.Value.Trim());

        var brushKey = brandName + "Brush";
        var brush = theme.Elements().SingleOrDefault(candidate =>
            candidate.Name.LocalName == "SolidColorBrush" &&
            string.Equals(
                (string?)candidate.Attribute(XName.Get("Key", XamlNamespace)),
                brushKey,
                StringComparison.Ordinal));
        if (brush is null)
        {
            throw new InvalidOperationException($"Brush resource '{brushKey}' is missing from theme '{themeName}'.");
        }

        AssertChannelsMatch(
            themeName,
            brushKey,
            expected,
            ParseHexColor(brushKey, themeName, (string?)brush.Attribute("Color") ?? string.Empty),
            (string?)brush.Attribute("Color") ?? string.Empty);
    }

    private static void AssertChannelsMatch(
        string scope,
        string resourceKey,
        Rgba expected,
        Rgba actual,
        string actualDescription)
    {
        var tolerance = 1d / 255d;
        var channels = new[]
        {
            (Name: "red", Expected: expected.Red, Actual: actual.Red),
            (Name: "green", Expected: expected.Green, Actual: actual.Green),
            (Name: "blue", Expected: expected.Blue, Actual: actual.Blue),
            (Name: "alpha", Expected: expected.Alpha, Actual: actual.Alpha),
        };
        foreach (var channel in channels)
        {
            Assert.True(
                Math.Abs(channel.Expected - channel.Actual) <= tolerance + double.Epsilon,
                $"{scope}/{resourceKey} {channel.Name} differs from the expected table. " +
                $"Expected {channel.Expected.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                $"found {channel.Actual.ToString("0.###", CultureInfo.InvariantCulture)} ({actualDescription}).");
        }
    }

    private static Rgba ParseHexColor(string resourceKey, string sourceName, string value)
    {
        if (!value.StartsWith('#') || value.Length is not (7 or 9))
        {
            throw new InvalidOperationException(
                $"{sourceName}/{resourceKey} must be a #RRGGBB or #AARRGGBB literal, found '{value}'.");
        }

        var offset = value.Length == 9 ? 3 : 1;
        var alpha = value.Length == 9 ? ParseByte(value, 1) : byte.MaxValue;
        return new Rgba(
            ParseByte(value, offset) / 255d,
            ParseByte(value, offset + 2) / 255d,
            ParseByte(value, offset + 4) / 255d,
            alpha / 255d);
    }

    private static byte ParseByte(string value, int offset)
    {
        if (!byte.TryParse(
                value.AsSpan(offset, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var component))
        {
            throw new InvalidOperationException($"Invalid XAML color literal '{value}'.");
        }

        return component;
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

        throw new InvalidOperationException(
            $"Could not locate the repository root by walking up from test assembly directory '{AppContext.BaseDirectory}'. " +
            "Expected to find EnviousWispr.Windows.slnx.");
    }

    internal static string GetMacSnapshotPath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "macos-source",
            "Sources",
            "EnviousWisprAppKit",
            "Views",
            "Settings",
            "SettingsDesignTokens.swift");

    internal static string GetMacPillSnapshotPath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "macos-source",
            "Sources",
            "EnviousWisprAppKit",
            "App",
            "PreviewPillPalette.swift");

    private static string DescribeOffender(string repositoryRoot, string path, int line, string value) =>
        $"{Path.GetRelativePath(repositoryRoot, path)}:{line}: {value}";

    [GeneratedRegex(
        @"(?<![0-9A-Fa-f])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(
        // `Color` belongs here and was missing: <SolidColorBrush Color="Transparent" /> and
        // <Color>Red</Color>-style attribute forms are literal colours by any reading, and a
        // detector that lists five sibling attribute names but not the most obvious one is
        // exactly the shape of gap that reads as covered.
        @"\b(?:Brush|Background|Foreground|Fill|Stroke|BorderBrush|Color)\s*=\s*[""'](?<color>[A-Za-z]+)[""']",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamedColorAttributeRegex();

    [GeneratedRegex(
        @"static\s+let\s+(?<name>\w+)\s*=\s*(?:Color\.)?stDynamic\(\s*lightRGB:\s*\((?<light>[^)]*)\),\s*darkRGB:\s*\((?<dark>[^)]*)\)\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SwiftDynamicColorRegex();

    [GeneratedRegex(
        @"^\{ThemeResource\s+(?<key>[^}]+)\}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ThemeResourceRegex();

    [GeneratedRegex(
        @"^\{StaticResource\s+(?<key>[^}]+)\}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StaticResourceRegex();

    [GeneratedRegex(
        @"Pill(?:ModeQuiet|Notice|Live|Dimmed)TextStyle",
        RegexOptions.CultureInvariant)]
    private static partial Regex CodeBehindPillStyleRegex();

    private readonly record struct DynamicColor(Rgba Light, Rgba Dark);

    private readonly record struct ThemePair(string Light, string Dark);

    private readonly record struct Rgba(double Red, double Green, double Blue, double Alpha);
}
