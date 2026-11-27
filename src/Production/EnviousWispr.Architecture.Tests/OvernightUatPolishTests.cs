using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Regression tests for the four Interactive Session 1 UAT defects (#346):
/// live-preview toggle ASR reload, Reading Well corner fringe and badge overlap,
/// Fluent page scrollbar text clipping, and SidebarResizeHandle hit-testing.
/// </summary>
public sealed class OvernightUatPolishTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void LivePreviewToggleWithoutFinalEngineChangeTakesFastPreviewOnlyPathWithoutReloadingFinalAsr()
    {
        var root = FindRepositoryRoot();
        var appCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.xaml.cs"));
        var transcriptionCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.Transcription.cs"));
        var deliveryCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.ModelDelivery.cs"));
        var graphicsCode = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.GraphicsRuntime.cs"));

        var appTree = CSharpSyntaxTree.ParseText(appCode).GetRoot();
        var deliveryTree = CSharpSyntaxTree.ParseText(deliveryCode).GetRoot();

        // 1. OnSettingsChanged routes LivePreviewEnabled/LivePreviewEngine changes only to _livePreviewReconfigurePending.
        var onSettingsChanged = appTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "OnSettingsChanged");
        var finalEngineIf = onSettingsChanged.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString().Contains("previousFinalEngine", StringComparison.Ordinal));
        Assert.Contains("_transcriptionReconfigurePending = true", finalEngineIf.Statement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("_livePreviewReconfigurePending", finalEngineIf.Statement.ToString(), StringComparison.Ordinal);

        var livePreviewIf = onSettingsChanged.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single(statement => statement.Condition.ToString().Contains("previousPreviewEnabled", StringComparison.Ordinal));
        Assert.Contains("_livePreviewReconfigurePending = true", livePreviewIf.Statement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("_transcriptionReconfigurePending", livePreviewIf.Statement.ToString(), StringComparison.Ordinal);

        // 2. PresentModelDeliveryCoreAsync handles _livePreviewReconfigurePending separately from _transcriptionReconfigurePending
        // and drains any pending reconfiguration that became unblocked before the task completes.
        var presentMethod = deliveryTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "PresentModelDeliveryCoreAsync");
        var doLoop = presentMethod.DescendantNodes()
            .OfType<DoStatementSyntax>()
            .Single();
        Assert.Contains("_livePreviewReconfigurePending", doLoop.Condition.ToString(), StringComparison.Ordinal);
        Assert.Contains("!IsSpeechEngineBusy", doLoop.Condition.ToString(), StringComparison.Ordinal);

        var whileLoop = presentMethod.DescendantNodes()
            .OfType<WhileStatementSyntax>()
            .Single();
        var previewBranch = whileLoop.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "_livePreviewReconfigurePending");
        var previewBranchText = previewBranch.Statement.ToString();
        Assert.Contains("ReconfigureLivePreviewOnlyAsync", previewBranchText, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconfigureTranscriptionAsync", previewBranchText, StringComparison.Ordinal);
        Assert.DoesNotContain("TeardownTranscriptionAsync", previewBranchText, StringComparison.Ordinal);

        var refreshMethod = deliveryTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "RefreshAfterSettingsChangeAsync");
        Assert.Contains("PostToLiveWindow(_ => TrySchedulePendingModelRefresh());", refreshMethod.ToString(), StringComparison.Ordinal);

        // Full ReconfigureTranscriptionAsync only lives inside the _transcriptionReconfigurePending branch.
        var transcriptionBranch = whileLoop.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "_transcriptionReconfigurePending");
        Assert.Contains("ReconfigureTranscriptionAsync", transcriptionBranch.Statement.ToString(), StringComparison.Ordinal);

        // 3. ReconfigureLivePreviewOnlyAsync never touches _transcriptionEngine or full transcription setup.
        var reconfigurePreview = deliveryTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ReconfigureLivePreviewOnlyAsync");
        var previewMethodText = reconfigurePreview.ToString();
        Assert.DoesNotContain("_transcriptionEngine", previewMethodText, StringComparison.Ordinal);
        Assert.DoesNotContain("TeardownTranscriptionAsync", previewMethodText, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconfigureTranscriptionAsync", previewMethodText, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureTranscriptionAsync", previewMethodText, StringComparison.Ordinal);

        // Model directory resolution is guarded by previewEnabledAtStart (skipped when disabling Live Preview)
        // and runs BEFORE acquiring SessionHolder.SpeechEngineSwitch so dictation is never blocked during hashing.
        var resolveIndex = previewMethodText.IndexOf("ResolveModelDirectoryAsync", StringComparison.Ordinal);
        var holdIndex = previewMethodText.IndexOf("SessionHolder.SpeechEngineSwitch", StringComparison.Ordinal);
        Assert.True(resolveIndex > 0, "ReconfigureLivePreviewOnlyAsync should resolve preview directories when enabled.");
        Assert.True(holdIndex > resolveIndex, "ResolveModelDirectoryAsync must run before acquiring SpeechEngineSwitch.");
        var resolveIf = reconfigurePreview.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Statement.ToString().Contains("ResolveModelDirectoryAsync", StringComparison.Ordinal));
        Assert.Equal("previewEnabledAtStart", resolveIf.Condition.ToString());

        // When disabled, _previewEngine is disposed and nulled without calling ConfigureLivePreview.
        var disabledIf = reconfigurePreview.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(statement => statement.Condition.ToString() == "!_settings.Preferences.LivePreviewEnabled");
        Assert.Contains("_previewUnavailableReason = null", disabledIf.Statement.ToString(), StringComparison.Ordinal);
        Assert.Contains("_previewEngine = null", previewMethodText, StringComparison.Ordinal);

        // CPU fallback state from ConfigureTranscriptionAsync flows through ConfiguredTranscription.ForcePreviewCpu.
        Assert.Contains("bool ForcePreviewCpu = false", graphicsCode, StringComparison.Ordinal);
        Assert.Contains("ForcePreviewCpu = true", transcriptionCode, StringComparison.Ordinal);
        Assert.Contains("forceCpu: configured.ForcePreviewCpu", previewMethodText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingWellUsesSquareBleedRootExactRegionClipAndNonOverlappingBadgeSpacing()
    {
        var root = FindRepositoryRoot();
        var xamlPath = Path.Combine(root, "src", "Production", "EnviousWispr.App", "DictationOverlayWindow.xaml");

        var doc = XDocument.Load(xamlPath);
        var overlayRoot = FindNamed(doc, "OverlayRoot");
        Assert.Equal("0", (string?)overlayRoot.Attribute("CornerRadius"));

        var handsFreeBadge = FindNamed(doc, "WellHandsFreeBadge");
        Assert.Equal("8,2", (string?)handsFreeBadge.Attribute("Padding"));

        var code = AppXamlSources.LoadOverlayCodeText(root);
        var headerHeightMatch = Regex.Match(code, @"ReadingWellHeaderHeight\s*=\s*(?<height>\d+);");
        Assert.True(headerHeightMatch.Success);
        Assert.True(int.Parse(headerHeightMatch.Groups["height"].Value, CultureInfo.InvariantCulture) >= 36);

        var surfaceText = File.ReadAllText(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "Overlay", "OverlayChromeAnimator.cs"));
        var surfaceTree = CSharpSyntaxTree.ParseText(surfaceText).GetRoot();
        var applySurface = surfaceTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ApplySurface")
            .ToString();
        Assert.Contains("_overlayRoot.CornerRadius = new CornerRadius(0);", applySurface, StringComparison.Ordinal);
        Assert.DoesNotContain("radius + WindowBleed", applySurface, StringComparison.Ordinal);

        var hostText = File.ReadAllText(Path.Combine(
            root, "src", "Production", "EnviousWispr.App", "Overlay", "OverlayWin32Host.cs"));
        var hostTree = CSharpSyntaxTree.ParseText(hostText).GetRoot();
        var clipMethod = hostTree.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ClipToPillShape")
            .ToString();
        Assert.Contains("_overlayWidth - fringe,", clipMethod, StringComparison.Ordinal);
        Assert.Contains("_overlayHeight - fringe,", clipMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_overlayWidth - fringe + 1", clipMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_overlayHeight - fringe + 1", clipMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void FluentPagesReserveRightScrollbarGutterWithoutChangingCappedColumnWidth()
    {
        var root = FindRepositoryRoot();
        var doc = AppXamlSources.LoadMainWindowAndPagesDocument(root);

        foreach (var pageName in new[]
                 {
                     "WhatsNewPage",
                     "DictionaryPage",
                     "SnippetsPage",
                     "SettingsPage",
                 })
        {
            var page = FindNamed(doc, pageName);
            var grid = page.Elements().Single();
            var col = grid.Elements()
                .Single(e => e.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements()
                .Single();
            Assert.Equal("{StaticResource BrandFluentPageMaxWidth}", (string?)col.Attribute("MaxWidth"));

            var container = grid.Elements().Single(e =>
                e.Name.LocalName is "StackPanel" or "Grid");
            var pagePadding = (string?)page.Attribute("Padding");
            var containerMargin = (string?)container.Attribute("Margin");
            Assert.True(
                string.Equals(pagePadding, "0,0,16,0", StringComparison.Ordinal) ||
                string.Equals(containerMargin, "0,0,16,0", StringComparison.Ordinal),
                $"{pageName} must reserve 16 DIP on the right edge so overlay vertical scrollbars do not overlap text.");
        }
    }

    [Fact]
    public void SidebarResizeHandleHardensHitTestingAndZOrderAboveNavigationView()
    {
        var root = FindRepositoryRoot();
        var doc = XDocument.Load(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));

        var handle = FindNamed(doc, "SidebarResizeHandle");
        Assert.Equal("{ThemeResource BrandHitTestBrush}", (string?)handle.Attribute("Background"));
        Assert.Equal("{StaticResource BrandSidebarResizeHandleWidth}", (string?)handle.Attribute("Width"));
        Assert.True(
            int.TryParse((string?)handle.Attribute("Canvas.ZIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var zIndex) && zIndex > 0,
            "SidebarResizeHandle must declare a positive Canvas.ZIndex so NavigationView never intercepts seam hits.");
    }

    private static XElement FindNamed(XDocument document, string name) =>
        document.Descendants().Single(element =>
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
