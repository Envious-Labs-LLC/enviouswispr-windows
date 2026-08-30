using System.Globalization;
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
        // WIDTH WAS CHANGED TO 360 ON A GUESS AND PUT BACK. macOS returns 288 for this design, and
        // the arithmetic says it always fitted: what a guess costs is the parity number, silently.
        var bars = LevelBars();
        var barWidth = bars.Sum(bar => double.Parse(
            (string?)bar.Attribute("Width") ?? "0", CultureInfo.InvariantCulture));
        const double spacing = 4;
        const double timer = 52;
        const double gap = 8;
        const double padding = 32;
        var content = barWidth + spacing * (bars.Count - 1) + timer + gap + padding;

        Assert.True(
            content <= 288,
            $"The Level Rail needs {content} but the pill is 288 wide, so the newest bars clip.");
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
