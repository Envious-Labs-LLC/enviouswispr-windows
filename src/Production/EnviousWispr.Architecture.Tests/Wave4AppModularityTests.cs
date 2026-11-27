using System.IO;
using Xunit;

namespace EnviousWispr.Architecture.Tests;

public sealed class Wave4AppModularityTests
{
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

    [Theory]
    [InlineData("MainWindow.Onboarding.cs", 1100)]
    [InlineData("MainWindow.Onboarding.Steps.cs", 1100)]
    [InlineData("MainWindow.Onboarding.Rendering.cs", 1100)]
    [InlineData("MainWindow.Onboarding.TimersAndFocus.cs", 1100)]
    public void OnboardingPartialFilesExistAndStayUnderLineCeiling(string fileName, int maxLines)
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "Production", "EnviousWispr.App", fileName);
        Assert.True(File.Exists(path), $"Expected split partial file '{fileName}' to exist at '{path}'.");
        var lineCount = File.ReadAllLines(path).Length;
        Assert.True(lineCount <= maxLines, $"Expected '{fileName}' to stay <= {maxLines} lines, found {lineCount}.");
    }
}
