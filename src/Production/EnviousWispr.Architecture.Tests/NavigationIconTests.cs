using System.Text.RegularExpressions;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// One destination, one icon, everywhere it appears.
/// </summary>
/// <remarks>
/// A page's icon used to be written twice, once on the sidebar row and once on the page header,
/// with nothing joining them. Five pages drifted apart, and two of those landed on a DIFFERENT
/// page's icon, so Backup wore Clipboard's and Dictation history wore History's.
///
/// The earlier duplicate check compared sidebar rows against each other only, so a page header
/// colliding with a sidebar row was outside what it could ever see. These tests enumerate every
/// icon SITE rather than every icon of one kind, which is the difference that matters.
/// </remarks>
public sealed class NavigationIconTests
{
    /// <summary>A sidebar row: the tag it navigates to, its label, and the icon it draws.</summary>
    private sealed record NavigationRow(string Tag, string Label, string Glyph);

    /// <summary>A page header drawn straight into the markup rather than derived at runtime.</summary>
    private sealed record StaticHeader(string Title, string Glyph);

    // ROW AGAINST ROW IS NOT HERE ON PURPOSE. EveryNavigationRowHasItsOwnIcon in
    // TranscriptionEngineNameTests already asks it, and asks it better: it also refuses a built-in
    // symbol, which is the spelling axis a glyph-only comparison silently cannot see. A second,
    // weaker answer to the same question is how two gates end up disagreeing.

