using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.LLM;

public enum CloudModelCatalogStatus
{
    Ready,
    MissingCredential,
    CredentialUnavailable,
    KeyRejected,
    ProviderUnavailable,
    InvalidResponse,
}

public sealed record CloudModelCatalogResult(
    CloudModelCatalogStatus Status,
    IReadOnlyList<string> ModelIds);

/// <summary>
/// Lists transcript-polish model IDs exposed to the founder's direct provider
/// account. Discovery sends only the stored API credential; it never sends a
/// transcript and never invokes a billable generation endpoint.
/// </summary>
public sealed class CloudPolishModelCatalog : IDisposable
{
    internal static readonly Uri OpenAiModelsEndpoint = new("https://api.openai.com/v1/models");
    internal static readonly Uri AnthropicModelsEndpoint = new(
        "https://api.anthropic.com/v1/models?limit=1000");
    internal static readonly Uri GeminiModelsEndpoint = new(
        "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000");
    private static readonly string[] ExcludedPatterns =
    [
        "tts", "image", "robotics", "computer-use", "deep-research",
        "gemma", "exp-", "embedding", "aqa", "vision", "nano-banana",
        "lyria",
    ];
    private static readonly string[] VersionedDuplicateSuffixes = ["-001", "-002", "-003"];

    private readonly IApiKeyStore _apiKeyStore;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private bool _disposed;

