using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        // PARSED, NOT MATCHED, AND NOT MERELY NAMED EITHER. Three ways to look answered while doing
        // nothing had to be closed in turn. Text matching counts an action written inside a comment -
        // "// case PillActionKind.X:" and its block-comment twin satisfy any pattern ever written for
        // this. Reading every case label in the method counts one inside a nested local function or
        // lambda, which the switch never reaches. And a bare "case X: break;" is a label with nothing
        // behind it, which is the default page wearing a name.
        var handler = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(
                repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs")))
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText == "OnPillActionInvoked");
        Assert.True(
            handler is not null,
            "MainWindow has no OnPillActionInvoked method, so nothing here is checking anything.");

        var parameter = handler!.ParameterList.Parameters.Single().Identifier.ValueText;

        // THE SWITCH ON THE PARAMETER ITSELF, not any switch that happens to be inside. A helper
        // switching over something else is not the decision this gate is about.
        var decision = handler.DescendantNodes()
            .OfType<SwitchStatementSyntax>()
            .FirstOrDefault(one => one.Expression.ToString() == parameter);
        Assert.True(
            decision is not null,
            $"OnPillActionInvoked does not switch on its own '{parameter}' parameter, so the gate "
                + "cannot see which actions it answers.");

        // DIRECT CHILDREN OF THAT SWITCH. A section nested inside a local function or a lambda is
        // not reached by pressing a button, and DescendantNodes walks into both.
        var answered = decision!.Sections
            .Where(DoesSomething)
            .SelectMany(section => section.Labels.OfType<CaseSwitchLabelSyntax>())
            .Select(label => label.Value)
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Expression.ToString() == "PillActionKind")
            .Select(access => access.Name.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            answered.Count > 0,
            "No case section in the pill action handler does anything, so this gate is reading "
                + "nothing and would pass whatever the handler said.");
        var unmapped = Enum.GetNames<PillActionKind>()
            .Where(name => !answered.Contains(name))
            .ToArray();
        Assert.True(
            unmapped.Length == 0,
            "These pill actions are not answered by name anywhere in the handler, so pressing their "
                + "button lands on the default page: " + string.Join(", ", unmapped));

        var declaredTags = XDocument
            .Load(Path.Combine(
                repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"))
            .Descendants()
            .Select(element => (string?)element.Attribute("Tag"))
            .Where(tag => tag is not null)
            .ToHashSet(StringComparer.Ordinal);

        // THE ARGUMENT THE CALL ACTUALLY PASSES, read off the syntax rather than matched out of the
        // text. A pattern that matched a known prefix passed on "settings-ai-polish2", which is a
        // page that does not exist and a button that quietly does nothing.
        // THE METHOD'S NAME, NOT THE WHOLE EXPRESSION. "this.OpenPage(page)" is the same call and
        // was invisible to a comparison against the complete text, so the qualified spelling walked
        // straight past every check below it.
        var destinations = decision.Sections
            .SelectMany(section => section.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(call => InvokedName(call.Expression) == "OpenPage")
            .ToArray();

        // EVERY CALL HAS TO BE READABLE, and skipping the ones that are not was a hole rather than a
        // limitation. Dropping a non-literal argument left "OpenPage(page)" checked by nothing while
        // the two literal calls beside it kept the count satisfied, so a destination that does not
        // exist could be introduced through a local and pass.
        var opaque = destinations
            .Where(call => call.ArgumentList.Arguments.Count != 1 ||
                call.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax)
            .Select(call => call.ToString())
            .ToArray();
        Assert.True(
            opaque.Length == 0,
            "These OpenPage calls do not name their page as a plain string, so nothing here can "
                + "check the page exists: " + string.Join(", ", opaque));

        var named = destinations
            .Select(call => call.ArgumentList.Arguments[0].Expression)
            .OfType<LiteralExpressionSyntax>()
            .Select(literal => literal.Token.ValueText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(named.Length >= 2, $"Expected the mapped page tags, found {named.Length}.");
        var missing = named.Where(tag => !declaredTags.Contains(tag)).ToArray();
        Assert.True(
            missing.Length == 0,
            "The pill sends users to pages the window does not have, so the button quietly does "
                + "nothing: " + string.Join(", ", missing));
    }

    /// <summary>The final identifier of whatever an invocation names.</summary>
    private static string? InvokedName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Whether one case section actually runs something before it breaks.</summary>
    /// <remarks>
    /// A LABEL IS NOT AN ANSWER. "case PillActionKind.X: break;" satisfies every check about naming
    /// and leaves the button doing exactly what the default arm would have done, which is the whole
    /// failure this gate exists to refuse.
    ///
    /// Statements declared inside a local function or a lambda within the section do not count,
    /// because nothing in the section calls them.
    /// </remarks>
    private static bool DoesSomething(SwitchSectionSyntax section) => section.Statements
        .SelectMany(statement => statement.DescendantNodesAndSelf(descendIntoChildren: node =>
            node is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax))
        .OfType<InvocationExpressionSyntax>()
        .Any();

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
                // A DIRECT WRITE IS ONLY A DEFECT WHEN NOTHING ANNOUNCES THAT REGION BY NAME. Two
                // shapes are legitimate and neither can be expressed as an atomic assignment: the
                // pill sets its text and announces AFTER the window is shown, because raising while
                // hidden announces something nobody can see; and a history card's words are fixed in
                // markup, so what changes is which card is showing and there is nothing to assign.
                //
                // THE ATOMIC SETTERS ARE NOT ON THIS LIST AND THAT IS DELIBERATE. Adding them made
                // the gate stop catching anything at all: SetLiveText appears once for a region and
                // then excuses every direct write to that same region elsewhere in the file. Only an
                // EXPLICIT announcement, naming the region, buys the exemption. The pill has
                // to set its text and then announce AFTER the window is shown, because raising while
                // it is still hidden announces something nobody can see - so text-then-raise is
                // correct there and an atomic setter cannot express it. What must hold is that the
                // file which writes the region is also the file that announces it.
                // TWO INDEPENDENT CHECKS, BECAUSE ONE EXEMPTION COVERED BOTH. A single explicit
                // raise used to excuse every direct write to that region anywhere in the file, and
                // it also meant a region with no direct writes was never checked for having an
                // announcement at all - so deleting the raise for a fixed history title would have
                // gone unnoticed.
                var announced = Regex.IsMatch(
                    text,
                    @"(FromElement|CreatePeerForElement|AnnounceLiveRegion|AnnounceStateChange"
                        + @"|SetLiveRegion|SetLiveText|SetLiveVisibility)\(\s*"
                        + Regex.Escape(region) + @"\s*[,)]");
                if (!announced)
                {
                    offenders.Add(
                        $"{Path.GetFileName(codeBehind)}: {region} is declared a live region and nothing ever announces it");
                }

                // A DIRECT WRITE IS ALLOWED ONLY WHEN THE ANNOUNCEMENT FOLLOWS IT. The overlay has
                // to set its text, show the window, and announce after, because raising while the
                // pill is still hidden announces something nobody can see. That is a code SEQUENCE,
                // and it is recognised as one: an earlier draft exempted the control by NAME, which
                // would have covered any future control that happened to share it.
                foreach (Match direct in Regex.Matches(
                    // A COMPARISON IS NOT AN ASSIGNMENT. `HistoryList.Visibility ==` reads the
                    // property and this flagged it as a write. The character after must not be
                    // another `=`; matched rather than looked past, so the value is compared instead
                    // of asked what it is not.
                    text, @"\b" + Regex.Escape(region) + @"\.(Text|Visibility)\s*=[^=]"))
                {
                    var after = text[direct.Index..];
                    var window = after.Length < 2_000 ? after : after[..2_000];
                    var announcedAfter = Regex.IsMatch(
                        window,
                        @"(FromElement|CreatePeerForElement|AnnounceLiveRegion|AnnounceStateChange)\(\s*"
                            + Regex.Escape(region) + @"\s*[,)]");
                    if (!announcedAfter)
                    {
                        offenders.Add($"{Path.GetFileName(codeBehind)}: {direct.Value.Trim()}");
                    }
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
            // THE CALLER'S NAME IS PART OF THE STATEMENT. Starting the match at the control's name
            // cut the helper off the front, so the mapping could not be followed to its definition.
            @"[A-Za-z_]\w*\s*\(\s*HistoryLoadingState[^;]*_isHistoryLoading[^;]*;"
                + @"|HistoryLoadingState[^;]*_isHistoryLoading[^;]*;",
            RegexOptions.Singleline);
        Assert.True(
            loadingAssignment.Success,
            "Nothing in MainWindow.xaml.cs makes HistoryLoadingState follow _isHistoryLoading, so a "
                + "person cannot tell a history that is still loading from one that is empty.");
        // BOTH OUTCOMES MUST EXIST, WHEREVER THE MAPPING LIVES. The statement used to spell them
        // inline; it now hands the flag to a helper. Requiring the words in the statement itself
        // meant the gate broke when the mapping moved, without the property changing at all, so it
        // accepts either the statement or the helper it calls carrying both.
        var mapping = loadingAssignment.Value;
        if (!mapping.Contains("Visibility.", StringComparison.Ordinal))
        {
            var helper = Regex.Match(mapping, @"(\w+)\s*\(");
            Assert.True(helper.Success, $"Cannot tell what maps the loading flag onto a visibility: {mapping}");
            var body = Regex.Match(
                codeBehind,
                @"(static\s+)?void\s+" + Regex.Escape(helper.Groups[1].Value) + @"\([^)]*\)[^;{]*[;{](?:[^}]*\})?",
                RegexOptions.Singleline);
            Assert.True(body.Success, $"No definition found for {helper.Groups[1].Value}.");
            mapping = body.Value;
        }

        Assert.Contains("Visibility.Visible", mapping, StringComparison.Ordinal);
        Assert.Contains("Visibility.Collapsed", mapping, StringComparison.Ordinal);
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
    ///
    /// HEIGHTS ARE NO LONGER COMPARED, AND THE CHECK IS STRONGER FOR IT. The pill's bar heights used
    /// to be static decoration, so matching them exactly was a fair proxy for matching the design.
    /// They are now DATA - each bar holds one past level - so the heights in its markup are the
    /// empty state, and a preview that copied them would draw a flat dead line under a caption
    /// promising a live meter. What IS the design is the bar count and the colour run, and those are
    /// compared exactly. The preview must also show more than one height, which is the assertion
    /// that actually refuses the two failures this test was written for: a solid bar and a gradient
    /// are each a single height.
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
            previewMeter!.Length == real.Length,
            "The Level Rail preview draws a different number of bars from the recording pill.\n"
                + $"  pill: {real.Length}, preview: {previewMeter.Length}\n"
                + "The preview is the only view of this design a user gets before choosing it.");

        Assert.True(
            previewMeter.Select(bar => bar.Brush).SequenceEqual(real.Select(bar => bar.Brush)),
            "The Level Rail preview runs different colours from the recording pill.\n"
                + $"  pill    : {string.Join(" ", real.Select(b => b.Brush))}\n"
                + $"  preview : {string.Join(" ", previewMeter.Select(b => b.Brush))}");

        // NUMBERS, NOT STRINGS, AND A RANGE RATHER THAN A COUNT OF DIFFERENCES. "More than one
        // distinct string" is satisfied by "5" beside "5.0", which draws a flat line, and by
        // twenty-three bars at 5 with one at 1000, which draws off the pill. The claim that is
        // actually true of a meter is that its bars sit inside the height the rail can draw and
        // reach both ends of it.
        var heights = previewMeter
            .Select(bar => double.TryParse(bar.Height, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : double.NaN)
            .ToArray();
        Assert.True(
            heights.All(height => !double.IsNaN(height)),
            "A Level Rail preview bar has a height that is not a plain number, so nothing here can "
                + "tell what it draws.");
        Assert.True(
            heights.All(height => height is >= 4 and <= 25),
            "The Level Rail preview draws bars outside the 4 to 25 the live meter uses, so the "
                + "picture is not the meter: " + string.Join(" ", heights));
        Assert.True(
            heights.Min() <= 8 && heights.Max() >= 20,
            "The Level Rail preview never reaches quiet or loud, so it shows a band rather than a "
                + $"meter. Lowest {heights.Min()}, highest {heights.Max()}.");
        Assert.True(
            heights.Distinct().Count() >= 6,
            "The Level Rail preview draws too few different heights to read as a level history. A "
                + "solid bar and a gradient both failed this test before, and both are one height.");
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

    /// <summary>
    /// A settings row that commits its toggle must not put a label inside the toggle.
    /// </summary>
    /// <remarks>
    /// THE ROW HANDLER DRAWS ITS LINE AT THE TOGGLE'S OWN RECTANGLE, so anything inside that
    /// rectangle is deaf to the row. A ToggleSwitch wires its pointer handling to one template part
    /// and its Header sits outside that part, which means a header put back on one of these controls
    /// is a label that responds to neither the switch nor the row - the deadest pixels in the row,
    /// and exactly the sentence a person aims at. The label therefore lives beside the control, and
    /// the control points at it with LabeledBy so a screen reader still announces what is being
    /// switched while the words themselves are read once rather than twice.
    ///
    /// THE SEARCH STARTS FROM THE TOGGLES, NOT FROM THE ROWS, and that direction is the whole
    /// difference between a gate and a decoration. Counting rows can only find a row that went
    /// wrong; it cannot find a TWELFTH toggle added with no row around it at all, which is the
    /// cheapest way to ship a dead one. Every toggle in the file is enumerated and each one must
    /// have a row, so a new toggle is in this gate's scope the moment it exists.
    ///
    /// AND A ROW OWNS EXACTLY ONE TOGGLE ANYWHERE BENEATH IT, not merely one directly under it. A
    /// second toggle nested deeper is still inside the row's tapped area, so a tap meant for it
    /// bubbles up and flips the FIRST one - a control that changes a different setting than the one
    /// under the pointer. Direct children and descendants are counted separately and compared,
    /// because agreement between them is what rules that out.
    /// </remarks>
    [Fact]
    public void EveryRowCommittedToggleKeepsItsLabelOutsideTheSwitch()
    {
        var markup = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var toggles = markup.Descendants()
            .Where(element => element.Name.LocalName == "ToggleSwitch")
            .ToArray();

        // NO COUNT IS ASSERTED HERE, and the reason is that the compiler already asserts one. Every
        // one of these switches is named in MainWindow.xaml.cs - read, written, or wired to a
        // handler - so a switch that disappears from the markup fails the BUILD rather than passing
        // this gate quietly. A roster of eleven written down here would only be a second place to
        // update, and the kind that is wrong for a while before anyone notices.

        var faults = new List<string>();
        foreach (var toggle in toggles)
        {
            var identity = (string?)toggle.Attribute(xaml + "Name") ?? "an unnamed toggle";
            var row = toggle.Parent;
            if (row is null || (string?)row.Attribute("Tapped") != "SettingRow_Tapped")
            {
                faults.Add($"{identity} is not inside a row that commits it, so only its switch responds");
                continue;
            }

            foreach (var banned in new[] { "Header", "HeaderTemplate", "AutomationProperties.Name" })
            {
                if (toggle.Attribute(banned) is not null)
                {
                    faults.Add(banned == "AutomationProperties.Name"
                        ? $"{identity} names itself as well as pointing at its label, which reads the words twice"
                        : $"{identity} carries {banned}, which puts its label inside the switch where the row cannot reach it");
                }
            }

            var direct = row.Elements().Count(child => child.Name.LocalName == "ToggleSwitch");
            var beneath = row.Descendants().Count(child => child.Name.LocalName == "ToggleSwitch");
            if (direct != 1 || beneath != 1)
            {
                faults.Add(
                    $"the row around {identity} holds {beneath} toggles ({direct} of them directly); a tap anywhere " +
                    "in it commits the first, so a second one flips a setting the pointer was not over");
                continue;
            }

            var labelName = CompiledBindingTarget((string?)toggle.Attribute("AutomationProperties.LabeledBy"));
            if (labelName is null)
            {
                faults.Add($"{identity} has no AutomationProperties.LabeledBy, so a screen reader announces a switch with no subject");
                continue;
            }

            var labels = row.Descendants()
                .Where(child => child.Name.LocalName == "TextBlock"
                    && (string?)child.Attribute(xaml + "Name") == labelName)
                .ToArray();
            if (labels.Length != 1)
            {
                faults.Add($"{identity} points at \"{labelName}\", and its row holds {labels.Length} elements by that name");
                continue;
            }

            if (string.IsNullOrWhiteSpace((string?)labels[0].Attribute("Text")))
            {
                faults.Add($"the label {labelName} has no text, so {identity} is announced as nothing");
            }

            if ((string?)labels[0].Attribute("AutomationProperties.AccessibilityView") != "Raw")
            {
                faults.Add(
                    $"the label {labelName} is not AccessibilityView=Raw, so a screen reader reads its words once as " +
                    $"text and again as {identity}'s name");
            }
        }

        Assert.True(faults.Count == 0, string.Join(Environment.NewLine, faults));
    }

    /// <summary>Reads the element a compiled binding points at, or null if it is not one.</summary>
    /// <remarks>
    /// ONLY x:Bind IS ACCEPTED, AND THAT IS THE POINT RATHER THAN A PREFERENCE. LabeledBy needs a
    /// UIElement, and a classic binding will happily be given something that is not one -
    /// "{Binding ElementName=AutoStopToggleLabel, Path=Text}" is a string, resolves to null at
    /// runtime, and leaves the switch anonymous while looking entirely reasonable in a diff. A
    /// compiled binding names a field and is checked against its type when the project builds, so
    /// the same mistake stops being possible rather than being caught later.
    /// </remarks>
    private static string? CompiledBindingTarget(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return null;
        }

        var match = Regex.Match(binding, @"^\{x:Bind\s+(\w+)\s*,\s*Mode=OneTime\s*\}$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// One place in the app builds a custom word, and it builds it from the picker's own value.
    /// </summary>
    /// <remarks>
    /// THE PAGE HAS TWO ADD BUTTONS AND ONLY ONE OF THEM USED TO ASK. Accepting a suggested
    /// mishearing saved a word under the ordinary rule while the picker in front of the person still
    /// said Loose, and nothing about that reads as wrong in a diff - the suggested path simply did
    /// not mention strictness at all, which is what an absence looks like.
    ///
    /// EVERY QUESTION HERE IS PUT TO THE COMPILER, and each of the three was a bypass first. A scan
    /// for the constructor could not see "CustomWordEntry entry = new(...)", which names the type on
    /// the LEFT of the equals sign. Comparing a type's NAME could be satisfied by an error symbol,
    /// so a machine that failed to resolve the type would report one construction and stay green -
    /// a gate that passes hardest when it is working least. And searching the argument's TEXT for
    /// the picker was satisfied by a mapping that mentioned the picker and threw its answer away.
    ///
    /// SO THE MAPPING ITSELF LIVES IN CORE where a test can call it, and this gate's job is only to
    /// prove the app uses it, with the picker's own position, in the one place allowed to build a
    /// word. What the mapping RETURNS is pinned by MatchStrictnessChoiceTests.
    /// </remarks>
    [Fact]
    public void OnePlaceInTheAppBuildsACustomWordFromThePickersOwnValue()
    {
        const string door = "SaveCustomWordFromPickerAsync";
        const string picker = "WordStrictnessComboBox";
        var app = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");

        var trees = Directory
            .EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file))
            .ToArray();

        // CORE IS NAMED EXPLICITLY RATHER THAN HOPED FOR. The rest of the app's dependencies are not
        // all here and do not need to be; the one type this gate is about has to resolve, and a
        // machine where it did not would otherwise report a clean scan of nothing.
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0 && File.Exists(path))
            .Append(typeof(CustomWordEntry).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create("app-scan", trees, references);

        var expected = compilation.GetTypeByMetadataName(typeof(CustomWordEntry).FullName!);
        Assert.True(
            expected is { TypeKind: not TypeKind.Error },
            $"{typeof(CustomWordEntry).FullName} did not resolve, so this gate would have scanned for nothing.");

        var built = new List<(BaseObjectCreationExpressionSyntax Node, SemanticModel Model)>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var creation in tree.GetRoot().DescendantNodes()
                .OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(creation).Type, expected))
                {
                    built.Add((creation, model));
                }
            }
        }

        Assert.True(
            built.Count == 1,
            $"Expected exactly one place in the app to build a custom word, found {built.Count}: " +
                string.Join(", ", built.Select(one => Where(one.Node))));

        var (node, model2) = built[0];
        var owner = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        Assert.True(
            owner?.Identifier.Text == door,
            $"A word is built outside {door}, so the choice on screen is not the one saved.");

        var arguments = node.ArgumentList?.Arguments;
        Assert.True(arguments is { Count: 3 }, "A word is built without being told how closely it must match.");

        // THE PICKER'S OWN SELECTED VALUE, taken as it is. Nothing is looked up from a position, so
        // there is no order for this file and the markup to disagree about.
        var fallback = arguments!.Value[2].Expression as BinaryExpressionSyntax;
        var cast = fallback?.Left as BinaryExpressionSyntax;
        var taken = cast?.Left as MemberAccessExpressionSyntax;
        Assert.True(
            fallback?.IsKind(SyntaxKind.CoalesceExpression) == true &&
                cast?.IsKind(SyntaxKind.AsExpression) == true &&
                taken?.Name.Identifier.Text == "SelectedValue" &&
                (taken.Expression as IdentifierNameSyntax)?.Identifier.Text == picker,
            $"A word is not built from {picker}.SelectedValue, so the picker on screen decides nothing.");

        // AND WHAT IT FALLS BACK TO WHEN NOTHING IS CHOSEN IS PART OF THE PROMISE. A fallback of
        // Strict would read as an ordinary line and would quietly correct less of what everyone
        // says. Both the type asked for and the value fallen back to are read as symbols.
        var strictness = compilation.GetTypeByMetadataName(typeof(MatchStrictness).FullName!);
        Assert.True(
            strictness is { TypeKind: not TypeKind.Error },
            $"{typeof(MatchStrictness).FullName} did not resolve, so this gate would have checked nothing.");
        Assert.True(
            SymbolEqualityComparer.Default.Equals(
                (model2.GetTypeInfo(cast!.Right).Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault(),
                strictness),
            $"A word's strictness is not read as a {nameof(MatchStrictness)}, so anything the picker "
                + "holds would be taken.");

        var fell = model2.GetSymbolInfo(fallback!.Right).Symbol;
        Assert.True(
            fell is IFieldSymbol field &&
                SymbolEqualityComparer.Default.Equals(field.ContainingType, strictness) &&
                field.Name == nameof(MatchStrictness.Default),
            $"A word with nothing chosen does not fall back to {nameof(MatchStrictness)}."
                + $"{nameof(MatchStrictness.Default)}.");
    }

    /// <summary>
    /// Each choice in the strictness picker carries the meaning it shows.
    /// </summary>
    /// <remarks>
    /// THIS IS THE HALF NO GATE OVER C# CAN SEE. The app takes whatever value the chosen item holds,
    /// so what a person actually gets is decided by the markup - and a version of this feature that
    /// mapped a POSITION to a meaning would let somebody reorder three strings and silently change
    /// what everyone was choosing, with every test still green.
    ///
    /// THE LABEL AND THE VALUE ARE CHECKED TOGETHER, because either alone passes while wrong: three
    /// correct values behind labels in another order reads as a lie to the person, and three correct
    /// labels over one repeated value quietly gives everyone the same rule.
    /// </remarks>
    [Fact]
    public void EachChoiceInTheStrictnessPickerCarriesTheMeaningItShows()
    {
        var markup = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var box = markup.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "ComboBox"
                && (string?)element.Attribute(xaml + "Name") == "WordStrictnessComboBox");
        Assert.True(box is not null, "The strictness picker is not in the markup.");
        Assert.Equal("Tag", (string?)box!.Attribute("SelectedValuePath"));

        // NEITHER FORM OF IT. An attribute and a property element are the same setting written two
        // ways, and a rule that knows only one of them is a rule with a spelling for a loophole.
        Assert.True(
            box.Attribute("SelectedIndex") is null &&
                !box.Elements().Any(child => child.Name.LocalName == "ComboBox.SelectedIndex"),
            "The picker starts on a POSITION, so reordering its choices silently changes what everyone "
                + "gets. It should start on a value by name.");

        Assert.True(
            box.Attribute("ItemsSource") is null &&
                !box.Elements().Any(child => child.Name.LocalName == "ComboBox.ItemsSource"),
            "The picker is filled from somewhere else, so the choices checked below are not the choices "
                + "a person sees.");

        // THE VALUE HAS TO BE IN THE TAG, WHICH IS THE ONLY PLACE SelectedValuePath READS. An enum
        // written anywhere else under the item - a resource, say - looks correct beside a Tag="Loose"
        // attribute that is a STRING, and every selection then falls through to the ordinary rule
        // while this file appears to say otherwise.
        var settings = XNamespace.Get("using:EnviousWispr.Core.Settings");
        var choices = box.Elements().Where(child => child.Name.LocalName == "ComboBoxItem").ToArray();
        var offered = new List<(string Value, string Label)>();
        foreach (var choice in choices)
        {
            Assert.True(
                choice.Attribute("Tag") is null,
                "A choice carries its Tag as an attribute, which makes it a string rather than a "
                    + $"{nameof(MatchStrictness)}, so what it is worth is not what it says.");

            var tag = choice.Elements()
                .Where(child => child.Name.LocalName == "ComboBoxItem.Tag")
                .ToArray();
            Assert.True(tag.Length == 1, $"A choice has {tag.Length} tags, and exactly one is its value.");

            var held = tag[0].Elements().ToArray();
            Assert.True(
                held.Length == 1 && held[0].Name == settings + nameof(MatchStrictness),
                $"A choice's tag does not hold exactly one {nameof(MatchStrictness)}.");

            Assert.True(
                choice.Descendants().Count(node => node.Name == settings + nameof(MatchStrictness)) == 1,
                $"A choice mentions {nameof(MatchStrictness)} somewhere other than its tag, which is "
                    + "the only place the picker reads.");

            // ONE LABEL, AND ITS WORDS WRITTEN HERE. Reading the FIRST label meant a second one
            // beside it could say anything; an x:Uid means the words on screen come from a resource
            // file this gate never opens, so the sentence pinned below would be checked against text
            // nobody ever sees.
            Assert.True(
                choice.Descendants().All(node => node.Attribute(xaml + "Uid") is null),
                $"A choice carries an x:Uid, so its words are replaced at run time and what is written "
                    + "here is not what anybody reads.");

            var labels = choice.Descendants().Where(node => node.Name.LocalName == "TextBlock").ToArray();
            Assert.True(
                labels.Length == 1,
                $"A choice shows {labels.Length} pieces of text, and exactly one of them is what it means.");

            var label = (string?)labels[0].Attribute("Text");
            Assert.True(
                !string.IsNullOrWhiteSpace(label),
                $"The choice worth {held[0].Value.Trim()} shows no words written here, so nobody can tell "
                    + "what it does from this file.");

            offered.Add((held[0].Value.Trim(), label!));
        }

        // THE WHOLE SENTENCE, NOT ITS FIRST WORD. "Loose: correct it only when exact" begins with the
        // right word and describes the opposite rule.
        Assert.Equal(
            new[]
            {
                (nameof(MatchStrictness.Default), "Default: the balance every word had before"),
                (nameof(MatchStrictness.Loose), "Loose: correct it even when what I said was some way off"),
                (nameof(MatchStrictness.Strict), "Strict: correct it only when I said it almost exactly"),
            }.OrderBy(pair => pair.Item1, StringComparer.Ordinal),
            offered.Select(pair => (pair.Value, pair.Label)).OrderBy(pair => pair.Item1, StringComparer.Ordinal));
    }

    /// <summary>
    /// The strictness picker starts on the ordinary rule, and nothing replaces what it offers.
    /// </summary>
    /// <remarks>
    /// THE MARKUP CAN ONLY PROMISE WHAT THE CODE DOES NOT UNDO. Three choices carrying their own
    /// values are worth nothing if the window fills the picker from a list built in C#, points it at
    /// a position, or is simply never told where to start - and each of those is a single ordinary
    /// line that reads as housekeeping.
    ///
    /// THE BANNED WRITES ARE NAMED, RATHER THAN EVERY OTHER WRITE BEING FORBIDDEN. An earlier version
    /// required every assignment to this control to target SelectedValue, which would have refused a
    /// perfectly reasonable IsEnabled and still missed the same write made through "this.".
    /// </remarks>
    [Fact]
    public void TheStrictnessPickerStartsOnTheOrdinaryRuleAndNothingReplacesItsChoices()
    {
        const string picker = "WordStrictnessComboBox";
        const string reset = "ResetMatchStrictness";
        string[] banned = ["SelectedIndex", "SelectedItem", "ItemsSource", "SelectedValuePath"];

        var window = Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(window), path: window);
        var root = tree.GetRoot();

        var constructor = root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(one => one.Body is not null &&
                Calls(one.Body, "InitializeComponent") is not null);
        Assert.True(constructor is not null, "The window has no constructor that builds its own controls.");

        // THE VERY NEXT STATEMENT, not merely somewhere later. Anything allowed to run in between
        // can set the picker to something else and the reset then does nothing visible, which is a
        // single ordinary line that leaves every other rule here satisfied.
        var statements = constructor!.Body!.Statements;
        var built = statements
            .Select((statement, index) => (statement, index))
            .First(one => Calls(one.statement, "InitializeComponent") is not null)
            .index;
        Assert.True(
            built + 1 < statements.Count && Calls(statements[built + 1], reset) is not null,
            $"The window does not call {reset} immediately after building its controls, so what the "
                + "picker starts on is whatever ran in between.");

        var resetBody = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.Text == reset);
        Assert.True(resetBody is not null, $"{reset} is not in the window.");

        var assigned = resetBody!.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToArray();
        Assert.True(assigned.Length == 1, $"{reset} makes {assigned.Length} changes, and exactly one is its job.");
        Assert.True(
            (assigned[0].Left as MemberAccessExpressionSyntax)?.Name.Identifier.Text == "SelectedValue",
            $"{reset} puts the picker back by something other than its value.");
        Assert.Equal(
            $"{nameof(MatchStrictness)}.{nameof(MatchStrictness.Default)}",
            assigned[0].Right.ToString());

        // BARE OR THROUGH "this.", because the same write spelled two ways is one write.
        var writes = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(assignment => assignment.Left as MemberAccessExpressionSyntax)
            .Where(member => member is not null && Receiver(member) == picker)
            .Where(member => banned.Contains(member!.Name.Identifier.Text, StringComparer.Ordinal))
            .Select(member => $"{member!.Name.Identifier.Text} at line " +
                $"{member.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToArray();
        Assert.True(
            writes.Length == 0,
            $"{picker} is written to in a way that replaces or reorders what it offers: "
                + string.Join(", ", writes));

        // AND THE CHOICE IS SET IN ONE PLACE. A second assignment anywhere else runs after the reset
        // and quietly decides what everyone starts on, while the reset above still reads correctly.
        var chosen = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is MemberAccessExpressionSyntax member
                && Receiver(member) == picker
                && member.Name.Identifier.Text == "SelectedValue")
            .Where(assignment => assignment.Ancestors().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault()?.Identifier.Text != reset)
            .Select(assignment => $"line {assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToArray();
        Assert.True(
            chosen.Length == 0,
            $"{picker}.SelectedValue is set outside {reset}, so what a person starts on is decided in "
                + "more than one place: " + string.Join(", ", chosen));

        var bound = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => call.Expression as MemberAccessExpressionSyntax)
            .Where(member => member is not null && Receiver(member) == picker)
            .Where(member => member!.Name.Identifier.Text is "SetBinding" or "SetValue")
            .Select(member => $"line {member!.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToArray();
        Assert.True(
            bound.Length == 0,
            $"{picker} is bound or set through the property system, which the markup gate cannot see: "
                + string.Join(", ", bound));

        // ASKED OF THE TREE, NOT OF THE SPELLING. "Items?.Clear()" empties the picker and contains no
        // ".Items." at all, and "this." or a pair of brackets around the name change the text again
        // without changing what happens.
        var touched = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(member => Receiver(member) == picker && member.Name.Identifier.Text == "Items")
            .Select(member => $"line {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToArray();
        Assert.True(
            touched.Length == 0,
            $"{picker}.Items is reached in code, so the choices a person sees are not the ones in the "
                + "markup: " + string.Join(", ", touched));
    }

    /// <summary>The call to a named method inside a statement or block, or null if it is not there.</summary>
    private static InvocationExpressionSyntax? Calls(SyntaxNode body, string name) =>
        body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(call => call.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text == name,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text == name,
                _ => false,
            });

    /// <summary>The control a member access is reading, past "this" and any brackets around it.</summary>
    private static string? Receiver(MemberAccessExpressionSyntax? member) => Named(member?.Expression);

    private static string? Named(ExpressionSyntax? expression) => expression switch
    {
        ParenthesizedExpressionSyntax parens => Named(parens.Expression),
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } inner => inner.Name.Identifier.Text,
        _ => null,
    };

    private static string Where(SyntaxNode node) =>
        $"{Path.GetFileName(node.SyntaxTree.FilePath)} " +
        $"line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    /// <summary>
    /// Every section header carries an accent glyph, and no glyph is a new guess.
    /// </summary>
    /// <remarks>
    /// macOS DRAWS A SECTION HEADER AS A MARK BESIDE A LABEL. Its BrandedPanel takes the icon as
    /// optional and all seven callers pass one, so it is not optional in practice - it is what a
    /// section header looks like, and eighteen bare labels read as a different product.
    ///
    /// A WRONG ICON CODE DOES NOT FAIL, IT DRAWS A HOLLOW BOX. A codepoint missing from Segoe Fluent
    /// Icons lays out and takes the right space, and two different codes can be the same drawing, so
    /// nothing here could tell a bad choice from a good one by reading it. The rule is therefore
    /// about WHERE a glyph comes from rather than which one it is: every section header reuses a
    /// glyph already declared on a sidebar row, which is on screen and has been looked at. A section
    /// header that writes its own Glyph in the markup is refused whether or not the code is real.
    /// </remarks>
    [Fact]
    public void EverySectionHeaderTakesItsGlyphFromOneAlreadyOnScreen()
    {
        var markup = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var eyebrows = markup.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock"
                && ((string?)element.Attribute("Style"))?.Contains(
                    "BrandSectionEyebrowStyle", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.True(eyebrows.Length >= 18, $"Expected the section headers, found {eyebrows.Length}.");

        // EVERY ICON THE SIDEBAR DRAWS, which is the set somebody has actually looked at - and ONLY
        // the one in a row's icon slot. Anything named anywhere beneath a row would have counted,
        // including a FontIcon dropped into the row's own content, which nobody has seen and which
        // would have carried a guessed codepoint straight past this.
        var sidebar = markup.Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .SelectMany(row => row.Elements())
            .Where(slot => slot.Name.LocalName == "NavigationViewItem.Icon")
            .SelectMany(slot => slot.Elements())
            .Where(icon => icon.Name.LocalName == "FontIcon")
            .Select(icon => (string?)icon.Attribute(xaml + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        var faults = new List<string>();
        foreach (var eyebrow in eyebrows)
        {
            var label = (string?)eyebrow.Attribute("Text") ?? "an unlabelled header";
            var row = eyebrow.Parent;
            if (row is null || row.Name.LocalName != "StackPanel")
            {
                faults.Add($"\"{label}\" is not in an eyebrow row, so it has nowhere for a glyph");
                continue;
            }

            var marks = row.Elements().Where(child => child.Name.LocalName == "FontIcon").ToArray();
            if (marks.Length != 1)
            {
                faults.Add($"\"{label}\" has {marks.Length} glyphs beside it, and exactly one is a header");
                continue;
            }

            // THE MARK MUST BE THE SIDEBAR'S OWN, RESOLVED HERE RATHER THAN TRUSTED. Forbidding a
            // literal was not enough: a codepoint could still arrive from C#, or through the
            // property-element form <FontIcon.Glyph>, and both left this green. Requiring the
            // binding names the exact icon the header borrows, and that icon has to be one the
            // sidebar draws - which is the only set anybody has looked at.
            var bound = (string?)marks[0].Attribute("Glyph");
            var source = bound is null
                ? null
                : Regex.Match(bound, @"^\{x:Bind\s+(\w+)\.Glyph,\s*Mode=OneTime\s*\}$") is { Success: true } hit
                    ? hit.Groups[1].Value
                    : null;
            if (source is null)
            {
                faults.Add(
                    $"\"{label}\" does not take its mark from a sidebar icon. Nothing here can tell a "
                        + "codepoint that draws a picture from one that draws a hollow box, so the mark "
                        + "has to be one the sidebar already draws.");
                continue;
            }

            if (!sidebar.Contains(source))
            {
                faults.Add($"\"{label}\" borrows \"{source}\", which is not an icon on a sidebar row");
            }

            // EITHER RESOURCE FORM APPLIES THE SAME STYLE. Insisting on the StaticResource spelling
            // refused a ThemeResource that does exactly the same thing, which is a rule about typing
            // rather than about what the header looks like.
            var styled = (string?)marks[0].Attribute("Style");
            if (styled is null ||
                !Regex.IsMatch(styled, @"^\{(?:Static|Theme)Resource\s+BrandSectionEyebrowGlyphStyle\s*\}$"))
            {
                faults.Add(
                    $"\"{label}\" sizes its own mark. Eighteen headers carry one and a size written "
                        + "eighteen times is a size that will disagree with itself.");
            }
        }

        // THE STYLE'S OWN VALUES, because "they all share one style" says nothing about what the
        // style says. macOS draws this mark at 16 semibold beside a 14 label, so it sits slightly
        // proud of the words; a shared style set to 12 would satisfy every rule above.
        var theme = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "Theme", "Typography.xaml"));
        var style = theme.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "Style"
                && (string?)element.Attribute(xaml + "Key") == "BrandSectionEyebrowGlyphStyle");
        Assert.True(style is not null, "The shared mark style is not in the theme.");

        var set = style!.Elements()
            .Where(setter => setter.Name.LocalName == "Setter")
            .ToDictionary(
                setter => (string?)setter.Attribute("Property") ?? string.Empty,
                setter => (string?)setter.Attribute("Value") ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("16", set.GetValueOrDefault("FontSize"));
        Assert.Equal("SemiBold", set.GetValueOrDefault("FontWeight"));

        Assert.True(faults.Count == 0, string.Join(Environment.NewLine, faults));
    }

    private readonly record struct DynamicColor(Rgba Light, Rgba Dark);

    private readonly record struct ThemePair(string Light, string Dark);

    private readonly record struct Rgba(double Red, double Green, double Blue, double Alpha);
}
