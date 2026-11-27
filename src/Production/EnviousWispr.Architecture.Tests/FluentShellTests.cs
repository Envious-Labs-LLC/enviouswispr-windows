using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Guards the Wave 2 native Windows 11 Fluent shell, right-aligned setting rows, real version
/// badge, accurate privacy/keybind copy, and dead HomePage cleanup (#318).
/// </summary>
public sealed class FluentShellTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void MainWindowDeclaresMicaBackdropFlushShellAndAnnouncementGlyph()
    {
        var document = LoadMainWindow();
        var root = document.Root!;

        var backdrop = root.Element(XName.Get("Window.SystemBackdrop", PresentationNamespace))?
            .Element(XName.Get("MicaBackdrop", PresentationNamespace));
        Assert.NotNull(backdrop);
        Assert.Equal("Base", (string?)backdrop!.Attribute("Kind"));

        var navigation = FindNamed(document, "ProductNavigation");
        Assert.Null(navigation.Attribute("Margin"));

        var navResources = navigation.Descendants()
            .Where(element => element.Attribute(XName.Get("Key", XamlNamespace)) is not null)
            .ToDictionary(
                element => (string)element.Attribute(XName.Get("Key", XamlNamespace))!,
                element => element,
                StringComparer.Ordinal);

        Assert.Equal("8,0,0,0", navResources["NavigationViewContentGridCornerRadius"].Value.Trim());
        Assert.Equal("1,1,0,0", navResources["NavigationViewContentGridBorderThickness"].Value.Trim());
        Assert.Equal("0", navResources["NavigationViewContentMargin"].Value.Trim());

        foreach (var paneKey in new[]
                 {
                     "NavigationViewDefaultPaneBackground",
                     "NavigationViewExpandedPaneBackground",
                 })
        {
            var brush = navResources[paneKey];
            Assert.Equal("{ThemeResource BrandSidebarBgColor}", (string?)brush.Attribute("Color"));
            Assert.True(
                double.TryParse((string?)brush.Attribute("Opacity"), out var opacity) && opacity is > 0 and < 1,
                $"{paneKey} must tint Mica subtly (0 < Opacity < 1).");
        }

        var whatsNewButton = FindNamed(document, "WhatsNewToolbarButton");
        var icon = whatsNewButton.Descendants(XName.Get("FontIcon", PresentationNamespace)).Single();
        Assert.Equal("\uE789", (string?)icon.Attribute("Glyph"));

        var titleBar = FindNamed(document, "AppTitleBar");
        Assert.Contains(
            titleBar.Descendants(XName.Get("Image", PresentationNamespace)),
            image => string.Equals((string?)image.Attribute("Source"), "Assets/Brand/EnviousWisprMark.svg", StringComparison.Ordinal));
        Assert.Contains(
            titleBar.Descendants(XName.Get("TextBlock", PresentationNamespace)),
            text => string.Equals((string?)text.Attribute("Text"), "EnviousWispr", StringComparison.Ordinal));

        var feedbackButton = FindNamed(document, "FeedbackToolbarButton");
        Assert.Contains(titleBar, feedbackButton.Ancestors());
        var feedbackIcon = feedbackButton.Descendants(XName.Get("FontIcon", PresentationNamespace)).Single();
        Assert.Equal("\uED15", (string?)feedbackIcon.Attribute("Glyph"));
        Assert.Empty(feedbackButton.Descendants(XName.Get("TextBlock", PresentationNamespace)));

        var paneHeader = navigation.Element(XName.Get("NavigationView.PaneHeader", PresentationNamespace));
        Assert.NotNull(paneHeader);
        Assert.Contains(paneHeader!.Descendants(), e => (string?)e.Attribute(XName.Get("Name", XamlNamespace)) == "SidebarSearchBox");
        Assert.DoesNotContain(
            paneHeader.Descendants(XName.Get("Image", PresentationNamespace)),
            image => string.Equals((string?)image.Attribute("Source"), "Assets/Brand/EnviousWisprMark.svg", StringComparison.Ordinal));
        Assert.DoesNotContain(
            paneHeader.Descendants(XName.Get("TextBlock", PresentationNamespace)),
            text => string.Equals((string?)text.Attribute("Text"), "EnviousWispr", StringComparison.Ordinal));
        Assert.DoesNotContain(
            paneHeader.Descendants(),
            e => (string?)e.Attribute(XName.Get("Name", XamlNamespace)) is "SidebarVersionText" or "TitleBarVersionText");
    }

    [Fact]
    public void SidebarVersionBadgeUsesRealAppVersionAndNeverHardcodesV260()
    {
        var root = FindRepositoryRoot();
        var markup = AppXamlSources.ReadMainWindowAndPagesText(root);
        var codeBehind = string.Join(
            "\n",
            Directory.EnumerateFiles(Path.Combine(root, "src", "Production", "EnviousWispr.App"), "MainWindow*.cs")
                .Order()
                .Select(File.ReadAllText));

        Assert.DoesNotContain("v2.6.0", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("v2.6.0", codeBehind, StringComparison.Ordinal);

        var document = AppXamlSources.LoadMainWindowAndPagesDocument(root);
        var titleBar = FindNamed(document, "AppTitleBar");
        var versionBadge = FindNamed(document, "TitleBarVersionBadge");
        Assert.Contains(titleBar, versionBadge.Ancestors());
        Assert.Equal("{StaticResource BrandAccentChipStyle}", (string?)versionBadge.Attribute("Style"));

        var badgeText = FindNamed(document, "TitleBarVersionText");
        Assert.Equal("TextBlock", badgeText.Name.LocalName);
        Assert.Equal(string.Empty, (string?)badgeText.Attribute("Text") ?? string.Empty);
        Assert.Contains(titleBar, badgeText.Ancestors());

        Assert.Contains("TitleBarVersionText.Text = FormatTitleBarVersion(", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRowsAndHistoryProvideContextualHelpFlyouts()
    {
        var document = LoadMainWindow();

        var historyPage = FindNamed(document, "HistoryPage");
        var historyHelpButton = FindNamed(historyPage, "HistoryHelpButton");
        Assert.Equal("How History works", (string?)historyHelpButton.Attribute("AutomationProperties.Name"));
        Assert.NotNull(FindNamed(historyHelpButton, "HistoryHelpFlyout"));

        foreach (var sectionName in new[]
                 {
                     "MicrophoneSection",
                     "TranscriptionEngineSection",
                     "KeybindsSection",
                     "SoundSection",
                     "DeterministicCleanupSection",
                     "AiPolishSection",
                     "HistorySettingsSection",
                     "RecordingPillSection",
                     "AppearanceSection",
                     "LivePreviewSection",
                     "ClipboardSection",
                     "DiagnosticsSection",
                     "YourDataSection",
                     "PermissionsSection",
                 })
        {
            var section = FindNamed(document, sectionName);
            var helpButtons = section.Descendants(XName.Get("Button", PresentationNamespace))
                .Where(button => string.Equals((string?)button.Attribute("Style"), "{StaticResource BrandHelpButtonStyle}", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(helpButtons);

            foreach (var button in helpButtons)
            {
                var name = (string?)button.Attribute("AutomationProperties.Name");
                Assert.False(string.IsNullOrWhiteSpace(name));
                var glyph = button.Descendants(XName.Get("FontIcon", PresentationNamespace)).Single();
                Assert.Equal("\uE9CE", (string?)glyph.Attribute("Glyph"));
                var flyout = button.Descendants(XName.Get("Flyout", PresentationNamespace)).Single();
                Assert.Equal("{StaticResource BrandHelpFlyoutPresenterStyle}", (string?)flyout.Attribute("FlyoutPresenterStyle"));
                var helpText = flyout.Descendants(XName.Get("TextBlock", PresentationNamespace)).Single();
                Assert.False(string.IsNullOrWhiteSpace((string?)helpText.Attribute("Text")));
            }
        }
    }

    [Fact]
    public void AllSettingsToggleRowsAreRightAlignedThreeColumnRowsWithHairlineDividers()
    {
        var document = LoadMainWindow();
        var toggleNames = new[]
        {
            "AutoStopToggle",
            "EscapeRecoveryToggle",
            "WordCorrectionToggle",
            "FillerRemovalToggle",
            "EmojiFormatterToggle",
            "SpokenPunctuationToggle",
            "CasualModeToggle",
            "PlayRecordingSoundsToggle",
            "StartAtLoginToggle",
            "LivePreviewToggle",
            "CopyInsteadOfPasteToggle",
            "HistoryEnabledToggle",
            "LocalDiagnosticsToggle",
            "ShareUsageMetricsToggle",
            "SendCrashReportsToggle",
        };

        foreach (var toggleName in toggleNames)
        {
            var toggle = FindNamed(document, toggleName);
            Assert.Equal("Right", (string?)toggle.Attribute("HorizontalAlignment"));
            Assert.Equal("Center", (string?)toggle.Attribute("VerticalAlignment"));
            Assert.Equal(string.Empty, (string?)toggle.Attribute("OnContent"));
            Assert.Equal(string.Empty, (string?)toggle.Attribute("OffContent"));
            Assert.Equal("0", (string?)toggle.Attribute("MinWidth"));

            var row = toggle.Parent;
            Assert.NotNull(row);
            Assert.Equal("Grid", row!.Name.LocalName);
            Assert.Equal("SettingRow_Tapped", (string?)row.Attribute("Tapped"));

            var columns = row.Element(XName.Get("Grid.ColumnDefinitions", PresentationNamespace))?
                .Elements(XName.Get("ColumnDefinition", PresentationNamespace))
                .Select(col => (string?)col.Attribute("Width") ?? string.Empty)
                .ToArray() ?? [];
            Assert.True(columns.Length >= 3, $"{toggleName} row must have at least 3 columns (icon | text | toggle).");
            Assert.Equal("Auto", columns[0]);
            Assert.Equal("*", columns[1]);
            Assert.Equal("Auto", columns[^1]);
        }

        // Every multi-row settings card separates its rows with hairlines. Sounds and Live Preview are not
        // listed: since the sections stopped being one card each, their cards hold a single toggle row and the
        // chime tiles and preview-engine cards sit on the page beside them, so there is no row to divide.
        foreach (var sectionName in new[]
                 {
                     "KeybindsSection",
                     "DeterministicCleanupSection",
                     "AppearanceSection",
                     "ClipboardSection",
                     "HistorySettingsSection",
                     "DiagnosticsSection",
                 })
        {
            var section = FindNamed(document, sectionName);
            var hairlines = section.Descendants(XName.Get("Border", PresentationNamespace))
                .Where(b => (string?)b.Attribute("Height") == "1"
                    && (string?)b.Attribute("Background") == "{ThemeResource BrandDividerBrush}")
                .ToArray();
            Assert.NotEmpty(hairlines);
        }
    }

    [Fact]
    public void OnboardingAndPermissionsCopyReflectDefaultOnTelemetryAndImmediateKeybinds()
    {
        var root = FindRepositoryRoot();
        var markup = AppXamlSources.ReadMainWindowAndPagesText(root);
        var document = AppXamlSources.LoadMainWindowAndPagesDocument(root);

        Assert.DoesNotContain("Anonymous usage data is shared only if you turn it on.", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Anonymous sharing is off by default", markup, StringComparison.Ordinal);

        var onboarding = FindNamed(document, "OnboardingView");
        var onboardingTexts = onboarding.Descendants(XName.Get("TextBlock", PresentationNamespace))
            .Select(tb => (string?)tb.Attribute("Text"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

        Assert.Contains(
            "Audio stays on this PC; dictated text stays local unless you turn on cloud polish. Anonymous usage metrics and crash reports are on by default to help us catch broken updates, and can be turned off anytime in Diagnostics.",
            onboardingTexts);
        Assert.Contains(
            "Change it any time on the Keybinds page. A new key starts working the next time EnviousWispr opens.",
            onboardingTexts);

        var permissions = FindNamed(document, "PermissionsSection");
        var permissionsTexts = permissions.Descendants(XName.Get("TextBlock", PresentationNamespace))
            .Select(tb => (string?)tb.Attribute("Text"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();

        Assert.Contains(
            permissionsTexts,
            text => text!.Contains(
                "Anonymous usage metrics and crash reports are on by default and never include dictated text, audio, API keys, or file paths. You can turn either off anytime in Diagnostics.",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DeadHomePageAndLegacyStatusHandlersAreRemoved()
    {
        var root = FindRepositoryRoot();
        var markup = AppXamlSources.ReadMainWindowAndPagesText(root);
        var codeBehind = string.Join(
            "\n",
            Directory.EnumerateFiles(Path.Combine(root, "src", "Production", "EnviousWispr.App"), "MainWindow*.cs")
                .Order()
                .Select(File.ReadAllText));

        foreach (var removed in new[]
                 {
                     "HomePage",
                     "HotkeyStatusText",
                     "SessionStatusText",
                     "EngineReadinessText",
                     "MicrophoneReadinessText",
                     "MicrophonePrivacyFixButton",
                     "HistorySummaryText",
                     "LivePreviewText",
                     "OpenHistoryButton_Click",
                     "HelpNavItem",
                     "HelpPage",
                     "ConfigureHelpPage",
                     "HelpSections",
                 })
        {
            Assert.DoesNotContain(removed, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(removed, codeBehind, StringComparison.Ordinal);
        }

        var doc = LoadMainWindow();
        Assert.Equal("{StaticResource BrandToolbarIconButtonStyle}", (string?)FindNamed(doc, "WhatsNewToolbarButton").Attribute("Style"));
        Assert.Equal("{StaticResource BrandToolbarIconButtonStyle}", (string?)FindNamed(doc, "FeedbackToolbarButton").Attribute("Style"));
        Assert.Equal("{StaticResource BrandRecordToolbarButtonStyle}", (string?)FindNamed(doc, "RecordToolbarButton").Attribute("Style"));
        Assert.NotNull(FindNamed(doc, "BuildInfoText"));

        Assert.Contains("\"home\" => (\"history\", null)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveHeaderAndToolbarGridsWireTwoRowStackingHandlers()
    {
        var root = FindRepositoryRoot();
        var doc = LoadMainWindow();
        var shellCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.Shell.cs"));

        Assert.Contains("ApplyResponsiveTwoClusterRowLayout(", shellCode, StringComparison.Ordinal);

        var specs = new[]
        {
            (GridName: "HistoryHeaderBarGrid", Handler: "HistoryHeaderBarGrid_SizeChanged", Primary: "HistoryFilterButtonsPanel", Secondary: "HistoryStatusChipsPanel"),
            (GridName: "WordListHeaderGrid", Handler: "WordListHeaderGrid_SizeChanged", Primary: "WordCountText", Secondary: "WordListActionButtonsPanel"),
            (GridName: "SnippetsToolbarGrid", Handler: "SnippetsToolbarGrid_SizeChanged", Primary: "SnippetSearchContainer", Secondary: "SnippetsToolbarActionsPanel"),
        };

        foreach (var spec in specs)
        {
            var grid = FindNamed(doc, spec.GridName);
            Assert.Equal("Grid", grid.Name.LocalName);
            Assert.Equal(spec.Handler, (string?)grid.Attribute("SizeChanged"));
            Assert.Contains(spec.Handler, shellCode, StringComparison.Ordinal);

            var rows = grid.Element(XName.Get("Grid.RowDefinitions", PresentationNamespace))?
                .Elements(XName.Get("RowDefinition", PresentationNamespace))
                .Select(r => (string?)r.Attribute("Height") ?? string.Empty)
                .ToArray() ?? [];
            Assert.Equal(["Auto", "Auto"], rows);

            var columns = grid.Element(XName.Get("Grid.ColumnDefinitions", PresentationNamespace))?
                .Elements(XName.Get("ColumnDefinition", PresentationNamespace))
                .Select(c => (string?)c.Attribute("Width") ?? string.Empty)
                .ToArray() ?? [];
            Assert.Equal(["*", "Auto"], columns);

            Assert.NotNull(FindNamed(grid, spec.Primary));
            var secondary = FindNamed(grid, spec.Secondary);
            Assert.Equal("1", (string?)secondary.Attribute("Grid.Column"));
        }

        Assert.Equal("NoWrap", (string?)FindNamed(doc, "WordCountText").Attribute("TextWrapping"));
        var snippetsActions = FindNamed(doc, "SnippetsToolbarActionsPanel");
        Assert.NotNull(FindNamed(snippetsActions, "ExportSnippetsButton"));
        Assert.NotNull(FindNamed(snippetsActions, "AddSnippetToolbarButton"));
    }

    private static XDocument LoadMainWindow() =>
        AppXamlSources.LoadMainWindowAndPagesDocument(FindRepositoryRoot());

    private static XElement FindNamed(XContainer container, string name) =>
        container.Descendants().Single(element =>
            string.Equals((string?)element.Attribute(XName.Get("Name", XamlNamespace)), name, StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
