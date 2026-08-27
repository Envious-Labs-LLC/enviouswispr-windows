using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Binds the window's minimum width to the two-card frame's own arithmetic.
/// </summary>
/// <remarks>
/// The frame pins <c>PaneDisplayMode="Left"</c> so the sidebar card cannot collapse, which
/// removed WinUI's own narrow-width pane behaviour. Nothing then stops the window being
/// dragged until the content card is squeezed past zero, so the minimum size is the
/// replacement for that lost safety net and it has to track the frame rather than sit
/// beside it as a number someone typed once.
///
/// These are SOURCE-level assertions. A WinUI <c>Window</c> cannot be constructed in a unit
/// test, so what is checked is that the runtime computation is DERIVED from the three
/// inputs — not that a particular pixel value came out. That distinction is the point: a
/// test asserting <c>MinWidth == 842</c> would pass forever while the sidebar grew.
/// </remarks>
public sealed partial class WindowMinimumSizeTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The content card must stay wide enough for a header card and a setting row to read.
    /// Below this the frame is technically intact and practically useless, so it is a floor
    /// on the FLOOR: it guards against someone shrinking the content minimum token until the
    /// derivation is satisfied by a window nobody can use.
    /// </summary>
    private const double UsableContentFloor = 400;

    [Fact]
    public void MinimumWidthIsDerivedFromTheFrameAndNotHardcoded()
    {
        var root = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var match = ConfigureMinimumWidthBodyRegex().Match(codeBehind);
        Assert.True(
            match.Success,
            "ConfigureMinimumWindowWidth was not found in MainWindow.xaml.cs. If it was renamed or "
                + "removed, this test's subject is gone and its green is meaningless — update it or "
                + "delete it deliberately.");

        var body = match.Groups["body"].Value;

        // Assert against the ASSIGNMENT, not against the method body. Checking that three
        // identifiers appear somewhere in the method is satisfied by reading all three into
        // unused variables and then assigning something else entirely — which is exactly the
        // regression this test is supposed to catch, so the weaker form could not catch it.
        var assignment = MinimumWidthAssignmentRegex().Match(body);
        Assert.True(
            assignment.Success,
            "No assignment to PreferredMinimumWidth was found inside ConfigureMinimumWindowWidth. "
                + $"Body was:\n{body}");

        var expression = assignment.Groups["expression"].Value;

        foreach (var input in new[]
                 {
                     "OpenPaneLength",
                     "BrandWindowFrameInset",
                     "BrandContentCardMinimumWidth",
                 })
        {
            Assert.True(
                ExpressionReaches(body, expression, input),
                $"The value assigned to PreferredMinimumWidth is not derived from '{input}'. The minimum "
                    + "must track the frame's own inputs; one that stops tracking an input is stale the "
                    + $"next time that input changes.\nAssigned expression: {expression}");
        }

        // A bare numeric literal here is the regression this test exists for: it means somebody
        // resolved the arithmetic once and pasted the answer. Loop counts and small multipliers
        // live in named constants, so the body should carry no free-standing number.
        var literals = NumericLiteralRegex()
            .Matches(body)
            .Select(m => m.Value)
            .ToArray();
        Assert.True(
            literals.Length == 0,
            "The minimum-width computation contains numeric literal(s) "
                + $"[{string.Join(", ", literals)}]. Derive the value from the layout tokens and the "
                + "pane length instead; a number that is correct today is the defect one change later.");
    }

    [Fact]
    public void TheDerivedMinimumLeavesAUsableContentCard()
    {
        var root = FindRepositoryRoot();

        var mainWindow = XDocument.Load(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var navigation = mainWindow.Descendants()
            .SingleOrDefault(element =>
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "ProductNavigation");
        Assert.True(
            navigation is not null,
            "ProductNavigation was not found in MainWindow.xaml, so the sidebar width could not be read.");

        var openPaneLength = ParseDouble(
            (string?)navigation!.Attribute("OpenPaneLength"),
            "ProductNavigation/OpenPaneLength");

        var layout = XDocument.Load(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "Theme", "Layout.xaml"));
        var frameInset = ReadLayoutDouble(layout, "BrandWindowFrameInset");
        var contentMinimum = ReadLayoutDouble(layout, "BrandContentCardMinimumWidth");

        Assert.True(
            contentMinimum >= UsableContentFloor,
            $"BrandContentCardMinimumWidth is {contentMinimum}, below the {UsableContentFloor} needed for "
                + "a header card and a setting row to read. Satisfying the frame arithmetic with a content "
                + "card nobody can use is not a fix.");

        // Three gutters: window edge, between the cards, window edge.
        var derived = openPaneLength + (3 * frameInset) + contentMinimum;
        Assert.True(
            derived > openPaneLength + contentMinimum,
            "The derived minimum does not account for the frame gutters.");
    }

    private static double ReadLayoutDouble(XDocument layout, string key)
    {
        var element = layout.Root!
            .Elements()
            .SingleOrDefault(e => (string?)e.Attribute(XName.Get("Key", XamlNamespace)) == key);
        Assert.True(
            element is not null,
            $"Layout token '{key}' was not found in Theme/Layout.xaml. The minimum-width derivation reads "
                + "it, so its absence is a build-time-invisible break.");
        return ParseDouble(element!.Value, key);
    }

    private static double ParseDouble(string? raw, string what)
    {
        Assert.True(
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value),
            $"Could not parse '{what}' as a number; read '{raw ?? "(absent)"}'.");
        return value;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Walked to the filesystem root from "
                + $"'{AppContext.BaseDirectory}' without finding EnviousWispr.Windows.slnx. Failing loudly "
                + "rather than defaulting to a path, because a wrong root makes every file read below "
                + "silently vacuous.");
    }

    /// <summary>
    /// True when <paramref name="input"/> reaches <paramref name="expression"/>, directly or
    /// through one local declared earlier in the same method.
    /// </summary>
    /// <remarks>
    /// One hop, deliberately. The real computation reads the two layout tokens into locals and
    /// then uses those locals in the assignment, which is ordinary and readable code, so a
    /// direct-substring check would fail on correct source. Following arbitrary chains would
    /// mean writing a dataflow analyser inside a test; one hop covers the shape this method
    /// actually has, and anything deeper should make the reader suspicious anyway.
    /// </remarks>
    private static bool ExpressionReaches(string body, string expression, string input)
    {
        if (expression.Contains(input, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (Match local in LocalDeclarationRegex().Matches(body))
        {
            var name = local.Groups["name"].Value;
            var initialiser = local.Groups["initialiser"].Value;
            if (initialiser.Contains(input, StringComparison.Ordinal)
                && Regex.IsMatch(expression, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])"))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(
        @"private\s+void\s+ConfigureMinimumWindowWidth\s*\(\s*\)\s*\{(?<body>.*?)\n    \}",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ConfigureMinimumWidthBodyRegex();

    [GeneratedRegex(
        @"PreferredMinimumWidth\s*=\s*(?<expression>[^;]+);",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MinimumWidthAssignmentRegex();

    [GeneratedRegex(
        @"var\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<initialiser>[^;]+);",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LocalDeclarationRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])\d+(\.\d+)?(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLiteralRegex();
}
