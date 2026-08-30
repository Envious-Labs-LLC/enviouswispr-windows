using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ModelDelivery;

public sealed class LocalParakeetModelProbe : IParakeetModelProbe
{
    private static readonly string[] SharedFiles = ["nemo128.onnx", "vocab.txt"];
    private static readonly string[] Int8Files =
        ["encoder-model.int8.onnx", "decoder_joint-model.int8.onnx"];
    private static readonly string[] Fp32Files =
        ["encoder-model.onnx", "encoder-model.onnx.data", "decoder_joint-model.onnx"];

    public ParakeetModelInventory Probe(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        var directory = Path.GetFullPath(modelDirectory);
        var sharedComplete = AllNonEmpty(directory, SharedFiles);
        return new ParakeetModelInventory(
            Int8Complete: sharedComplete && AllNonEmpty(directory, Int8Files),
            Fp32Complete: sharedComplete && AllNonEmpty(directory, Fp32Files));
    }

    private static bool AllNonEmpty(string directory, IEnumerable<string> fileNames) =>
        fileNames.All(fileName =>
        {
            var path = Path.Combine(directory, fileName);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });
}
