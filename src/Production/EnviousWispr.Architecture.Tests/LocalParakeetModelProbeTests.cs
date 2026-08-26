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

    private static async Task WriteFilesAsync(string directory, params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), [1]);
        }
    }
}
