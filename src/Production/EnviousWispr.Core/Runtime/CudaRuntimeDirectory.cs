using EnviousWispr.Core.Distribution;

namespace EnviousWispr.Core.Runtime;

/// <summary>
/// Where the CUDA runtime files live, answered the same way for the app and for the harnesses that
/// judge it.
/// </summary>
/// <remarks>
/// ONE ANSWER, BECAUSE TWO ANSWERS WERE READ AS A PRODUCT DEFECT. The app looked in the environment
/// variable and then in its own data directory; the acceptance harnesses looked only at the variable.
/// So on a machine where the runtime had been provisioned but the variable was unset, the app selected
/// and ran on the card while the gate reported that CUDA could not load - and #45 was filed on that
/// output, against the product, for a fault that was in the measurement. A gate that fails where the
/// product succeeds teaches people to ignore it. Ref: #129.
/// </remarks>
public static class CudaRuntimeDirectory
{
    public const string EnvironmentVariable = "ENVIOUSWISPR_CUDA_RUNTIME_DIR";
    public const string DataDirectoryVariable = "ENVIOUSWISPR_DATA_DIRECTORY";

    private const string RuntimeFolder = "runtime";
    private const string CudaFolder = "cuda";

    /// <summary>
    /// The rule itself, with nothing read from the machine, so it can be tested without one.
    /// </summary>
    /// <remarks>
    /// AN EXPLICIT SETTING WINS EVEN WHEN IT IS WRONG, ONLY IF IT EXISTS. A configured directory that
    /// is not there is not honoured: it would put the caller on a path with no files in it and report
    /// success, which is the failure this whole type exists to stop.
    /// </remarks>
    public static string? Resolve(
        string? configured,
        IEnumerable<string> dataDirectories,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(dataDirectories);
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (!string.IsNullOrWhiteSpace(configured) && directoryExists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var dataDirectory in dataDirectories)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                continue;
            }

            var candidate = Path.Combine(dataDirectory, RuntimeFolder, CudaFolder);
            if (directoryExists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// What the running app uses: its own data directory and nothing else's.
    /// </summary>
    public static string? ForApplication(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            [dataDirectory],
            Directory.Exists);
    }

    /// <summary>
    /// What a harness uses, which has no data directory of its own.
    /// </summary>
    /// <remarks>
    /// EVERY CHANNEL, IN A FIXED ORDER, because a development machine may have a stable install, a
    /// founder install, or both, and a harness that guessed one would fail on the other for a reason
    /// nobody could read. This is deliberately NOT what the app does: the app knows which channel it
    /// is and must never reach into another one's folder.
    /// </remarks>
    public static string? ForTooling() => Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        ToolingDataDirectories(),
        Directory.Exists);

    private static IEnumerable<string> ToolingDataDirectories()
    {
        var configured = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        foreach (var channel in Enum.GetValues<ReleaseChannel>())
        {
            yield return Path.Combine(
                localApplicationData,
                "Envious Labs",
                ReleaseIdentity.For(channel).DataDirectoryName);
        }
    }
}
