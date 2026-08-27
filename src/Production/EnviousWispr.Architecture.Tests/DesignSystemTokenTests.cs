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
        foreach (var name in new[]
                 {
                     "EngineComboBox",
                     "PolishProviderComboBox",
                     "ThemeComboBox",
                     "OverlayPositionComboBox",
                 })
        {
            var element = FindNamedElement(document, name);
            Assert.Equal("RadioButtons", element.Name.LocalName);
        }

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
