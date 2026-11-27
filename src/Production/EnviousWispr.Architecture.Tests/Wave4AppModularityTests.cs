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

    [Fact]
    public void OnboardingPartialFilesExistAndStayUnderLineCeiling()
    {
        var appDir = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App");
        var files = Directory.EnumerateFiles(appDir, "MainWindow.Onboarding*.cs", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedOnboardingPartials.Order(StringComparer.Ordinal).ToArray(),
            files.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());

        foreach (var path in files)
        {
            var lineCount = File.ReadAllLines(path).Length;
            Assert.True(
                lineCount <= 1100,
                $"Expected '{Path.GetFileName(path)}' to stay <= 1100 lines, found {lineCount}.");
        }
    }
}
