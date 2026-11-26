using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class HistoryDiaryLayoutTests
{
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void HistoryPageIsSingleColumnCardDiaryWithoutLegacySplit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var document = LoadMainWindow(repositoryRoot);

        var historyPage = FindNamed(document, "HistoryPage");
        Assert.Equal("Grid", historyPage.Name.LocalName);
        Assert.Equal("1", (string?)historyPage.Attribute("Grid.Row"));
        Assert.Equal("Collapsed", (string?)historyPage.Attribute("Visibility"));
        Assert.Equal("{ThemeResource BrandPageBgBrush}", (string?)historyPage.Attribute("Background"));

        Assert.DoesNotContain(
            historyPage.Descendants(XName.Get("ColumnDefinition", PresentationNamespace)),
            column => (string?)column.Attribute("Width") == "304");
        Assert.DoesNotContain(
            historyPage.Descendants(),
            element => (string?)element.Attribute(XName.Get("Name", XamlNamespace))
                is "HistoryDetailContent" or "HistoryDetailEmptyState" or "HistoryDetailTextBox");

        var boundedColumn = historyPage
            .Elements(XName.Get("Grid", PresentationNamespace))
            .Single();
        Assert.Equal("{StaticResource BrandFluentPageMaxWidth}", (string?)boundedColumn.Attribute("MaxWidth"));

        var historyList = FindNamed(document, "HistoryList");
        Assert.Null(historyList.Attribute("MaxHeight"));
        Assert.Equal("None", (string?)historyList.Attribute("SelectionMode"));
        Assert.Equal("{StaticResource BrandHistoryListItemStyle}", (string?)historyList.Attribute("ItemContainerStyle"));

        var windowCode = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.xaml.cs"));
        Assert.Contains(
            "var isHistoryPage = resolvedTag == \"history\";",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "MainContentGrid.Padding = isHistoryPage",
            windowCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryHeaderHasTabStripSearchDateSelectHelpAndSelectionToolbar()
    {
        var document = LoadMainWindow(FindRepositoryRoot());
        var historyPage = FindNamed(document, "HistoryPage");

        Assert.DoesNotContain(
            historyPage.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                "HistoryFilterAllButton",
                StringComparison.Ordinal));
        Assert.Equal(
            "{StaticResource BrandTabStripActiveButtonStyle}",
            (string?)FindNamed(document, "HistoryFilterDictationButton").Attribute("Style"));
        Assert.Equal(
            "{StaticResource BrandTabStripButtonStyle}",
            (string?)FindNamed(document, "HistoryFilterTranscribeButton").Attribute("Style"));

        Assert.NotNull(FindNamed(document, "HistoryEngineStatusText"));
        Assert.NotNull(FindNamed(document, "HistoryPolishStatusText"));
        Assert.NotNull(FindNamed(document, "HistoryCountText"));
        Assert.NotNull(FindNamed(document, "HistorySearchBox"));
        Assert.NotNull(FindNamed(document, "InlineClearHistorySearchButton"));

        var dateFlyout = FindNamed(document, "HistoryDateFilterFlyout");
        var dateTags = dateFlyout
            .Descendants(XName.Get("MenuFlyoutItem", PresentationNamespace))
            .Select(item => (string?)item.Attribute("Tag") ?? string.Empty)
            .ToArray();
        Assert.Equal(["AnyTime", "Today", "Yesterday", "Last7Days", "Last30Days"], dateTags);

        Assert.NotNull(FindNamed(document, "HistorySelectToggleButton"));
        Assert.NotNull(FindNamed(document, "HistoryHelpButton"));
        Assert.NotNull(FindNamed(document, "HistoryHelpFlyout"));
        Assert.NotNull(FindNamed(document, "HistoryMoreDeleteAllItem"));

        var selectionToolbar = FindNamed(document, "HistorySelectionToolbar");
        Assert.Equal("Collapsed", (string?)selectionToolbar.Attribute("Visibility"));
        Assert.NotNull(FindNamed(document, "HistorySelectionCountText"));
        Assert.NotNull(FindNamed(document, "HistorySelectAllButton"));
        Assert.Equal(
            "{StaticResource BrandDestructiveButtonStyle}",
            (string?)FindNamed(document, "DeleteSelectedHistoryButton").Attribute("Style"));
        Assert.Equal(
            "{StaticResource BrandDestructiveButtonStyle}",
            (string?)FindNamed(document, "ClearHistoryButton").Attribute("Style"));

        Assert.DoesNotContain(
            historyPage.Descendants(),
            element => ((string?)element.Attribute("CornerRadius")) == "100"
                || ((string?)element.Attribute("CornerRadius"))?.Contains("BrandPillRadius", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void HistoryDiaryCardTemplateHasDayHeadersChipsShowMoreEditsComparisonPlaybackAndActions()
    {
        var document = LoadMainWindow(FindRepositoryRoot());
        var historyList = FindNamed(document, "HistoryList");
        var templates = historyList
            .Descendants(XName.Get("DataTemplate", PresentationNamespace))
            .ToArray();
        Assert.Equal(2, templates.Length);

        var dayHeaderMarkup = templates[0].ToString();
        Assert.Contains("BrandDayTitleStyle", dayHeaderMarkup, StringComparison.Ordinal);
        Assert.Contains("BrandSubsectionEyebrowStyle", dayHeaderMarkup, StringComparison.Ordinal);
        Assert.Contains("DayHeaderTitle", dayHeaderMarkup, StringComparison.Ordinal);
        Assert.Contains("DayHeaderSubtitle", dayHeaderMarkup, StringComparison.Ordinal);
        Assert.Contains("DayCountText", dayHeaderMarkup, StringComparison.Ordinal);

        var templateMarkup = templates[1].ToString();
        Assert.Contains("SelectionCheckBoxVisibility", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardSelectCheckBox_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("BrandNeutralChipStyle", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("BrandWarningChipStyle", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("BrandAccentChipStyle", templateMarkup, StringComparison.Ordinal);

        Assert.Contains("TranscriptMaxLines", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardShowMoreToggle_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardShowEditsToggle_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardShowPolished_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardShowOriginal_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("ComparisonOtherText", templateMarkup, StringComparison.Ordinal);

        Assert.Contains("CardPlayPauseButton_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardWaveform_SeekRequested", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("PlaybackClockText", templateMarkup, StringComparison.Ordinal);

        Assert.Contains("ProvenanceText", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardPasteButton_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardCopyButton_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardKeepButton_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardRepolishButton_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("RepolishButtonVisibility", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CanRepolish", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CardDeleteButton_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("PasteButtonAutomationId", templateMarkup, StringComparison.Ordinal);

        var loadMoreButton = FindNamed(document, "LoadMoreHistoryButton");
        Assert.Equal("Show older dictations", (string?)loadMoreButton.Attribute("Content"));
    }

    [Fact]
    public void HistoryRecoveryCardAndEmptyStatesArePreserved()
    {
        var document = LoadMainWindow(FindRepositoryRoot());

        var recoveryCard = FindNamed(document, "RecoveryCard");
        Assert.Equal("Collapsed", (string?)recoveryCard.Attribute("Visibility"));
        var undoButton = FindNamed(document, "UndoRecoveryButton");
        Assert.Equal(
            "UndoRecoveryButton",
            undoButton.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.AutomationId")?.Value);

        Assert.Null(FindNamed(document, "HistoryLoadingState").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)FindNamed(document, "HistoryEmptyState").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)FindNamed(document, "HistorySearchEmptyState").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)FindNamed(document, "HistoryUnavailableState").Attribute("Visibility"));
        Assert.NotNull(FindNamed(document, "HistoryDetailReadyHotkeyText"));
        Assert.NotNull(FindNamed(document, "HistoryEmptyClearSearchButton"));
        Assert.NotNull(FindNamed(document, "HistoryEmptyClearFiltersButton"));
    }

    [Fact]
    public void HistoryCardsSeparateProvenanceFromActionsOfferExplicitAlternateCopyAndResetSelectionLabel()
    {
        var repositoryRoot = FindRepositoryRoot();
        var document = LoadMainWindow(repositoryRoot);
        var historyList = FindNamed(document, "HistoryList");
        var template = historyList
            .Descendants(XName.Get("DataTemplate", PresentationNamespace))
            .Last();
        var templateMarkup = template.ToString();

        Assert.Contains("CardCopyAlternateButton_Click", templateMarkup, StringComparison.Ordinal);
        Assert.Contains("CopyAlternateButtonLabel", templateMarkup, StringComparison.Ordinal);

        var footerActionButtonCount = template
            .Descendants(XName.Get("Button", PresentationNamespace))
            .Count(button => (string?)button.Attribute("Click") is "CardPasteButton_Click"
                or "CardCopyButton_Click"
                or "CardKeepButton_Click"
                or "CardRepolishButton_Click"
                or "CardDeleteButton_Click");
        Assert.Equal(5, footerActionButtonCount);

        var historyCode = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.History.cs"));
        Assert.Contains("package.SetText(card.Text);", historyCode, StringComparison.Ordinal);
        Assert.Contains("package.SetText(card.ComparisonOtherText);", historyCode, StringComparison.Ordinal);
        Assert.Contains("SetHistorySelectionMode(false);", historyCode, StringComparison.Ordinal);
        Assert.Contains("\"Select dictations\"", historyCode, StringComparison.Ordinal);
    }

    [Fact]
    public void BrandHistoryListItemStyleBindsContentTemplateSelectorAndSelectorHasBothRowTemplates()
    {
        var repositoryRoot = FindRepositoryRoot();
        var controlsDoc = XDocument.Load(Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App", "Theme", "Controls.xaml"));
        var itemStyle = controlsDoc
            .Descendants(XName.Get("Style", PresentationNamespace))
            .Single(style => string.Equals((string?)style.Attribute(XName.Get("Key", XamlNamespace)), "BrandHistoryListItemStyle", StringComparison.Ordinal));
        var presenter = itemStyle.Descendants(XName.Get("ContentPresenter", PresentationNamespace)).Single();

        Assert.Equal("{TemplateBinding Content}", (string?)presenter.Attribute("Content"));
        Assert.Equal("{TemplateBinding ContentTemplate}", (string?)presenter.Attribute("ContentTemplate"));
        Assert.Equal("{TemplateBinding ContentTemplateSelector}", (string?)presenter.Attribute("ContentTemplateSelector"));

        var mainDoc = LoadMainWindow(repositoryRoot);
        var historyList = FindNamed(mainDoc, "HistoryList");
        var selector = historyList
            .Descendants()
            .Single(el => el.Name.LocalName == "HistoryDiaryRowTemplateSelector");
        Assert.Contains(selector.Elements(), el => el.Name.LocalName == "HistoryDiaryRowTemplateSelector.DayHeaderTemplate");
        Assert.Contains(selector.Elements(), el => el.Name.LocalName == "HistoryDiaryRowTemplateSelector.CardTemplate");
    }

    private static XDocument LoadMainWindow(string repositoryRoot) =>
        XDocument.Load(Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

    private static XElement FindNamed(XDocument document, string name) =>
        document.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(XName.Get("Name", XamlNamespace)), name, StringComparison.Ordinal));

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

        throw new InvalidOperationException("Could not locate repository root containing EnviousWispr.Windows.slnx.");
    }
}
