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
    public void TheBarsAreDrawnFromStoredSamplesRatherThanFromOneLiveNumber()
    {
        // THE DEFECT WAS A SHAPE, SO THE CHECK IS ABOUT SHAPE. A rail driven from the current level
        // needs no memory at all; one that shows a history cannot work without it, and the sine wave
        // that dressed one number up as many is exactly what must not come back.
        var overlay = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"));

        Assert.Contains("_levelHistory", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("MathF.Sin", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleIsTakenOnACadenceRatherThanOnEveryBuffer()
    {
        // Capture reports a level per audio buffer, hundreds of times a second. Drawing every one
        // scrolls a second of speech past in an eighth of a second, which reads as noise.
        var overlay = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml.cs"));

        Assert.Contains("LevelSampleInterval", overlay, StringComparison.Ordinal);
        Assert.Contains("FromMilliseconds(50)", overlay, StringComparison.Ordinal);
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
