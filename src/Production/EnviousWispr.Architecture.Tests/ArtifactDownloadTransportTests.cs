using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnviousWispr.ModelDelivery;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// One transfer attempt against a scripted source and a temporary directory: a fresh fetch, a
/// range resume with its validator, the resume records it refuses to trust, a source that answers
/// transiently or permanently, a read that outlasts its timeout, a caller that cancels, and a
/// response that keeps going past the size the manifest promised.
/// </summary>
public sealed class ArtifactDownloadTransportTests : IDisposable
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
    private readonly string _directory = Directory.CreateTempSubdirectory("EnviousWispr.Transport.").FullName;
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<ModelDeliveryEvent> _events = [];

    [Fact]
    public async Task AFreshFetchWritesTheWholeFileAndRecordsTheValidator()
    {
        var transport = Transport(_ => Ok(Payload, etag: "\"v1\""));

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.Complete, outcome.Result);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Partial));
        Assert.Null(_requests.Single().Headers.Range);
        var resume = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllBytesAsync(Resume));
        Assert.Equal(Source.AbsoluteUri, resume.GetProperty("source").GetString());
        Assert.Equal("\"v1\"", resume.GetProperty("eTag").GetString());
        Assert.Equal(ModelDeliveryEventCode.DownloadStarted, _events[0].Code);
        Assert.Equal(Payload.Length, _events[^1].CompletedBytes);
    }

    [Fact]
    public async Task APartialFileWithAValidatorIsResumedWithARangeAndIfRange()
    {
        await File.WriteAllBytesAsync(Partial, Payload[..10]);
        await WriteResumeAsync(Source, etag: "\"v1\"");
        var transport = Transport(request =>
        {
            Assert.Equal(10, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\"v1\"", request.Headers.GetValues("If-Range").Single());
            return PartialContent(Payload[10..], from: 10, total: Payload.Length, etag: "\"v1\"");
        });

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 100, 200, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.Complete, outcome.Result);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Partial));
        Assert.Equal(ModelDeliveryEventCode.DownloadResumed, _events[0].Code);
        Assert.Equal(110, _events[0].CompletedBytes);
        Assert.Equal(200, _events[0].TotalBytes);
    }

    [Fact]
    public async Task AResumeWithOnlyALastModifiedDateUsesItAsTheCondition()
    {
        var modified = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await File.WriteAllBytesAsync(Partial, Payload[..10]);
        await WriteResumeAsync(Source, lastModified: modified);
        var transport = Transport(request =>
        {
            Assert.Equal(modified, request.Headers.IfRange?.Date);
            return PartialContent(Payload[10..], from: 10, total: Payload.Length);
        });

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.Complete, outcome.Result);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-source")]
    [InlineData("no-validator")]
    [InlineData("corrupt")]
    public async Task APartialFileWhoseRecordCannotBeTrustedIsThrownAwayAndFetchedWhole(string record)
    {
        await File.WriteAllBytesAsync(Partial, Payload[..10]);
        switch (record)
        {
            case "other-source":
                await WriteResumeAsync(new Uri("https://mirror.example/x.bin"), etag: "\"v1\"");
                break;
            case "no-validator":
                await WriteResumeAsync(Source);
                break;
            case "corrupt":
                await File.WriteAllTextAsync(Resume, "{ not json");
                break;
            default:
                break;
        }

        var transport = Transport(_ => Ok(Payload));

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.Complete, outcome.Result);
        Assert.Null(_requests.Single().Headers.Range);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Partial));
        Assert.Equal(ModelDeliveryEventCode.DownloadStarted, _events[0].Code);
    }

    [Fact]
    public async Task ASourceThatIgnoresTheRangeAndSendsTheWholeFileStartsOver()
    {
        await File.WriteAllBytesAsync(Partial, Payload[..10]);
        await WriteResumeAsync(Source, etag: "\"v1\"");
        var transport = Transport(_ => Ok(Payload));

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.Complete, outcome.Result);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(Partial));
    }

    [Fact]
    public async Task ARangeAnsweredFromTheWrongOffsetIsAPermanentFailure()
    {
        await File.WriteAllBytesAsync(Partial, Payload[..10]);
        await WriteResumeAsync(Source, etag: "\"v1\"");
        var transport = Transport(_ => PartialContent(Payload[5..], from: 5, total: Payload.Length));

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.PermanentFailure, outcome.Result);
        Assert.Equal(10, new FileInfo(Partial).Length);
    }

    [Fact]
    public async Task ARangeTheSourceCannotSatisfyIsCompleteOnlyWhenTheFileAlreadyIs()
    {
        await File.WriteAllBytesAsync(Partial, Payload);
        await WriteResumeAsync(Source, etag: "\"v1\"");
        var transport = Transport(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        Assert.Equal(DownloadAttemptResult.Complete, (await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, 1, CancellationToken.None)).Result);

        await File.WriteAllBytesAsync(Partial, Payload[..10]);
        Assert.Equal(DownloadAttemptResult.PermanentFailure, (await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, 1, CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)522)]
    public async Task ATransientStatusIsTransientAndCarriesABoundedRetryAfter(HttpStatusCode status)
    {
        var transport = Transport(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return response;
        });

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, 1, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.TransientFailure, outcome.Result);
        Assert.Equal(TimeSpan.FromSeconds(10), outcome.RetryAfter);
        Assert.False(File.Exists(Partial));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusalIsPermanent(HttpStatusCode status)
    {
        var transport = Transport(_ => new HttpResponseMessage(status));

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, 1, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.PermanentFailure, outcome.Result);
        Assert.Null(outcome.RetryAfter);
    }

    [Fact]
    public async Task AResponseThatKeepsGoingPastThePromisedSizeIsAPermanentFailureAtTheFirstByteOver()
    {
        var transport = Transport(_ => Ok([.. Payload, .. Payload]));

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.PermanentFailure, outcome.Result);
    }

    [Fact]
    public async Task AShortResponseIsTransientAndKeepsWhatArrivedForAResume()
    {
        var transport = Transport(_ => Ok(Payload[..20], etag: "\"v1\""));

        var outcome = await transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, CancellationToken.None);

        Assert.Equal(DownloadAttemptResult.TransientFailure, outcome.Result);
        Assert.Equal(20, new FileInfo(Partial).Length);
        Assert.True(File.Exists(Resume));
    }

    [Fact]
    public async Task AReadThatOutlastsTheTimeoutIsCancelledAsATimeoutNotAsTheCaller()
    {
        var stalled = new TaskCompletionSource();
        var transport = Transport(_ => Ok(new StallingStream(Payload[..10], stalled.Task)), requestTimeout: TimeSpan.FromMilliseconds(200));

        // THE STORE TELLS THE TWO APART BY ITS OWN TOKEN, not by the exception: a cancellation that
        // arrives while the caller's token is untouched is an inactivity timeout, and transient.
        using var caller = new CancellationTokenSource();
        var attempt = transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, caller.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);

        Assert.False(caller.Token.IsCancellationRequested);
        Assert.Equal(10, new FileInfo(Partial).Length);
        stalled.SetResult();
    }

    [Fact]
    public async Task TheCallersCancellationReachesTheReadAndIsTheirs()
    {
        using var cancellation = new CancellationTokenSource();
        var stalled = new TaskCompletionSource();
        var transport = Transport(_ => Ok(new StallingStream(Payload[..10], stalled.Task)), requestTimeout: TimeSpan.FromSeconds(30));

        var attempt = transport.DownloadAttemptAsync(Source, Artifact(), Partial, Resume, 0, Payload.Length, cancellation.Token);
        Assert.False(attempt.IsCompleted, "the read is stalled; the attempt waits on it");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);

        Assert.True(cancellation.Token.IsCancellationRequested);
        stalled.SetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Uri Source => new("https://models.example/x.bin");

    private string Partial => Path.Combine(_directory, "x.bin.partial");

    private string Resume => Path.Combine(_directory, "x.bin.resume.json");

    private static ModelArtifact Artifact() => new("x.bin", Payload.Length, "unused", [Source]);

    private ArtifactDownloadTransport Transport(Func<HttpRequestMessage, HttpResponseMessage> route, TimeSpan? requestTimeout = null)
    {
        var handler = new RoutingHandler(request =>
        {
            _requests.Add(request);
            return route(request);
        });
        return new ArtifactDownloadTransport(
            new HttpClient(handler, disposeHandler: false),
            requestTimeout ?? TimeSpan.FromSeconds(30),
            new RecordingObserver(_events));
    }

    private async Task WriteResumeAsync(Uri source, string? etag = null, DateTimeOffset? lastModified = null)
    {
        var record = new { source = source.AbsoluteUri, eTag = etag, lastModified };
        await File.WriteAllBytesAsync(Resume, JsonSerializer.SerializeToUtf8Bytes(record));
    }

    private static HttpResponseMessage Ok(byte[] bytes, string? etag = null) => Ok(new MemoryStream(bytes), etag);

    private static HttpResponseMessage Ok(Stream content, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static HttpResponseMessage PartialContent(byte[] bytes, long from, long total, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, total - 1, total);
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
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

    /// <summary>Delivers its bytes, then stalls until released, honouring the read's token.</summary>
    private sealed class StallingStream(byte[] bytes, Task release) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _offset);
                bytes.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await release.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
