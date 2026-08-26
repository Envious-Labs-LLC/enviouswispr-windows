using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["EnviousWispr.Core"] = [],
            ["EnviousWispr.Audio"] = ["EnviousWispr.Core"],
            ["EnviousWispr.ASR"] = ["EnviousWispr.Core"],
            ["EnviousWispr.PostProcessing"] = ["EnviousWispr.Core"],
            ["EnviousWispr.LLM"] = ["EnviousWispr.Core"],
            ["EnviousWispr.Pipeline"] = ["EnviousWispr.Core", "EnviousWispr.PostProcessing"],
            ["EnviousWispr.RuntimeWorker"] = ["EnviousWispr.ASR", "EnviousWispr.Core"],
            ["EnviousWispr.Services"] = ["EnviousWispr.Core"],
            ["EnviousWispr.ModelDelivery"] = ["EnviousWispr.Core"],
            ["EnviousWispr.App"] =
            [
                "EnviousWispr.Audio",
                "EnviousWispr.ASR",
                "EnviousWispr.Core",
                "EnviousWispr.LLM",
                "EnviousWispr.ModelDelivery",
                "EnviousWispr.Pipeline",
                "EnviousWispr.PostProcessing",
                "EnviousWispr.RuntimeWorker",
                "EnviousWispr.Services",
            ],
            ["EnviousWispr.Architecture.Tests"] =
            [
                "EnviousWispr.ASR",
                "EnviousWispr.Audio",
                "EnviousWispr.LLM",
                "EnviousWispr.ModelDelivery",
                "EnviousWispr.Pipeline",
                "EnviousWispr.PostProcessing",
                "EnviousWispr.RuntimeWorker",
                "EnviousWispr.Services",
            ],
        };

    [Fact]
    public void ProductionProjectsHaveOnlyApprovedDirectReferences()
    {
        var productionDirectory = FindProductionDirectory();
        var projects = Directory.GetFiles(productionDirectory, "*.csproj", SearchOption.AllDirectories);

        Assert.Equal(AllowedReferences.Keys.Order(), projects.Select(Path.GetFileNameWithoutExtension).Order());

        foreach (var projectPath in projects)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var document = XDocument.Load(projectPath);
            var actual = document
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFileNameWithoutExtension(path!))
                .Order()
                .ToArray();

            Assert.Equal(AllowedReferences[projectName].Order(), actual);
        }
    }

    private static string FindProductionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "Production");
    }
}
