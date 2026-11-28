using System.IO.Compression;
using System.Reflection;

namespace EnviousWispr.PostProcessing;

internal static class CasingResources
{
    private static readonly Assembly PostProcessingAssembly = typeof(CasingResources).Assembly;

    internal static Stream? TryOpenBrotliDecompressedStream(string resourceFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceFileName);
        var exactName = $"EnviousWispr.PostProcessing.Resources.Casing.{resourceFileName}";
        var compressed = PostProcessingAssembly.GetManifestResourceStream(exactName);
        if (compressed is null)
        {
            var match = PostProcessingAssembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith($".{resourceFileName}", StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return null;
            }

            compressed = PostProcessingAssembly.GetManifestResourceStream(match);
            if (compressed is null)
            {
                return null;
            }
        }

        using (compressed)
        using (var brotli = new BrotliStream(compressed, CompressionMode.Decompress))
        {
            var output = new MemoryStream();
            brotli.CopyTo(output);
            output.Position = 0;
            return output;
        }
    }
}
