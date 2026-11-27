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
}
