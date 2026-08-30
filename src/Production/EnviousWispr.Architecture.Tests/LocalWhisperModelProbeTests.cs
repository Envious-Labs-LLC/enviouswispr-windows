using EnviousWispr.Core.Runtime;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

public sealed class LocalWhisperModelProbeTests
{
    [Fact]
    public void DetectsOnlyNonEmptyPinnedModelFiles()
    {
        var directory = Directory.CreateTempSubdirectory("EnviousWispr.WhisperProbe.");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, WhisperModelFileNames.Quantized), [1]);
            File.WriteAllBytes(Path.Combine(directory.FullName, WhisperModelFileNames.FullPrecision), []);
            File.WriteAllBytes(Path.Combine(directory.FullName, WhisperModelFileNames.PreviewSmall), [2]);

            var inventory = new LocalWhisperModelProbe().Probe(directory.FullName);

            Assert.True(inventory.QuantizedComplete);
            Assert.False(inventory.FullPrecisionComplete);
            Assert.True(inventory.PreviewSmallComplete);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
