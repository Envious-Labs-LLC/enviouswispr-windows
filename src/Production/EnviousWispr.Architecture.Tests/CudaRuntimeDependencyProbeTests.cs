using EnviousWispr.Services.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class CudaRuntimeDependencyProbeTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        $"EnviousWisprCudaProbeTests-{Guid.NewGuid():N}");

    [Fact]
    public void CompleteDependencySetMaySpanPinnedRuntimeDirectories()
    {
        var cuda = Path.Combine(_scratch, "cuda");
        var cudnn = Path.Combine(_scratch, "cudnn");
        Directory.CreateDirectory(cuda);
        Directory.CreateDirectory(cudnn);
        foreach (var library in CudaRuntimeDependencyProbe.RequiredLibraryNames)
        {
            File.WriteAllBytes(
                Path.Combine(library.StartsWith("cudnn", StringComparison.Ordinal) ? cudnn : cuda, library),
                [0]);
        }

        Assert.True(CudaRuntimeDependencyProbe.IsCompleteInDirectories([cuda, cudnn]));
    }

    [Fact]
    public void MissingSingleDependencyFailsClosed()
    {
        Directory.CreateDirectory(_scratch);
        foreach (var library in CudaRuntimeDependencyProbe.RequiredLibraryNames.Skip(1))
        {
            File.WriteAllBytes(Path.Combine(_scratch, library), [0]);
        }

        Assert.False(CudaRuntimeDependencyProbe.IsCompleteInDirectories([_scratch]));
    }

    [Fact]
    public void MalformedSearchDirectoryFailsClosed()
    {
        Assert.False(CudaRuntimeDependencyProbe.IsCompleteInDirectories(["\0invalid"]));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_scratch))
        {
            return;
        }

        var resolved = Path.GetFullPath(_scratch);
        var expectedRoot = Path.GetFullPath(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }
}
