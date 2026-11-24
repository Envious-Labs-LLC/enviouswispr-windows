using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Recording Pill and Appearance are distinct sections routed separately, and the three pill
/// designs form one single-selection group.
/// </summary>
public sealed class PillDesignPlacementTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("CapsulePillButton")]
    [InlineData("LevelRailPillButton")]
    [InlineData("ReadingWellPillButton")]
    [InlineData("TopPositionSegmentButton")]
    [InlineData("BottomPositionSegmentButton")]
    public void EveryPillControlSitsInRecordingPillSection(string control)
    {
        var section = SectionContaining(control);

        Assert.Equal("RecordingPillSection", section);
    }

    [Theory]
    [InlineData("SystemThemeSegmentButton")]
    [InlineData("LightThemeSegmentButton")]
    [InlineData("DarkThemeSegmentButton")]
    [InlineData("StartAtLoginToggle")]
    public void EveryAppearanceControlSitsInAppearanceSection(string control)
    {
        var section = SectionContaining(control);

        Assert.Equal("AppearanceSection", section);
    }

    [Fact]
    public void RecordingPillDesignCardsFormOneMutuallyExclusiveGroupWithoutSplitHeadings()
    {
        var markup = Markup();
        Assert.Null(FindNamedElement(markup, "WithoutWordsPillHeading"));
        Assert.Null(FindNamedElement(markup, "WithWordsPillHeading"));

        foreach (var cardName in new[] { "CapsulePillButton", "LevelRailPillButton", "ReadingWellPillButton" })
        {
            var card = FindNamedElement(markup, cardName);
            Assert.NotNull(card);
            Assert.Equal("RecordingPillStyle", (string?)card!.Attribute("GroupName"));
        }
    }

    [Fact]
    public void RecordingPillAndAppearanceSectionsAreRoutedSeparately()
    {
        var code = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));

        var dictationTabMethod = Regex.Match(
            code,
            @"private void ShowSettingsSectionForDictationTab\(string tabKey\).*?\n    \}",
            RegexOptions.Singleline);
        Assert.True(dictationTabMethod.Success);
        Assert.Contains("\"pill\" =>", dictationTabMethod.Value, StringComparison.Ordinal);
        Assert.Contains("RecordingPillSection", dictationTabMethod.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("AppearanceSection", dictationTabMethod.Value, StringComparison.Ordinal);

        var appTabMethod = Regex.Match(
            code,
            @"private void ShowSettingsSectionForAppTab\(string tabKey\).*?\n    \}",
            RegexOptions.Singleline);
        Assert.True(appTabMethod.Success);
        Assert.Contains("AppearanceSection", appTabMethod.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordingPillSection", appTabMethod.Value, StringComparison.Ordinal);

        var sectionsList = Regex.Match(
            code,
            @"private Border\[\] SettingsSections\(\)\s*=>\s*\[(?<body>[^\]]+)\];",
            RegexOptions.Singleline);
        Assert.True(sectionsList.Success);
        Assert.Contains("RecordingPillSection", sectionsList.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("AppearanceSection", sectionsList.Groups["body"].Value, StringComparison.Ordinal);
    }

    private static XElement? FindNamedElement(XDocument markup, string name) =>
        markup.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == name);

    private static string SectionContaining(string controlName)
    {
        var markup = Markup();
        var control = FindNamedElement(markup, controlName);
        Assert.True(control is not null, $"{controlName} is not in MainWindow.xaml at all.");

        for (var parent = control!.Parent; parent is not null; parent = parent.Parent)
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