    public CloudPolishModelCatalog(
        IApiKeyStore apiKeyStore,
        HttpMessageHandler? messageHandler = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        _apiKeyStore = apiKeyStore;
        _httpClient = messageHandler is null
            ? new HttpClient()
            : new HttpClient(messageHandler, disposeHandler: false);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_requestTimeout, TimeSpan.Zero);
    }

    public async Task<CloudModelCatalogResult> DiscoverAsync(
        PolishProvider provider,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (provider is not (PolishProvider.OpenAI or PolishProvider.Anthropic or PolishProvider.Gemini))
        {
            throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Cloud model discovery supports OpenAI, Anthropic, and Gemini.");
        }

        ApiKeyReadResult credential;
        try
        {
            credential = _apiKeyStore.Read(provider);
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
            return Empty(CloudModelCatalogStatus.CredentialUnavailable);
        }

        if (credential.Status == ApiKeyReadStatus.Missing)
        {
            return Empty(CloudModelCatalogStatus.MissingCredential);
        }

        if (credential.Status != ApiKeyReadStatus.Found || string.IsNullOrWhiteSpace(credential.Value))
        {
            return Empty(CloudModelCatalogStatus.CredentialUnavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            var modelIds = provider switch
            {
                PolishProvider.OpenAI => await DiscoverOpenAiAsync(
                    credential.Value,
                    timeout.Token).ConfigureAwait(false),
                PolishProvider.Anthropic => await DiscoverAnthropicAsync(
                    credential.Value,
                    timeout.Token).ConfigureAwait(false),
                PolishProvider.Gemini => await DiscoverGeminiAsync(
                    credential.Value,
                    timeout.Token).ConfigureAwait(false),
                _ => throw new UnreachableException(),
            };
            return new CloudModelCatalogResult(
                CloudModelCatalogStatus.Ready,
                Filter(provider, modelIds));
        }
        catch (CatalogHttpException exception)
        {
            return Empty(exception.Status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty(CloudModelCatalogStatus.ProviderUnavailable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Empty(CloudModelCatalogStatus.ProviderUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            return Empty(CloudModelCatalogStatus.InvalidResponse);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    internal static IReadOnlyList<string> Filter(
        PolishProvider provider,
        IEnumerable<string> modelIds) => modelIds
        .Where(modelId => IsCandidate(provider, modelId))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private async Task<IReadOnlyList<string>> DiscoverOpenAiAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiModelsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, providerRejectsForbidden: true, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse();
        }

        return data.EnumerateArray()
            .Select(model => model.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
            .Cast<string>()
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> DiscoverAnthropicAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        var modelIds = new List<string>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? afterId = null;
        for (var page = 0; page < 20; page++)
        {
            var endpoint = afterId is null
                ? AnthropicModelsEndpoint
                : new Uri($"{AnthropicModelsEndpoint}&after_id={Uri.EscapeDataString(afterId)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicPolishProvider.ApiVersion);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(response, providerRejectsForbidden: true, cancellationToken)
                .ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse();
            }

            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    modelIds.Add(id.GetString()!);
                }
            }

            var hasMore = root.TryGetProperty("has_more", out var hasMoreElement) &&
                hasMoreElement.ValueKind == JsonValueKind.True;
            if (!hasMore)
            {
                return modelIds;
            }

            afterId = root.TryGetProperty("last_id", out var lastIdElement)
                ? lastIdElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(afterId) || !seenCursors.Add(afterId))
            {
                throw InvalidResponse();
            }
        }

        throw InvalidResponse();
    }

    private async Task<IReadOnlyList<string>> DiscoverGeminiAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GeminiModelsEndpoint);
        request.Headers.Add("x-goog-api-key", apiKey);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, providerRejectsForbidden: true, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse();
        }

        var modelIds = new List<string>();
        foreach (var model in models.EnumerateArray())
        {
            if (!model.TryGetProperty("supportedGenerationMethods", out var methods) ||
                methods.ValueKind != JsonValueKind.Array ||
                !methods.EnumerateArray().Any(method =>
                    string.Equals(method.GetString(), "generateContent", StringComparison.Ordinal)))
            {
                continue;
            }

            if (model.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                modelIds.Add(name.GetString()!.StartsWith("models/", StringComparison.Ordinal)
                    ? name.GetString()!["models/".Length..]
                    : name.GetString()!);
            }
        }

        return modelIds;
    }

    private static async Task RequireSuccessAsync(
        HttpResponseMessage response,
        bool providerRejectsForbidden,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var rejected = response.StatusCode == HttpStatusCode.Unauthorized ||
            providerRejectsForbidden && response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.BadRequest &&
            body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase);
        throw new CatalogHttpException(
            rejected
                ? CloudModelCatalogStatus.KeyRejected
                : CloudModelCatalogStatus.ProviderUnavailable);
    }

    private static bool IsCandidate(PolishProvider provider, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var lowered = modelId.ToLowerInvariant();
        if (ExcludedPatterns.Any(pattern => lowered.Contains(pattern, StringComparison.Ordinal)) ||
            VersionedDuplicateSuffixes.Any(suffix => lowered.EndsWith(suffix, StringComparison.Ordinal)) ||
            lowered.Contains("latest", StringComparison.Ordinal))
        {
            return false;
        }

        return provider switch
        {
            PolishProvider.OpenAI => CloudPolishOptions.ModelIdLooksLikeProvider(modelId, provider) &&
                !lowered.Contains("realtime", StringComparison.Ordinal) &&
                !lowered.Contains("audio", StringComparison.Ordinal) &&
                !lowered.Contains("search", StringComparison.Ordinal) &&
                !lowered.Contains("transcribe", StringComparison.Ordinal) &&
                !lowered.Contains("codex", StringComparison.Ordinal) &&
                !lowered.Contains("-pro", StringComparison.Ordinal),
            PolishProvider.Anthropic or PolishProvider.Gemini =>
                CloudPolishOptions.ModelIdLooksLikeProvider(modelId, provider),
            _ => false,
        };
    }

    private static CloudModelCatalogResult Empty(CloudModelCatalogStatus status) =>
        new(status, []);

    private static CatalogHttpException InvalidResponse() =>
        new(CloudModelCatalogStatus.InvalidResponse);

    private sealed class CatalogHttpException(CloudModelCatalogStatus status) : Exception
    {
        public CloudModelCatalogStatus Status { get; } = status;
    }
}
