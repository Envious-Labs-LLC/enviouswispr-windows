using System.Xml.Linq;

namespace EnviousWispr.Architecture.Tests;

internal static class AppXamlSources
{
    internal static string AppDirectory(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "src", "Production", "EnviousWispr.App");

    internal static IReadOnlyList<string> MainWindowAndPagePaths(string repositoryRoot)
    {
        var appDir = AppDirectory(repositoryRoot);
        var paths = new List<string> { Path.Combine(appDir, "MainWindow.xaml") };
        var pagesDir = Path.Combine(appDir, "Pages");
        if (Directory.Exists(pagesDir))
        {
            paths.AddRange(Directory.EnumerateFiles(pagesDir, "*.xaml", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal));
        }

        return paths;
    }

    internal static string ReadMainWindowAndPagesText(string repositoryRoot) =>
        string.Join("\n", MainWindowAndPagePaths(repositoryRoot).Select(File.ReadAllText));

    internal static XDocument LoadMainWindowAndPagesDocument(string repositoryRoot)
    {
        var paths = MainWindowAndPagePaths(repositoryRoot);
        var root = XDocument.Load(paths[0]);
        var rootElement = root.Root!;
        foreach (var pagePath in paths.Skip(1))
        {
            var pageDoc = XDocument.Load(pagePath);
            if (pageDoc.Root is not null)
            {
                rootElement.Add(pageDoc.Root);
            }
        }

        return root;
    }
}
