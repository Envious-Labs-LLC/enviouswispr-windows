using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnviousWispr.ModelDelivery;

var storeDirectory = Path.Combine(
    Path.GetTempPath(),
    $"EnviousWispr-model-delivery-uat-{Guid.NewGuid():N}");
Directory.CreateDirectory(storeDirectory);

try
{
    var versionOneBytes = Enumerable.Range(0, 128 * 1024)
        .Select(value => (byte)(value % 251))
        .ToArray();
    var versionTwoBytes = Enumerable.Range(0, 96 * 1024)
        .Select(value => (byte)((value + 17) % 251))
        .ToArray();
    using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var verifier = new ModelManifestVerifier(
        new Dictionary<string, string>
        {
            ["uat-2026"] = ModelManifestSigning.ExportPublicKeyPem(signingKey),
        },
        allowLoopbackHttp: true);

    await using var server = new LoopbackModelServer(versionOneBytes, versionTwoBytes);
    var v1Envelope = Envelope(signingKey, server.BaseUri, "1.0.0", "model-v1.bin", versionOneBytes);
    var v2Envelope = Envelope(signingKey, server.BaseUri, "2.0.0", "model-v2.bin", versionTwoBytes);
    server.SetManifests(v1Envelope, v2Envelope);
    using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var manifestClient = new ModelManifestClient(httpClient, verifier);
    var store = new ModelStore(
        storeDirectory,
        httpClient,
        verifier,
        new Version(1, 0, 0),
        options: new ModelDeliveryOptions(
            DiskReserveBytes: 0,
            MaximumAttemptsPerSource: 1,
            RetryDelay: _ => TimeSpan.Zero));

    var remoteV1 = await manifestClient.FetchAndVerifyAsync(new Uri(server.BaseUri, "manifest-v1.json"));
    Require(remoteV1.Succeeded, "The loopback signed manifest was not trusted.");
    var interrupted = await store.InstallAsync(remoteV1.Manifest!);
    Require(!interrupted.Succeeded, "The deliberate network interruption was not observed.");

    var resumed = await store.InstallAsync(remoteV1.Manifest!);
    Require(resumed.Succeeded, "The interrupted model did not resume and install.");
    Require(server.RangeRequests == 1, "The second request did not use HTTP Range.");
    Require(server.ValidatorRequests == 1, "The resumed request did not carry If-Range.");

    var remoteV2 = await manifestClient.FetchAndVerifyAsync(new Uri(server.BaseUri, "manifest-v2.json"));
    Require(remoteV2.Succeeded, "The upgrade manifest was not trusted.");
    Require((await store.InstallAsync(remoteV2.Manifest!)).Succeeded, "The model upgrade failed.");
    Require(
        (await store.OpenActiveOfflineAsync("uat-model")).Installed?.Version == "2.0.0",
        "The upgraded version was not pinned as active.");
    Require((await store.ActivateAsync("uat-model", "1.0.0")).Succeeded, "Downgrade failed.");
    Require(
        (await store.OpenActiveOfflineAsync("uat-model")).Installed?.Version == "1.0.0",
        "Offline reuse did not return the downgraded pinned version.");
    Require((await store.ActivateAsync("uat-model", "2.0.0")).Succeeded, "Reactivation failed.");
    Require((await store.CleanupAsync("uat-model", inactiveVersionsToKeep: 0)).Succeeded, "Cleanup failed.");
    Require(
        !Directory.Exists(Path.Combine(storeDirectory, "uat-model", "versions", "1.0.0")),
        "Cleanup retained an obsolete model version.");

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "passed",
        signedManifestsVerified = 2,
        interruptedTransfers = 1,
        resumedRangeRequests = server.RangeRequests,
        resumedValidatorRequests = server.ValidatorRequests,
        upgrades = 1,
        downgrades = 1,
        offlineReuses = 2,
        obsoleteVersionsRemoved = 1,
        userContentProcessed = 0,
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    if (Directory.Exists(storeDirectory))
    {
        Directory.Delete(storeDirectory, recursive: true);
    }
}

