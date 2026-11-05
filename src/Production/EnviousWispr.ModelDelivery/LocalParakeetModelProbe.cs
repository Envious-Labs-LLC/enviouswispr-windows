using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ModelDelivery;

public sealed class LocalParakeetModelProbe : IParakeetModelProbe
{
    private static readonly string[] SharedFiles = ["nemo128.onnx", "vocab.txt"];
    private static readonly string[] Int8Files =
        ["encoder-model.int8.onnx", "decoder_joint-model.int8.onnx"];
    private static readonly string[] LegacyFp32Files =
        ["encoder-model.onnx", "encoder-model.onnx.data", "decoder_joint-model.onnx"];
    private static readonly string[] UltraExternalFiles =
        ["encoder-model.onnx.data.000", "encoder-model.onnx.data.001", "decoder_joint-model.onnx.data"];
    private static readonly string[] UltraFp32Files =
        ["encoder-model.onnx", "encoder-model.onnx.data.000", "encoder-model.onnx.data.001",
            "decoder_joint-model.onnx", "decoder_joint-model.onnx.data"];

    public ParakeetModelInventory Probe(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        var directory = Path.GetFullPath(modelDirectory);
        var sharedComplete = AllNonEmpty(directory, SharedFiles);
        // This recognizes file layouts only; pinned graph identity/dependencies are validated separately.
        // Even an empty conflicting file marks a mixed layout, rather than satisfying either pack.
        var legacyComplete = AllNonEmpty(directory, LegacyFp32Files) &&
            !UltraExternalFiles.Any(fileName => File.Exists(Path.Combine(directory, fileName)));
        var ultraComplete = AllNonEmpty(directory, UltraFp32Files) &&
            !File.Exists(Path.Combine(directory, "encoder-model.onnx.data"));
        return new ParakeetModelInventory(
            Int8Complete: sharedComplete && AllNonEmpty(directory, Int8Files),
            Fp32Complete: sharedComplete && (legacyComplete || ultraComplete));
    }

    private static bool AllNonEmpty(string directory, IEnumerable<string> fileNames) =>
        fileNames.All(fileName =>
        {
            var path = Path.Combine(directory, fileName);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        });
}
