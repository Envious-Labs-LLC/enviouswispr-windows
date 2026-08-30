using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A group of choice cards is one full-width column, and nothing was holding it to that.
/// </summary>
/// <remarks>
/// THE RULE WAS WRITTEN DOWN AND THEN CONTRADICTED ON ONE PAGE. design-system.md says one full-width
/// column always, and pins the column count rather than letting a layout negotiate one. The recording
/// pill group put Capsule and Level Rail side by side while Reading Well beneath them ran full width,
/// so a single set of choices used two layouts at once.
///
/// A RULE IN A DOCUMENT IS NOT A RULE. Nothing failed when the markup disagreed with it, so it
/// disagreed for as long as nobody was looking at that page - which is exactly how long it takes for
/// a written rule to become a written wish.
/// </remarks>
public sealed class ChoiceCardColumnTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NoChoiceCardIsPlacedInASecondColumn()
    {
        var cards = SelectableCards();
        Assert.True(cards.Count >= 4, $"Expected the choice cards, found {cards.Count}.");

        var sideBySide = cards
            .Where(card => (string?)card.Attribute("Grid.Column") is { } column && column != "0")
            .Select(card => (string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? "unnamed")
            .ToArray();

        Assert.True(
            sideBySide.Length == 0,
            "These choice cards sit in a second column, so their group is not one full-width column: "
                + string.Join(", ", sideBySide));
    }

    [Fact]
    public void NoChoiceCardSitsInsideAMultiColumnGrid()
    {
        // A CARD WITH NO Grid.Column IS STILL IN COLUMN ZERO OF A TWO-COLUMN GRID. Checking only the
        // attribute would pass a group that had been split by adding definitions around it.
        // THE CARD'S OWN PARENT, NOT ANY ANCESTOR. Walking up to the first Grid found the settings
        // page's own layout, which is legitimately two columns and says nothing about how a group of
        // choices is arranged inside one of them. What matters is the container that lays the cards
        // out, which is the one they are children of.
        var offenders = SelectableCards()
            .Where(card => card.Parent is { } parent &&
                parent.Name.LocalName == "Grid" &&
                parent.Elements()
                    .FirstOrDefault(child => child.Name.LocalName == "Grid.ColumnDefinitions")
                    is { } columns &&
                columns.Elements().Count() > 1)
            .Select(card => (string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? "unnamed")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These choice cards are laid out by a multi-column grid, so the group negotiates its "
                + "column count instead of pinning it: " + string.Join(", ", offenders));
    }

    private static List<XElement> SelectableCards() => Markup()
        .Descendants()
        .Where(element => (string?)element.Attribute("Style") == "{StaticResource BrandSelectableCardStyle}")
        .ToList();

    private static XDocument Markup() => XDocument.Load(Path.Combine(
        RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
