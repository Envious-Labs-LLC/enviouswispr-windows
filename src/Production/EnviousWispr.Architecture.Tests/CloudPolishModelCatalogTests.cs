using System.Net;
using System.Text;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;

namespace EnviousWispr.Architecture.Tests;

public sealed class CloudPolishModelCatalogTests
{
    [Fact]
    public async Task MissingCredentialDoesNotContactProvider()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException());
        using var catalog = new CloudPolishModelCatalog(
            new FakeKeyStore(value: null),
            handler);

        var result = await catalog.DiscoverAsync(PolishProvider.OpenAI);

        Assert.Equal(CloudModelCatalogStatus.MissingCredential, result.Status);
        Assert.Empty(result.ModelIds);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpenAiListsOnlyCompatibleChatModelsWithoutGenerationRequest()
    {
        var handler = new ScriptedHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.openai.com", request.RequestUri?.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("unit-test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse(
                """{"data":[{"id":"gpt-4o-mini"},{"id":"o3-mini"},{"id":"gpt-audio"},{"id":"gpt-5-codex"},{"id":"text-embedding-3-small"},{"id":"gpt-4o-mini"}]}""");
        });
        using var catalog = new CloudPolishModelCatalog(
            new FakeKeyStore("unit-test-key"),
            handler);

        var result = await catalog.DiscoverAsync(PolishProvider.OpenAI);

        Assert.Equal(CloudModelCatalogStatus.Ready, result.Status);
        Assert.Equal(["gpt-4o-mini", "o3-mini"], result.ModelIds);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnthropicFollowsCursorPaginationAndRejectsDuplicateAliases()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                """{"data":[{"id":"claude-haiku-4-5"},{"id":"claude-sonnet-latest"}],"has_more":true,"last_id":"cursor-1"}"""),
            JsonResponse(
                """{"data":[{"id":"claude-opus-4-1"},{"id":"claude-haiku-4-5"}],"has_more":false}"""),
        ]);
        var handler = new ScriptedHandler(request =>
        {
            Assert.Equal("api.anthropic.com", request.RequestUri?.Host);
            Assert.Equal("unit-test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal(
                AnthropicPolishProvider.ApiVersion,
                Assert.Single(request.Headers.GetValues("anthropic-version")));
            return responses.Dequeue();
        });
        using var catalog = new CloudPolishModelCatalog(
            new FakeKeyStore("unit-test-key"),
            handler);

        var result = await catalog.DiscoverAsync(PolishProvider.Anthropic);

        Assert.Equal(CloudModelCatalogStatus.Ready, result.Status);
        Assert.Equal(["claude-haiku-4-5", "claude-opus-4-1"], result.ModelIds);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("after_id=cursor-1", handler.Requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiRequiresGenerateContentAndRemovesModelsPrefix()
    {
        var handler = new ScriptedHandler(request =>
        {
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri?.Host);
            Assert.Equal("unit-test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            return JsonResponse(
                """{"models":[{"name":"models/gemini-3.7-flash","supportedGenerationMethods":["generateContent"]},{"name":"models/gemini-embedding-001","supportedGenerationMethods":["embedContent"]},{"name":"models/gemini-image-preview","supportedGenerationMethods":["generateContent"]}]}""");
        });
        using var catalog = new CloudPolishModelCatalog(
            new FakeKeyStore("unit-test-key"),
            handler);

        var result = await catalog.DiscoverAsync(PolishProvider.Gemini);

        Assert.Equal(CloudModelCatalogStatus.Ready, result.Status);
        Assert.Equal(["gemini-3.7-flash"], result.ModelIds);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedCredentialHasSpecificNonThrowingStatus(HttpStatusCode status)
    {
        var handler = new ScriptedHandler(_ => JsonResponse("{}", status));
        using var catalog = new CloudPolishModelCatalog(
            new FakeKeyStore("rejected-key"),
            handler);

        var result = await catalog.DiscoverAsync(PolishProvider.OpenAI);

        Assert.Equal(CloudModelCatalogStatus.KeyRejected, result.Status);
        Assert.Empty(result.ModelIds);
    }

    private static HttpResponseMessage JsonResponse(
        string body,
        HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeKeyStore(string? value) : IApiKeyStore
    {
        public ApiKeyReadResult Read(PolishProvider provider) => value is null
            ? ApiKeyReadResult.Missing
            : ApiKeyReadResult.Found(value);

        public void Store(PolishProvider provider, string newValue) =>
            throw new NotSupportedException();

        public void Delete(PolishProvider provider) => throw new NotSupportedException();
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException());
            return Task.FromResult(send(request));
        }
    }
}
