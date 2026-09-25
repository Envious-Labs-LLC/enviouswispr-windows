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

    /// <summary>whisper.cpp's three files, spelled here rather than read from the probe.</summary>
    /// <remarks>
    /// LITERALS ON PURPOSE. A fixture built from the probe's own list passes whatever the list says -
    /// add cuDNN to it and the fixture creates cuDNN too. cuBLAS and cuBLASLt come from the native import
    /// chain; the CUDA runtime from Whisper.net's own CUDA check. Ref: #99, #163.
    /// </remarks>
    private static readonly string[] WhisperFiles = ["cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll"];

    [Fact]
    public void WhispersThreeFilesAloneAreComplete()
    {
        Directory.CreateDirectory(_scratch);
        foreach (var library in WhisperFiles)
        {
            File.WriteAllBytes(Path.Combine(_scratch, library), [0]);
        }

        Assert.True(CudaRuntimeDependencyProbe.IsWhisperComplete(_scratch));
        Assert.False(CudaRuntimeDependencyProbe.IsCompleteInDirectories([_scratch]));
    }

    [Theory]
    [InlineData("cublas64_13.dll")]
    [InlineData("cublasLt64_13.dll")]
    [InlineData("cudart64_13.dll")]
    public void AMissingWhisperFileFailsClosed(string missing)
    {
        Directory.CreateDirectory(_scratch);
        foreach (var library in WhisperFiles.Where(name => name != missing))
        {
            File.WriteAllBytes(Path.Combine(_scratch, library), [0]);
        }

        // Everything the OTHER engine needs is present too, so it cannot stand in for the missing file.
        foreach (var library in new[] { "cufft64_12.dll", "cudnn64_9.dll", "cudnn_ops64_9.dll" })
        {
            File.WriteAllBytes(Path.Combine(_scratch, library), [0]);
        }

        Assert.False(CudaRuntimeDependencyProbe.IsCompleteInDirectories(
            [_scratch],
            CudaRuntimeDependencyProbe.WhisperRequiredLibraryNames));
    }

    [Fact]
    public void WhispersSetIsExactlyTheThreeFiles()
    {
        Assert.Equal(
            WhisperFiles.Order(StringComparer.OrdinalIgnoreCase),
            CudaRuntimeDependencyProbe.WhisperRequiredLibraryNames.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The discovery hands the Whisper answer to the Whisper flag, not the other one.</summary>
    [Fact]
    public async Task DiscoveryReportsWhispersFilesOnTheWhisperFlag()
    {
        Directory.CreateDirectory(_scratch);
        foreach (var library in WhisperFiles)
        {
            File.WriteAllBytes(Path.Combine(_scratch, library), [0]);
        }

        var hardware = await new WindowsHardwareDiscovery(_scratch).ProbeAsync();

        Assert.True(hardware.IsWhisperCudaDependencySetAvailable);
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
