using EnviousWispr.Core.Presentation;
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
public sealed class MacSectionSnapshotFactAttribute : FactAttribute
{
    public MacSectionSnapshotFactAttribute()
    {
        var swiftPath = DesignSystemTokenTests.GetMacSectionSnapshotPath();
        if (!File.Exists(swiftPath))
        {
            Skip = $"macOS section snapshot not found at '{swiftPath}'; the parity check did not run.";
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
        // Dark raised from #7A7290, which measured 3.70:1 on the card against a 4.5:1 floor.
        // TextContrastTests owns the reason and asserts the arithmetic.
        ["BrandTextTertiary"] = new("#6B5E86", "#9289AD"),
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

        // THE SEVERITY SET IS THE UNIT, not the severity somebody noticed. Before these existed the
        // pill drew one surface, one border and one ink for every outcome, so an error and a
        // success were the same capsule differing only by a small glyph - the same hole the app's
        // notification severities had, one surface over. Every severity the pill can show is listed
        // here, and a new one that forgets its pair goes red rather than rendering as the neutral
        // pill and looking deliberate.
        //
        // Washes are the base colour at low alpha, matching the design system's soft-tint rule:
        // #1A in Light, #24 in Dark. Distress carries no ink of its own on purpose - it is the
        // interruption look and shares the error red, with a deeper wash and a pulse carrying the
        // difference. That is a chosen reuse, stated here, rather than a token quietly falling back
        // to a neighbour's value.
        ["PillSuccessInk"] = new("#FF00A366", "#FF5CC99A"),
        ["PillSuccessWash"] = new("#1A00A366", "#245CC99A"),
        ["PillWarningInk"] = new("#FFCC7000", "#FFE6B766"),
        ["PillWarningWash"] = new("#1ACC7000", "#24E6B766"),
        ["PillErrorInk"] = new("#FFC0392B", "#FFEF7C89"),
        ["PillErrorWash"] = new("#1AC0392B", "#24EF7C89"),
        ["PillAdvisoryInk"] = new("#FF7C3AED", "#FFA78BFA"),
        ["PillAdvisoryWash"] = new("#1A7C3AED", "#24A78BFA"),
        ["PillDistressWash"] = new("#3DC0392B", "#4AEF7C89"),
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

    /// <summary>
    /// Tokens that deliberately differ from macOS, each with the reason it is allowed to.
    /// </summary>
    /// <remarks>
    /// PARITY IS ABOUT WHAT THE PRODUCT DOES, NOT ABOUT COPYING A DEFECT. This table exists so a
    /// divergence has to be written down and justified rather than achieved by deleting a row from
    /// the mapping above, which is what makes a parity check quietly stop covering something.
    ///
    /// Anything not listed here must still match macOS exactly, and an entry that stops being
    /// needed - because macOS moved too - should be deleted, which turns this back into a normal
    /// parity row. Keep it short: a long list means the two apps have drifted, and this file should
    /// be the place that says so.
    /// </remarks>
    private static readonly Dictionary<string, string> AllowedDeviationsFromMac = new(StringComparer.Ordinal)
    {
        ["BrandTextTertiary"] = "Dark only. The shared value #7A7290 measures 3.70:1 on the card and "
            + "4.16:1 on the page, below the 4.5:1 WCAG AA floor for normal-size text, and it is the "
            + "colour every helper line in the window is painted in. Windows uses #9289AD, which "
            + "clears every dark surface. The macOS app has the same failing value and should be "
            + "raised too; until it is, matching it would mean shipping text that is hard to read in "
            + "order to keep a table green. Ref: dark-theme contrast audit, 2026-08-27.",
    };

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

    /// <summary>
    /// Every allowed deviation from macOS is real, named, and still needed.
    /// </summary>
    /// <remarks>
    /// A LIST OF EXCEPTIONS IS A PLACE TO HIDE THINGS UNLESS SOMETHING WATCHES IT. Two ways it rots
    /// and both are silent: a name that no longer maps to anything, so the entry excuses nothing and
    /// reads as though it does; and an entry whose values have since converged, which keeps a real
    /// parity row switched off forever after the reason for it has gone.
    ///
    /// The second is the one that matters. Nothing else would ever turn that row back on.
    /// </remarks>
    [MacSnapshotFact]
    public void EveryAllowedDeviationIsRealAndStillNeeded()
    {
        var swiftTokens = ParseSwiftTokens(File.ReadAllText(GetMacSnapshotPath()));

        foreach (var (brandName, reason) in AllowedDeviationsFromMac)
        {
            Assert.True(
                reason.Length >= 60,
                $"{brandName} is excused from macOS parity without a reason worth reading.");

            var mapping = SwiftTokenMap.FirstOrDefault(entry => entry.BrandName == brandName);
            Assert.True(
                mapping.BrandName is not null,
                $"{brandName} is listed as a deviation but is not a mapped token. Delete the entry.");

            Assert.True(
                ExpectedTokenColors.TryGetValue(brandName, out var expected),
                $"{brandName} is listed as a deviation but is not in the expected table.");
            Assert.True(
                swiftTokens.TryGetValue(mapping.SwiftName, out var actual),
                $"{brandName} maps to {mapping.SwiftName}, which is not in the macOS snapshot.");

            var lightDiffers = !SameColor(expected!.Light, actual!.Light);
            var darkDiffers = !SameColor(expected.Dark, actual.Dark);
            Assert.True(
                lightDiffers || darkDiffers,
                $"{brandName} now matches macOS on both themes, so the deviation is spent. Delete "
                    + "the entry and let the parity check cover it again.");
        }
    }

    /// <summary>
    /// Whether two colours agree, by the same rule the parity assertion uses.
    /// </summary>
    /// <remarks>
    /// THE TOLERANCE IS THE WHOLE POINT AND THE FIRST VERSION OF THIS USED `==`. The macOS values
    /// are parsed from Swift floats and the Windows ones from hex, so two colours that are the same
    /// colour are never bit-identical as doubles. Exact equality therefore reported EVERY token as
    /// differing, which made the stale-deviation branch unreachable: the guard could not fire, and
    /// it passed on first run looking exactly like a guard that had nothing to report.
    ///
    /// One channel step, matching AssertChannelsMatch, because a rule that disagrees with the
    /// assertion it protects is a second answer to the same question.
    /// </remarks>
    private static bool SameColor(string expectedHex, Rgba actual)
    {
        var expected = ParseHexColor("deviation check", "expected table", expectedHex);
        const double tolerance = 1d / 255d;
        return Math.Abs(expected.Red - actual.Red) <= tolerance + double.Epsilon &&
            Math.Abs(expected.Green - actual.Green) <= tolerance + double.Epsilon &&
            Math.Abs(expected.Blue - actual.Blue) <= tolerance + double.Epsilon &&
            Math.Abs(expected.Alpha - actual.Alpha) <= tolerance + double.Epsilon;
    }

    /// <summary>
    /// The primary action is never inside something that scrolls.
    /// </summary>
    /// <remarks>
    /// A BUTTON INSIDE A SCROLL AREA IS ONLY VISIBLE IF THE PAGE HAPPENS TO BE SHORT ENOUGH, and
    /// "happens to be" is not a property anybody maintains. One settings page was already 2.6% too
    /// tall, which presented Save as a twelve-pixel sliver until the user scrolled - reachable, and
    /// invisible as an action.
    ///
    /// TRIMMING THAT PAGE WOULD HAVE BEEN THE INSTANCE FIX and it would have looked complete. The
    /// next page to grow past the viewport rediscovers it, silently, and nothing connects the new
    /// symptom to the old cause. Moving the button out of every scrolling container is the version
    /// that cannot come back.
    /// </remarks>
    [Fact]
    public void ThePrimaryActionIsNotInsideAnythingThatScrolls()
    {
        var document = LoadMainWindow();
        var button = FindNamedElement(document, "SaveSettingsButton");

        var scrollers = button.Ancestors()
            .Where(element => element.Name.LocalName == "ScrollViewer")
            .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace)) ?? "unnamed")
            .ToArray();

