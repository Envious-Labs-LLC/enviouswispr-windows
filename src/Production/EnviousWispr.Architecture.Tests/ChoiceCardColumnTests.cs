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

    private static readonly HashSet<string> RecordingPillHorizontalCards = new(StringComparer.Ordinal)
    {
        "CapsulePillButton",
        "LevelRailPillButton",
        "ReadingWellPillButton",
    };

    [Fact]
    public void NoChoiceCardIsPlacedInASecondColumnExceptTheThreeColumnPillSelector()
    {
        var cards = SelectableCards();
        Assert.True(cards.Count >= 4, $"Expected the choice cards, found {cards.Count}.");

        var sideBySide = cards
            .Where(card => !RecordingPillHorizontalCards.Contains((string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? string.Empty))
            .Where(card => (string?)card.Attribute("Grid.Column") is { } column && column != "0")
            .Select(card => (string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? "unnamed")
            .ToArray();

        Assert.True(
            sideBySide.Length == 0,
            "These choice cards sit in a second column, so their group is not one full-width column: "
                + string.Join(", ", sideBySide));
    }

    [Fact]
    public void NoChoiceCardSitsInsideAMultiColumnGridExceptTheThreeColumnPillSelector()
    {
        var offenders = SelectableCards()
            .Where(card => !RecordingPillHorizontalCards.Contains((string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? string.Empty))
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

    [Fact]
    public void RecordingPillCardsSitTogetherInOneEqualThreeColumnGrid()
    {
        var cards = SelectableCards()
            .Where(card => RecordingPillHorizontalCards.Contains((string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? string.Empty))
            .ToArray();
        Assert.Equal(3, cards.Length);

        var parentGrid = cards[0].Parent;
        Assert.NotNull(parentGrid);
        Assert.Equal("Grid", parentGrid!.Name.LocalName);
        Assert.All(cards, card => Assert.Same(parentGrid, card.Parent));

        var columns = parentGrid.Elements()
            .FirstOrDefault(child => child.Name.LocalName == "Grid.ColumnDefinitions")?
            .Elements()
            .Select(col => (string?)col.Attribute("Width") ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(["*", "*", "*"], columns);
        Assert.Equal(["0", "1", "2"], cards.Select(card => (string?)card.Attribute("Grid.Column") ?? string.Empty).ToArray());
        Assert.Equal("RecordingPillCardsGrid_SizeChanged", (string?)parentGrid.Attribute("SizeChanged"));
        var rows = parentGrid.Elements()
            .FirstOrDefault(child => child.Name.LocalName == "Grid.RowDefinitions")?
            .Elements()
            .Select(row => (string?)row.Attribute("Height") ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(["Auto", "Auto", "Auto"], rows);
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
