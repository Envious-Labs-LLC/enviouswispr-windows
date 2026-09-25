using System.Net;
using System.Net.Sockets;
using System.Text;
using EnviousWispr.Core.Polish;
using EnviousWispr.LLM;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Downloading and removing local models through Ollama, against scripted streams. Ref: #213.</summary>
public sealed class OllamaModelManagementTests
{
    private const string Endpoint = "http://127.0.0.1:11434";

    [Fact]
    public async Task ADownloadSucceedsOnlyWhenOllamaSaysSuccessAndReportsEachPhase()
    {
        var handler = new StreamHandler(Chunks(
            """{"status":"pulling manifest"}""",
            """{"status":"pulling aaa","digest":"sha256:aaa","total":1000,"completed":500}""",
            """{"status":"pulling aaa","digest":"sha256:aaa","total":1000,"completed":1000}""",
            """{"status":"pulling bbb","digest":"sha256:bbb","total":20,"completed":20}""",
            """{"status":"verifying sha256 digest"}""",
            """{"status":"writing manifest"}""",
            """{"status":"success"}"""));
        await using var client = new OllamaApiClient(Endpoint, handler);
        var seen = new List<OllamaPullUpdate>();

        var outcome = await client.PullAsync("qwen3:0.6b", new Collect(seen), CancellationToken.None);

        Assert.Equal(OllamaPullOutcome.Succeeded, outcome);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri($"{Endpoint}/api/pull"), handler.Uri);
        Assert.Contains("\"model\":\"qwen3:0.6b\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", handler.Body, StringComparison.Ordinal);
        Assert.Equal(
            [OllamaPullPhase.Starting, OllamaPullPhase.Starting, OllamaPullPhase.Downloading, OllamaPullPhase.Downloading,
             OllamaPullPhase.Downloading, OllamaPullPhase.Verifying, OllamaPullPhase.Finishing],
            seen.Select(update => update.Phase));
        Assert.Equal("sha256:bbb", seen[4].Digest);
        Assert.Equal(1000, seen[3].Total);
    }

    /// <summary>A layer at 100% is not the model: a stream that stops before "success" was cut.</summary>
    [Fact]
    public async Task AStreamThatEndsBeforeSuccessIsInterruptedWhateverTheBytesSaid()
    {
        var handler = new StreamHandler(Chunks(
            """{"status":"pulling aaa","digest":"sha256:aaa","total":1000,"completed":1000}"""));
        await using var client = new OllamaApiClient(Endpoint, handler);

        Assert.Equal(OllamaPullOutcome.Interrupted, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    /// <summary>The network hands over bytes where it likes; a line split across reads is still one line.</summary>
    [Fact]
    public async Task ALineSplitAcrossReadsIsReadWhole()
    {
        var text = "{\"status\":\"pulling manifest\"}\r\n{\"status\":\"succ" + "ess\"}\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        var pieces = bytes.Chunk(3).Select(piece => (piece, TimeSpan.Zero)).ToArray();
        await using var client = new OllamaApiClient(Endpoint, new StreamHandler(pieces));

        Assert.Equal(OllamaPullOutcome.Succeeded, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("pull model manifest: file does not exist", OllamaPullOutcome.NotFound)]
    [InlineData("write /models/blobs/x: no space left on device", OllamaPullOutcome.DiskFull)]
    [InlineData("Get \"https://registry.ollama.ai/v2/\": dial tcp: lookup registry.ollama.ai: no such host", OllamaPullOutcome.NetworkFailed)]
    [InlineData("something Ollama has never said before", OllamaPullOutcome.Refused)]
    public async Task AnErrorLineEndsTheDownloadAsWhatItMeans(string message, OllamaPullOutcome expected)
    {
        var line = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}";
        await using var client = new OllamaApiClient(Endpoint, new StreamHandler(Chunks("""{"status":"pulling manifest"}""", line)));

        Assert.Equal(expected, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    /// <summary>Ollama hashes a big layer without a word. A silence is reported as quiet and the download goes on.</summary>
    [Fact]
    public async Task ASilenceIsQuietNotAFailure()
    {
        var handler = new StreamHandler(
        [
            (Line("""{"status":"verifying sha256 digest"}"""), TimeSpan.Zero),
            (Line("""{"status":"success"}"""), TimeSpan.FromMilliseconds(400)),
        ]);
        await using var client = new OllamaApiClient(Endpoint, handler);
        var seen = new List<OllamaPullUpdate>();

        var outcome = await client.PullAsync("qwen3:0.6b", new Collect(seen), CancellationToken.None, quietAfter: TimeSpan.FromMilliseconds(50));

        Assert.Equal(OllamaPullOutcome.Succeeded, outcome);
        Assert.Contains(seen, update => update is { Quiet: true, Phase: OllamaPullPhase.Verifying });
    }

    [Fact]
    public async Task AStopEndsTheDownloadAsCancelled()
    {
        using var stop = new CancellationTokenSource();
        var handler = new StreamHandler(
        [
            (Line("""{"status":"pulling aaa","digest":"sha256:aaa","total":10,"completed":1}"""), TimeSpan.Zero),
            (Line("""{"status":"success"}"""), TimeSpan.FromSeconds(30)),
        ]);
        await using var client = new OllamaApiClient(Endpoint, handler);
        var pull = client.PullAsync("qwen3:0.6b", new Collect([], _ => stop.Cancel()), stop.Token);

        Assert.Equal(OllamaPullOutcome.Cancelled, await pull.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AGarbledLineIsRefusedNotThrown()
    {
        await using var client = new OllamaApiClient(Endpoint, new StreamHandler(Chunks("<html>proxy error</html>")));

        Assert.Equal(OllamaPullOutcome.Refused, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
    }

    [Fact]
    public async Task AnInvalidEndpointNeverSendsAnything()
    {
        var handler = new StreamHandler(Chunks("""{"status":"success"}"""));
        await using var client = new OllamaApiClient("http://example.com:11434", handler);

        Assert.Equal(OllamaPullOutcome.EndpointInvalid, await client.PullAsync("qwen3:0.6b", null, CancellationToken.None));
        Assert.Equal(OllamaDeleteOutcome.EndpointInvalid, await client.DeleteAsync("qwen3:0.6b", CancellationToken.None));
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, OllamaDeleteOutcome.Deleted)]
    [InlineData(HttpStatusCode.NotFound, OllamaDeleteOutcome.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, OllamaDeleteOutcome.Refused)]
    public async Task ARemovalSaysHowItEnded(HttpStatusCode status, OllamaDeleteOutcome expected)
    {
        var handler = new StreamHandler([], status);
        await using var client = new OllamaApiClient(Endpoint, handler);

        Assert.Equal(expected, await client.DeleteAsync("qwen3:0.6b", CancellationToken.None));
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal(new Uri($"{Endpoint}/api/delete"), handler.Uri);
        Assert.Contains("\"model\":\"qwen3:0.6b\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryKeepsWhatAModelOccupiesAndItsParameterLabel()
    {
        var handler = new StreamHandler(Chunks(
            """{"models":[{"name":"qwen2.5:3b","size":1929912432,"details":{"parameter_size":"3.1B"},"capabilities":["completion"]}]}"""));
        await using var client = new OllamaApiClient(Endpoint, handler);

        var model = Assert.Single((await client.DiscoverAsync()).LocalModels);

        Assert.Equal(1929912432, model.SizeBytes);
        Assert.Equal("3.1B", model.ParameterSize);
    }

    /// <summary>Only "nothing is listening" is grounds to offer starting Ollama; a slow server is not.</summary>
    [Fact]
    public async Task DiscoverySaysWhenNothingIsListening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var closed = new OllamaApiClient($"http://127.0.0.1:{port}", readinessTimeout: TimeSpan.FromSeconds(5));

        var refused = await closed.DiscoverAsync();

        Assert.Equal(OllamaHealth.ServerUnavailable, refused.Health);
        Assert.True(refused.ConnectionRefused);

        var slow = new StreamHandler([(Line("{}"), TimeSpan.FromSeconds(10))]);
        await using var waiting = new OllamaApiClient(Endpoint, slow, TimeSpan.FromMilliseconds(100));
        var timedOut = await waiting.DiscoverAsync();
        Assert.Equal(OllamaHealth.ServerUnavailable, timedOut.Health);
        Assert.False(timedOut.ConnectionRefused);
    }

    [Fact]
    public void TheCatalogueIsTheElevenMacModelsWithTheRecommendationAmongThem()
    {
        Assert.Equal(11, OllamaModelCatalog.Entries.Count);
        Assert.Equal(11, OllamaModelCatalog.Entries.Select(entry => OllamaModelCatalog.Canonical(entry.Id)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(OllamaModelVerdict.Recommended, OllamaModelCatalog.Offered(OllamaModelCatalog.RecommendedModelId)?.Verdict);
    }

    [Fact]
    public void ModelsAreTheSameUnderEitherSpellingAndOnlyThat()
    {
        Assert.True(OllamaModelCatalog.SameModel("llama3.2", "Llama3.2:latest"));
        Assert.False(OllamaModelCatalog.SameModel("llama3.2", "llama3.2:1b"));
        Assert.NotNull(OllamaModelCatalog.Offered("gemma2:latest"));
        Assert.Equal((OllamaModelVerdict.NotTested, string.Empty), OllamaModelCatalog.VerdictFor("my-own-finetune"));
        Assert.Equal(OllamaModelVerdict.Unreliable, OllamaModelCatalog.VerdictFor("deepseek-r1:1.5b").Verdict);
        Assert.Null(OllamaModelCatalog.Offered("deepseek-r1:1.5b"));
    }

    /// <summary>Bands are ranked; models inside a band keep their order, and untested sits above the measured failures.</summary>
    [Fact]
    public void OrderingRanksBandsAndNeverWithinOne()
    {
        string[] ids = ["tinyllama", "gemma2", "my-own", "qwen2.5:7b", "mistral", "gemma2:2b", "qwen3:0.6b"];

        var ordered = OllamaModelCatalog.OrderedByVerdict(ids, id => id).ToArray();

        Assert.Equal(["qwen2.5:7b", "qwen3:0.6b", "gemma2", "gemma2:2b", "my-own", "mistral", "tinyllama"], ordered);
    }

    private static byte[] Line(string json) => Encoding.UTF8.GetBytes(json + "\n");

    private static (byte[] Bytes, TimeSpan Delay)[] Chunks(params string[] lines) =>
        lines.Select(line => (Line(line), TimeSpan.Zero)).ToArray();

    private sealed class Collect(List<OllamaPullUpdate> into, Action<OllamaPullUpdate>? then = null) : IProgress<OllamaPullUpdate>
    {
        public void Report(OllamaPullUpdate value)
        {
            into.Add(value);
            then?.Invoke(value);
        }
    }

    /// <summary>Answers any request with a body that arrives piece by piece, each after its own pause.</summary>
    private sealed class StreamHandler((byte[] Bytes, TimeSpan Delay)[] pieces, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StreamContent(new PacedStream(pieces)) };
        }
    }

    private sealed class PacedStream((byte[] Bytes, TimeSpan Delay)[] pieces) : Stream
    {
        private int _piece;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_piece >= pieces.Length)
            {
                return 0;
            }

            var (bytes, delay) = pieces[_piece];
            if (_offset == 0 && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var count = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == bytes.Length)
            {
                _piece++;
                _offset = 0;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
