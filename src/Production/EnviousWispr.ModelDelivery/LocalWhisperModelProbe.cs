using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ModelDelivery;

public sealed class LocalWhisperModelProbe : IWhisperModelProbe
{
    public WhisperModelInventory Probe(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        var directory = Path.GetFullPath(modelDirectory);
        return new WhisperModelInventory(
            QuantizedComplete: IsNonEmpty(Path.Combine(directory, WhisperModelFileNames.Quantized)),
            FullPrecisionComplete: IsNonEmpty(Path.Combine(directory, WhisperModelFileNames.FullPrecision)),
            PreviewSmallComplete: IsNonEmpty(Path.Combine(directory, WhisperModelFileNames.PreviewSmall)));
    }

    private static bool IsNonEmpty(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;
}
