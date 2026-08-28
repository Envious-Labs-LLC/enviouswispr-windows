using System.Globalization;
using System.Text.RegularExpressions;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Every text colour is legible on every surface it is painted on, in every theme.
/// </summary>
/// <remarks>
/// THE ONE PROPERTY OF A USER INTERFACE THAT EYES CANNOT JUDGE. Twenty screens were inspected by
/// eye, the helper text on five of them looked at deliberately, and it read as fine every time. It
/// measured 3.70:1 against a 4.5:1 floor. Eyes adapt to a palette, and whoever looks at it longest
/// adapts hardest, so the person best placed to notice is the person least able to.
///
/// It is also invisible to every other kind of check. It builds, it renders, it is on screen, it is
/// the intended colour from the intended token. There is nothing wrong except the arithmetic.
///
/// SO THE ARITHMETIC LIVES HERE. This computes the WCAG relative-luminance ratio for every text
/// brush against every surface a brush can sit on, in all three theme dictionaries, and fails with
/// the numbers rather than with a verdict.
/// </remarks>
public sealed partial class TextContrastTests
{
    /// <summary>The WCAG AA floor for normal-size text.</summary>
    /// <remarks>
    /// 4.5:1. The relaxed 3:1 floor applies only to large text - 24px, or 18.66px bold - and this
    /// app's helper style is 14px regular, so nothing here qualifies for it. Shrinking text does not
    /// lower the requirement, which is worth saying because the obvious tidy-up on this palette was
    /// to make the helper text smaller.
    /// </remarks>
    private const double NormalTextFloor = 4.5;

    /// <summary>Text brushes, each checked against every surface below.</summary>
    private static readonly string[] TextBrushes =
    [
        "BrandTextPrimaryColor",
        "BrandTextBodyColor",
        "BrandTextSecondaryColor",
        "BrandTextTertiaryColor",
    ];

    /// <summary>Every theme dictionary in the file, used to find where one block ends.</summary>
    private static readonly string[] ThemeNames = ["Light", "Dark", "HighContrast"];

