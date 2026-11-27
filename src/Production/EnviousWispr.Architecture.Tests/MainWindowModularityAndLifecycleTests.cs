using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EnviousWispr.Architecture.Tests;

public sealed class MainWindowModularityAndLifecycleTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ControlBrushesAreExtractedOutOfMainWindowXaml()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App");
        var mainWindowDoc = XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        var controlBrushesDoc = XDocument.Load(Path.Combine(appRoot, "Theme", "ControlBrushes.xaml"));
        var xaml = XNamespace.Get(XamlNamespace);

        var windowRoot = FindNamedElement(mainWindowDoc, "WindowRoot");
        var resourcesElement = windowRoot.Elements().First(e => e.Name.LocalName == "Grid.Resources");
        Assert.DoesNotContain(resourcesElement.Descendants(), e => e.Name.LocalName == "SolidColorBrush");

        var mergedBrushSource = resourcesElement.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary"
                && string.Equals((string?)e.Attribute("Source"), "Theme/ControlBrushes.xaml", StringComparison.Ordinal));
        Assert.NotNull(mergedBrushSource);

        var themeKeys = controlBrushesDoc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(xaml + "Key") is not null)
            .Select(e => (string)e.Attribute(xaml + "Key")!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["Dark", "HighContrast", "Light"], themeKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void NonDefaultPagesUseDeferredXLoadAndRegistryActivation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App");
        var mainWindowDoc = XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        var xaml = XNamespace.Get(XamlNamespace);

        foreach (var deferredName in new[] { "OnboardingView", "WhatsNewPage", "DictionaryPage", "SnippetsPage", "SettingsPage" })
        {
            var element = FindNamedElement(mainWindowDoc, deferredName);
            Assert.Equal("False", (string?)element.Attribute(xaml + "Load"));
        }

        var historyPage = FindNamedElement(mainWindowDoc, "HistoryPage");
        Assert.Null(historyPage.Attribute(xaml + "Load"));

        var pagesSource = File.ReadAllText(Path.Combine(appRoot, "MainWindow.Pages.cs"));
        foreach (var ensureMethod in new[]
        {
            "EnsureOnboardingViewLoaded",
            "EnsureWhatsNewPageLoaded",
            "EnsureDictionaryPageLoaded",
            "EnsureSnippetsPageLoaded",
            "EnsureSettingsPageLoaded",
        })
        {
            Assert.Contains(ensureMethod, pagesSource, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(appRoot, "Pages", "WindowPageRegistry.cs")));
    }

    [Fact]
    public void EveryPublicMainWindowEventAndHostPropertyHasSymmetricBinderTeardown()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App");

        var declaredEvents = new HashSet<string>(StringComparer.Ordinal);
        var declaredHostProperties = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.GetFiles(appRoot, "MainWindow*.cs"))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
            var mainWindowClasses = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => c.Identifier.ValueText == "MainWindow");

            foreach (var cls in mainWindowClasses)
            {
                foreach (var member in cls.Members)
                {
                    if (!member.Modifiers.Any(m => m.ValueText == "public"))
                    {
                        continue;
                    }

                    switch (member)
                    {
                        case EventFieldDeclarationSyntax eventField:
                            foreach (var variable in eventField.Declaration.Variables)
                            {
                                declaredEvents.Add(variable.Identifier.ValueText);
                            }

                            break;
                        case PropertyDeclarationSyntax prop
                            when prop.AccessorList?.Accessors.Any(a => a.Keyword.ValueText == "set") == true:
                            declaredHostProperties.Add(prop.Identifier.ValueText);
                            break;
                    }
                }
            }
        }

        Assert.NotEmpty(declaredEvents);
        Assert.NotEmpty(declaredHostProperties);

        var binderSource = File.ReadAllText(Path.Combine(appRoot, "Contracts", "MainWindowHostBinder.cs"));
        foreach (var evt in declaredEvents)
        {
            Assert.Contains($"_window.{evt} +=", binderSource, StringComparison.Ordinal);
            Assert.Contains($"_window.{evt} -=", binderSource, StringComparison.Ordinal);
        }

        foreach (var prop in declaredHostProperties)
        {
            Assert.Contains($"_window.{prop} = _", binderSource, StringComparison.Ordinal);
            Assert.Contains($"_window.{prop} = null;", binderSource, StringComparison.Ordinal);
        }

        var appSource = File.ReadAllText(Path.Combine(appRoot, "App.xaml.cs"));
        Assert.Contains("MainWindowHostBinder.Attach(", appSource, StringComparison.Ordinal);
        Assert.Contains("_windowHostBinding?.Dispose();", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("window.SettingsChanged -=", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TriggerAutoSaveAsyncPreservesUnloadedPagePreferencesInsteadOfHardcodedDefaults()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsCs = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Production", "EnviousWispr.App", "MainWindow.Settings.cs"));

        Assert.DoesNotContain("LearnFromEditsToggle?.IsOn ?? true", settingsCs, StringComparison.Ordinal);
        Assert.DoesNotContain("FixLearnedWordsToggle?.IsOn ?? true", settingsCs, StringComparison.Ordinal);
        Assert.Contains("dictation.LearnFromEditsEnabled", settingsCs, StringComparison.Ordinal);
        Assert.Contains("dictation.FixLearnedWordsEnabled", settingsCs, StringComparison.Ordinal);
        Assert.Contains("IsSettingsPageLoaded ?", settingsCs, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Production", "EnviousWispr.App", "MainWindow.xaml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root above test output.");
    }

    private static XElement FindNamedElement(XDocument doc, string name)
    {
        var xaml = XNamespace.Get(XamlNamespace);
        return doc.Descendants()
            .Single(e => string.Equals((string?)e.Attribute(xaml + "Name"), name, StringComparison.Ordinal));
    }
}
