using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The pill's look and the pill's position are one decision and belong on one page.
/// </summary>
/// <remarks>
/// THEY WERE SPLIT ACROSS TWO PAGES AND THE PAGE THAT PROMISED IT DID NOT HAVE IT. Appearance's own
/// subtitle says "the theme, and where the recording pill appears while you dictate", while the cards
/// that choose how the pill LOOKS sat on Live Preview. Nobody looking for the pill's appearance goes
/// to Live Preview, and worse, most people have Live Preview switched off, which is the page they are
/// least likely to open.
///
/// A PLACEMENT TEST BECAUSE PLACEMENT IS THE DEFECT. Nothing about the cards was wrong; where they
/// were was, and only a check that reads which section contains them can see that.
/// </remarks>
public sealed class PillDesignPlacementTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("CapsulePillButton")]
    [InlineData("LevelRailPillButton")]
    [InlineData("ReadingWellPillButton")]
    public void EveryPillDesignCardSitsOnTheAppearancePage(string card)
    {
        var section = SectionContaining(card);

        Assert.Equal("AppearanceSection", section);
    }

    [Fact]
    public void TheTwoPillQuestionsAreSeparatedSoTwoTicksReadAsTwoAnswers()
    {
        // Both groups are radio groups, so each legitimately shows a tick. Run together, the page
        // showed two ticked cards side by side, which reads as a bug whatever the helper text says.
        var markup = Markup();
        var appearance = markup.Descendants().First(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "AppearanceSection");

        var children = appearance.Descendants().ToList();
        var firstHeading = children.FindIndex(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "WithoutWordsPillHeading");
        var secondHeading = children.FindIndex(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "WithWordsPillHeading");
        Assert.True(firstHeading >= 0 && secondHeading > firstHeading, "The two pill groups are gone.");

        var divider = children.GetRange(firstHeading, secondHeading - firstHeading)
            .Any(element => element.Name.LocalName == "Border" &&
                (string?)element.Attribute("Height") == "1");

        Assert.True(
            divider,
            "Nothing separates the two recording pill questions, so their two ticked cards read as "
                + "one group with two answers.");
    }

    private static string SectionContaining(string cardName)
    {
        var markup = Markup();
        var card = markup.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == cardName);
        Assert.True(card is not null, $"{cardName} is not in MainWindow.xaml at all.");

        for (var parent = card!.Parent; parent is not null; parent = parent.Parent)
        {
            if ((string?)parent.Attribute(XName.Get("Name", XamlNamespace)) is { } name &&
                name.EndsWith("Section", StringComparison.Ordinal))
            {
                return name;
            }
        }

        return "no section";
    }

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
