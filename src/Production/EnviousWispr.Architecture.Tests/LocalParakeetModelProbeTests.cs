using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

public sealed class LocalParakeetModelProbeTests
{
    [Fact]
    public async Task ProbeRequiresEveryNonEmptyFileForEachPack()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"enviouswispr-model-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await WriteFilesAsync(directory,
                "nemo128.onnx",
                "vocab.txt",
                "encoder-model.int8.onnx",
                "decoder_joint-model.int8.onnx",
                "encoder-model.onnx",
                "encoder-model.onnx.data");
            await File.WriteAllBytesAsync(Path.Combine(directory, "decoder_joint-model.onnx"), []);

            var result = new LocalParakeetModelProbe().Probe(directory);

            Assert.True(result.Int8Complete);
            Assert.False(result.Fp32Complete);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static readonly string[] Shared = ["nemo128.onnx", "vocab.txt"];
    private static readonly string[] Cpu = ["encoder-model.int8.onnx", "decoder_joint-model.int8.onnx"];
    private static readonly string[] Legacy = ["encoder-model.onnx", "encoder-model.onnx.data", "decoder_joint-model.onnx"];
    private static readonly string[] Ultra = ["encoder-model.onnx", "encoder-model.onnx.data.000", "encoder-model.onnx.data.001",
        "decoder_joint-model.onnx", "decoder_joint-model.onnx.data"];

    [Fact]
    public async Task CompleteUltraLayoutIsAccepted()
    {
        using var run = new Run();
        await WriteFilesAsync(run.Directory, [.. Shared, .. Ultra]);
        var result = new LocalParakeetModelProbe().Probe(run.Directory);
        Assert.True(result.Fp32Complete);
        Assert.False(result.Int8Complete);
    }

    [Fact]
    public async Task CompleteLegacyLayoutIsAccepted()
    {
        using var run = new Run();
        await WriteFilesAsync(run.Directory, [.. Shared, .. Legacy, .. Cpu]);
        var result = new LocalParakeetModelProbe().Probe(run.Directory);
        Assert.True(result.Fp32Complete);
        Assert.True(result.Int8Complete);
    }

    public static IEnumerable<object[]> MissingOrEmptyFiles()
    {
        foreach (var layout in new[] { "legacy", "ultra" })
        foreach (var file in Shared.Concat(layout == "legacy" ? Legacy : Ultra))
        foreach (var empty in new[] { false, true })
            yield return [layout, file, empty];
    }

    [Theory]
    [MemberData(nameof(MissingOrEmptyFiles))]
    public async Task EveryRequiredFileMustBeNonempty(string layout, string file, bool empty)
    {
        using var run = new Run();
        await WriteFilesAsync(run.Directory, [.. Shared, .. (layout == "legacy" ? Legacy : Ultra), .. Cpu]);
        if (empty) await File.WriteAllBytesAsync(Path.Combine(run.Directory, file), []);
        else File.Delete(Path.Combine(run.Directory, file));
        var result = new LocalParakeetModelProbe().Probe(run.Directory);
        Assert.False(result.Fp32Complete);
        Assert.Equal(!Shared.Contains(file), result.Int8Complete);
    }

    [Theory]
    [InlineData("legacy", "encoder-model.onnx.data.000", false)]
    [InlineData("legacy", "encoder-model.onnx.data.001", false)]
    [InlineData("legacy", "decoder_joint-model.onnx.data", false)]
    [InlineData("legacy", "encoder-model.onnx.data.000", true)]
    [InlineData("legacy", "encoder-model.onnx.data.001", true)]
    [InlineData("legacy", "decoder_joint-model.onnx.data", true)]
    [InlineData("ultra", "encoder-model.onnx.data", false)]
    [InlineData("ultra", "encoder-model.onnx.data", true)]
    public async Task ConflictingFilesRejectFp32EvenWhenEmpty(string layout, string conflict, bool empty)
    {
        using var run = new Run();
        await WriteFilesAsync(run.Directory, [.. Shared, .. (layout == "legacy" ? Legacy : Ultra), .. Cpu]);
        await File.WriteAllBytesAsync(Path.Combine(run.Directory, conflict), empty ? [] : [1]);
        var result = new LocalParakeetModelProbe().Probe(run.Directory);
        Assert.False(result.Fp32Complete);
        Assert.True(result.Int8Complete);
    }

    [Fact]
    public async Task BothCompleteLayoutsTogetherAreRejected()
    {
        using var run = new Run();
        await WriteFilesAsync(run.Directory, [.. Shared, .. Legacy, .. Ultra, .. Cpu]);
        var result = new LocalParakeetModelProbe().Probe(run.Directory);
        Assert.False(result.Fp32Complete);
        Assert.True(result.Int8Complete);
    }

    [Theory]
    [InlineData("encoder-model.int8.onnx", false)]
    [InlineData("encoder-model.int8.onnx", true)]
    [InlineData("decoder_joint-model.int8.onnx", false)]
    [InlineData("decoder_joint-model.int8.onnx", true)]
    public async Task CpuRequirementsRemainIndependent(string file, bool empty)
    {
        using var run = new Run();
        await WriteFilesAsync(run.Directory, [.. Shared, .. Ultra, .. Cpu]);
        if (empty) await File.WriteAllBytesAsync(Path.Combine(run.Directory, file), []);
        else File.Delete(Path.Combine(run.Directory, file));
        var result = new LocalParakeetModelProbe().Probe(run.Directory);
        Assert.False(result.Int8Complete);
        Assert.True(result.Fp32Complete);
    }

    private sealed class Run : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "enviouswispr-layout-" + Guid.NewGuid().ToString("N"));
        public Run() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private static async Task WriteFilesAsync(string directory, params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), [1]);
        }
    }
}
