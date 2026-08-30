using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.LLM;

namespace EnviousWispr.Architecture.Tests;

public sealed class OllamaPolishProviderTests
{
    private static readonly ProcessedText Input = new(
        DictationSessionId.Create(),
        "so um move the meeting to thursday no wait friday");

    [Fact]
    public void PromptMatchesValidatedMacOSLocalL3Source()
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(OllamaLocalPrompt.SystemPrompt))).ToLowerInvariant();

        Assert.Equal(1_813, OllamaLocalPrompt.SystemPrompt.Length);
        Assert.Equal("8bbf91442614902f0d9dfee0df8ee39141cdeefcddef7422358b26855fb1c66e", hash);
        Assert.DoesNotContain('\r', OllamaLocalPrompt.SystemPrompt);
        Assert.DoesNotContain("<transcript>", OllamaLocalPrompt.BuildUserMessage(Input.Text));
    }

    [Theory]
    [InlineData(null, "http://localhost:11434/")]
    [InlineData("http://127.0.0.1:22000", "http://127.0.0.1:22000/")]
    [InlineData("https://[::1]:11434/", "https://[::1]:11434/")]
    public void EndpointPolicyAcceptsAndNormalizesOnlyLoopback(
        string? configured,
        string expected)
    {
        Assert.True(OllamaEndpointPolicy.TryNormalize(configured, out var endpoint));
        Assert.Equal(expected, endpoint?.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://example.com:11434")]
    [InlineData("http://192.168.1.50:11434")]
    [InlineData("file:///C:/ollama")]
    [InlineData("http://user:secret@localhost:11434")]
    [InlineData("http://localhost:11434/private")]
    [InlineData("http://localhost:11434?token=secret")]
    public void EndpointPolicyRejectsAnythingThatCouldLeaveThisPc(string configured)
    {
        Assert.False(OllamaEndpointPolicy.TryNormalize(configured, out var endpoint));
        Assert.Null(endpoint);
    }

    [Fact]
    public async Task DiscoveryListsOnlyLocalModelsAndPreservesCapabilityTriState()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(Tags(
            """{"name":"qwen3:4b","capabilities":["completion","thinking"]}""",
            """{"name":"llama3.2:latest","capabilities":["completion"]}""",
            """{"name":"legacy:latest"}""",
            """{"name":"bge-m3:latest","capabilities":["embedding"]}""",
            """{"name":"gpt-oss:20b-cloud","remote_host":"https://ollama.com","capabilities":["completion","thinking"]}""")));
        await using var client = new OllamaApiClient(messageHandler: handler);

        var discovery = await client.DiscoverAsync();

        Assert.Equal(OllamaHealth.Ready, discovery.Health);
        Assert.Equal(["legacy:latest", "llama3.2:latest", "qwen3:4b"],
            discovery.LocalModels.Select(model => model.Id));
        Assert.Null(discovery.LocalModels[0].SupportsThinking);
        Assert.False(discovery.LocalModels[1].SupportsThinking);
        Assert.True(discovery.LocalModels[2].SupportsThinking);
        Assert.Equal(["gpt-oss:20b-cloud"], discovery.RemoteModelIds);
        Assert.All(handler.Exchanges, exchange => Assert.True(exchange.Uri.IsLoopback));
    }

    [Theory]
    [InlineData("qwen3:4b", true, 2048, true)]
    [InlineData("llama3.2:latest", false, 256, false)]
    [InlineData("legacy:latest", null, 2048, false)]
    public async Task MultipleLocalModelsUseCapabilityDrivenThinkingAndBudget(
        string modelId,
        bool? thinks,
        int expectedFloor,
        bool expectThink)
    {
        var capabilities = thinks switch
        {
            true => "\"capabilities\":[\"completion\",\"thinking\"]",
            false => "\"capabilities\":[\"completion\"]",
            null => string.Empty,
        };
        var comma = capabilities.Length == 0 ? string.Empty : ",";
        var tags = Tags($"{{\"name\":\"{modelId}\"{comma}{capabilities}}}");
        var handler = new ScriptedHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(tags)
            : JsonResponse(Chat("Move the meeting to Friday.")));
        await using var provider = new OllamaPolishProvider(
            new OllamaPolishOptions(null, modelId),
            handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input, "en"));

        Assert.Equal(PolishAttemptStatus.Polished, result.Status);
        Assert.Equal("Move the meeting to Friday.", result.Output.Text);
        var chat = Assert.Single(
            handler.Exchanges,
            exchange => exchange.Uri.AbsolutePath == "/api/chat");
        using var document = JsonDocument.Parse(chat.Body);
        var root = document.RootElement;
        Assert.Equal(modelId, root.GetProperty("model").GetString());
        Assert.Equal(expectedFloor, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(expectThink, root.TryGetProperty("think", out var think));
        if (expectThink)
        {
            Assert.Equal("low", think.GetString());
        }
        Assert.Equal("60m", root.GetProperty("keep_alive").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var requestJson = root.GetRawText();
        Assert.Contains(Input.Text, requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("audio", requestJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteModelIsRefusedBeforeChatRequest()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(Tags(
            """{"name":"gpt-oss:20b-cloud","remote_host":"https://ollama.com"}""")));
        await using var provider = new OllamaPolishProvider(
            new OllamaPolishOptions(null, "gpt-oss:20b-cloud"),
            handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, result.Output);
        Assert.Equal(PolishAttemptStatus.Unavailable, result.Status);
        Assert.Equal(AppErrorCode.PolishRemoteModelDisallowed, result.Error?.Code);
        Assert.Single(handler.Exchanges);
        Assert.Equal("/api/tags", handler.Exchanges[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task ServiceStoppingAfterReadinessFallsDownDeterministically()
    {
        var handler = new ScriptedHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(Tags("""{"name":"llama3.2:latest","capabilities":["completion"]}"""))
            : throw new HttpRequestException("controlled daemon stop"));
        var delays = new List<TimeSpan>();
        await using var provider = new OllamaPolishProvider(
            new OllamaPolishOptions(null, "llama3.2"),
            handler,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, result.Output);
        Assert.Equal(PolishAttemptStatus.Unavailable, result.Status);
        Assert.Equal(AppErrorCode.PolishProviderUnavailable, result.Error?.Code);
        Assert.Equal(3, handler.Exchanges.Count(exchange => exchange.Uri.AbsolutePath == "/api/chat"));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], delays);
    }

    [Theory]
    [InlineData("length", AppErrorCode.PolishOutputTruncated)]
    [InlineData("stop", AppErrorCode.PolishFailed)]
    public async Task UnsafeOrIncompleteOutputPreservesInput(
        string doneReason,
        AppErrorCode expectedError)
    {
        var content = doneReason == "stop" ? "```powershell\nRemove-Item file\n```" : "partial";
        var handler = new ScriptedHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(Tags("""{"name":"llama3.2","capabilities":["completion"]}"""))
            : JsonResponse(Chat(content, doneReason)));
        await using var provider = new OllamaPolishProvider(
            new OllamaPolishOptions(null, "llama3.2"),
            handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, result.Output);
        Assert.Equal(expectedError, result.Error?.Code);
    }

    [Fact]
    public async Task ChatTimeoutPreservesInputAndCallerCancellationIsNotSwallowed()
    {
        var handler = new ScriptedHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(Tags("""{"name":"llama3.2"}"""));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        await using var timedProvider = new OllamaPolishProvider(
            new OllamaPolishOptions(null, "llama3.2", TimeSpan.FromMilliseconds(25)),
            handler);

        var timed = await timedProvider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, timed.Output);
        Assert.Equal(PolishAttemptStatus.TimedOut, timed.Status);
        Assert.Equal(AppErrorCode.PolishTimedOut, timed.Error?.Code);

        await using var cancelledProvider = new OllamaPolishProvider(
            new OllamaPolishOptions(null, "llama3.2", TimeSpan.FromMinutes(1)),
            handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelledProvider.TryPolishAsync(new PolishRequest(Input), cancellation.Token));
    }

    [Fact]
    public async Task InvalidManualEndpointMakesNoNetworkRequest()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException());
        await using var provider = new OllamaPolishProvider(
            new OllamaPolishOptions("http://example.com:11434", "llama3.2"),
            handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, result.Output);
        Assert.Equal(AppErrorCode.PolishEndpointInvalid, result.Error?.Code);
        Assert.Empty(handler.Exchanges);
    }

    private static string Tags(params string[] models) =>
        $"{{\"models\":[{string.Join(',', models)}]}}";

    private static string Chat(string content, string doneReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            message = new { content },
            done_reason = doneReason,
        });

    private static HttpResponseMessage JsonResponse(
        string body,
        HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request)))
        {
        }

        public ScriptedHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        public List<CapturedExchange> Exchanges { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Exchanges.Add(new CapturedExchange(
                request.RequestUri ?? throw new InvalidOperationException(),
                body));
            return await _send(request, cancellationToken);
        }
    }

    private sealed record CapturedExchange(Uri Uri, string Body);
}
