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

    [Fact]
    public void EverySidebarRowDrawsAnIconNoOtherRowDraws()
    {
        var rows = SidebarRows();
        Assert.True(rows.Count >= 18, $"Only {rows.Count} sidebar rows were parsed, so this check swept less than the sidebar.");

        var collisions = rows
            .GroupBy(row => row.Glyph)
            .Where(group => group.Count() > 1)
            .Select(group => $"{Describe(group.Key)} is on: {string.Join(", ", group.Select(row => row.Label))}")
            .ToArray();

        Assert.True(collisions.Length == 0, "Two sidebar rows draw the same icon:\n  " + string.Join("\n  ", collisions));
    }

    /// <summary>
    /// The check the old sweep could not perform. A page header and a sidebar row are different
    /// sites, so a collision between them was invisible to a row-against-row comparison.
    /// </summary>
    [Fact]
    public void NoPageHeaderWearsAnotherPagesIcon()
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
                var borrowed = thief is null ? "" : $" That icon belongs to {thief.Label}.";
                wrong.Add($"'{header.Title}' header draws {Describe(header.Glyph)} but its sidebar row draws {Describe(owner.Glyph)}.{borrowed}");
            }
        }

        Assert.True(wrong.Count == 0, "A page header disagrees with its own sidebar row:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The settings and help headers take their icon from the sidebar at runtime, which is what
    /// makes them unable to drift. That only holds while every tag they pass has a row to read.
    /// </summary>
    [Fact]
    public void EveryPageTagPassedToTheHeaderHasASidebarRowToReadFrom()
    {
        var tags = SidebarRows().Select(row => row.Tag).ToHashSet(StringComparer.Ordinal);
        var code = File.ReadAllText(AppSourcePath("MainWindow.xaml.cs"));

        var requested = new List<string>();
        foreach (var method in new[] { "ConfigureSettingsPage", "ConfigureHelpPage" })
        {
            var start = code.IndexOf($"private void {method}(string tag)", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{method} was not found, so this check verified nothing.");
            var end = code.IndexOf("NavigationGlyphFor(navigationTag);", start, StringComparison.Ordinal);
            Assert.True(end > start, $"{method} no longer reads its icon from the sidebar.");

            // BOTH CLOSING SHAPES, AND THE SECOND ONE IS WHY THIS COMMENT EXISTS. The tag is the
            // last tuple element on the help pages, so it ends in a bracket rather than a comma.
            // A comma-only pattern parsed twelve settings tags, zero help tags, and reported a
            // clean sweep, which is the same shape of miss this whole file is about.
            requested.AddRange(Regex.Matches(code[start..end], "^\\s*\"([a-z0-9-]+)\"[,)]", RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value));
        }

        Assert.True(requested.Count >= 16,
            $"Only {requested.Count} page tags were parsed. Twelve settings tags and four help tags are expected, " +
            "so a lower number means the sweep is reading less than the app.");

        var orphans = requested.Where(tag => !tags.Contains(tag)).Distinct().ToArray();
        Assert.True(orphans.Length == 0,
            "A page asks the sidebar for an icon under a tag no sidebar row carries, so its header renders blank: " +
            string.Join(", ", orphans));
    }

    /// <summary>
    /// A settings or help page must ask the sidebar for its OWN row.
    /// </summary>
    /// <remarks>
    /// Deriving the header icon from the sidebar removes drift and leaves one way to still be
    /// wrong: naming the wrong row. Backup pointed at Clipboard's row would render a perfectly
    /// consistent, perfectly wrong icon, and every other check here would pass it. That is not
    /// hypothetical: showing Clipboard's icon on Backup is the exact defect this file was
    /// written for, in its new clothes.
    /// </remarks>
    [Fact]
    public void EveryPageAsksTheSidebarForItsOwnRow()
    {
        var code = File.ReadAllText(AppSourcePath("MainWindow.xaml.cs"));
        var mismatched = new List<string>();
        var checkedBranches = 0;

        foreach (var method in new[] { "ConfigureSettingsPage", "ConfigureHelpPage" })
        {
            var start = code.IndexOf($"private void {method}(string tag)", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{method} was not found, so this check verified nothing.");
            var end = code.IndexOf("NavigationGlyphFor(navigationTag);", start, StringComparison.Ordinal);
            Assert.True(end > start, $"{method} no longer reads its icon from the sidebar.");

            foreach (Match branch in Regex.Matches(
                code[start..end],
                // Verbatim, so the regex escapes stay regex escapes. Written as an ordinary
                // string this line had four single backslashes the C# compiler read as its
                // own escapes, and CS1009 stopped the whole build.
                @"""(?<case>[a-z0-9-]+)"" => \(\s*""[^""]*"",\s*""[^""]*"",\s*""(?<asked>[a-z0-9-]+)"""))
            {
                checkedBranches++;
                var declared = branch.Groups["case"].Value;
                var asked = branch.Groups["asked"].Value;
                if (declared != asked)
                {
                    mismatched.Add($"the '{declared}' page takes its icon from the '{asked}' sidebar row");
                }
            }
        }

        Assert.True(checkedBranches >= 14,
            $"Only {checkedBranches} page branches were parsed, so this check swept less than the app.");
        Assert.True(mismatched.Count == 0,
            "A page wears a different page's icon:\n  " + string.Join("\n  ", mismatched));
    }

    // ---- parsing ----

    private static List<NavigationRow> SidebarRows()
    {
        var markup = File.ReadAllText(AppSourcePath("MainWindow.xaml"));
        var rows = Regex.Matches(
                markup,
                "<NavigationViewItem\\b(?<attributes>[^>]*?)Tag=\"(?<tag>[^\"]+)\"[^>]*>\\s*<NavigationViewItem.Icon>\\s*<FontIcon Glyph=\"&#x(?<glyph>[0-9A-Fa-f]{4});\"")
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
