using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public sealed class OpenAiPolishProvider : CloudPolishProviderBase
{
    internal static readonly Uri Endpoint = new("https://api.openai.com/v1/chat/completions");

    public OpenAiPolishProvider(
        IApiKeyStore apiKeyStore,
        string? modelId = null,
        TimeSpan? requestTimeout = null,
        HttpMessageHandler? messageHandler = null)
        : this(
            apiKeyStore,
            new CloudPolishOptions(
                PolishProvider.OpenAI,
                modelId ?? CloudPolishOptions.DefaultModel(PolishProvider.OpenAI),
                requestTimeout),
            messageHandler,
            delay: null)
    {
    }

    internal OpenAiPolishProvider(
        IApiKeyStore apiKeyStore,
        CloudPolishOptions options,
        HttpMessageHandler? messageHandler,
        Func<TimeSpan, CancellationToken, Task>? delay)
        : base(apiKeyStore, options, messageHandler, delay)
    {
        if (options.Provider != PolishProvider.OpenAI)
        {
            throw new ArgumentException("OpenAI options are required.", nameof(options));
        }
    }

    public override string ProviderId => "openai";

    protected override async Task<string?> SendOnceAsync(
        string apiKey,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var modelId = Options.ModelId.ToLowerInvariant();
        if (modelId.Contains("codex", StringComparison.Ordinal) ||
            modelId.Contains("-pro", StringComparison.Ordinal))
        {
            throw new CloudPolishException(AppErrorCode.PolishModelUnavailable, canRetry: false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var body = new Dictionary<string, object>
        {
            ["model"] = Options.ModelId,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            ["store"] = false,
        };
        var chatVariant = modelId.Contains("-chat", StringComparison.Ordinal);
        var reasoning = modelId.StartsWith("o1", StringComparison.Ordinal) ||
            modelId.StartsWith("o3", StringComparison.Ordinal) ||
            modelId.StartsWith("o4", StringComparison.Ordinal) ||
            modelId.StartsWith("gpt-5", StringComparison.Ordinal) && !chatVariant;
        if (reasoning)
        {
            body["reasoning_effort"] = "low";
        }
        else
        {
            body["temperature"] = 0;
        }

        request.Content = JsonContent.Create(body);
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var errorBody = await ReadErrorBodyAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                if (errorBody.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CloudPolishException(AppErrorCode.PolishInputTooLarge, canRetry: false);
                }

                if (errorBody.Contains("content_filter", StringComparison.OrdinalIgnoreCase) ||
                    errorBody.Contains("content_policy", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CloudPolishException(AppErrorCode.PolishContentBlocked, canRetry: false);
                }
            }

            throw ClassifyCommonStatus(response.StatusCode, errorBody);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new CloudPolishException(AppErrorCode.PolishEmptyResponse, canRetry: false);
        }

        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
            string.Equals(finishReason.GetString(), "length", StringComparison.Ordinal))
        {
            throw new CloudPolishException(AppErrorCode.PolishOutputTruncated, canRetry: false);
        }

        if (!choice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new CloudPolishException(AppErrorCode.PolishEmptyResponse, canRetry: false);
        }

        return content.GetString();
    }
}
