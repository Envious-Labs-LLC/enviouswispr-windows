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
        var bars = LevelBars();
        var railWidth = bars.Sum(bar => double.Parse(
            (string?)bar.Attribute("Width") ?? "0", CultureInfo.InvariantCulture))
            + BarSpacing * (bars.Count - 1);
        var overlay = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"));
        var railConst = Regex.Match(overlay, @"LevelRailBarsWidth\s*=\s*(?<w>\d+(?:\.\d+)?)\s*;");
        var paddingConst = Regex.Match(overlay, @"CapsuleHorizontalPadding\s*=\s*(?<p>\d+)\s*;");
        Assert.True(railConst.Success && paddingConst.Success);

        var declaredRailWidth = double.Parse(railConst.Groups["w"].Value, CultureInfo.InvariantCulture);
        var padding = double.Parse(paddingConst.Groups["p"].Value, CultureInfo.InvariantCulture);
        Assert.True(
            declaredRailWidth >= railWidth,
            $"LevelRailBarsWidth ({declaredRailWidth}) is narrower than the 24 markup bars ({railWidth}).");
        Assert.Equal(14, padding);
    }

    [Fact]
    public void TheMeterSitsBesideTheTimerOnBothThePillAndItsPicture()
    {
        // THE PAGE DESCRIBED A PILL THE APP DID NOT BUILD. The preview drew the meter alongside the
        // timer under a caption saying "beside the timer", and the real pill drew it on its own row
        // underneath. Somebody choosing this design got a different shape from the one they picked.
        Assert.True(
            TimerAndRailAreSiblings(
                XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Production",
                    "EnviousWispr.App", "DictationOverlayWindow.xaml"))),
            "The recording pill draws its level meter somewhere other than beside the timer.");

        Assert.True(
            TimerAndRailAreSiblings(
                XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Production",
                    "EnviousWispr.App", "MainWindow.xaml"))),
            "The Level Rail preview draws its meter somewhere other than beside the timer.");
    }

    /// <summary>Whether a timer and a bar strip share one row container in this markup.</summary>
    private static bool TimerAndRailAreSiblings(XDocument markup) => markup
        .Descendants()
        .Any(container =>
        {
            var children = container.Elements().ToArray();
            var hasRail = children.Any(child => child.Name.LocalName == "StackPanel"
                && (string?)child.Attribute("Orientation") == "Horizontal"
                && child.Elements().Count(bar => bar.Name.LocalName == "Border") > 1);
            var hasTimer = children.Any(child => child.Name.LocalName == "TextBlock"
                && (string?)child.Attribute("FontFamily") == "Consolas");
            return hasRail && hasTimer;
        });

    [Fact]
    public void ThePreviewIsTheWidthOfThePillItIsAPictureOf()
    {
        // Both the live Capsule/Level Rail pills and their settings previews hug their contents
        // dynamically with 14 DIP symmetric horizontal padding instead of oversized fixed widths.
        var overlay = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"));
        Assert.Matches(
            new Regex(@"case RecordingPillDesign\.LevelRail:.*?Resize\(LevelRailPillWidth\(\), RecordingCapsuleHeight\);", RegexOptions.Singleline),
            overlay);
        Assert.Matches(
            new Regex(@"case RecordingPillDesign\.Classic:.*?Resize\(CapsulePillWidth\(\), RecordingCapsuleHeight\);", RegexOptions.Singleline),
            overlay);

        var markup = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        foreach (var cardName in new[] { "CapsulePillButton", "LevelRailPillButton" })
        {
            var card = markup.Descendants().FirstOrDefault(element =>
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == cardName);
            Assert.True(card is not null, $"{cardName} was not found in MainWindow.xaml.");
            var capsule = card!.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Border"
                    && element.Attribute("CornerRadius") is not null);
            Assert.True(capsule is not null, $"{cardName} draws no capsule to measure.");
            Assert.Null(capsule!.Attribute("Width"));
        }
    }

    /// <summary>The gap between bars, as the markup declares it (RainbowLevelMeter's 2pt rail spacing).</summary>
    private const double BarSpacing = 2;

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
