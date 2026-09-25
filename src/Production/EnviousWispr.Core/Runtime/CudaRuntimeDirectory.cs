using System.Text.Json;
using System.Text.RegularExpressions;
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
///
/// THREE PLACES, IN ONE ORDER: the environment variable a person set on purpose; the graphics runtime
/// pack the app downloaded (the <c>cuda-runtime</c> delivery manifest, which the model store installs
/// under <c>models/cuda-runtime/versions/&lt;version&gt;/&lt;digest&gt;</c>); and the hand-provisioned
/// <c>runtime/cuda</c> folder a development machine already has. The Store package carries no CUDA
/// files (founder decision 2026-09-25), so on a customer's PC the pack is the only one there is.
/// </remarks>
public static partial class CudaRuntimeDirectory
{
    public const string EnvironmentVariable = "ENVIOUSWISPR_CUDA_RUNTIME_DIR";
    public const string DataDirectoryVariable = "ENVIOUSWISPR_DATA_DIRECTORY";

    /// <summary>The delivery manifest id of the graphics runtime pack: <c>models/manifests/cuda-runtime.json</c>.</summary>
    public const string PackId = "cuda-runtime";

    private const string RuntimeFolder = "runtime";
    private const string CudaFolder = "cuda";
    private const string ModelsFolder = "models";
    private const string VersionsFolder = "versions";
    private const string ActivePointerFileName = "active.json";

    /// <summary>
    /// The rule itself, with nothing read from the machine, so it can be tested without one.
    /// </summary>
    /// <remarks>
    /// AN EXPLICIT SETTING WINS ONLY IF IT HOLDS THE RUNTIME. A configured directory that is not there, or
    /// that lacks any file the probe requires, is not honoured: it would put the caller on a path the card
    /// cannot load from, and - worse since the pack exists - shadow a verified pack that would have worked.
    /// </remarks>
    public static string? Resolve(
        string? configured,
        IEnumerable<string> dataDirectories,
        Func<string, bool> directoryExists,
        Func<string, bool> holdsRuntime) =>
        Resolve(configured, installedPackDirectory: null, dataDirectories, directoryExists, holdsRuntime);

    /// <summary>The rule with the downloaded pack in it: configured, then the pack, then a hand-provisioned folder.</summary>
    /// <remarks>
    /// THE PACK OUTRANKS THE HAND-PROVISIONED FOLDER because every byte of it was hashed against a manifest
    /// compiled into the build, the same reason <c>InstalledModelLocator</c> puts a store-activated model
    /// ahead of a hand-copied one.
    /// </remarks>
    public static string? Resolve(
        string? configured,
        string? installedPackDirectory,
        IEnumerable<string> dataDirectories,
        Func<string, bool> directoryExists,
        Func<string, bool> holdsRuntime)
    {
        ArgumentNullException.ThrowIfNull(dataDirectories);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(holdsRuntime);
        if (!string.IsNullOrWhiteSpace(configured) && directoryExists(configured) && holdsRuntime(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (!string.IsNullOrWhiteSpace(installedPackDirectory) && directoryExists(installedPackDirectory))
        {
            return Path.GetFullPath(installedPackDirectory);
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
    /// <param name="dataDirectory">The app's data directory.</param>
    /// <param name="verifiedPackDirectory">
    /// The pack directory the model store opened and re-hashed, or null when the pack is not installed or
    /// did not verify. The app asks the store, which owns verification; a harness has no store and reads the
    /// pointer instead (<see cref="ForTooling"/>).
    /// </param>
    public static string? ForApplication(string dataDirectory, string? verifiedPackDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            verifiedPackDirectory,
            [dataDirectory],
            Directory.Exists,
            HoldsRuntime);
    }

    /// <summary>
    /// What a harness uses, which has no data directory of its own.
    /// </summary>
    /// <remarks>
    /// EVERY CHANNEL, IN A FIXED ORDER, because a development machine may have a stable install, a
    /// founder install, or both, and a harness that guessed one would fail on the other for a reason
    /// nobody could read. This is deliberately NOT what the app does: the app knows which channel it
    /// is and must never reach into another one's folder. The first channel with an activated pack
    /// supplies the pack; a harness reads its pointer without re-hashing, which is the one place the
    /// two answers can differ (a pack whose files were altered after admission).
    /// </remarks>
    public static string? ForTooling()
    {
        var dataDirectories = ToolingDataDirectories().ToArray();
        return Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            dataDirectories
                .Select(dataDirectory => ActivePackDirectory(dataDirectory, ReadIfExists))
                .FirstOrDefault(pack => pack is not null && Directory.Exists(pack)),
            dataDirectories,
            Directory.Exists,
            HoldsRuntime);
    }

    /// <summary>
    /// Where the model store activated the pack under a data directory, read from its pointer alone, or null.
    /// </summary>
    /// <remarks>
    /// THE STORE'S LAYOUT, READ WITHOUT THE STORE, because Core cannot reference the delivery library. A test
    /// installs a pack through the real store and requires this to name the directory it admitted, so the
    /// two cannot drift apart unnoticed. The version and the digest are checked for shape before they become
    /// path segments: the pointer is a file on disk, and a hand-edited one must not steer this out of the
    /// pack's own folder.
    /// </remarks>
    public static string? ActivePackDirectory(string dataDirectory, Func<string, string?> readAllText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(readAllText);
        var packRoot = Path.Combine(dataDirectory, ModelsFolder, PackId);
        string? text;
        try
        {
            text = readAllText(Path.Combine(packRoot, ActivePointerFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var pointer = JsonDocument.Parse(text);
            var root = pointer.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "modelId", out var modelId) ||
                !string.Equals(modelId, PackId, StringComparison.Ordinal) ||
                !TryGetString(root, "version", out var version) ||
                !SafeVersionRegex().IsMatch(version) ||
                !TryGetString(root, "manifestDigest", out var digest) ||
                !DigestRegex().IsMatch(digest))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(packRoot, VersionsFolder, version, digest));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return value.Length > 0;
            }
        }

        return false;
    }

    /// <summary>The probe's own check, on one folder: every file of the full runtime set is there.</summary>
    public static bool HoldsRuntime(string directory) =>
        CudaRuntimeLibraries.AllPresentIn(Path.GetFullPath(directory), File.Exists);

    private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

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

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersionRegex();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();
}
