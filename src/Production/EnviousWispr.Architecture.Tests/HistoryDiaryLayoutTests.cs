using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Guards the Wave 3 full-height two-pane History Diary layout (#322).
/// </summary>
public sealed class HistoryDiaryLayoutTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void HistoryPageIsFullHeightTwoPaneGridWithoutLegacyBloat()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var document = XDocument.Parse(markup);

        var historyPage = FindNamed(document, "HistoryPage");
        Assert.Equal("Grid", historyPage.Name.LocalName);
        Assert.Equal("1", (string?)historyPage.Attribute("Grid.Row"));
        Assert.Equal("Collapsed", (string?)historyPage.Attribute("Visibility"));

        var columns = historyPage.Element(XName.Get("Grid.ColumnDefinitions", PresentationNamespace))?
            .Elements(XName.Get("ColumnDefinition", PresentationNamespace))
            .Select(col => (string?)col.Attribute("Width") ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(["304", "*"], columns);

        Assert.DoesNotContain("FoundationInfoBar", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Parakeet v2", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(
            historyPage.Descendants(),
            element => (string?)element.Attribute("Style") == "{StaticResource BrandHeaderCardStyle}");

        var historyList = FindNamed(document, "HistoryList");
        Assert.Null(historyList.Attribute("MaxHeight"));

        var detailTextBox = FindNamed(document, "HistoryDetailTextBox");
        Assert.Null(detailTextBox.Attribute("MaxHeight"));
        var detailScroller = detailTextBox.Parent;
        Assert.NotNull(detailScroller);
        Assert.Equal("ScrollViewer", detailScroller!.Name.LocalName);
        Assert.Null(detailScroller.Attribute("MaxHeight"));

        var engineStatus = FindNamed(document, "HistoryEngineStatusText");
        Assert.Equal("Automatic", (string?)engineStatus.Attribute("Text"));

        var polishStatus = FindNamed(document, "HistoryPolishStatusText");
        Assert.Equal("Off", (string?)polishStatus.Attribute("Text"));
    }

    [Fact]
    public void HistoryLeftRailHasFiveRowsSegmentedFiltersAndLightweightEmptyStates()
    {
        var document = LoadMainWindow();
        var historyPage = FindNamed(document, "HistoryPage");
        var leftRailBorder = historyPage.Elements(XName.Get("Border", PresentationNamespace))
            .Single(b => (string?)b.Attribute("Grid.Column") == "0");
        var leftRailGrid = leftRailBorder.Elements(XName.Get("Grid", PresentationNamespace)).Single();

        var rows = leftRailGrid.Element(XName.Get("Grid.RowDefinitions", PresentationNamespace))?
            .Elements(XName.Get("RowDefinition", PresentationNamespace))
            .Select(row => (string?)row.Attribute("Height") ?? string.Empty)
            .ToArray() ?? [];
        Assert.Equal(["Auto", "Auto", "Auto", "*", "Auto"], rows);

        Assert.Equal("All", (string?)FindNamed(document, "HistoryFilterAllButton").Attribute("Tag"));
        Assert.Equal("Dictation", (string?)FindNamed(document, "HistoryFilterDictationButton").Attribute("Tag"));
        Assert.Equal("TranscribeFile", (string?)FindNamed(document, "HistoryFilterTranscribeButton").Attribute("Tag"));

        foreach (var stateName in new[]
                 {
                     "HistoryLoadingState",
                     "HistoryEmptyState",
                     "HistorySearchEmptyState",
                     "HistoryUnavailableState",
                 })
        {
            var state = FindNamed(document, stateName);
            Assert.NotEqual("Border", state.Name.LocalName);
            Assert.NotEqual("{StaticResource BrandSectionCardStyle}", (string?)state.Attribute("Style"));
            Assert.DoesNotContain(
                state.Descendants(),
                element => (string?)element.Attribute("Style") == "{StaticResource BrandIconTileStyle}");
        }
    }

    [Fact]
    public void HistoryDetailEmptyStateIsCenteredAndBorderlessAndShowPageTogglesEdgeToEdgePadding()
    {
        var root = FindRepositoryRoot();
        var document = LoadMainWindow();

        var emptyState = FindNamed(document, "HistoryDetailEmptyState");
        Assert.NotEqual("Border", emptyState.Name.LocalName);
        Assert.NotEqual("{StaticResource BrandSectionCardStyle}", (string?)emptyState.Attribute("Style"));
        Assert.Equal("Center", (string?)emptyState.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)emptyState.Attribute("VerticalAlignment"));
        Assert.Contains(
            emptyState.Descendants(XName.Get("TextBlock", PresentationNamespace)),
            tb => (string?)tb.Attribute("Text") == "Ready to Transcribe");

        var recoveryCard = FindNamed(document, "RecoveryCard");
        Assert.Equal("Collapsed", (string?)recoveryCard.Attribute("Visibility"));

        var mainContentGrid = FindNamed(document, "MainContentGrid");
        Assert.Equal("28,18,28,32", (string?)mainContentGrid.Attribute("Padding"));

        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        Assert.Contains("MainContentGrid.Padding =", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new Thickness(0)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new Thickness(28, 18, 28, 32)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OperationInfoBar.Margin =", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("FoundationInfoBar", codeBehind, StringComparison.Ordinal);

        var deletionBar = FindNamed(document, "DeletionLeftoverInfoBar");
        Assert.Equal("2", (string?)deletionBar.Attribute("Grid.Row"));
        Assert.Equal("True", (string?)deletionBar.Attribute("IsClosable"));

        var yourDataCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.YourData.cs"));
        Assert.Contains("DeletionLeftoverInfoBar.IsOpen = true;", yourDataCode, StringComparison.Ordinal);

        var detailContent = FindNamed(document, "HistoryDetailContent");
        var headerBorder = detailContent.Elements(XName.Get("Border", PresentationNamespace))
            .Single(b => (string?)b.Attribute("Grid.Row") == "0");
        var headerStack = headerBorder.Elements(XName.Get("StackPanel", PresentationNamespace)).Single();
        Assert.Contains(headerStack.Descendants(), e => (string?)e.Attribute(XName.Get("Name", XamlNamespace)) == "HistoryDetailTimestampText");
        var pasteButton = FindNamed(document, "PasteHistoryButton");
        var actionBarScroller = pasteButton.Parent?.Parent;
        Assert.NotNull(actionBarScroller);
        Assert.Equal("ScrollViewer", actionBarScroller!.Name.LocalName);
        Assert.Equal("Auto", (string?)actionBarScroller.Attribute("HorizontalScrollBarVisibility"));
    }

    private static XDocument LoadMainWindow()
    {
        var root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
    }

    private static XElement FindNamed(XDocument document, string name) =>
        document.Descendants().Single(element =>
            (string?)element.Attribute(XName.Get("Name", XamlNamespace)) == name);

    private static string FindRepositoryRoot()
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
