using System.Globalization;
using System.Text.RegularExpressions;

namespace EnviousWispr.Services.Distribution;

/// <summary>Resolves which <c>llama-server.exe</c> executable local EG-1 polish launches. Ref #42.</summary>
/// <remarks>
/// STORE POLICY 10.2.2 GOVERNS DYNAMIC CODE. A packaged Store install carries <c>runtime\llama-server.exe</c>
/// inside its signed package directory and must never execute an unverified binary from the writable
/// <c>%LocalAppData%</c> data folder or an environment variable override. Only an unpackaged development build
/// accepts <c>ENVIOUSWISPR_EG1_SERVER_EXE</c> or falls back to the founder-provisioned
/// <c>runtime\llama.cpp\llama-server.exe</c> under the data directory.
/// </remarks>
public static partial class EgOneRuntimeLocator
{
    public const int ShippingShardCount = 8;

    public static string ResolveServerExecutable(
        string baseDirectory,
        string dataDirectory,
        bool isStoreInstalled,
        string? environmentOverride = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var bundledServer = Path.Combine(baseDirectory, "runtime", "llama-server.exe");
        if (isStoreInstalled)
        {
            return bundledServer;
        }

        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return environmentOverride.Trim();
        }

        var exists = fileExists ?? File.Exists;
        var provisionedServer = Path.Combine(
            dataDirectory,
            "runtime",
            "llama.cpp",
            "llama-server.exe");
        return exists(provisionedServer) ? provisionedServer : bundledServer;
    }

    public static string ResolveModelFile(
        string dataDirectory,
        string? modelId,
        string? environmentOverride = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return environmentOverride.Trim();
        }

        var normalizedId = NormalizeModelId(modelId);
        var modelDirectory = Path.Combine(dataDirectory, "models", normalizedId);
        var founderModel = Path.Combine(modelDirectory, "active.gguf");
        if (!string.Equals(normalizedId, "eg-1", StringComparison.Ordinal))
        {
            return founderModel;
        }

        var exists = fileExists ?? File.Exists;
        var firstShippingShard = Path.Combine(
            modelDirectory,
            FormatShippingShardName(1));
        return IsModelFileReady(firstShippingShard, exists) ? firstShippingShard : founderModel;
    }

    public static bool IsModelFileReady(
        string? modelFilePath,
        Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(modelFilePath))
        {
            return false;
        }

        var exists = fileExists ?? File.Exists;
        var fileName = Path.GetFileName(modelFilePath);
        var directory = Path.GetDirectoryName(modelFilePath);
        var match = ShardedGgufFileName().Match(fileName);
        if (!match.Success || string.IsNullOrEmpty(directory))
        {
            return exists(modelFilePath);
        }

        var prefix = match.Groups["prefix"].Value;
        var totalText = match.Groups["total"].Value;
        if (!int.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || total <= 0
            || total > 256)
        {
            return exists(modelFilePath);
        }

        var width = totalText.Length;
        for (var shard = 1; shard <= total; shard++)
        {
            var shardName = string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}-of-{2}.gguf",
                prefix,
                shard.ToString("D" + width, CultureInfo.InvariantCulture),
                totalText);
            if (!exists(Path.Combine(directory, shardName)))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeModelId(string? modelId)
    {
        var trimmed = modelId?.Trim();
        if (string.Equals(trimmed, "eg-1-mini", StringComparison.OrdinalIgnoreCase))
        {
            return "eg-1-mini";
        }

        if (string.Equals(trimmed, "eg-1-micro", StringComparison.OrdinalIgnoreCase))
        {
            return "eg-1-micro";
        }

        return "eg-1";
    }

    private static string FormatShippingShardName(int shardIndex) => string.Format(
        CultureInfo.InvariantCulture,
        "eg-1-v2-q5_k_m-{0:D5}-of-{1:D5}.gguf",
        shardIndex,
        ShippingShardCount);

    [GeneratedRegex(@"^(?<prefix>.+)-(?<index>\d+)-of-(?<total>\d+)\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShardedGgufFileName();
}
