// Authors and checks the bundled delivery manifests under models/manifests/.
//
//   create  --model-id <id> --version <semver> --minimum-app <version> --directory <local model dir>
//           --file <relativePath> [--file ...] --mirror <base url> [--backup <base url>]
//           [--shard-bytes <n>] --license-name <name> --license-url <url> --license-notice <text>
//           --output <path>
//
// SHARDS. With --shard-bytes, any file larger than that is ALSO described as parts of that size,
// each with its own hash and a mirror-only source named <file>.part<N>. The whole-file sources stay
// as the fallback. The mirror cannot edge-cache anything over 512 MB, so the pinned files that
// cross that line are published as parts under it.
//   verify  <manifest path> [--directory <local model dir>]
//   provision <manifest path> --store <dir>
//
// PROVISION IS THE LIVE PROOF. It installs the manifest into a store directory exactly as the app
// would, printing every request the store makes and what each source answered, so "the mirror
// served it" and "it fell over to the backup" are read from the wire rather than inferred from
// speed. Run it against a fresh --store to prove a manifest before it ships.
//
// SIZES AND HASHES COME FROM THE LOCAL FILES, NEVER FROM THE COMMAND LINE. A manifest is a pin, and a
// pin typed by hand is a pin nobody checked; every artefact is read from disk and hashed here so the
// document can only describe bytes that exist. `verify` re-derives the digest and, given the
// directory, re-hashes every file, which is what CI and a reviewer run.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.ModelDelivery;

if (args.Length == 0)
{
    return Usage();
}

try
{
    return args[0] switch
    {
        "create" => Create(args[1..]),
        "verify" => Verify(args[1..]),
        "provision" => await ProvisionAsync(args[1..]),
        _ => Usage(),
    };
}
catch (ToolException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine(
        "usage: create --model-id <id> --version <semver> --minimum-app <version> --directory <dir> " +
        "--file <relativePath>... --mirror <base url> [--backup <base url>] [--shard-bytes <n>] --license-name <n> " +
        "--license-url <url> --license-notice <text> --output <path>");
    Console.Error.WriteLine("       verify <manifest path> [--directory <dir>]");
    Console.Error.WriteLine("       provision <manifest path> --store <dir>");
    return 1;
}

static int Create(string[] arguments)
{
    var options = Options.Parse(arguments);
    var modelId = options.Required("model-id");
    var version = options.Required("version");
    var minimumApp = options.Required("minimum-app");
    var directory = Path.GetFullPath(options.Required("directory"));
    var mirror = options.Required("mirror");
    var backup = options.Optional("backup");
    var shardBytes = options.Optional("shard-bytes") is { } shardText
        ? long.Parse(shardText, System.Globalization.CultureInfo.InvariantCulture)
        : 0;
    if (shardBytes < 0)
    {
        throw new ToolException("--shard-bytes must be positive.");
    }

    var files = options.All("file");
    if (files.Count == 0)
    {
        throw new ToolException("At least one --file is required.");
    }

    var fileNodes = new JsonArray();
    foreach (var relativePath in files)
    {
        if (!ModelManifestVerifier.IsSafeRelativePath(relativePath))
        {
            throw new ToolException($"'{relativePath}' is not a safe relative path.");
        }

        var localPath = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(localPath))
        {
            throw new ToolException($"Missing local artefact: {localPath}");
        }

        var (size, sha256) = Measure(localPath);
        Console.WriteLine($"{sha256}  {size,12}  {relativePath}");
        var sharded = shardBytes > 0 && size > shardBytes;
        // A SHARDED FILE IS NEVER ON THE MIRROR WHOLE, so its whole-file sources name only the
        // backup. The mirror URL for the whole would be a guaranteed 404 on the fallback path -
        // and, worse, an edge cache rule caches its negatives for the full TTL, so one client
        // asking for it would pin that 404 for a year. The mirror lives in the parts instead.
        if (sharded && backup is null)
        {
            throw new ToolException($"{relativePath} is sharded, so --backup is required for its whole-file fallback.");
        }

        var sources = new JsonArray();
        if (!sharded)
        {
            sources.Add(JoinUrl(mirror, relativePath));
        }

        if (backup is not null)
        {
            sources.Add(JoinUrl(backup, relativePath));
        }

        var node = new JsonObject
        {
            ["relativePath"] = relativePath,
            ["sizeBytes"] = size,
            ["sha256"] = sha256,
            ["sources"] = sources,
        };
        if (sharded)
        {
            var parts = new JsonArray();
            var index = 0;
            foreach (var (partSize, partSha256) in MeasureParts(localPath, shardBytes))
            {
                Console.WriteLine($"{partSha256}  {partSize,12}  {relativePath}.part{index}");
                parts.Add(new JsonObject
                {
                    ["sizeBytes"] = partSize,
                    ["sha256"] = partSha256,
                    ["sources"] = new JsonArray { JoinUrl(mirror, $"{relativePath}.part{index}") },
                });
                index++;
            }

            node["parts"] = parts;
        }

        fileNodes.Add(node);
    }

    var manifest = new JsonObject
    {
        ["schemaVersion"] = ModelManifestVerifier.CurrentManifestSchemaVersion,
        ["modelId"] = modelId,
        ["version"] = version,
        ["minimumAppVersion"] = minimumApp,
        ["license"] = new JsonObject
        {
            ["name"] = options.Required("license-name"),
            ["url"] = options.Required("license-url"),
            ["notice"] = options.Required("license-notice"),
        },
        ["files"] = fileNodes,
    };

    using (var document = JsonDocument.Parse(manifest.ToJsonString()))
    {
        manifest[ModelManifestCanonicalJson.DigestPropertyName] =
            ModelManifestCanonicalJson.DigestOf(document.RootElement);
    }

    var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(Json.Indented) + "\n");
    var check = new ModelManifestVerifier(new Dictionary<string, string>()).VerifyBundled(bytes);
    if (!check.Succeeded)
    {
        throw new ToolException($"The manifest this tool just wrote does not verify: {check.Status}.");
    }

    var output = Path.GetFullPath(options.Required("output"));
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllBytes(output, bytes);
    Console.WriteLine($"wrote {output}");
    Console.WriteLine($"manifestDigest {check.Manifest!.ManifestDigest}");
    return 0;
}

