namespace EnviousWispr.Services.Distribution;

/// <summary>Resolves which <c>llama-server.exe</c> executable local EG-1 polish launches. Ref #42.</summary>
/// <remarks>
/// STORE POLICY 10.2.2 GOVERNS DYNAMIC CODE. A packaged Store install carries <c>runtime\llama-server.exe</c>
/// inside its signed package directory and must never execute an unverified binary from the writable
/// <c>%LocalAppData%</c> data folder or an environment variable override. Only an unpackaged development build
/// accepts <c>ENVIOUSWISPR_EG1_SERVER_EXE</c> or falls back to the founder-provisioned
/// <c>runtime\llama.cpp\llama-server.exe</c> under the data directory.
/// </remarks>
public static class EgOneRuntimeLocator
{
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
}
