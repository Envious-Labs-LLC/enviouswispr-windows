using System.IO;
using System.Linq;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class Wave4AppModularityTests
{
    private static readonly string[] ExpectedOnboardingPartials =
    [
        "MainWindow.Onboarding.cs",
        "MainWindow.Onboarding.Rendering.cs",
        "MainWindow.Onboarding.Steps.cs",
        "MainWindow.Onboarding.TimersAndFocus.cs",
    ];

    private static readonly string[] ExpectedHistoryPartials =
    [
        "MainWindow.History.Cards.cs",
        "MainWindow.History.Recovery.cs",
        "MainWindow.History.ViewModels.cs",
        "MainWindow.History.cs",
    ];

    private static readonly string[] ExpectedModelDeliveryPartials =
    [
        "App.ModelDelivery.Onboarding.cs",
        "App.ModelDelivery.Operations.cs",
        "App.ModelDelivery.cs",
    ];

    private static readonly string[] ExpectedMainWindowShellPartials =
    [
        "MainWindow.ChromeAndFeedback.cs",
        "MainWindow.Navigation.cs",
        "MainWindow.xaml.cs",
    ];

    private static readonly string[] ExpectedAllMainWindowFiles =
    [
        "MainWindow.AiPolish.cs",
        "MainWindow.AudioAndChimes.cs",
        "MainWindow.ChromeAndFeedback.cs",
        "MainWindow.DiagnosticsAndUpdates.cs",
        "MainWindow.Dictionary.cs",
        "MainWindow.History.Cards.cs",
        "MainWindow.History.Recovery.cs",
        "MainWindow.History.ViewModels.cs",
        "MainWindow.History.cs",
        "MainWindow.Keybinds.cs",
        "MainWindow.Navigation.cs",
        "MainWindow.OllamaModels.cs",
        "MainWindow.Onboarding.Rendering.cs",
        "MainWindow.Onboarding.Steps.cs",
        "MainWindow.Onboarding.TimersAndFocus.cs",
        "MainWindow.Onboarding.cs",
        "MainWindow.PageControls.cs",
        "MainWindow.Pages.cs",
        "MainWindow.Settings.cs",
        "MainWindow.SetupLeaveGuards.cs",
        "MainWindow.Shell.cs",
        "MainWindow.Snippets.cs",
        "MainWindow.SpeechEngine.cs",
        "MainWindow.TranscribeFile.cs",
        "MainWindow.WordsAndSnippets.cs",
        "MainWindow.WordsFromApps.cs",
        "MainWindow.YourData.cs",
        "MainWindow.xaml.cs",
    ];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Production", "EnviousWispr.App", "EnviousWispr.App.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static void AssertPartialFamily(string searchPattern, string[] expectedFiles, int maxLines)
    {
        var appDir = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var files = Directory.EnumerateFiles(appDir, searchPattern, SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedFiles.Order(StringComparer.Ordinal).ToArray(),
            files.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());

        foreach (var path in files)
        {
            var lineCount = File.ReadAllLines(path).Length;
            Assert.True(
                lineCount <= maxLines,
                $"Expected '{Path.GetFileName(path)}' to stay <= {maxLines} lines, found {lineCount}.");
        }
    }

    [Fact]
    public void OnboardingPartialFilesExistAndStayUnderLineCeiling() =>
        AssertPartialFamily("MainWindow.Onboarding*.cs", ExpectedOnboardingPartials, 1100);

    [Fact]
    public void HistoryPartialFilesExistAndStayUnderLineCeiling() =>
        AssertPartialFamily("MainWindow.History*.cs", ExpectedHistoryPartials, 950);

    [Fact]
    public void ModelDeliveryPartialFilesExistAndStayUnderLineCeiling() =>
        AssertPartialFamily("App.ModelDelivery*.cs", ExpectedModelDeliveryPartials, 950);

    [Fact]
    public void MainWindowShellPartialFilesExistAndStayUnderLineCeiling()
    {
        var appDir = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var actualAllMainWindowFiles = Directory.EnumerateFiles(appDir, "MainWindow*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedAllMainWindowFiles.Order(StringComparer.Ordinal).ToArray(),
            actualAllMainWindowFiles);

        foreach (var fileName in ExpectedMainWindowShellPartials)
        {
            var path = Path.Combine(appDir, fileName);
            var lineCount = File.ReadAllLines(path).Length;
            Assert.True(
                lineCount <= 950,
                $"Expected '{fileName}' to stay <= 950 lines, found {lineCount}.");
        }
    }
}
