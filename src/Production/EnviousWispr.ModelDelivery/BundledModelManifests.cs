using System.Reflection;

namespace EnviousWispr.ModelDelivery;

/// <summary>
/// The delivery manifests compiled into this assembly, one per model id the product can install.
/// </summary>
/// <remarks>
/// THE MANIFESTS TRAVEL WITH THE CODE, AS ON macOS. `models/manifests/&lt;modelId&gt;.json` at the
/// repository root is the reviewable source; the build embeds each one, and the application never
/// fetches a manifest over the network. So what a given build can install is fixed at build time and
/// can only change through an update, which is the macOS contract's invariant 4 restated for Windows.
///
/// A model id with no embedded manifest is not an error here; it is the answer "this build cannot
/// install that", and the caller decides what to say about it.
/// </remarks>
public static class BundledModelManifests
{
    private const string ResourcePrefix = "manifests/";

    public static IReadOnlyList<string> ModelIds { get; } = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
            name.EndsWith(".json", StringComparison.Ordinal))
        .Select(name => name[ResourcePrefix.Length..^".json".Length])
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    public static byte[]? TryRead(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (!ModelManifestVerifier.IsSafeModelId(modelId))
        {
            return null;
        }

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourcePrefix + modelId + ".json");
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Loads and digest-checks the bundled manifest for a model id, or reports why it cannot.</summary>
    public static ManifestVerificationResult Load(string modelId, ModelManifestVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var bytes = TryRead(modelId);
        if (bytes is null)
        {
            return new(ManifestVerificationStatus.Unreachable);
        }

        var result = verifier.VerifyBundled(bytes);
        if (result.Succeeded &&
            !string.Equals(result.Manifest!.Payload.ModelId, modelId, StringComparison.Ordinal))
        {
            // THE FILE NAME AND THE PAYLOAD DISAGREE. A manifest saved under the wrong name would
            // otherwise install one model when another was asked for, and report success.
            return new(ManifestVerificationStatus.InvalidPayload);
        }

        return result;
    }
}