static int Verify(string[] arguments)
{
    if (arguments.Length == 0)
    {
        throw new ToolException("verify needs a manifest path.");
    }

    var path = Path.GetFullPath(arguments[0]);
    var options = Options.Parse(arguments[1..]);
    var bytes = File.ReadAllBytes(path);
    var result = new ModelManifestVerifier(new Dictionary<string, string>()).VerifyBundled(bytes);
    if (!result.Succeeded)
    {
        throw new ToolException($"{path}: {result.Status}");
    }

    var payload = result.Manifest!.Payload;
    Console.WriteLine($"{payload.ModelId} {payload.Version} digest {result.Manifest.ManifestDigest} files {payload.Files.Count}");
    var directory = options.Optional("directory");
    if (directory is null)
    {
        return 0;
    }

    var failures = 0;
    foreach (var file in payload.Files)
    {
        var localPath = Path.Combine(
            Path.GetFullPath(directory),
            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(localPath))
        {
            Console.WriteLine($"MISSING   {file.RelativePath}");
            failures++;
            continue;
        }

        var (size, sha256) = Measure(localPath);
        var ok = size == file.SizeBytes && string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"{(ok ? "ok       " : "MISMATCH ")} {file.RelativePath}");
        if (!ok)
        {
            failures++;
        }

        if (file.Parts is not { Count: > 0 })
        {
            continue;
        }

        var measured = MeasureParts(localPath, file.Parts[0].SizeBytes).ToArray();
        for (var index = 0; index < file.Parts.Count; index++)
        {
            var partOk = index < measured.Length &&
                measured[index].Size == file.Parts[index].SizeBytes &&
                string.Equals(measured[index].Sha256, file.Parts[index].Sha256, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"{(partOk ? "ok       " : "MISMATCH ")} {file.RelativePath}.part{index}");
            if (!partOk)
            {
                failures++;
            }
        }
    }

    return failures == 0 ? 0 : 3;
}

