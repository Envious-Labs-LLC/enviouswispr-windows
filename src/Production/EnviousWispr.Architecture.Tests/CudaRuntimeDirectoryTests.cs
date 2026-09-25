using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class CudaRuntimeDirectoryTests
{
    private static Func<string, bool> Existing(params string[] directories) =>
        candidate => directories.Contains(Path.GetFullPath(candidate), StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders that hold every runtime file; any other configured folder is incomplete.</summary>
    private static Func<string, bool> Complete(params string[] directories) =>
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
            directoryExists: Existing(installed),
            holdsRuntime: Complete());

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
            directoryExists: Existing(),
            holdsRuntime: Complete());

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
            Existing(configured, installed),
            Complete(configured));

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
            Existing(installed),
            Complete());

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
            Existing(secondInstalled),
            Complete());

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
            Existing(installed),
            Complete());

        Assert.Equal(installed, resolved);
    }

    [Fact]
    public void TheApplicationRefusesToResolveWithoutADataDirectory()
    {
        Assert.Throws<ArgumentException>(() => CudaRuntimeDirectory.ForApplication("  ", verifiedPackDirectory: null));
    }

    private const string Digest = "1f7c92b03c81cc9d44a0f2627a4ae8f4bba9d57f0aded3e634ed554277fcd8f5";

    [Fact]
    public void TheDownloadedPackOutranksTheHandProvisionedFolderAndTheOverrideOutranksBoth()
    {
        var configured = Path.GetFullPath(Path.Combine("C:", "chosen"));
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var pack = Path.Combine(data, "models", "cuda-runtime", "versions", "1.0.0", Digest);
        var legacy = Path.Combine(data, "runtime", "cuda");

        Assert.Equal(configured, CudaRuntimeDirectory.Resolve(configured, pack, [data], Existing(configured, pack, legacy), Complete(configured)));
        Assert.Equal(pack, CudaRuntimeDirectory.Resolve(null, pack, [data], Existing(pack, legacy), Complete()));
        Assert.Equal(legacy, CudaRuntimeDirectory.Resolve(null, null, [data], Existing(pack, legacy), Complete()));
    }

    [Fact]
    public void AnOverrideMissingAnyRuntimeFileNeverShadowsTheVerifiedPack()
    {
        // A STALE OR PARTIAL ENVIRONMENT OVERRIDE used to win on existence alone, so a folder the card cannot
        // load from put the verified pack out of reach. It must hold the whole runtime to be honoured.
        var configured = Path.GetFullPath(Path.Combine("C:", "stale"));
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var pack = Path.Combine(data, "models", "cuda-runtime", "versions", "1.0.0", Digest);
        var legacy = Path.Combine(data, "runtime", "cuda");

        Assert.Equal(pack, CudaRuntimeDirectory.Resolve(configured, pack, [data], Existing(configured, pack, legacy), Complete()));
        Assert.Equal(legacy, CudaRuntimeDirectory.Resolve(configured, null, [data], Existing(configured, legacy), Complete()));
        Assert.Null(CudaRuntimeDirectory.Resolve(configured, null, [data], Existing(configured), Complete()));
    }

    [Fact]
    public void TheRealRuntimeCheckIsTheProbesFullSetFileByFile()
    {
        // THE PREDICATE THE APP AND THE HARNESSES PASS, on real files: all twelve, or it is not a runtime.
        var folder = Directory.CreateTempSubdirectory("EnviousWispr.CudaOverride.").FullName;
        try
        {
            Assert.False(CudaRuntimeDirectory.HoldsRuntime(folder));
            foreach (var library in EnviousWispr.Services.Runtime.CudaRuntimeDependencyProbe.RequiredLibraryNames)
            {
                File.WriteAllBytes(Path.Combine(folder, library), [0]);
            }

            Assert.True(CudaRuntimeDirectory.HoldsRuntime(folder));
            File.Delete(Path.Combine(folder, "cudnn_ops64_9.dll"));
            Assert.False(CudaRuntimeDirectory.HoldsRuntime(folder));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                File.Delete(file);
            }

            Directory.Delete(folder);
        }
    }

    [Fact]
    public void APackDirectoryThatIsNotThereFallsThroughToTheHandProvisionedFolder()
    {
        // THIS PC'S CASE: the founder machine has only the hand-provisioned folder, and must keep its card.
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var pack = Path.Combine(data, "models", "cuda-runtime", "versions", "1.0.0", Digest);
        var legacy = Path.Combine(data, "runtime", "cuda");

        Assert.Equal(legacy, CudaRuntimeDirectory.Resolve(null, pack, [data], Existing(legacy), Complete()));
        Assert.Null(CudaRuntimeDirectory.Resolve(null, pack, [data], Existing(), Complete()));
    }

    [Fact]
    public void TheActivePointerNamesTheVersionAndDigestFolder()
    {
        var data = Path.GetFullPath(Path.Combine("C:", "data"));
        var pointer = Path.Combine(data, "models", "cuda-runtime", "active.json");
        string? Read(string path) => string.Equals(path, pointer, StringComparison.OrdinalIgnoreCase)
            ? $$"""{"schemaVersion":1,"modelId":"cuda-runtime","version":"1.0.0","manifestDigest":"{{Digest}}"}"""
            : null;

        Assert.Equal(
            Path.Combine(data, "models", "cuda-runtime", "versions", "1.0.0", Digest),
            CudaRuntimeDirectory.ActivePackDirectory(data, Read));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"modelId":"some-other-pack","version":"1.0.0","manifestDigest":"1f7c92b03c81cc9d44a0f2627a4ae8f4bba9d57f0aded3e634ed554277fcd8f5"}""")]
    [InlineData("""{"modelId":"cuda-runtime","version":"..","manifestDigest":"1f7c92b03c81cc9d44a0f2627a4ae8f4bba9d57f0aded3e634ed554277fcd8f5"}""")]
    [InlineData("""{"modelId":"cuda-runtime","version":"1.0.0/../../x","manifestDigest":"1f7c92b03c81cc9d44a0f2627a4ae8f4bba9d57f0aded3e634ed554277fcd8f5"}""")]
    [InlineData("""{"modelId":"cuda-runtime","version":"1.0.0","manifestDigest":"../../../windows"}""")]
    [InlineData("""{"modelId":"cuda-runtime","version":"1.0.0"}""")]
    [InlineData("""{"modelId":"cuda-runtime","version":1,"manifestDigest":"1f7c92b03c81cc9d44a0f2627a4ae8f4bba9d57f0aded3e634ed554277fcd8f5"}""")]
    public void APointerThatIsMissingMalformedForeignOrSteeringOutOfThePackIsNoPack(string? pointerText)
    {
        var data = Path.GetFullPath(Path.Combine("C:", "data"));

        Assert.Null(CudaRuntimeDirectory.ActivePackDirectory(data, _ => pointerText));
    }

    [Fact]
    public void AnUnreadablePointerIsNoPackRatherThanAFault()
    {
        var data = Path.GetFullPath(Path.Combine("C:", "data"));

        Assert.Null(CudaRuntimeDirectory.ActivePackDirectory(data, _ => throw new IOException("locked")));
        Assert.Null(CudaRuntimeDirectory.ActivePackDirectory(data, _ => throw new UnauthorizedAccessException()));
    }
}
