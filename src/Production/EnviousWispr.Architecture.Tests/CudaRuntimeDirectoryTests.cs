using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class CudaRuntimeDirectoryTests
{
    private static Func<string, bool> Existing(params string[] directories) =>
        candidate => directories.Contains(Path.GetFullPath(candidate), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheDefectThatWasReadAsAProductFault()
    {
        // THE EXACT SHAPE OF #45 AND #129. The runtime is provisioned under the data directory and the
        // environment variable is unset. The app found it; the harness, reading the variable alone,
        // did not - and reported that the product could not load CUDA on a machine where it could.
        // One rule, so both get the same answer.
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var installed = Path.Combine(data, "runtime", "cuda");

        var resolved = CudaRuntimeDirectory.Resolve(
            configured: null,
            dataDirectories: [data],
            directoryExists: Existing(installed));

        Assert.Equal(installed, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingConfiguredAndNothingInstalledIsNoDirectory(string? configured)
    {
        var resolved = CudaRuntimeDirectory.Resolve(
            configured,
            dataDirectories: [Path.GetFullPath(Path.Combine("C:", "data"))],
            directoryExists: Existing());

        Assert.Null(resolved);
    }

    [Fact]
    public void AConfiguredDirectoryWins()
    {
        var configured = Path.GetFullPath(Path.Combine("C:", "chosen"));
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var installed = Path.Combine(data, "runtime", "cuda");

        var resolved = CudaRuntimeDirectory.Resolve(
            configured,
            [data],
            Existing(configured, installed));

        Assert.Equal(configured, resolved);
    }

    [Fact]
    public void AConfiguredDirectoryThatIsNotThereIsNotHonoured()
    {
        // POINTING SOMEWHERE EMPTY IS NOT AN ANSWER. Returning it would put the caller on a path with
        // no files in it and call that success, which is the failure this type exists to stop.
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var installed = Path.Combine(data, "runtime", "cuda");

        var resolved = CudaRuntimeDirectory.Resolve(
            configured: Path.GetFullPath(Path.Combine("C:", "missing")),
            [data],
            Existing(installed));

        Assert.Equal(installed, resolved);
    }

    [Fact]
    public void DataDirectoriesAreTriedInTheOrderGiven()
    {
        var first = Path.GetFullPath(Path.Combine("C:", "first"));
        var second = Path.GetFullPath(Path.Combine("C:", "second"));
        var secondInstalled = Path.Combine(second, "runtime", "cuda");

        var resolved = CudaRuntimeDirectory.Resolve(
            configured: null,
            [first, second],
            Existing(secondInstalled));

        Assert.Equal(secondInstalled, resolved);
    }

    [Fact]
    public void AnEmptyDataDirectoryInTheListIsSkippedRatherThanCombined()
    {
        // Path.Combine("", "runtime", "cuda") is a RELATIVE path, which Directory.Exists would then
        // resolve against the process's working directory - a different machine's answer depending on
        // where the harness was launched from.
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var installed = Path.Combine(data, "runtime", "cuda");

        var resolved = CudaRuntimeDirectory.Resolve(
            configured: null,
            ["", "  ", data],
            Existing(installed));

        Assert.Equal(installed, resolved);
    }

    [Fact]
    public void TheApplicationRefusesToResolveWithoutADataDirectory()
    {
        Assert.Throws<ArgumentException>(() => CudaRuntimeDirectory.ForApplication("  "));
    }
}