static async Task<int> ProvisionAsync(string[] arguments)
{
    if (arguments.Length == 0)
    {
        throw new ToolException("provision needs a manifest path.");
    }

    var path = Path.GetFullPath(arguments[0]);
    var options = Options.Parse(arguments[1..]);
    var storeRoot = Path.GetFullPath(options.Required("store"));
    var verifier = new ModelManifestVerifier(new Dictionary<string, string>());
    var manifest = verifier.VerifyBundled(File.ReadAllBytes(path));
    if (!manifest.Succeeded)
    {
        throw new ToolException($"{path}: {manifest.Status}");
    }

    using var handler = new WireLog(new SocketsHttpHandler());
    using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var store = new ModelStore(
        storeRoot,
        httpClient,
        verifier,
        new Version(999, 0, 0),
        observer: new ConsoleObserver(clock));
    var result = await new ModelProvisioner(store, _ => manifest).ProvisionAsync(manifest.Manifest!.Payload.ModelId);
    Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F1}s  result {(result.Succeeded ? "INSTALLED" : "FAILED " + result.Failure)} {result.Installed?.DirectoryPath}");
    return result.Succeeded ? 0 : 4;
}

static (long Size, string Sha256) Measure(string path)
{
    using var stream = File.OpenRead(path);
    var hash = SHA256.HashData(stream);
    return (stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
}

// SLICES ARE HASHED FROM THE SAME BYTES THE WHOLE-FILE HASH COVERS, in one pass per file, so a
// part can never describe a different file than the whole.
static IEnumerable<(long Size, string Sha256)> MeasureParts(string path, long shardBytes)
{
    using var stream = File.OpenRead(path);
    var buffer = new byte[1024 * 1024];
    while (stream.Position < stream.Length)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long partSize = 0;
        while (partSize < shardBytes)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, shardBytes - partSize));
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            partSize += read;
        }

        yield return (partSize, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}

static string JoinUrl(string baseUrl, string relativePath) =>
    baseUrl.EndsWith('/') ? baseUrl + relativePath : baseUrl + "/" + relativePath;

internal sealed class ToolException(string message) : Exception(message);

// EVERY REQUEST AND EVERY ANSWER, ON ONE LINE EACH. The store never says which source served a file;
// this is where that becomes visible.
internal sealed class WireLog(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var range = request.Headers.Range?.ToString();
        var response = await base.SendAsync(request, cancellationToken);
        var cache = response.Headers.TryGetValues("cf-cache-status", out var values) ? string.Join(",", values) : "-";
        Console.WriteLine($"          {(int)response.StatusCode} {request.RequestUri}{(range is null ? string.Empty : " range " + range)} cf-cache-status={cache} length={response.Content.Headers.ContentLength}");
        return response;
    }
}

internal sealed class ConsoleObserver(System.Diagnostics.Stopwatch clock) : IModelDeliveryObserver
{
    private long _lastReported = -1;

    public void Observe(ModelDeliveryEvent deliveryEvent)
    {
        if (deliveryEvent.Code == ModelDeliveryEventCode.DownloadStarted && deliveryEvent.CompletedBytes is { } completed)
        {
            // ONE LINE PER 64 MB, not one per 128 KB chunk.
            var bucket = completed / (64L * 1024 * 1024);
            if (bucket == _lastReported)
            {
                return;
            }

            _lastReported = bucket;
        }

        Console.WriteLine($"{clock.Elapsed.TotalSeconds,8:F1}s  {deliveryEvent.Code}{(deliveryEvent.Failure == ModelDeliveryFailure.None ? string.Empty : " " + deliveryEvent.Failure)}{(deliveryEvent.CompletedBytes is null ? string.Empty : $" {deliveryEvent.CompletedBytes / (1024 * 1024)}/{deliveryEvent.TotalBytes / (1024 * 1024)} MB")}");
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

internal sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public static Options Parse(string[] arguments)
    {
        var options = new Options();
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            {
                throw new ToolException($"Unexpected argument '{argument}'.");
            }

            var name = argument[2..];
            if (!options._values.TryGetValue(name, out var list))
            {
                list = [];
                options._values[name] = list;
            }

            list.Add(arguments[++index]);
        }

        return options;
    }

    public string Required(string name) =>
        Optional(name) ?? throw new ToolException($"--{name} is required.");

    public string? Optional(string name) =>
        _values.TryGetValue(name, out var list) && list.Count > 0
            ? list.Count == 1
                ? list[0]
                : throw new ToolException($"--{name} was given more than once.")
            : null;

    public IReadOnlyList<string> All(string name) =>
        _values.TryGetValue(name, out var list) ? list : [];
}
