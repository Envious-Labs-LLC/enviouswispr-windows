using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The rail showed one number twenty-four different ways, which looks alive and says nothing.
/// </summary>
/// <remarks>
/// macOS DRAWS A HISTORY AND WINDOWS DREW A MIRROR. Every bar was driven from the CURRENT level
/// through a sine wave, so a quiet moment and a loud one differed only in overall height and the
/// SHAPE of what somebody just said was nowhere on screen.
/// </remarks>
public sealed class RecordingLevelRailTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheRailHasTheTwentyFourBarsTheHistoryNeeds()
    {
        var bars = LevelBars();

        Assert.Equal(24, bars.Count);
    }

    [Fact]
    public void EveryBarCarriesASpectrumColourSoTheRailReadsAsOne()
    {
        var bars = LevelBars();

        Assert.All(bars, bar =>
        {
            var background = (string?)bar.Attribute("Background") ?? string.Empty;
            Assert.Contains("BrandSpectrum", background, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheRailDrawsAsManyBarsAsTheMeterKeepsLevels()
    {
        // The two are separate declarations - one in markup, one in Core - and a meter that keeps
        // more levels than the rail draws silently throws away the oldest ones twice.
        Assert.Equal(EnviousWispr.Core.Presentation.RecordingLevelHistory.Capacity, LevelBars().Count);
    }

    [Fact]
    public void TheRailFitsInsideThePillItIsDrawnOn()
    {
        // WIDTH WAS CHANGED TO 360 ON A GUESS AND PUT BACK, so this does the sum instead of holding
        // an opinion. The rail is its OWN ROW under the title and timer rather than beside them -
        // measuring it against the timer's width was arithmetic about a layout that does not exist,
        // and it happened to pass, which is the worst way for a geometry test to be wrong.
        var bars = LevelBars();
        var railWidth = bars.Sum(bar => double.Parse(
            (string?)bar.Attribute("Width") ?? "0", CultureInfo.InvariantCulture))
            + BarSpacing * (bars.Count - 1);
        var pillWidth = DeclaredPillWidth();

        Assert.True(
            railWidth + ContentMargin <= pillWidth,
            $"The Level Rail needs {railWidth + ContentMargin} but the pill is declared {pillWidth} "
                + "wide, so the newest bars clip.");
    }

    [Fact]
    public void ThePreviewIsTheWidthOfThePillItIsAPictureOf()
    {
        // Hardcoding the number let either side shrink while this stayed green. Both are read.
        Assert.Equal(DeclaredPillWidth(), DeclaredPreviewWidth());
    }

    /// <summary>The gap between bars, from the token the markup uses.</summary>
    private const double BarSpacing = 4;

    /// <summary>The overlay content panel's horizontal margin, both sides.</summary>
    private const double ContentMargin = 32;

    /// <summary>The width the overlay actually resizes the Level Rail pill to.</summary>
    private static double DeclaredPillWidth()
    {
        var overlay = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"));
        var match = Regex.Match(
            overlay,
            @"case RecordingPillDesign\.LevelRail:\s*Resize\((?<width>\d+),",
            RegexOptions.Singleline);
        Assert.True(
            match.Success,
            "The overlay no longer resizes the Level Rail to a literal width, so this test is "
                + "measuring against nothing.");
        return double.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The width of the capsule drawn on the Appearance page for this design.</summary>
    private static double DeclaredPreviewWidth()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var card = markup.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "LevelRailPillButton");
        Assert.True(card is not null, "LevelRailPillButton was not found in MainWindow.xaml.");

        var capsule = card!.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Border"
                && element.Attribute("CornerRadius") is not null
                && element.Attribute("Width") is not null);
        Assert.True(capsule is not null, "The Level Rail preview draws no capsule to measure.");
        return double.Parse(
            (string?)capsule!.Attribute("Width") ?? "0",
            CultureInfo.InvariantCulture);
    }

    private static List<XElement> LevelBars()
    {
        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml"));
        var rail = markup.Descendants()
            .FirstOrDefault(element =>
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == "LevelBars");
        Assert.True(rail is not null, "The overlay has no LevelBars panel, so nothing here is checking a rail.");
        return rail!.Elements().ToList();
    }

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
