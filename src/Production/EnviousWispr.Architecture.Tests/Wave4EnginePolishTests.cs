using System.Xml.Linq;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;

namespace EnviousWispr.Architecture.Tests;

public sealed class Wave4EnginePolishTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(64d, true, "This PC: 64 GB RAM · NVIDIA GPU")]
    [InlineData(32d, false, "This PC: 32 GB RAM")]
    [InlineData(63.7d, true, "This PC: 64 GB RAM · NVIDIA GPU")]
    [InlineData(15.8d, false, "This PC: 16 GB RAM")]
    [InlineData(0d, false, "This PC: Local Memory")]
    [InlineData(0d, true, "This PC: Local Memory · NVIDIA GPU")]
    public void HardwareProfileSummaryFormatsRamAndNvidiaGpu(
        double totalRamGb,
        bool hasNvidiaGpu,
        string expected)
    {
        Assert.Equal(expected, HardwareProfileSummary.FormatSummary(totalRamGb, hasNvidiaGpu));
    }

    [Theory]
    [InlineData(64d, "eg-1")]
    [InlineData(24d, "eg-1")]
    [InlineData(23.9d, "eg-1-mini")]
    [InlineData(16d, "eg-1-mini")]
    [InlineData(12d, "eg-1-mini")]
    [InlineData(11.9d, "eg-1-micro")]
    [InlineData(8d, "eg-1-micro")]
    [InlineData(0d, "eg-1-micro")]
    public void RecommendEgOneModelIdFollowsRamThresholds(double totalRamGb, string expectedModelId)
    {
        Assert.Equal(expectedModelId, HardwareProfileSummary.RecommendEgOneModelId(totalRamGb));
    }

    [Fact]
    public void HardwareProfileSummaryGetCurrentReturnsPopulatedProfile()
    {
        var profile = HardwareProfileSummary.GetCurrent();

        Assert.True(profile.TotalRamGb > 0d);
        Assert.StartsWith("This PC: ", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("GB RAM", profile.SummaryText, StringComparison.Ordinal);
        Assert.True(EnginePolishPresentation.IsKnownEgOneModelId(profile.RecommendedEgOneModelId));
    }

    [Theory]
    [InlineData(FinalAsrEngine.Parakeet, "Parakeet", "Recommended", false)]
    [InlineData(FinalAsrEngine.Whisper, "Whisper", "99+ languages", true)]
    public void EngineSummaryDescribesEachEngineAndWhisperLanguageVisibility(
        FinalAsrEngine engine,
        string expectedName,
        string expectedBadge,
        bool expectedShowWhisperLanguage)
    {
        var summary = EnginePolishPresentation.DescribeEngine(engine);

        Assert.Equal(expectedName, summary.Name);
        Assert.Equal(expectedBadge, summary.BadgeText);
        Assert.Equal(expectedShowWhisperLanguage, summary.ShowWhisperLanguage);
        Assert.False(string.IsNullOrWhiteSpace(summary.Description));
        Assert.DoesNotContain("not yet recommended", summary.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "eg-1")]
    [InlineData("", "eg-1")]
    [InlineData("   ", "eg-1")]
    [InlineData("eg-1", "eg-1")]
    [InlineData("EG-1", "eg-1")]
    [InlineData("eg-1-mini", "eg-1-mini")]
    [InlineData(" EG-1-Mini ", "eg-1-mini")]
    [InlineData("eg-1-micro", "eg-1-micro")]
    [InlineData("EG-1-MICRO", "eg-1-micro")]
    [InlineData("gpt-4o-mini", "eg-1")]
    public void NormalizeEgOneModelIdDefaultsCleanly(string? input, string expected)
    {
        Assert.Equal(expected, EnginePolishPresentation.NormalizeEgOneModelId(input));
    }

    [Theory]
    [InlineData("eg-1", true)]
    [InlineData("eg-1-mini", true)]
    [InlineData("eg-1-micro", true)]
    [InlineData("EG-1-MINI", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("gpt-4o-mini", false)]
    public void IsKnownEgOneModelIdRecognizesOnlyEgOneSizes(string? input, bool expected)
    {
        Assert.Equal(expected, EnginePolishPresentation.IsKnownEgOneModelId(input));
    }

    [Theory]
    [InlineData(true, true, "Ready on this PC")]
    [InlineData(true, false, "Not installed yet (local model files required)")]
    [InlineData(false, true, "Not installed yet (local model files required)")]
    [InlineData(false, false, "Not installed yet (local model files required)")]
    public void FormatEgOneInstallStatusRequiresBothServerAndModel(
        bool serverExists,
        bool modelExists,
        string expected)
    {
        Assert.Equal(expected, EnginePolishPresentation.FormatEgOneInstallStatus(serverExists, modelExists));
    }

    [Fact]
    public void EgOneRuntimeLocatorRoutesEachSizeAndRejectsPartialShards()
    {
        const string dataDir = @"C:\data";
        var partialShards = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(dataDir, "models", "eg-1", "eg-1-v2-q5_k_m-00001-of-00008.gguf"),
        };

        var partialResolved = EgOneRuntimeLocator.ResolveModelFile(
            dataDir,
            "eg-1",
            fileExists: partialShards.Contains);
        Assert.Equal(Path.Combine(dataDir, "models", "eg-1", "active.gguf"), partialResolved);
        Assert.False(EgOneRuntimeLocator.IsModelFileReady(
            Path.Combine(dataDir, "models", "eg-1", "eg-1-v2-q5_k_m-00001-of-00008.gguf"),
            partialShards.Contains));

        var completeShards = Enumerable.Range(1, EgOneRuntimeLocator.ShippingShardCount)
            .Select(i => Path.Combine(dataDir, "models", "eg-1", $"eg-1-v2-q5_k_m-{i:D5}-of-00008.gguf"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var completeResolved = EgOneRuntimeLocator.ResolveModelFile(
            dataDir,
            "eg-1",
            fileExists: completeShards.Contains);
        Assert.Equal(
            Path.Combine(dataDir, "models", "eg-1", "eg-1-v2-q5_k_m-00001-of-00008.gguf"),
            completeResolved);
        Assert.True(EgOneRuntimeLocator.IsModelFileReady(completeResolved, completeShards.Contains));

        Assert.Equal(
            Path.Combine(dataDir, "models", "eg-1-mini", "active.gguf"),
            EgOneRuntimeLocator.ResolveModelFile(dataDir, "eg-1-mini", fileExists: _ => false));
        Assert.Equal(
            Path.Combine(dataDir, "models", "eg-1-micro", "active.gguf"),
            EgOneRuntimeLocator.ResolveModelFile(dataDir, "eg-1-micro", fileExists: _ => false));
    }

    [Fact]
    public void DictationOutputLanguageCollapsesWhenOffAndDifferentiatesClaude()
    {
        var off = EnginePolishPresentation.DescribeDictationOutputLanguage(PolishProvider.None);
        Assert.False(off.IsVisible);
        Assert.Equal(string.Empty, off.StatusText);

        var claude = EnginePolishPresentation.DescribeDictationOutputLanguage(PolishProvider.Anthropic);
        Assert.True(claude.IsVisible);
        Assert.Contains("Claude is selected", claude.StatusText, StringComparison.Ordinal);

        foreach (var other in new[] { PolishProvider.EgOne, PolishProvider.Ollama, PolishProvider.OpenAI, PolishProvider.Gemini })
        {
            var presentation = EnginePolishPresentation.DescribeDictationOutputLanguage(other);
            Assert.True(presentation.IsVisible);
            Assert.Contains("Switch AI Polish to Claude (Anthropic)", presentation.StatusText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainWindowMarkupHasWave4EngineAndPolishControlsWithoutQaJargon()
    {
        var root = FindRepositoryRoot();
        var xamlPath = Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml");
        var xamlText = File.ReadAllText(xamlPath);
        var document = XDocument.Parse(xamlText);

        Assert.DoesNotContain("German (experimental)", xamlText, StringComparison.Ordinal);
        Assert.DoesNotContain("Deterministic cleanup", xamlText, StringComparison.Ordinal);
        Assert.DoesNotContain("Built-in on this PC", xamlText, StringComparison.Ordinal);
        Assert.DoesNotContain("trusted-fixture", xamlText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accuracy gate", xamlText, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Text cleanup", xamlText, StringComparison.Ordinal);
        Assert.Contains(
            "Applies to English dictation. Keep American if you dictate other languages with Parakeet.",
            xamlText,
            StringComparison.Ordinal);

        var chooserPanel = FindNamedElement(document, "EngineChooserPanel");
        Assert.NotEqual("Collapsed", (string?)chooserPanel.Attribute("Visibility"));
        Assert.NotNull(FindNamedElement(document, "ActiveEngineSummaryCard"));
        Assert.NotNull(FindNamedElement(document, "EngineCardsGrid"));
        Assert.NotNull(FindNamedElement(document, "ParakeetEngineCard"));
        Assert.NotNull(FindNamedElement(document, "WhisperEngineCard"));
        Assert.NotNull(FindNamedElement(document, "SpeechEngineOptionsCard"));
        Assert.NotNull(FindNamedElement(document, "WhisperLanguageRow"));
        Assert.NotNull(FindNamedElement(document, "GraphicsRuntimeRow"));
        Assert.Null(document.Descendants().FirstOrDefault(e =>
            (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ModelDeliveryRow"));
        Assert.Null(document.Descendants().FirstOrDefault(e =>
            (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ModelInventoryText"));

        var whisperCombo = FindNamedElement(document, "WhisperLanguageComboBox");
        var whisperItems = whisperCombo.Elements().Where(e => e.Name.LocalName == "String").Select(e => e.Value).ToArray();
        Assert.Equal(["Automatic detection", "English", "French", "German", "Spanish"], whisperItems);

        foreach (var name in new[] { "EgOneSizeFullCard", "EgOneSizeMiniCard", "EgOneSizeMicroCard" })
        {
            var card = FindNamedElement(document, name);
            Assert.Equal("RadioButton", card.Name.LocalName);
            Assert.Equal("EgOneSize", (string?)card.Attribute("GroupName"));
            Assert.Equal("{StaticResource BrandSelectableCardStyle}", (string?)card.Attribute("Style"));
            Assert.Equal("EgOneSizeCard_Checked", (string?)card.Attribute("Checked"));
        }

        Assert.NotNull(FindNamedElement(document, "EgOneInstallStatusText"));
        Assert.NotNull(FindNamedElement(document, "DictationOutputLanguageCard"));
        Assert.NotNull(FindNamedElement(document, "DictationOutputLanguageStatusText"));
    }

    [Fact]
    public void TrayAppearanceSyncsLiveMainWindowViaApplyThemeFromExternal()
    {
        var root = FindRepositoryRoot();
        var mainWindowCode = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.Settings.cs"));
        var appCode = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.TrayAndLifecycle.cs"));

        Assert.Contains("public void ApplyThemeFromExternal(AppTheme theme)", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("window.ApplyThemeFromExternal(theme);", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineAndPolishCardHandlersSyncSelectionCollapseChooserAndPersist()
    {
        var root = FindRepositoryRoot();
        var mainWindowXaml = File.ReadAllText(
            Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.xaml"));
        var mainWindowCode = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(root, "src", "Production", "EnviousWispr.App"),
                    "MainWindow*.cs")
                .Order()
                .Select(File.ReadAllText));

        Assert.Contains("Click=\"EngineCardClicked\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PolishProviderCardClicked\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("if (_lastActiveEngine == selectedEngine)", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("if (_lastActivePolishProvider == provider)", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("SetSelectedFinalEngineCards(selectedEngine);", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("SelectChoice(PolishProviderChoices, selectedIndex);", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("PolishProviderChooserPanel.Visibility = Visibility.Collapsed;", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("PolishProvider.EgOne => \"EG-1\",", mainWindowCode, StringComparison.Ordinal);
    }

    private static XElement FindNamedElement(XDocument document, string name)
    {
        var element = document.Descendants()
            .FirstOrDefault(node => (string?)node.Attribute(XName.Get("Name", XamlNamespace)) == name);
        Assert.True(element is not null, $"Expected XAML element '{name}'.");
        return element!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