    /// <summary>Surfaces text is painted on.</summary>
    /// <remarks>
    /// The card and the page, because those are the two grounds a paragraph of copy can land on. The
    /// sidebar and the window canvas carry labels rather than prose and are checked with them.
    /// </remarks>
    private static readonly string[] SurfaceColors =
    [
        "BrandCardBgColor",
        "BrandPageBgColor",
        "BrandSidebarBgColor",
    ];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void EveryTextColourIsLegibleOnEverySurfaceItLandsOn(string theme)
    {
        var colors = ReadTheme(theme);

        var failures = new List<string>();
        foreach (var text in TextBrushes)
        {
            foreach (var surface in SurfaceColors)
            {
                if (!colors.TryGetValue(text, out var foreground) ||
                    !colors.TryGetValue(surface, out var background))
                {
                    failures.Add($"{theme}: {text} or {surface} is not defined.");
                    continue;
                }

                var ratio = Contrast(foreground, background);
                if (ratio < NormalTextFloor)
                {
                    failures.Add(
                        $"{theme}: {text} {foreground} on {surface} {background} is "
                            + $"{ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, "
                            + $"below {NormalTextFloor}:1.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The edge of a control the user is meant to click into can be seen.
    /// </summary>
    /// <remarks>
    /// A DIVIDER AND A CONTROL BOUNDARY ARE DIFFERENT THINGS AND ONE TOKEN WAS ANSWERING BOTH. A
    /// divider is meant to be faint. The edge of a text field is meant to say "type here", and WCAG
    /// asks 3:1 for it. Sharing a token made the fields nearly edgeless: 1.13:1 in light, 1.31:1 in
    /// dark.
    ///
    /// IT LOOKED FINE, AND FOR A REASON WORTH KNOWING. The field fill is a different HUE from the
    /// card, so the boundary is visible to most people in a screenshot while measuring near 1:1.
    /// Contrast arithmetic is blind to hue - and so is a colourblind user, which is exactly who the
    /// floor exists for. "I can see it" was true and irrelevant.
    ///
    /// BOTH SIDES OF THE LINE, because a border sits between two colours and clearing the floor
    /// against one of them is half a check that looks like a whole one.
    /// </remarks>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void EveryControlBoundaryCanBeSeenAgainstWhatIsOnEitherSideOfIt(string theme)
    {
        var colors = ReadTheme(theme);

        Assert.True(
            colors.TryGetValue("BrandControlBorderColor", out var border),
            $"{theme} has no BrandControlBorderColor.");

        var failures = new List<string>();
        foreach (var surface in new[] { "BrandCardBgColor", "BrandPageBgColor" })
        {
            var ratio = Contrast(border!, colors[surface]);
            if (ratio < ControlBoundaryFloor)
            {
                failures.Add(
                    $"{theme}: control border {border} on {surface} {colors[surface]} is "
                        + $"{ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, "
                        + $"below {ControlBoundaryFloor}:1.");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>The WCAG floor for a boundary that carries meaning rather than decoration.</summary>
    private const double ControlBoundaryFloor = 3.0;

    /// <summary>
    /// The control. Without it, a reader that returned an empty palette would report every theme as
    /// perfectly legible, because a loop over nothing finds no failures.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TheThemeReaderActuallyFindsThePalette(string theme)
    {
        var colors = ReadTheme(theme);

        foreach (var name in TextBrushes.Concat(SurfaceColors))
        {
            Assert.True(colors.ContainsKey(name), $"{theme} has no {name}.");
        }
    }

    /// <summary>
    /// The arithmetic itself, checked against a pair whose answer is fixed by the specification.
    /// </summary>
    /// <remarks>
    /// Black on white is exactly 21:1 by definition, so this fails if the luminance formula is wrong
    /// in any way that matters. A contrast checker with a subtly wrong curve would otherwise produce
    /// plausible numbers for every real pair and be believed.
    /// </remarks>
    [Fact]
    public void TheContrastArithmeticAgreesWithTheSpecification()
    {
        Assert.Equal(21.0, Contrast("#000000", "#FFFFFF"), 3);
        Assert.Equal(1.0, Contrast("#7A7290", "#7A7290"), 3);
    }

    /// <summary>
    /// Every window the app draws is told the theme, not only the settings window.
    /// </summary>
    /// <remarks>
    /// A SECOND TOP-LEVEL WINDOW DOES NOT INHERIT THE FIRST ONE'S THEME. The recording pill is its
    /// own window, so setting the theme on the settings window never reached it and it followed the
    /// MACHINE instead - invisible while the two agree, wrong the moment someone picks Light on a
    /// machine set to Dark.
    ///
    /// IT WAS WORSE THAN A MISMATCHED COLOUR. The settings window shows a PREVIEW of that pill, and
    /// the preview DID follow the app theme. So the preview showed a pill that would never appear,
    /// which is worse than having no preview at all.
    ///
    /// Enumerated from the window files rather than from a list kept here, so a third window is
    /// covered without anyone remembering to add it - which is the whole failure being guarded
    /// against.
    /// </remarks>
    [Fact]
    public void EveryWindowTheAppDrawsIsToldTheTheme()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var code = string.Concat(Directory
            .EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        var roots = Directory
            .EnumerateFiles(app, "*Window.xaml", SearchOption.TopDirectoryOnly)
            .Select(path => (Window: Path.GetFileName(path), Root: RootName(File.ReadAllText(path))))
            .ToArray();

        Assert.True(roots.Length >= 2, $"Expected the app's windows, found {roots.Length}.");

        var untold = roots
            .Where(entry => entry.Root is null ||
                !code.Contains($"{entry.Root}.RequestedTheme", StringComparison.Ordinal))
            .Select(entry => entry.Window)
            .ToArray();

        Assert.True(
            untold.Length == 0,
            "These windows are never told the app's theme, so they follow the machine instead: "
                + string.Join(", ", untold));
    }

    private static string? RootName(string markup)
    {
        var match = Regex.Match(markup, @"<(?:Grid|StackPanel|Border)[^>]*x:Name=""(\w+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static Dictionary<string, string> ReadTheme(string theme)
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", "Theme", "DesignTokens.xaml"));

        // Each theme is one ResourceDictionary keyed by name. Slicing to that block first is what
        // stops a colour from another theme answering for this one - the keys are identical across
        // all three, so a whole-file search would return whichever came first and be wrong twice.
        var start = markup.IndexOf($"x:Key=\"{theme}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No {theme} theme dictionary in DesignTokens.xaml.");

        var next = ThemeNames
            .Select(other => markup.IndexOf($"x:Key=\"{other}\"", StringComparison.Ordinal))
            .Where(index => index > start)
            .DefaultIfEmpty(markup.Length)
            .Min();

        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ColorEntry().Matches(markup[start..next]))
        {
            colors[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return colors;
    }

    /// <summary>WCAG relative-luminance contrast between two opaque colours.</summary>
    private static double Contrast(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var value = hex.TrimStart('#');

        // An eight-digit value carries alpha first. A translucent colour has no single contrast
        // ratio - it depends what is behind it - so this refuses rather than quietly measuring the
        // colour as if it were opaque, which would report a number that is simply not true.
        Assert.True(value.Length == 6, $"{hex} is not an opaque colour; contrast is undefined for it.");

        double Channel(int offset)
        {
            var raw = int.Parse(
                value.AsSpan(offset, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture) / 255.0;
            return raw <= 0.03928 ? raw / 12.92 : Math.Pow((raw + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
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
            $"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
    }

    [GeneratedRegex(@"<Color x:Key=""(\w+)"">(#[0-9A-Fa-f]{6,8})</Color>")]
    private static partial Regex ColorEntry();
}
