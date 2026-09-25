namespace EnviousWispr.Architecture.Tests;

/// <summary>The snippet keyword cannot be dropped by a change to the words or the snippets.</summary>
/// <remarks>
/// THE KEYWORD IS THE CONSTRUCTOR'S DEFAULTED THIRD ARGUMENT, so the settings file binds to it and a
/// file written before it existed reads as "backslash". The same default is a trap everywhere else: a
/// change that rebuilt the saved data from its words and snippets would compile, pass, and quietly put
/// somebody's chosen keyword back to "backslash" - a capability lost with no call-site token to find.
/// So production code outside the type itself never calls the constructor; it goes through
/// <c>WithCustomWords</c>, <c>WithSnippets</c> and <c>WithSnippetKeyword</c>, which keep what they do not change.
/// </remarks>
public sealed class SnippetKeywordSourceTests
{
    [Fact]
    public void NoProductionCodeRebuildsTheSavedDataWithoutItsKeyword()
    {
        var production = Path.Combine(FindRepositoryRoot(), "src", "Production");
        var owner = Path.Combine(production, "EnviousWispr.Core", "Settings", "ReusableUserData.cs");
        var files = Directory.EnumerateFiles(production, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains("EnviousWispr.Architecture.Tests", StringComparison.Ordinal))
            .ToArray();

        var offenders = files
            .Where(path => !string.Equals(path, owner, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("new ReusableUserData(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(production, path))
            .ToArray();

        Assert.True(offenders.Length == 0, "Rebuilds the saved data and can drop the keyword: " + string.Join(", ", offenders));

        // Control: the sweep reads the production tree, and the owner it exempts does construct one.
        Assert.Contains(files, path => path.EndsWith("VocabularyPresenter.cs", StringComparison.Ordinal));
        Assert.Contains("new(CustomWords, snippets, SnippetKeyword)", File.ReadAllText(owner), StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test binaries.");
    }
}
