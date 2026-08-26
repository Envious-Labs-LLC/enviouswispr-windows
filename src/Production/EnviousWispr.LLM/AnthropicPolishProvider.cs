using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public sealed class AnthropicPolishProvider : CloudPolishProviderBase
{
    internal static readonly Uri Endpoint = new("https://api.anthropic.com/v1/messages");
    internal const string ApiVersion = "2023-06-01";
    internal const int MaximumOutputTokens = 8_192;

    public AnthropicPolishProvider(
        IApiKeyStore apiKeyStore,
        string? modelId = null,
        TimeSpan? requestTimeout = null,
        HttpMessageHandler? messageHandler = null)
        : this(
            apiKeyStore,
            new CloudPolishOptions(
                PolishProvider.Anthropic,
                modelId ?? CloudPolishOptions.DefaultModel(PolishProvider.Anthropic),
                requestTimeout),
            messageHandler,
            delay: null)
    {
    }

    internal AnthropicPolishProvider(
        IApiKeyStore apiKeyStore,
        CloudPolishOptions options,
        HttpMessageHandler? messageHandler,
        Func<TimeSpan, CancellationToken, Task>? delay)
        : base(apiKeyStore, options, messageHandler, delay)
    {
        if (options.Provider != PolishProvider.Anthropic)
        {
            throw new ArgumentException("Anthropic options are required.", nameof(options));
        }
    }

    public override string ProviderId => "anthropic";

    protected override async Task<string?> SendOnceAsync(
        string apiKey,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Content = JsonContent.Create(new
        {
            model = Options.ModelId,
            max_tokens = MaximumOutputTokens,
            system = systemPrompt,
            messages = new object[]
            {
                new { role = "user", content = userMessage },
            },
            thinking = new { type = "disabled" },
        });
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var body = await ReadErrorBodyAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 400 && body.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
                status == 413)
            {
                throw new CloudPolishException(AppErrorCode.PolishInputTooLarge, canRetry: false);
            }

            if (status == 400 && body.Contains("credit balance", StringComparison.OrdinalIgnoreCase) ||
                status == 402)
            {
                throw new CloudPolishException(AppErrorCode.PolishQuotaExceeded, canRetry: false);
            }

            throw ClassifyCommonStatus(response.StatusCode, body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("stop_reason", out var stopReason))
        {
            if (string.Equals(stopReason.GetString(), "max_tokens", StringComparison.Ordinal))
            {
                throw new CloudPolishException(AppErrorCode.PolishOutputTruncated, canRetry: false);
            }

            if (string.Equals(stopReason.GetString(), "refusal", StringComparison.Ordinal))
            {
                throw new CloudPolishException(AppErrorCode.PolishContentBlocked, canRetry: false);
            }
        }

        if (!root.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw new CloudPolishException(AppErrorCode.PolishEmptyResponse, canRetry: false);
        }

        var output = string.Concat(content.EnumerateArray()
            .Where(part =>
                part.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "text", StringComparison.Ordinal))
            .Select(part =>
                part.TryGetProperty("text", out var text) ? text.GetString() : null));
        return output;
    }
}