    /// <summary>
    /// A page header must declare the same icon code as its own sidebar row.
    /// </summary>
    /// <remarks>
    /// The check the old sweep could not perform: a page header and a sidebar row are different
    /// SITES, so a collision between them was invisible to a row-against-row comparison.
    ///
    /// WHAT THIS CANNOT CLOSE, STATED BECAUSE THE NAME WOULD OTHERWISE PROMISE IT. Two different
    /// codes can be the same drawing - E8A5 and E7C3 are byte-identical folded-corner documents.
    /// This compares declared codes, so a header could name a different code that renders the
    /// identical picture and pass. That axis lives in the font file, nothing in this repository can
    /// reach it, and it needs eyes.
    /// Owner: `.claude/knowledge/design-system.md` FACT: the-icon-font-has-a-hole-and-two-codes-can-be-one-picture.
    /// </remarks>
    [Fact]
    public void EveryPageHeaderDeclaresItsOwnSidebarRowsIconCode()
    {
        var rows = SidebarRows();
        var headers = StaticHeaders();
        Assert.True(headers.Count >= 4, $"Only {headers.Count} page headers were parsed, so this check swept less than the app.");

        var wrong = new List<string>();
        foreach (var header in headers)
        {
            var owner = rows.FirstOrDefault(row => LabelsMatch(row.Label, header.Title));
            Assert.True(owner is not null, $"Page header '{header.Title}' has no sidebar row, so nothing owns its icon.");

            if (owner!.Glyph != header.Glyph)
            {
                var thief = rows.FirstOrDefault(row => row.Glyph == header.Glyph && row != owner);
                var borrowed = thief is null ? "" : $" That code belongs to {thief.Label}.";
                wrong.Add($"'{header.Title}' header declares {Describe(header.Glyph)} but its sidebar row declares {Describe(owner.Glyph)}.{borrowed}");
            }
        }

        Assert.True(wrong.Count == 0, "A page header disagrees with its own sidebar row:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The settings and help headers take their icon from the sidebar at runtime, which is what
    /// makes them unable to drift. That only holds while every tag they answer has a row to read.
    /// </summary>
    [Fact]
    public void EveryPageTagTheHeaderAnswersHasASidebarRowToReadFrom()
    {
        var tags = SidebarRows().Select(row => row.Tag).ToHashSet(StringComparer.Ordinal);

        var answered = PageDispatches()
            // \r? BEFORE THE ANCHOR. `$` in multiline mode matches before the \n, so on a checkout
            // with Windows line endings the \r sits exactly where this pattern expects the line to
            // end. It parsed fourteen tags on the development Mac and zero on CI, which reads as a
            // broken app rather than a broken pattern.
            .SelectMany(dispatch => Regex.Matches(dispatch.Body, "^\\s*\"([a-z0-9-]+)\" => \\(\\r?$", RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value))
            .ToArray();

        Assert.True(answered.Length >= 14,
            $"Only {answered.Length} page tags were parsed. Eleven settings tags and three help tags are " +
            "expected, so a lower number means the sweep is reading less than the app.");

        var orphans = answered.Where(tag => !tags.Contains(tag)).Distinct().ToArray();
        Assert.True(orphans.Length == 0,
            "A page asks the sidebar for an icon under a tag no sidebar row carries, so its header " +
            "renders blank: " + string.Join(", ", orphans));
    }

    /// <summary>
    /// Neither header may carry a glyph of its own.
    /// </summary>
    /// <remarks>
    /// This is the whole property. A page that writes its own glyph is a second place to change one
    /// fact, and the five pages that drifted apart all drifted that way. The header must derive the
    /// icon from the tag it was handed, so the only way to be wrong is to have no row at all, which
    /// is what the test above covers.
    ///
    /// The tag used to be repeated inside each switch arm to carry it to the lookup. That was
    /// removed: an unrelated gate slices a switch arm at the next tag literal, so the copy ended
    /// the arm before its own section and reported that Appearance led nowhere. The arm's case
    /// label was always the tag, so the copy was never needed.
    /// </remarks>
    [Fact]
    public void NeitherPageHeaderCarriesAGlyphOfItsOwn()
    {
        foreach (var dispatch in PageDispatches())
        {
            var literals = Regex.Matches(dispatch.Body, @"""\\u[0-9A-Fa-f]{4}""")
                .Select(match => match.Value)
                .ToArray();

            // PageDispatches already refuses to return a body unless the call is there, so the
            // presence of the lookup is asserted once, where the slice is taken. Repeating it here
            // would assert against a body that stops just before the call.
            Assert.True(literals.Length == 0,
                $"{dispatch.Name} writes its own icon instead of reading the sidebar's: {string.Join(", ", literals)}");
        }
    }

    // ---- parsing ----

    /// <summary>The two methods that build a page header.</summary>
    private static readonly string[] PageDispatchNames = ["ConfigureSettingsPage", "ConfigureHelpPage"];

    /// <summary>The two page dispatches, sliced from their signature to where they set the glyph.</summary>
    private static (string Name, string Body)[] PageDispatches()
    {
        var code = File.ReadAllText(AppSourcePath("MainWindow.xaml.cs"));
        return PageDispatchNames
            .Select(name =>
            {
                var start = code.IndexOf($"private void {name}(string tag)", StringComparison.Ordinal);
                Assert.True(start >= 0, $"{name} was not found, so this check verified nothing.");
                var end = code.IndexOf("NavigationGlyphFor(tag)", start, StringComparison.Ordinal);
                Assert.True(end > start, $"{name} no longer reads its icon from the sidebar.");
                return (name, code[start..end]);
            })
            .ToArray();
    }

    private static List<NavigationRow> SidebarRows()
    {
        var markup = File.ReadAllText(AppSourcePath("MainWindow.xaml"));
        var rows = Regex.Matches(
                markup,
                "<NavigationViewItem\\b(?<attributes>[^>]*?)Tag=\"(?<tag>[^\"]+)\"[^>]*>\\s*<NavigationViewItem.Icon>\\s*<FontIcon\\b[^>]*?Glyph=\"&#x(?<glyph>[0-9A-Fa-f]{4});\"")
            .Select(match => new NavigationRow(
                match.Groups["tag"].Value,
                Regex.Match(match.Value, "Content=\"(?<label>[^\"]*)\"").Groups["label"].Value,
                match.Groups["glyph"].Value.ToUpperInvariant()))
            .ToList();

        Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(row.Label), $"Sidebar row '{row.Tag}' parsed with no label."));
        return rows;
    }

    private static List<StaticHeader> StaticHeaders()
    {
        var markup = File.ReadAllText(AppSourcePath("MainWindow.xaml"));
        return Regex.Matches(
                markup,
                "BrandIconTileStyle\\}\"><FontIcon [^>]*Glyph=\"&#x(?<glyph>[0-9A-Fa-f]{4});\"[^>]*/></Border>\\s*<StackPanel[^>]*>\\s*<TextBlock Style=\"\\{StaticResource BrandPageTitleStyle\\}\" Text=\"(?<title>[^\"]+)\"")
            .Select(match => new StaticHeader(match.Groups["title"].Value, match.Groups["glyph"].Value.ToUpperInvariant()))
            .ToList();
    }

    /// <summary>The sidebar writes a curly apostrophe; comparing raw text would call that a miss.</summary>
    private static bool LabelsMatch(string label, string title) =>
        Normalise(label) == Normalise(title);

    private static string Normalise(string value) =>
        value.Replace('’', '\'').Replace("&#x2019;", "'").Trim();

    private static string Describe(string glyph) => $"icon U+{glyph}";

    private static string AppSourcePath(string fileName) =>
        Path.Combine(RepositoryRoot(), "src", "Production", "EnviousWispr.App", fileName);

    private static string RepositoryRoot()
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
            $"Could not find the repository root above '{AppContext.BaseDirectory}'.");
    }
}
