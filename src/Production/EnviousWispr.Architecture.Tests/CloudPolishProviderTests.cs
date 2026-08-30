using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;

namespace EnviousWispr.Architecture.Tests;

public sealed class CloudPolishProviderTests
{
    private static readonly ProcessedText Input = new(
        DictationSessionId.Create(),
        "so um move the meeting to thursday no wait friday");

    [Fact]
    public void PromptMatchesValidatedMacOSV7Source()
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(CloudPolishPrompt.SystemPrompt))).ToLowerInvariant();

        Assert.Equal("cloud-fixed-v7", CloudPolishPrompt.TemplateId);
        Assert.Equal(5_080, CloudPolishPrompt.SystemPrompt.Length);
        Assert.Equal("1382f15841b3e1118f10f0c4603dcb5269551da9ab8cbe7845266ea703860cef", hash);
        Assert.DoesNotContain('\r', CloudPolishPrompt.SystemPrompt);
        Assert.DoesNotContain("<transcript>", CloudPolishPrompt.BuildUserMessage(Input.Text));
    }

    [Theory]
    [InlineData(PolishProvider.OpenAI, "OpenAI")]
    [InlineData(PolishProvider.Anthropic, "Anthropic")]
    [InlineData(PolishProvider.Gemini, "Google Gemini")]
    public void ConsentNamesDestinationAndPrivacyBoundary(
        PolishProvider provider,
        string name)
    {
        var consent = CloudPolishConsent.For(provider);

        Assert.Contains(name, consent.Notice, StringComparison.Ordinal);
        Assert.Contains("transcribed text directly", consent.Notice, StringComparison.Ordinal);
        Assert.Contains("Audio never leaves this PC", consent.Notice, StringComparison.Ordinal);
        Assert.Contains("Envious Labs never receives", consent.Notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PolishProvider.OpenAI, "gpt-4o-mini", true)]
    [InlineData(PolishProvider.OpenAI, "claude-haiku-4-5", false)]
    [InlineData(PolishProvider.Anthropic, "claude-haiku-4-5", true)]
    [InlineData(PolishProvider.Anthropic, "gemini-3.7-flash", false)]
    [InlineData(PolishProvider.Gemini, "gemini-3.7-flash", true)]
    [InlineData(PolishProvider.Gemini, "eg-1", false)]
    public void ModelIdentityCannotLeakAcrossProviderSwitches(
        PolishProvider provider,
        string modelId,
        bool expected)
    {
        Assert.Equal(expected, CloudPolishOptions.ModelIdLooksLikeProvider(modelId, provider));
    }

    [Fact]
    public async Task OpenAiSendsOnlyTextDirectlyAndReturnsCleanedOutput()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(
            """{"choices":[{"finish_reason":"stop","message":{"content":"Move the meeting to Friday."}}]}"""));
        await using var provider = new OpenAiPolishProvider(
            new FakeApiKeyStore(PolishProvider.OpenAI, "unit-test-openai-key"),
            messageHandler: handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input, "en"));

        Assert.Equal(PolishAttemptStatus.Polished, result.Status);
        Assert.Equal("Move the meeting to Friday.", result.Output.Text);
        var exchange = Assert.Single(handler.Exchanges);
        Assert.Equal("api.openai.com", exchange.Uri.Host);
        Assert.Equal("Bearer", exchange.Headers["Authorization"]?.Split(' ')[0]);
        Assert.DoesNotContain("unit-test-openai-key", exchange.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(exchange.Body);
        Assert.False(document.RootElement.GetProperty("store").GetBoolean());
        AssertRequestContainsTextButNoAudio(document.RootElement, exchange.Uri);
    }

    [Fact]
    public async Task AnthropicUsesMessagesApiRequiredHeadersAndRejectsTruncation()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(
            """{"stop_reason":"max_tokens","content":[{"type":"text","text":"partial"}]}"""));
        await using var provider = new AnthropicPolishProvider(
            new FakeApiKeyStore(PolishProvider.Anthropic, "unit-test-anthropic-key"),
            messageHandler: handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, result.Output);
        Assert.Equal(AppErrorCode.PolishOutputTruncated, result.Error?.Code);
        var exchange = Assert.Single(handler.Exchanges);
        Assert.Equal("api.anthropic.com", exchange.Uri.Host);
        Assert.Equal(AnthropicPolishProvider.ApiVersion, exchange.Headers["anthropic-version"]);
        using var document = JsonDocument.Parse(exchange.Body);
        Assert.Equal(
            AnthropicPolishProvider.MaximumOutputTokens,
            document.RootElement.GetProperty("max_tokens").GetInt32());
        AssertRequestContainsTextButNoAudio(document.RootElement, exchange.Uri);
    }

    [Fact]
    public async Task GeminiUsesDirectApiKeyHeaderAndIgnoresThoughtParts()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(
            """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"thought":true,"text":"hidden"},{"text":"Move it to Friday."}]}}]}"""));
        await using var provider = new GeminiPolishProvider(
            new FakeApiKeyStore(PolishProvider.Gemini, "unit-test-gemini-key"),
            messageHandler: handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal("Move it to Friday.", result.Output.Text);
        var exchange = Assert.Single(handler.Exchanges);
        Assert.Equal("generativelanguage.googleapis.com", exchange.Uri.Host);
        Assert.Equal("unit-test-gemini-key", exchange.Headers["x-goog-api-key"]);
        using var document = JsonDocument.Parse(exchange.Body);
        Assert.False(document.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(
            "low",
            document.RootElement
                .GetProperty("generationConfig")
                .GetProperty("thinkingConfig")
                .GetProperty("thinkingLevel")
                .GetString());
        AssertRequestContainsTextButNoAudio(document.RootElement, exchange.Uri);
    }

    [Fact]
    public async Task OpenAiReasoningModelUsesLowEffortAndOmitsTemperature()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(
            """{"choices":[{"finish_reason":"stop","message":{"content":"Clean."}}]}"""));
        await using var provider = new OpenAiPolishProvider(
            new FakeApiKeyStore(PolishProvider.OpenAI, "secret"),
            modelId: "gpt-5.4-mini",
            messageHandler: handler);

        _ = await provider.TryPolishAsync(new PolishRequest(Input));

        using var document = JsonDocument.Parse(Assert.Single(handler.Exchanges).Body);
        Assert.Equal("low", document.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(document.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task MissingCredentialFailsDownWithoutNetworkRequest()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException());
        await using var provider = new OpenAiPolishProvider(
            new FakeApiKeyStore(PolishProvider.OpenAI, value: null),
            messageHandler: handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, result.Output);
        Assert.Equal(PolishAttemptStatus.Unavailable, result.Status);
        Assert.Equal(AppErrorCode.PolishCredentialMissing, result.Error?.Code);
        Assert.Empty(handler.Exchanges);
    }

    [Fact]
    public async Task TransientFailureRetriesThenSucceedsWithoutLeakingContentToDiagnostics()
    {
        var responses = new Queue<HttpResponseMessage>([
            JsonResponse("{}", HttpStatusCode.InternalServerError),
            JsonResponse(
                """{"choices":[{"finish_reason":"stop","message":{"content":"Move it to Friday."}}]}"""),
        ]);
        var handler = new ScriptedHandler(_ => responses.Dequeue());
        var delays = new List<TimeSpan>();
        await using var provider = new OpenAiPolishProvider(
            new FakeApiKeyStore(PolishProvider.OpenAI, "secret"),
            new CloudPolishOptions(PolishProvider.OpenAI, "gpt-4o-mini"),
            handler,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(PolishAttemptStatus.Polished, result.Status);
        Assert.Equal(2, handler.Exchanges.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task NetworkLossReturnsTheLastDeterministicTextAfterBoundedRetries()
    {
        var handler = new ScriptedHandler(_ =>
            throw new HttpRequestException("controlled offline fault"));
        var delays = new List<TimeSpan>();
        await using var provider = new OpenAiPolishProvider(
            new FakeApiKeyStore(PolishProvider.OpenAI, "secret"),
            new CloudPolishOptions(PolishProvider.OpenAI, "gpt-4o-mini"),
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
        Assert.Equal(3, handler.Exchanges.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], delays);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        await using var provider = new OpenAiPolishProvider(
            new FakeApiKeyStore(PolishProvider.OpenAI, "secret"),
            requestTimeout: TimeSpan.FromMinutes(1),
            messageHandler: handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TryPolishAsync(new PolishRequest(Input), cancellation.Token));
    }

    [Fact]
    public async Task RequestBudgetTimesOutToCompleteDeterministicInput()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        await using var provider = new OpenAiPolishProvider(
            new FakeApiKeyStore(PolishProvider.OpenAI, "secret"),
            requestTimeout: TimeSpan.FromMilliseconds(25),
            messageHandler: handler);

        var result = await provider.TryPolishAsync(new PolishRequest(Input));

        Assert.Equal(Input, result.Output);
        Assert.Equal(PolishAttemptStatus.TimedOut, result.Status);
        Assert.Equal(AppErrorCode.PolishTimedOut, result.Error?.Code);
    }

    private static void AssertRequestContainsTextButNoAudio(JsonElement root, Uri uri)
    {
        var body = root.GetRawText();
        Assert.Contains(Input.Text, body, StringComparison.Ordinal);
        Assert.DoesNotContain("audio", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enviouslabs", uri.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enviouswispr", uri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage JsonResponse(
        string body,
        HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeApiKeyStore(PolishProvider provider, string? value) : IApiKeyStore
    {
        public ApiKeyReadResult Read(PolishProvider requestedProvider)
        {
            Assert.Equal(provider, requestedProvider);
            return value is null ? ApiKeyReadResult.Missing : ApiKeyReadResult.Found(value);
        }

        public void Store(PolishProvider requestedProvider, string newValue) =>
            throw new NotSupportedException();

        public void Delete(PolishProvider requestedProvider) => throw new NotSupportedException();
    }

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
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> contentHeaders =
                request.Content?.Headers ??
                Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>();
            var headers = request.Headers
                .Concat(contentHeaders)
                .ToDictionary(
                    pair => pair.Key,
                    pair => string.Join(",", pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Exchanges.Add(new CapturedExchange(
                request.RequestUri ?? throw new InvalidOperationException(),
                headers,
                body));
            return await _send(request, cancellationToken);
        }
    }

    private sealed record CapturedExchange(
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