        Assert.True(
            scrollers.Length == 0,
            "Save settings sits inside a scrolling container, so it is only visible when the page "
                + "is short enough: " + string.Join(" > ", scrollers));

        // Control. The lookup must be finding a real element inside a real tree - otherwise an
        // element with no ancestors at all would report the same clean result.
        Assert.NotEmpty(button.Ancestors());
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "ScrollViewer");
    }

    /// <summary>
    /// Every settings section macOS offers has a navigation row on Windows.
    /// </summary>
    /// <remarks>
    /// THE PARITY CLAIM, MADE MECHANICAL. "Nothing is missing" was a list somebody wrote from memory
    /// and then checked against their own memory, which is the weakest possible form of an absence
    /// claim - and absence claims are the ones that fail silently, because a feature nobody
    /// remembers is exactly the feature nobody looks for.
    ///
    /// macOS declares its sections in one enum with one user-facing label each. That is a CLOSED
    /// SET from the producing code rather than a description of one, so it can be compared rather
    /// than believed. Windows may have MORE rows - it does, five of them - and that is parity in
    /// the direction that matters.
    ///
    /// APOSTROPHES ARE NORMALISED, and that is not cosmetic. macOS writes "What's New" with a
    /// straight quote and Windows with a typographic one; comparing raw would report a missing
    /// section that is plainly there, which is the kind of false alarm that gets a parity gate
    /// deleted rather than fixed.
    ///
    /// LIMIT, STATED: this compares the SETTINGS SURFACE, which is what the macOS snapshot in this
    /// repo contains. It is not a whole-product audit and must not be read as one.
    /// </remarks>
    [MacSectionSnapshotFact]
    public void EveryMacSettingsSectionHasAWindowsHome()
    {
        var swift = File.ReadAllText(GetMacSectionSnapshotPath());
        var markup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        // SLICE TO THE LABEL PROPERTY FIRST. The enum has three switch statements over the same
        // cases - label, icon and description - all written "case .x: return "y"". Matching the
        // whole file therefore compared SF Symbol names and marketing sentences against navigation
        // rows and reported thirty missing sections, none of which was real.
        //
        // The pattern was right and the SCOPE was wrong, which is the failure that produces a
        // confident, complete-looking, entirely false answer. It went red on its first run only
        // because it was wrong in the loud direction; scoped one switch too NARROW it would have
        // passed while checking almost nothing.
        var labelBlock = LabelProperty().Match(swift);
        Assert.True(labelBlock.Success, "The macOS snapshot has no label property to read.");

        var macLabels = Regex.Matches(labelBlock.Value, @"case \.\w+: return ""([^""]+)""")
            .Select(match => Normalise(match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            macLabels.Length >= 14,
            $"Expected the macOS section labels, found {macLabels.Length}.");

        var windowsRows = Regex.Matches(markup, @"<NavigationViewItem [^>]*Content=""([^""]+)""")
            .Select(match => Normalise(match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = macLabels.Where(label => !windowsRows.Contains(label)).ToArray();

        Assert.True(
            missing.Length == 0,
            "macOS offers these and Windows has no navigation row for them: "
                + string.Join(", ", missing));

        // Control. The comparison must be finding real rows on both sides, or "nothing missing"
        // would be true of two empty sets.
        Assert.Contains("Transcription", windowsRows);
        Assert.Contains("Transcription", macLabels);
    }

    /// <summary>The label property alone, not the icon or description switches beside it.</summary>
    [GeneratedRegex(@"var label: String \{.*?\n  \}", RegexOptions.Singleline)]
    private static partial Regex LabelProperty();

    /// <summary>Folds the difference between a straight and a typographic apostrophe.</summary>
    private static string Normalise(string label) =>
        label.Replace('\u2019', '\'').Trim();

    internal static string GetMacSectionSnapshotPath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "macos-source",
            "Sources",
            "EnviousWisprAppKit",
            "Views",
            "Settings",
            "SettingsSection.swift");

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

            if (AllowedDeviationsFromMac.ContainsKey(mapping.BrandName))
            {
                continue;
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

    /// <summary>
    /// Every state the pill can be in has a look, and every look it names exists.
    /// </summary>
    /// <remarks>
    /// THE SEVERITY SET IS THE UNIT, NOT THE SEVERITY SOMEBODY NOTICED. The app's in-window
    /// notifications had exactly this hole: two severities had no soft tint of their own, so they
    /// fell through to the card colour and an error arrived with no colour behind it. Nobody chose
    /// that - it was what a missing token renders as, and a missing token renders as something
    /// plausible.
    ///
    /// The pill had the same hole one surface over: one capsule for every outcome. So this checks
    /// the whole set from both ends. Every member of the state enum must be answered by name in
    /// the overlay's severity switch, including the quiet ones, and every style that switch names
    /// must exist in the pill theme.
    ///
    /// IT ENUMERATES FROM THE SOURCE AT BOTH ENDS. A roster kept here would stop covering a state
    /// the first time somebody adds one without thinking of this file, which is the same failure
    /// the code-behind style gate had until it was widened.
    /// </remarks>
    [Fact]
    public void EveryPillStateHasASeverityLookAndEveryLookExists()
    {
        var repositoryRoot = FindRepositoryRoot();
        var overlay = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Production", "EnviousWispr.App",
            "DictationOverlayWindow.xaml.cs"));

        // THE REAL ENUM, NOT ITS FORMATTING. The first draft read the members out of the source
        // with a regex anchored on four spaces, a word and a comma, so writing `Advisory = 4,`
        // would hide a member from the check while the other seven kept the count control
        // satisfied. Enum.GetNames cannot be styled around.
        var states = Enum.GetNames<DictationOverlayState>();
        Assert.True(states.Length >= 6, $"Expected the pill's states, found {states.Length}.");

        var severityBody = SliceBetween(
            overlay,
            "var (icon, edge, wash) = state switch",
            "\n        };",
            "the overlay's severity switch");

        var unanswered = states
            .Where(state => !severityBody.Contains(
                "DictationOverlayState." + state, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            unanswered.Length == 0,
            "These pill states are not answered by name in the severity switch, so they render as "
                + "the neutral capsule and look deliberate: " + string.Join(", ", unanswered));

        var declaredStyleKeys = XDocument
            .Load(Path.Combine(
                repositoryRoot, "src", "Production", "EnviousWispr.App", "Theme", "PillTokens.xaml"))
            .Descendants()
            .Select(element => (string?)element.Attribute(XName.Get("Key", XamlNamespace)))
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);
        var named = PillStyleNameRegex().Matches(severityBody)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(named.Length >= 15, $"Expected the severity styles, found {named.Length}.");
        var missing = named.Where(key => !declaredStyleKeys.Contains(key)).ToArray();
        Assert.True(
            missing.Length == 0,
            "The pill asks for styles that the theme does not declare, which throws when the pill "
                + "is first shown rather than when the app is built: " + string.Join(", ", missing));
    }

    /// <summary>The text between two markers, or a named failure if either is gone.</summary>
    /// <remarks>
    /// A MISSING MARKER USED TO THROW AN INDEX EXCEPTION, which is a failure that names the test
    /// rather than the thing that moved. The gate still refuses, which is the safe direction, but
    /// whoever renamed the switch reads a stack trace instead of a sentence telling them what this
    /// was looking for.
    /// </remarks>
    private static string SliceBetween(string text, string opening, string closing, string what)
    {
        var start = text.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {what}: '{opening}' is no longer in the source.");
        var rest = text[start..];
        var end = rest.IndexOf(closing, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find the end of {what} after '{opening}'.");
        return rest[..end];
    }

    [GeneratedRegex(@"Pill\w+Style", RegexOptions.CultureInvariant)]
    private static partial Regex PillStyleNameRegex();

    /// <summary>Every action the pill can offer goes somewhere the user asked for.</summary>
    /// <remarks>
    /// A BUTTON THAT OPENS THE WRONG PAGE IS WORSE THAN NO BUTTON, because the user acted on it and
    /// now has to work out where they are. The intent-to-page switch has a default arm - an enum
    /// can hold a value nobody declared, and crashing mid-dictation over a settings page is not a
    /// trade worth making - so without this the default is where a forgotten action would land,
    /// silently and looking deliberate.
    ///
    /// It reads the real enum and the real page tags, both enumerated from source, so an action
    /// added next month is covered on arrival and a page renamed next month goes red here.
    /// </remarks>
    [Fact]
    public void EveryPillActionNamesAPageThatExists()
    {
        var repositoryRoot = FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var mapping = SliceBetween(
            window,
            "var tag = kind switch",
            "\n        };",
            "the pill action to page mapping");

        // CAPTURE THE NAME AND COMPARE IT, NEVER ASK WHETHER THE TEXT CONTAINS IT. A substring test
        // reports an action named OpenPolish as mapped, because OpenPolishSettings contains it -
        // green on exactly the edit the gate exists to refuse. Same correction the repository's own
        // rule about a lookahead after a quantifier makes: a question about a VALUE, not a position.
        var mapped = ActionKindRegex().Matches(mapping)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var unmapped = Enum.GetNames<PillActionKind>()
            .Where(name => !mapped.Contains(name))
            .ToArray();
        Assert.True(
            unmapped.Length == 0,
            "These pill actions are not named in the page mapping, so pressing their button lands "
                + "on the default page: " + string.Join(", ", unmapped));

        var declaredTags = XDocument
            .Load(Path.Combine(
                repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"))
            .Descendants()
            .Select(element => (string?)element.Attribute("Tag"))
            .Where(tag => tag is not null)
            .ToHashSet(StringComparer.Ordinal);
        // The whole literal each arm hands back, not a shape a tag happens to start with. A pattern
        // that matched a known prefix passed on "settings-ai-polish2", which is a page that does
        // not exist and a button that does nothing.
        var named = MappedPageTagRegex().Matches(mapping)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(named.Length >= 2, $"Expected the mapped page tags, found {named.Length}.");
        var missing = named.Where(tag => !declaredTags.Contains(tag)).ToArray();
        Assert.True(
            missing.Length == 0,
            "The pill sends users to pages the window does not have, so the button quietly does "
                + "nothing: " + string.Join(", ", missing));
    }

    /// <summary>A whole action name inside the mapping.</summary>
    [GeneratedRegex(@"PillActionKind\.([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ActionKindRegex();

    /// <summary>The whole page tag one switch arm hands back.</summary>
    [GeneratedRegex(@"=>\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex MappedPageTagRegex();

    /// <summary>Every flow that serves a dictation opens the scope for itself.</summary>
    /// <remarks>
    /// AN ASYNCLOCAL FLOWS DOWN A CALL TREE, NOT ACROSS EVENTS. A dictation spans separate
    /// invocations of the push-to-talk handler - one when the key goes down, another when it comes
    /// up - and a scope opened in the first is invisible to the second, because the second is a
    /// fresh flow off the message loop. The same is true of the watchdog, the streaming worker and
    /// the auto-stop watch, which the message loop starts on their own.
    ///
    /// So each flow opens its own, and this refuses one that forgets. The rule is decidable and
    /// needs no roster: a method handed a `DictationSessionId` is a flow that serves a dictation,
    /// and every one of them must begin a scope. Methods that merely START such a flow are
    /// exempt by the same rule - they pass the id on rather than logging under it.
    ///
    /// WHAT IT CANNOT SEE, ENUMERATED, BECAUSE AN UNSTATED LIMIT READS AS COVERAGE. It matches
    /// source text, so it does not see: a flow that logs through a HELPER rather than calling the
    /// logger directly; a method that serves a dictation without being handed its id, which is
    /// every Windows callback - the device-change and lifecycle handlers are scoped by hand and
    /// nothing here would notice if they stopped being; a scope opened conditionally or with the
    /// wrong id; and a `_logger.Write(` appearing first inside a comment or a string, which would
    /// false-alarm.
    ///
    /// It closes the shape the flows actually take. The callbacks are the known hole, and they are
    /// few enough to be read.
    /// </remarks>
    [Fact]
    public void EveryFlowThatServesADictationOpensItsScope()
    {
        var shell = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "App.xaml.cs"));

        var unscoped = new List<string>();
        var flows = 0;
        foreach (Match match in DictationFlowRegex().Matches(shell))
        {
            flows++;
            var body = shell[match.Index..];
            var stop = body.IndexOf("\n    private ", 20, StringComparison.Ordinal);
            if (stop > 0)
            {
                body = body[..stop];
            }

            // THE SCOPE MUST BE OPENED BEFORE THE FLOW LOGS ANYTHING, which is the property that
            // actually matters and needs no window at all. Two earlier drafts guessed at one - six
            // hundred characters, then eight lines - and a reviewer pointed out that a longer
            // signature, a blank line or a block comment moves a correct scope past either. A
            // window is a proxy; this is the thing itself.
            //
            // A flow that never logs is fine either way, and is required to open one anyway,
            // because the next line added to it will be a log line.
            var firstWrite = body.IndexOf("_logger.Write(", StringComparison.Ordinal);
            var opening = firstWrite > 0 ? body[..firstWrite] : body;
            if (!opening.Contains("DictationScope.Begin(", StringComparison.Ordinal))
            {
                unscoped.Add(match.Groups[1].Value);
            }
        }

        // THE FLOOR IS TODAY'S COUNT, so a flow deleted or renamed out of the pattern is noticed.
        // A lower floor lets the set shrink silently, which is how a gate stops covering the thing
        // it was written for while still reporting green.
        Assert.True(flows >= 5, $"Expected the dictation flows, found {flows}.");
        Assert.True(
            unscoped.Count == 0,
            "These methods are handed a dictation and never open its scope, so every line they "
                + "write is joined to nothing: " + string.Join(", ", unscoped));
    }

    /// <summary>A method that takes a dictation and does work under it.</summary>
    /// <remarks>
    /// `async` is the discriminator between a flow and a starter. A method that merely kicks one
    /// off is synchronous here and passes the id on; the ones that log under it are the ones that
    /// await.
    /// </remarks>
    [GeneratedRegex(@"private async Task(?:<[^>]+>)? (\w+)\([^)]*DictationSessionId ",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DictationFlowRegex();

    /// <summary>The journey harness reports its own failures instead of crashing.</summary>
    /// <remarks>
    /// A FAILING TEST USED TO LOOK EXACTLY LIKE A FAILING MACHINE. Every expectation in the harness
    /// threw a stock exception type and nothing caught it, so the process died on an unhandled
    /// exception - which Windows records in the event log the same way it records an application
    /// fault. On 2026-08-28 eleven such entries were read as evidence that the development machine
    /// was unstable, alongside real hypervisor faults, while somebody worked out whether the
    /// hardware was failing. They were failing tests.
    ///
    /// FOUR REVIEW ROUNDS EACH NAMED ONE MORE THROW THAT STILL CRASHED - a helper below the journey
    /// block, the cleanup, the preflight, then the file checks. The answer was not a fifth patch: it
    /// was that EVERY deliberate throw in this harness is a reason it can name, and only the runtime
    /// raises the rest. So they all carry one type, the reporter wraps the whole program, and this
    /// refuses the next stock throw before it can put another crash in the event log.
    /// </remarks>
    [Fact]
    public void TheJourneyHarnessNeverThrowsAStockExceptionType()
    {
        var harness = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "tools", "app-journey-uat", "Program.cs"));

        var stock = StockThrowRegex().Matches(harness)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            harness.Contains("throw new JourneyExpectationException(", StringComparison.Ordinal),
            "The harness throws no JourneyExpectationException at all, so this gate is checking a "
                + "file that no longer works the way it describes.");
        Assert.True(
            stock.Length == 0,
            "These stock exception types are thrown deliberately in the journey harness, and an "
                + "uncaught one ends the run as an application crash in the Windows event log: "
                + string.Join(", ", stock));
    }

    /// <summary>A deliberately thrown exception type that is not the harness's own.</summary>
    [GeneratedRegex(@"throw new (?!JourneyExpectationException)(\w*Exception)\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex StockThrowRegex();

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
        // THIS LINE HAS NOW FAILED A CORRECT FIX TWICE, and the second time is the signal.
        //
        // It first asserted Checked sets CardBorder.BorderThickness to 2 - which was the DEFECT: a
        // border's thickness is layout, so a selected card measured 125 tall where its siblings
        // were 123 and choosing an option shifted everything below it by two pixels. Rewritten to
        // read a Setter on the new overlay, it failed AGAIN the moment that setter became a
        // Storyboard so the ring could fade in.
        //
        // Both times the app was right and the gate was wrong, because it pinned the MECHANISM -
        // which setter holds which value - rather than the OUTCOME. A mechanism-pinning gate
        // accuses a healthy app every time the mechanism legitimately changes, and there is no
        // number of rewrites that fixes that; only changing what it asserts does.
        //
        // It now asserts the OUTCOME: selecting a card turns the ring on, by whatever means. The
        // two properties that actually matter are covered elsewhere and by construction -
        // SelectingAChoiceCardChangesNoLayoutProperty forbids the whole layout class.
        var checkedState = ReadVisualState(style, "Checked");
        Assert.Contains("SelectionBorder", checkedState, StringComparison.Ordinal);
        Assert.Contains("Opacity", checkedState, StringComparison.Ordinal);
        Assert.Contains(style.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                "CheckBadge",
                StringComparison.Ordinal) &&
            string.Equals((string?)element.Attribute("Background"), "{ThemeResource BrandAccentSolidBrush}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole of a visual state as text, so an assertion about it survives the state being
    /// expressed as setters, as a storyboard, or as both.
    /// </summary>
    private static string ReadVisualState(XElement style, string stateName) =>
        style.Descendants()
            .Where(element => element.Name.LocalName == "VisualState")
            .Where(element => string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                stateName,
                StringComparison.Ordinal))
            .Select(element => element.ToString())
            .DefaultIfEmpty(string.Empty)
            .First();

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

    /// <summary>
    /// Every live region the app declares is actually announced.
    /// </summary>
    /// <remarks>
    /// MARKING A LIVE REGION IS NOT ANNOUNCING IT, AND THE APP SHIPPED SEVEN THAT SAID NOTHING.
    /// AutomationProperties.LiveSetting tells a screen reader how urgently to read a region when it
    /// changes; it does NOT tell it that anything changed. WinUI raises no event of its own, so an
    /// app has to raise LiveRegionChanged itself. Without that, the markup reads as accessible, every
    /// gate passes, and a person using Narrator is told nothing at all - the failure is silent in the
    /// most literal sense available.
    ///
    /// THE ASSIGNMENT IS WHAT IS CHECKED, NOT THE CALL. Requiring a raise SOMEWHERE in the file is
    /// satisfied by one call for seven regions. What has to hold is that no live region's content is
    /// set directly, because a direct assignment is exactly the change that goes unannounced. The
    /// announcing setters are the only way in, and this refuses any other.
    /// </remarks>
    [Fact]
    public void EveryLiveRegionIsAnnouncedWhenItChanges()
    {
        var repositoryRoot = FindRepositoryRoot();
        var app = Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App");
        var offenders = new List<string>();

        foreach (var markup in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            // BUILD OUTPUT IS NOT SOURCE. bin and obj carry copies of every .xaml with no
            // code-behind beside them, so each one was reported as a file declaring live regions and
            // owning no way to announce them - three phantom offenders for two real files.
            if (markup.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || markup.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var document = XDocument.Load(markup);
            var regions = document.Descendants()
                // THE WHOLE ATTACHED-PROPERTY NAME. In XAML the attribute's local name is the entire
                // string "AutomationProperties.LiveSetting"; an equality test against "LiveSetting"
                // matches nothing, and this gate found no live regions at all, passed, and would
                // have gone on passing forever.
                .Where(element => element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "AutomationProperties.LiveSetting"))
                .Select(element => (string?)element.Attribute(XName.Get("Name", XamlNamespace)))
                .OfType<string>()
                .ToList();
            if (regions.Count == 0)
            {
                continue;
            }

            var codeBehind = markup + ".cs";
            if (!File.Exists(codeBehind))
            {
                offenders.Add($"{Path.GetFileName(markup)} declares live regions and has no code-behind to announce them");
                continue;
            }

            var text = File.ReadAllText(codeBehind);
            foreach (var region in regions)
            {
                // A DIRECT WRITE IS ONLY A DEFECT WHEN NOTHING RAISES FOR THAT REGION. The pill has
                // to set its text and then announce AFTER the window is shown, because raising while
                // it is still hidden announces something nobody can see - so text-then-raise is
                // correct there and an atomic setter cannot express it. What must hold is that the
                // file which writes the region is also the file that announces it.
                var announced = Regex.IsMatch(
                    text,
                    @"(FromElement|CreatePeerForElement)\(\s*" + Regex.Escape(region) + @"\s*\)");
                if (announced)
                {
                    continue;
                }

                foreach (Match direct in Regex.Matches(
                    text, @"\b" + Regex.Escape(region) + @"\.(Text|Visibility)\s*="))
                {
                    offenders.Add($"{Path.GetFileName(codeBehind)}: {direct.Value.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These live regions are written to directly, so the change is never announced and a "
                + "screen reader says nothing. Assign through SetLiveText or SetLiveVisibility, which "
                + "raise LiveRegionChanged with the text: " + string.Join(", ", offenders));
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
        // THE LOADING CARD FOLLOWS THE FLAG, HOWEVER THE ASSIGNMENT IS SPELLED. This asserted one
        // exact line, so routing the same assignment through the announcing setter - which is what
        // makes a screen reader say the card appeared - broke a gate whose property had not changed
        // at all. What matters is that the flag decides the card, and that both outcomes exist.
        var loadingAssignment = Regex.Match(
            codeBehind,
            @"HistoryLoadingState[^;]*_isHistoryLoading[^;]*;",
            RegexOptions.Singleline);
        Assert.True(
            loadingAssignment.Success,
            "Nothing in MainWindow.xaml.cs makes HistoryLoadingState follow _isHistoryLoading, so a "
                + "person cannot tell a history that is still loading from one that is empty.");
        Assert.Contains("Visibility.Visible", loadingAssignment.Value, StringComparison.Ordinal);
        Assert.Contains("Visibility.Collapsed", loadingAssignment.Value, StringComparison.Ordinal);
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

            // DESCENDANT, NOT CHILD. The property being checked is "the first thing on the page is
            // its header card", which says nothing about how many containers sit between the page
            // and its content. Requiring a direct child pinned the nesting instead, so wrapping
            // every page in a width-capped column broke this test without changing what a user sees
            // - a gate failing on the shape of a change rather than on its effect.
            var stack = page.Descendants().First(element => element.Name.LocalName == "StackPanel");
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

            // A single tab stop is only half of the Windows convention; the other half is arrow
            // movement WITHIN the group. Setting TabFocusNavigation alone made keyboard access
            // strictly worse than before: the group took one tab stop, and a plain ItemsControl
            // has no arrow navigation, so every unselected card became unreachable by keyboard
            // entirely. Measured on the running app - Tab entered the group and Down/Up/Right
            // moved nothing. Both properties, or neither.
            Assert.Equal("Once", (string?)list.Attribute("TabFocusNavigation"));

            // Arrow movement is supplied BY HAND and subscribed IN CODE, and the markup must NOT
            // carry a KeyDown attribute. Three mechanisms were tried and all three were verified
            // inert or partial on the running app: TabFocusNavigation alone gives a tab stop with
            // nothing to move within; XYFocusKeyboardNavigation governs directional/gamepad
            // navigation, not arrow keys in a radio group; and a KeyDown="" attribute subscribes
            // with handledEventsToo:false, so it stops firing the moment focus lands on a card,
            // because a focused RadioButton marks the arrow handled first. Since the handler's
            // last act is to focus the newly selected card, the attribute form defeats itself
            // after exactly one press.
            Assert.Null((string?)list.Attribute("KeyDown"));

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

    /// <summary>
    /// The Level Rail preview draws the same meter the recording pill actually draws.
    /// </summary>
    /// <remarks>
    /// The pill designs on the Appearance page are the only way a user sees what they are
    /// choosing before they choose it, so a preview that draws something else is not a cosmetic
    /// problem - it is the page lying about the product.
    ///
    /// It has been wrong twice. First a SOLID ROYAL BLUE bar sat directly beneath its own caption
    /// promising "a live rainbow meter". Then a smooth rainbow GRADIENT, which matched the caption
    /// but still not the pill: the real meter is twelve discrete bars of varying height, an audio
    /// level display rather than a progress bar. Both times the preview was internally plausible,
    /// which is exactly why neither was caught by looking at the page.
    ///
    /// Compared against the OVERLAY's own markup rather than a copy of its values kept here, so
    /// the two cannot drift: change the pill and this fails until the preview is changed with it.
    /// </remarks>
    [Fact]
    public void TheLevelRailPreviewMatchesTheRecordingPill()
    {
        var root = FindRepositoryRoot();
        var overlay = XDocument.Load(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml"));
        var window = LoadMainWindow();

        static (string Height, string Brush)[] BarsUnder(XElement container) => container
            .Elements()
            .Where(element => element.Name.LocalName == "Border")
            .Select(element => (
                Height: (string?)element.Attribute("Height") ?? string.Empty,
                Brush: (string?)element.Attribute("Background") ?? string.Empty))
            .ToArray();

        var realMeter = overlay.Descendants()
            .SingleOrDefault(element =>
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "LevelBars");
        Assert.True(
            realMeter is not null,
            "LevelBars was not found in DictationOverlayWindow.xaml. That element is this test's "
                + "oracle, so its absence means the test is comparing against nothing.");

        var real = BarsUnder(realMeter!);
        Assert.True(
            real.Length > 0,
            "The recording pill's level meter has no bars, so there is nothing to match.");

        // The preview lives inside the Level Rail card, which is identified by its own radio button.
        var previewCard = window.Descendants()
            .SingleOrDefault(element =>
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "LevelRailPillButton");
        Assert.True(previewCard is not null, "LevelRailPillButton was not found in MainWindow.xaml.");

        // The HORIZONTAL strip, matching how the real meter is declared. Taking the first panel
        // with any Border children instead picks the preview's outer pill frame, which is a single
        // Border and reports as a one-bar meter.
        var previewMeter = previewCard!.Descendants()
            .Where(element => element.Name.LocalName == "StackPanel"
                && (string?)element.Attribute("Orientation") == "Horizontal")
            .Select(BarsUnder)
            .FirstOrDefault(bars => bars.Length > 1);
        Assert.True(
            previewMeter is not null,
            "The Level Rail preview draws no bar meter at all. It has twice been drawn as a single "
                + "bar - once solid blue, once a gradient - while the real pill is a row of discrete "
                + "bars. A user choosing this design would not get what the picture showed.");

        Assert.True(
            previewMeter!.SequenceEqual(real),
            "The Level Rail preview does not match the recording pill.\n"
                + $"  pill    ({real.Length} bars): {string.Join(" ", real.Select(b => b.Height))}\n"
                + $"  preview ({previewMeter.Length} bars): {string.Join(" ", previewMeter.Select(b => b.Height))}\n"
                + "The preview is the only view of this design a user gets before choosing it.");
    }

    /// <summary>
    /// The arrow-key handler is subscribed so that it still fires once a card has focus.
    /// </summary>
    /// <remarks>
    /// The markup half of this claim lives in EveryChoiceListIsOneFullWidthColumn, which requires
    /// the absence of a KeyDown attribute. This is the other half: the subscription must exist in
    /// code AND pass handledEventsToo, because without it the handler is called exactly once per
    /// group and then never again. Split across two assertions because either half alone is
    /// satisfied by a build where the arrows do not work.
    /// </remarks>
    [Fact]
    public void ArrowNavigationIsSubscribedSoItStillFiresOnceACardHasFocus()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Production",
            "EnviousWispr.App",
            "MainWindow.xaml.cs"));

        Assert.Contains("new KeyEventHandler(ChoiceListKeyDown)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", codeBehind, StringComparison.Ordinal);

        var subscription = Regex.Match(
            codeBehind,
            @"AddHandler\(\s*UIElement\.KeyDownEvent,\s*new KeyEventHandler\(ChoiceListKeyDown\),\s*handledEventsToo:\s*true\s*\)",
            RegexOptions.Singleline);
        Assert.True(
            subscription.Success,
            "The arrow handler is not subscribed with handledEventsToo: true. Without it a focused "
                + "RadioButton marks the arrow handled first, the handler never sees it, and the "
                + "arrows die after one press - which is exactly what shipped.");
    }

    /// <summary>
    /// The notification bar occupies its own row rather than painting over the page.
    /// </summary>
    /// <remarks>
    /// It used to be a sibling of every page ScrollViewer in a single-cell Grid, floated above
    /// them with Canvas.ZIndex. Measured on the running app: with a validation message open on
    /// Your Words, the bar spanned y 122-198 and the page title y 152-212 - 46 of the title's 60px
    /// drawn over, so the heading rendered on the warning's amber background instead of its own
    /// card. The title's rectangle was IDENTICAL with the bar open and closed, which is what
    /// proves nothing was being displaced.
    ///
    /// It is a shared surface spanning the full content width, so ANY page whose header sits in
    /// that band does the same; Your Words is only where it was first triggered.
    ///
    /// An Auto row costs nothing when the bar is closed, so the fix does not shift the layout in
    /// the ordinary case.
    /// </remarks>
    [Fact]
    public void TheNotificationBarDoesNotPaintOverThePage()
    {
        var document = LoadMainWindow();
        var bar = document.Descendants().SingleOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "OperationInfoBar");
        Assert.True(bar is not null, "OperationInfoBar was not found in MainWindow.xaml.");

        var grid = bar!.Parent;
        Assert.True(grid is not null && grid.Name.LocalName == "Grid",
            "The notification bar is expected to sit directly in the content Grid.");

        var rows = grid!.Elements()
            .Where(child => child.Name.LocalName == "Grid.RowDefinitions")
            .SelectMany(definitions => definitions.Elements())
            .ToArray();
        Assert.True(
            rows.Length >= 2,
            "The content Grid has no rows, so the notification bar and the pages share one cell and "
                + "the bar draws over the page header.");

        Assert.Equal("0", (string?)bar.Attribute(XName.Get("Row", "http://schemas.microsoft.com/winfx/2006/xaml/presentation")) ?? (string?)bar.Attribute("Grid.Row"));

        // Every page must be in the row BELOW it. A page left in row 0 is overlapped again, and
        // the symptom - a heading rendered on the notification's background - is easy to miss.
        var pages = grid.Elements()
            .Where(child => child.Name.LocalName == "ScrollViewer")
            .ToArray();
        Assert.True(pages.Length > 0, "No pages were found in the content Grid.");
        foreach (var page in pages)
        {
            var name = (string?)page.Attribute(XName.Get("Name", XamlNamespace)) ?? "(unnamed)";
            Assert.True(
                (string?)page.Attribute("Grid.Row") == "1",
                $"Page '{name}' is not in the content row, so the notification bar overlaps it.");
        }

        Assert.Null((string?)bar.Attribute("Canvas.ZIndex"));
    }

    /// <summary>
    /// Every type bound to a list row says something a person would want read aloud.
    /// </summary>
    /// <remarks>
    /// A list row with no explicit automation name falls back to ToString on the bound item. A
    /// RECORD emits its type name and brace syntax; a plain CLASS emits its fully-qualified type
    /// name. Measured on the running app, a dictionary row announced "CustomWordEntry open brace
    /// SpokenForm equals ... close brace" before reaching any content.
    ///
    /// The defect only exists once a row is BOUND, which is why an accessibility audit of the same
    /// pages found nothing: the lists were empty. That is the general lesson - an empty list is not
    /// a list, and auditing one proves nothing about the other.
    /// </remarks>
    [Fact]
    public void EveryListRowTypeOverridesToString()
    {
        var root = FindRepositoryRoot();
        var sources = new (string Path, string Type)[]
        {
            (Path.Combine(root, "src", "Production", "EnviousWispr.Core", "Settings", "ReusableUserData.cs"), "CustomWordEntry"),
            (Path.Combine(root, "src", "Production", "EnviousWispr.Core", "Settings", "ReusableUserData.cs"), "SnippetEntry"),
            (Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"), "HistoryItemViewModel"),
        };

        foreach (var (path, type) in sources)
        {
            var text = File.ReadAllText(path);
            var declaration = Regex.Match(
                text,
                $@"(record|class)\s+{Regex.Escape(type)}\b(?<body>.*?)(?=\n(public|internal|private)\s+(sealed\s+)?(record|class)\s|\z)",
                RegexOptions.Singleline);
            Assert.True(
                declaration.Success,
                $"'{type}' was not found in {Path.GetFileName(path)}. This test's subject is gone, so "
                    + "its green means nothing - update it or delete it deliberately.");

            Assert.True(
                declaration.Groups["body"].Value.Contains("override string ToString()", StringComparison.Ordinal),
                $"'{type}' is bound to a list row and does not override ToString, so a screen reader "
                    + "announces its type name before any content the user cares about.");
        }
    }

    /// <summary>
    /// A keybind field captures the key you press; it is never a free-text box.
    /// </summary>
    /// <remarks>
    /// They were plain TextBoxes. A field labelled "Recording keybind" showing "F8" reads
    /// unmistakably as a capture control, so the likely user action is to click it and press a
    /// key. Measured on the running app: F9 did nothing at all, and Q produced "qF8" - the
    /// character inserted at a caret sitting at position 0. Silent in both directions, and the
    /// corrupted value still looks like a keybind.
    ///
    /// Both properties are required. IsReadOnly stops a keystroke being inserted as text; the
    /// handler supplies the value. Either alone leaves the field able to be corrupted or unable
    /// to be set.
    /// </remarks>
    [Fact]
    public void EveryKeybindFieldCapturesRatherThanAcceptsTypedText()
    {
        var document = LoadMainWindow();
        var fields = new[] { "HotkeyTextBox", "CancelHotkeyTextBox", "QuickAddHotkeyTextBox" };

        foreach (var name in fields)
        {
            var box = FindNamedElement(document, name);
            Assert.Equal("TextBox", box.Name.LocalName);
            Assert.True(
                (string?)box.Attribute("IsReadOnly") == "True",
                $"'{name}' is editable, so a typed character is inserted into the gesture - which "
                    + "produced values like \"qF8\" on the running app.");
            Assert.Equal("HotkeyBoxKeyDown", (string?)box.Attribute("KeyDown"));
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

    /// <summary>Any pill style the overlay's code-behind names.</summary>
    /// <remarks>
    /// A GATE CARRYING ITS OWN ROSTER STOPS COVERING THE THING IT WAS WRITTEN FOR the first time
    /// somebody adds a style and does not think to update the list. This used to spell out four
    /// text styles by name, so the six severity style families added later were invisible to it.
    /// It now enumerates from the code-behind, which is the same rule the layout gates already
    /// follow: read the document, never a list kept beside the check.
    ///
    /// CONSEQUENCE FOR THE CALLER, and it is deliberate: a style name built by interpolation is
    /// not matched, because the source text is not the name. That is why the overlay spells every
    /// style out in full rather than composing one from a severity word.
    /// </remarks>
    [GeneratedRegex(@"Pill\w+Style", RegexOptions.CultureInvariant)]
    private static partial Regex CodeBehindPillStyleRegex();

    private readonly record struct DynamicColor(Rgba Light, Rgba Dark);

    private readonly record struct ThemePair(string Light, string Dark);

    private readonly record struct Rgba(double Red, double Green, double Blue, double Alpha);
}
