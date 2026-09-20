using System.Net;
using System.Security.Cryptography;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The downloader on its own, with the staging directory inspected before the store's admission
/// would have tidied it: the shard path is tried first and cleans up after itself before the whole
/// file is asked for, and an assembly that does not hash is reported before the fallback succeeds.
/// </summary>
public sealed class ArtifactDownloaderTests : IDisposable
{
    private readonly string _staging = Directory.CreateTempSubdirectory("EnviousWispr.Downloader.").FullName;
    private readonly List<string> _requests = [];
    private readonly List<ModelDeliveryEvent> _events = [];

    [Fact]
    public async Task AShardThatFailsIsCleanedUpBeforeTheWholeFileIsAskedFor()
    {
        var bytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var downloader = Downloader(path =>
            path.EndsWith("/part0", StringComparison.Ordinal) ? Ok(bytes[..8])
            : path.EndsWith("/part1", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Ok(bytes));

        var result = await downloader.DownloadArtifactAsync(_staging, Sharded(bytes, bytes[..8], bytes[8..]), 0, bytes.Length, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["/part0", "/part1", "/model.bin"], _requests);
        Assert.Equal(["model.bin"], Directory.GetFiles(_staging, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_staging, "model.bin")));
    }

    [Fact]
    public async Task ShardsThatMatchButDoNotMakeTheWholeAreReportedAndDiscardedBeforeTheWholeFileDecides()
    {
        var bytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var wrongHalf = Enumerable.Repeat((byte)0xFF, 8).ToArray();
        var downloader = Downloader(path =>
            path.EndsWith("/part0", StringComparison.Ordinal) ? Ok(bytes[..8])
            : path.EndsWith("/part1", StringComparison.Ordinal) ? Ok(wrongHalf)
            : Ok(bytes));

        var result = await downloader.DownloadArtifactAsync(_staging, Sharded(bytes, bytes[..8], wrongHalf), 0, bytes.Length, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["/part0", "/part1", "/model.bin"], _requests);
        var mismatch = _events.FindIndex(e => e.Code == ModelDeliveryEventCode.SourceFailed && e.Failure == ModelDeliveryFailure.IntegrityMismatch);
        var verified = _events.FindLastIndex(e => e.Code == ModelDeliveryEventCode.ArtifactVerified);
        Assert.True(mismatch >= 0, "the assembly that did not hash was not reported");
        Assert.True(verified > mismatch, "the whole file was verified after the mismatch was reported");
        Assert.Equal(["model.bin"], Directory.GetFiles(_staging, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnArtifactAlreadyInStagingIsNotAskedForAgain()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(Path.Combine(_staging, "model.bin"), bytes);
        var downloader = Downloader(_ => throw new InvalidOperationException("nothing should be requested"));

        var result = await downloader.DownloadArtifactAsync(_staging, Whole(bytes), 0, bytes.Length, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(_requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_staging))
        {
            Directory.Delete(_staging, recursive: true);
        }
    }

    private static ModelArtifact Whole(byte[] bytes) =>
        new("model.bin", bytes.Length, Sha(bytes), [new Uri("https://whole.invalid/model.bin")]);

    private static ModelArtifact Sharded(byte[] bytes, byte[] part0, byte[] part1) => Whole(bytes) with
    {
        Parts =
        [
            new ModelArtifactPart(part0.Length, Sha(part0), [new Uri("https://shards.invalid/part0")]),
            new ModelArtifactPart(part1.Length, Sha(part1), [new Uri("https://shards.invalid/part1")]),
        ],
    };

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private ArtifactDownloader Downloader(Func<string, HttpResponseMessage> route)
    {
        var handler = new RoutingHandler(request =>
        {
            _requests.Add(request.RequestUri!.AbsolutePath);
            return route(request.RequestUri.AbsolutePath);
        });
        var observer = new RecordingObserver(_events);
        var options = new ModelDeliveryOptions(MaximumAttemptsPerSource: 1, RetryDelay: _ => TimeSpan.Zero);
        return new ArtifactDownloader(
            new ArtifactDownloadTransport(new HttpClient(handler, disposeHandler: false), TimeSpan.FromSeconds(30), observer),
            options,
            observer);
    }

    private sealed class RecordingObserver(List<ModelDeliveryEvent> events) : IModelDeliveryObserver
    {
        public void Observe(ModelDeliveryEvent modelDeliveryEvent) => events.Add(modelDeliveryEvent);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }
}