static byte[] Envelope(
    ECDsa key,
    Uri baseUri,
    string version,
    string fileName,
    byte[] bytes)
{
    var payload = new ModelManifestPayload(
        ModelManifestVerifier.CurrentManifestSchemaVersion,
        "uat-model",
        version,
        "1.0.0",
        new ModelLicenseNotice(
            "Synthetic UAT model license",
            new Uri(baseUri, "license"),
            "Synthetic bytes generated at runtime; no model weights or user content are used."),
        [
            new ModelArtifact(
                "model.bin",
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                [new Uri(baseUri, fileName)]),
        ]);
    return ModelManifestSigning.CreateEnvelope(payload, "uat-2026", key);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class LoopbackModelServer : IAsyncDisposable
{
    private readonly byte[] _versionOneBytes;
    private readonly byte[] _versionTwoBytes;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptLoop;
    private byte[] _manifestOne = [];
    private byte[] _manifestTwo = [];
    private int _versionOneRequests;
    private int _rangeRequests;
    private int _validatorRequests;

    public LoopbackModelServer(byte[] versionOneBytes, byte[] versionTwoBytes)
    {
        _versionOneBytes = versionOneBytes;
        _versionTwoBytes = versionTwoBytes;
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri BaseUri { get; }
    public int RangeRequests => Volatile.Read(ref _rangeRequests);
    public int ValidatorRequests => Volatile.Read(ref _validatorRequests);

    public void SetManifests(byte[] manifestOne, byte[] manifestTwo)
    {
        _manifestOne = manifestOne;
        _manifestTwo = manifestTwo;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (SocketException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _cancellation.Token).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var (path, headers) = request.Value;
            switch (path)
            {
                case "/manifest-v1.json":
                    await WriteResponseAsync(stream, "200 OK", _manifestOne, null, _cancellation.Token)
                        .ConfigureAwait(false);
                    break;
                case "/manifest-v2.json":
                    await WriteResponseAsync(stream, "200 OK", _manifestTwo, null, _cancellation.Token)
                        .ConfigureAwait(false);
                    break;
                case "/model-v1.bin":
                    await WriteVersionOneAsync(stream, headers, _cancellation.Token).ConfigureAwait(false);
                    break;
                case "/model-v2.bin":
                    await WriteResponseAsync(
                        stream,
                        "200 OK",
                        _versionTwoBytes,
                        "ETag: \"uat-v2\"\r\n",
                        _cancellation.Token).ConfigureAwait(false);
                    break;
                case "/license":
                    await WriteResponseAsync(stream, "200 OK", Encoding.UTF8.GetBytes("synthetic"), null, _cancellation.Token)
                        .ConfigureAwait(false);
                    break;
                default:
                    await WriteResponseAsync(stream, "404 Not Found", [], null, _cancellation.Token)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task WriteVersionOneAsync(
        NetworkStream stream,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var requestNumber = Interlocked.Increment(ref _versionOneRequests);
        if (requestNumber == 1)
        {
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {_versionOneBytes.Length}\r\n" +
                "ETag: \"uat-v1\"\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(_versionOneBytes.AsMemory(0, 16 * 1024), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var offset = 0;
        if (headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            offset = int.Parse(range[6..^1], System.Globalization.CultureInfo.InvariantCulture);
            Interlocked.Increment(ref _rangeRequests);
        }

        if (headers.TryGetValue("If-Range", out var validator) && validator == "\"uat-v1\"")
        {
            Interlocked.Increment(ref _validatorRequests);
        }

        var body = _versionOneBytes[offset..];
        await WriteResponseAsync(
            stream,
            offset > 0 ? "206 Partial Content" : "200 OK",
            body,
            $"ETag: \"uat-v1\"\r\nContent-Range: bytes {offset}-{_versionOneBytes.Length - 1}/{_versionOneBytes.Length}\r\n",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Path, IReadOnlyDictionary<string, string> Headers)?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var lastFour = new Queue<byte>(4);
        var single = new byte[1];
        while (buffer.Length < 32 * 1024)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            buffer.WriteByte(single[0]);
            lastFour.Enqueue(single[0]);
            if (lastFour.Count > 4)
            {
                lastFour.Dequeue();
            }

            if (lastFour.Count == 4 && lastFour.SequenceEqual(new byte[] { 13, 10, 13, 10 }))
            {
                break;
            }
        }

        var lines = Encoding.ASCII.GetString(buffer.ToArray())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return (requestParts[1], headers);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string status,
        byte[] body,
        string? additionalHeaders,
        CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\n" +
            additionalHeaders +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}
