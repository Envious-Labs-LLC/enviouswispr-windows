using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public sealed class GeminiPolishProvider : CloudPolishProviderBase
{
    internal static readonly Uri EndpointRoot = new(
        "https://generativelanguage.googleapis.com/v1beta/models/");

    public GeminiPolishProvider(
        IApiKeyStore apiKeyStore,
        string? modelId = null,
        TimeSpan? requestTimeout = null,
        HttpMessageHandler? messageHandler = null)
        : this(
            apiKeyStore,
            new CloudPolishOptions(
                PolishProvider.Gemini,
                modelId ?? CloudPolishOptions.DefaultModel(PolishProvider.Gemini),
                requestTimeout),
            messageHandler,
            delay: null)
    {
    }

    internal GeminiPolishProvider(
        IApiKeyStore apiKeyStore,
        CloudPolishOptions options,
        HttpMessageHandler? messageHandler,
        Func<TimeSpan, CancellationToken, Task>? delay)
        : base(apiKeyStore, options, messageHandler, delay)
    {
        if (options.Provider != PolishProvider.Gemini)
        {
            throw new ArgumentException("Gemini options are required.", nameof(options));
        }
    }

    public override string ProviderId => "gemini";

    protected override async Task<string?> SendOnceAsync(
        string apiKey,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            $"{EndpointRoot}{Uri.EscapeDataString(Options.ModelId)}:generateContent",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            systemInstruction = new
            {
                parts = new object[] { new { text = systemPrompt } },
            },
            contents = new object[]
            {
                new
                {
                    parts = new object[] { new { text = userMessage } },
                },
            },
            generationConfig = MakeGenerationConfig(Options.ModelId),
            store = false,
        });
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var body = await ReadErrorBodyAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                if (body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CloudPolishException(AppErrorCode.PolishApiKeyRejected, canRetry: false);
                }

                if (body.Contains("exceeds the maximum number of tokens", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CloudPolishException(AppErrorCode.PolishInputTooLarge, canRetry: false);
                }

                if (body.Contains("PROHIBITED_CONTENT", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("blockReason", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CloudPolishException(AppErrorCode.PolishContentBlocked, canRetry: false);
                }
            }

            throw ClassifyCommonStatus(
                response.StatusCode,
                body,
                ambiguousRateOrQuota: true);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out _))
        {
            throw new CloudPolishException(AppErrorCode.PolishContentBlocked, canRetry: false);
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            throw new CloudPolishException(AppErrorCode.PolishEmptyResponse, canRetry: false);
        }

        var candidate = candidates[0];
        if (candidate.TryGetProperty("finishReason", out var finishReason) &&
            string.Equals(finishReason.GetString(), "MAX_TOKENS", StringComparison.Ordinal))
        {
            throw new CloudPolishException(AppErrorCode.PolishOutputTruncated, canRetry: false);
        }

        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            throw new CloudPolishException(AppErrorCode.PolishEmptyResponse, canRetry: false);
        }

        return string.Concat(parts.EnumerateArray()
            .Where(part =>
                !part.TryGetProperty("thought", out var thought) ||
                thought.ValueKind != JsonValueKind.True)
            .Select(part =>
                part.TryGetProperty("text", out var text) ? text.GetString() : null));
    }

    internal static Dictionary<string, object> MakeGenerationConfig(string modelId)
    {
        var config = new Dictionary<string, object>
        {
            ["temperature"] = 0,
        };
        switch (modelId)
        {
            case "gemini-3.6-flash":
            case "gemini-3.5-flash":
            case "gemini-3.5-flash-lite":
            case "gemini-3.1-flash-lite":
            case "gemini-3.1-flash-lite-preview":
            case "gemini-3-flash-preview":
                config["thinkingConfig"] = new { thinkingLevel = "minimal" };
                break;
            case "gemini-3.7-flash":
            case "gemini-3.1-pro-preview":
            case "gemini-3.1-pro-preview-customtools":
                config["thinkingConfig"] = new { thinkingLevel = "low" };
                break;
            case "gemini-2.5-flash":
            case "gemini-2.5-flash-lite":
                config["thinkingConfig"] = new { thinkingBudget = 0 };
                break;
            case "gemini-2.5-pro":
                config["thinkingConfig"] = new { thinkingBudget = 128 };
                break;
        }

        return config;
    }
}
