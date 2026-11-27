using System.Net;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Networking;

namespace EnviousWispr.LLM;

public enum OllamaCloudCatalogError
{
    InvalidResponse,
    ResponseTooLarge,
}

public sealed class OllamaCloudCatalogException : Exception
{
    public OllamaCloudCatalogException(OllamaCloudCatalogError error, string? message = null, Exception? innerException = null)
        : base(message ?? error.ToString(), innerException)
    {
        Error = error;
    }

    public OllamaCloudCatalogError Error { get; }
}

/// <summary>
/// Discovers available Ollama cloud models from <c>https://ollama.com/v1/models</c>.
/// Enforces no redirects, no cookies, a 5-second timeout, a 1 MB response size ceiling, a 200 raw-entry ceiling,
/// and a 15-minute single-flight cache. Ref: macOS OllamaCloudCatalogClient (#1956).
/// </summary>
public sealed class OllamaCloudCatalogClient : IDisposable
{
    public const string EndpointUrl = "https://ollama.com/v1/models";
    public const string AllowedHost = "ollama.com";
    public const int MaximumBytes = 1_048_576;
    public const int MaximumEntries = 200;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    private static readonly Uri EndpointUri = new(EndpointUrl);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly object _cacheLock = new();
    private IReadOnlyList<string>? _cachedIds;
    private DateTimeOffset _fetchedAt;
    private Task<IReadOnlyList<string>>? _inFlightTask;
    private bool _disposed;

    public OllamaCloudCatalogClient(
        HttpMessageHandler? messageHandler = null,
        TimeProvider? clock = null,
        TimeSpan? timeout = null)
    {
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeout, TimeSpan.Zero);
        _httpClient = SharedHttpClients.CreateClient(
            messageHandler,
            HttpClientProfile.NoRedirectNoCookies,
            Timeout.InfiniteTimeSpan);
    }

    public IReadOnlyList<string>? CachedIds
    {
        get
        {
            lock (_cacheLock)
            {
                return _cachedIds;
            }
        }
    }

    /// <summary>
    /// Performs one bare uncached <c>GET https://ollama.com/v1/models</c> request and parses the advertised model IDs.
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchIdsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUri);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Ollama cloud catalog request timed out.");
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new OllamaCloudCatalogException(OllamaCloudCatalogError.InvalidResponse);
            }

            if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaximumBytes)
            {
                throw new OllamaCloudCatalogException(OllamaCloudCatalogError.ResponseTooLarge);
            }

            byte[] bytes;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                bytes = await ReadBoundedBytesAsync(stream, MaximumBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Ollama cloud catalog response transfer timed out.");
            }

            return ParseCloudCatalog(bytes, MaximumEntries);
        }
    }

    /// <summary>
    /// Returns the advertised Ollama cloud model IDs, reusing a cached result within the 15-minute window and
    /// coalescing concurrent refreshes into a single in-flight request. If a refresh fails after a prior success,
    /// the prior cached list is retained.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCatalogIdsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<IReadOnlyList<string>> task;
        lock (_cacheLock)
        {
            if (_inFlightTask is not null)
            {
                task = _inFlightTask;
            }
            else if (!forceRefresh &&
                     _cachedIds is not null &&
                     (_clock.GetUtcNow() - _fetchedAt) >= TimeSpan.Zero &&
                     (_clock.GetUtcNow() - _fetchedAt) < CacheDuration)
            {
                return _cachedIds;
            }
            else
            {
                var prior = _cachedIds;
                task = RefreshCoreAsync(prior);
                if (!task.IsCompleted)
                {
                    _inFlightTask = task;
                }
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort wrapper for UI inspection: returns the cached or freshly loaded cloud catalog IDs, or an empty
    /// list if the public endpoint cannot be reached and no prior cache exists.
    /// </summary>
    public async Task<IReadOnlyList<string>> TryGetCatalogIdsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetCatalogIdsAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            lock (_cacheLock)
            {
                return _cachedIds ?? [];
            }
        }
    }

    private async Task<IReadOnlyList<string>> RefreshCoreAsync(IReadOnlyList<string>? prior)
    {
        try
        {
            var ids = await FetchIdsAsync(CancellationToken.None).ConfigureAwait(false);
            var completedAt = _clock.GetUtcNow();
            lock (_cacheLock)
            {
                _cachedIds = ids;
                _fetchedAt = completedAt;
                _inFlightTask = null;
            }

            return ids;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            lock (_cacheLock)
            {
                _inFlightTask = null;
                if (prior is not null)
                {
                    return prior;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Redirect policy check: requires HTTPS and an exact case-insensitive host match against <paramref name="allowedHost"/>.
    /// </summary>
    public static bool ShouldFollowRedirect(Uri? target, string allowedHost = AllowedHost) =>
        target is not null &&
        target.IsAbsoluteUri &&
        string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(target.Host, allowedHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accumulates a stream up to <paramref name="maximumBytes"/> and throws <see cref="OllamaCloudCatalogError.ResponseTooLarge"/>
    /// on the first byte past the ceiling.
    /// </summary>
    public static async Task<byte[]> ReadBoundedBytesAsync(
        Stream stream,
        int maximumBytes = MaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        using var memory = new MemoryStream(Math.Min(maximumBytes, 16_384));
        var buffer = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }

            if (memory.Length + read > maximumBytes)
            {
                throw new OllamaCloudCatalogException(OllamaCloudCatalogError.ResponseTooLarge);
            }

            memory.Write(buffer, 0, read);
        }
    }

    public static IReadOnlyList<string> ParseCloudCatalog(
        string json,
        int maximumEntries = MaximumEntries)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ParseCloudCatalog(Encoding.UTF8.GetBytes(json), maximumEntries);
    }

    /// <summary>
    /// Parses the <c>data</c> array of an <c>ollama.com/v1/models</c> document. Bounds the RAW array count before
    /// filtering, drops individual malformed rows, trims whitespace, and deduplicates preserving first occurrence.
    /// </summary>
    public static IReadOnlyList<string> ParseCloudCatalog(
        ReadOnlySpan<byte> utf8Json,
        int maximumEntries = MaximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumEntries, 0);
        if (utf8Json.IsEmpty)
        {
            throw new OllamaCloudCatalogException(OllamaCloudCatalogError.InvalidResponse);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException exception)
        {
            throw new OllamaCloudCatalogException(OllamaCloudCatalogError.InvalidResponse, innerException: exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new OllamaCloudCatalogException(OllamaCloudCatalogError.InvalidResponse);
            }

            var rawCount = data.GetArrayLength();
            if (rawCount == 0 || rawCount > maximumEntries)
            {
                throw new OllamaCloudCatalogException(OllamaCloudCatalogError.InvalidResponse);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new List<string>(rawCount);
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var trimmed = (idElement.GetString() ?? string.Empty).Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    ids.Add(trimmed);
                }
            }

            if (ids.Count == 0)
            {
                throw new OllamaCloudCatalogException(OllamaCloudCatalogError.InvalidResponse);
            }

            return ids;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _httpClient.Dispose();
        }
    }
}
