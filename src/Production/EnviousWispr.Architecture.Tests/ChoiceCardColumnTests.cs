using System.Globalization;
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

    // Founder directive 2026-11-26: EG-1 size cards mirror macOS 3-column horizontal EGOneSizeCards layout.
    private static readonly HashSet<string> EgOneSizeHorizontalCards = new(StringComparer.Ordinal)
    {
        "EgOneSizeFullCard",
        "EgOneSizeMiniCard",
        "EgOneSizeMicroCard",
    };

    // Wave C Onboarding side-by-side cards mirror macOS Speech Compare, Polish Pick, and See All EG-1 grids.
    private static readonly HashSet<string> OnboardingHorizontalCards = new(StringComparer.Ordinal)
    {
        "OnboardingParakeetCompareCard",
        "OnboardingWhisperCompareCard",
        "OnboardingPolishPrimaryCard",
        "OnboardingPolishSecondaryCard",
        "OnboardingSeeAllEgOneFullCard",
        "OnboardingSeeAllEgOneMiniCard",
        "OnboardingSeeAllEgOneMicroCard",
    };

    private static bool IsApprovedThreeColumnCard(XElement card)
    {
        var name = (string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? string.Empty;
        return RecordingPillHorizontalCards.Contains(name)
            || EgOneSizeHorizontalCards.Contains(name)
            || OnboardingHorizontalCards.Contains(name);
    }

    [Fact]
    public void NoChoiceCardIsPlacedInASecondColumnExceptApprovedThreeColumnSelectors()
    {
        var cards = SelectableCards();
        Assert.True(cards.Count >= 4, $"Expected the choice cards, found {cards.Count}.");

        var sideBySide = cards
            .Where(card => !IsApprovedThreeColumnCard(card))
            .Where(card => (string?)card.Attribute("Grid.Column") is { } column && column != "0")
            .Select(card => (string?)card.Attribute(XName.Get("Name", XamlNamespace)) ?? "unnamed")
            .ToArray();

        Assert.True(
            sideBySide.Length == 0,
            "These choice cards sit in a second column, so their group is not one full-width column: "
                + string.Join(", ", sideBySide));
    }

    [Fact]
    public void NoChoiceCardSitsInsideAMultiColumnGridExceptApprovedThreeColumnSelectors()
    {
        var offenders = SelectableCards()
            .Where(card => !IsApprovedThreeColumnCard(card))
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

    [Fact]
    public void EgOneSizeCardsSitTogetherInOneEqualThreeColumnGridWithUnifiedFooters()
    {
        var doc = Markup();
        var cards = doc.Descendants()
            .Where(element => EgOneSizeHorizontalCards.Contains((string?)element.Attribute(XName.Get("Name", XamlNamespace)) ?? string.Empty))
            .ToArray();
        Assert.Equal(3, cards.Length);

        var containers = cards.Select(c => c.Parent).ToArray();
        Assert.All(containers, c => Assert.NotNull(c));
        Assert.All(containers, c => Assert.Equal("Grid", c!.Name.LocalName));

        var grid = containers[0]!.Parent;
        Assert.NotNull(grid);
        Assert.Equal("EgOneCardsGrid", (string?)grid!.Attribute(XName.Get("Name", XamlNamespace)));
        Assert.All(containers, c => Assert.Same(grid, c!.Parent));

        var columns = grid.Elements()
            .FirstOrDefault(child => child.Name.LocalName == "Grid.ColumnDefinitions")?
            .Elements()
            .Select(col => (string?)col.Attribute("Width") ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(["*", "*", "*"], columns);
        Assert.Equal(["0", "1", "2"], containers.Select(c => (string?)c!.Attribute("Grid.Column") ?? string.Empty).ToArray());
        Assert.Equal("EgOneCardsGrid_SizeChanged", (string?)grid.Attribute("SizeChanged"));

        var rows = grid.Elements()
            .FirstOrDefault(child => child.Name.LocalName == "Grid.RowDefinitions")?
            .Elements()
            .Select(row => (string?)row.Attribute("Height") ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(["Auto", "Auto", "Auto"], rows);
    }

    [Fact]
    public void OnboardingCompareAndPolishChoiceCardsSitInResponsiveEqualWidthGrids()
    {
        var doc = Markup();
        AssertResponsiveEqualGrid(
            doc,
            "OnboardingSpeechCompareGrid",
            "OnboardingSpeechCompareGrid_SizeChanged",
            ["OnboardingParakeetCompareCard", "OnboardingWhisperCompareCard"]);
        AssertResponsiveEqualGrid(
            doc,
            "OnboardingPolishPickGrid",
            "OnboardingPolishPickGrid_SizeChanged",
            ["OnboardingPolishPrimaryCard", "OnboardingPolishSecondaryCard"]);
        AssertResponsiveEqualGrid(
            doc,
            "OnboardingSeeAllEgOneGrid",
            "OnboardingSeeAllEgOneGrid_SizeChanged",
            ["OnboardingSeeAllEgOneFullCard", "OnboardingSeeAllEgOneMiniCard", "OnboardingSeeAllEgOneMicroCard"]);
    }

    private static void AssertResponsiveEqualGrid(
        XDocument doc,
        string gridName,
        string sizeChangedHandler,
        string[] expectedCardNames)
    {
        var cards = doc.Descendants()
            .Where(element => expectedCardNames.Contains((string?)element.Attribute(XName.Get("Name", XamlNamespace)) ?? string.Empty))
            .ToArray();
        Assert.Equal(expectedCardNames.Length, cards.Length);

        var grid = cards[0].Parent;
        Assert.NotNull(grid);
        Assert.Equal("Grid", grid!.Name.LocalName);
        Assert.Equal(gridName, (string?)grid.Attribute(XName.Get("Name", XamlNamespace)));
        Assert.All(cards, card => Assert.Same(grid, card.Parent));
        Assert.Equal(sizeChangedHandler, (string?)grid.Attribute("SizeChanged"));

        var columns = grid.Elements()
            .FirstOrDefault(child => child.Name.LocalName == "Grid.ColumnDefinitions")?
            .Elements()
            .Select(col => (string?)col.Attribute("Width") ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(Enumerable.Repeat("*", expectedCardNames.Length).ToArray(), columns);
        Assert.Equal(
            Enumerable.Range(0, expectedCardNames.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(),
            cards.Select(card => (string?)card.Attribute("Grid.Column") ?? string.Empty).ToArray());
    }

    private static List<XElement> SelectableCards() => Markup()
        .Descendants()
        .Where(element => (string?)element.Attribute("Style") == "{StaticResource BrandSelectableCardStyle}")
        .ToList();

    private static XDocument Markup() => AppXamlSources.LoadMainWindowAndPagesDocument(RepositoryRoot());

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
