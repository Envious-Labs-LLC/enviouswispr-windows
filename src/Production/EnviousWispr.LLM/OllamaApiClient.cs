using System.Net;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.LLM;

public enum OllamaHealth
{
    Ready,
    EndpointInvalid,
    ServerUnavailable,
    ServerUnhealthy,
    NoLocalModels,
}

public enum OllamaModelReadiness
{
    Ready,
    NoModelSelected,
    ModelMissing,
    RemoteModelDisallowed,
    EndpointInvalid,
    ServerUnavailable,
    ServerUnhealthy,
}

public sealed record OllamaModelInfo(
    string Id,
    bool? SupportsThinking);

public sealed record OllamaDiscoveryResult(
    OllamaHealth Health,
    IReadOnlyList<OllamaModelInfo> LocalModels,
    IReadOnlyList<string> RemoteModelIds,
    AppError? Error = null);

public sealed record OllamaModelReadinessResult(
    OllamaModelReadiness Readiness,
    OllamaModelInfo? Model = null,
    AppError? Error = null);

public sealed class OllamaApiClient : IModelCatalog, IAsyncDisposable
{
    private static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(1);

    private readonly HttpClient _httpClient;
    private readonly Uri? _endpoint;
    private readonly TimeSpan _readinessTimeout;
    private bool _disposed;

    public OllamaApiClient(
        string? endpoint = null,
        HttpMessageHandler? messageHandler = null,
        TimeSpan? readinessTimeout = null)
    {
        _ = OllamaEndpointPolicy.TryNormalize(endpoint, out _endpoint);
        _readinessTimeout = readinessTimeout ?? DefaultReadinessTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_readinessTimeout, TimeSpan.Zero);
        _httpClient = messageHandler is null
            ? new HttpClient()
            : new HttpClient(messageHandler, disposeHandler: false);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Uri? Endpoint => _endpoint;

    public async Task<IReadOnlyList<string>> GetAvailableModelIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return discovery.LocalModels.Select(model => model.Id).ToArray();
    }

    public async Task<OllamaDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_endpoint is null)
        {
            return DiscoveryFailure(
                OllamaHealth.EndpointInvalid,
                AppErrorCode.PolishEndpointInvalid,
                canRetry: false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readinessTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/tags"));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return DiscoveryFailure(
                    OllamaHealth.ServerUnhealthy,
                    AppErrorCode.PolishProviderUnavailable);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("models", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return DiscoveryFailure(
                    OllamaHealth.ServerUnhealthy,
                    AppErrorCode.PolishBadRequest,
                    canRetry: false);
            }

            var local = new List<OllamaModelInfo>();
            var remote = new List<string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryReadModel(
                        row,
                        out var model,
                        out var isRemote,
                        out var supportsCompletion) ||
                    model is null ||
                    supportsCompletion == false)
                {
                    continue;
                }

                if (isRemote)
                {
                    remote.Add(model.Id);
                }
                else
                {
                    local.Add(model);
                }
            }

            local = local
                .GroupBy(model => CanonicalModelId(model.Id), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            remote = remote
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new OllamaDiscoveryResult(
                local.Count == 0 ? OllamaHealth.NoLocalModels : OllamaHealth.Ready,
                local,
                remote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DiscoveryFailure(
                OllamaHealth.ServerUnavailable,
                AppErrorCode.PolishTimedOut);
        }
        catch (HttpRequestException)
        {
            return DiscoveryFailure(
                OllamaHealth.ServerUnavailable,
                AppErrorCode.PolishProviderUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return DiscoveryFailure(
                OllamaHealth.ServerUnhealthy,
                AppErrorCode.PolishBadRequest,
                canRetry: false);
        }
    }

    public async Task<OllamaModelReadinessResult> ProbeModelAsync(
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return new OllamaModelReadinessResult(
                OllamaModelReadiness.NoModelSelected,
                Error: LocalError(AppErrorCode.PolishModelUnavailable, canRetry: false));
        }

        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var unavailable = discovery.Health switch
        {
            OllamaHealth.EndpointInvalid => OllamaModelReadiness.EndpointInvalid,
            OllamaHealth.ServerUnavailable => OllamaModelReadiness.ServerUnavailable,
            OllamaHealth.ServerUnhealthy => OllamaModelReadiness.ServerUnhealthy,
            _ => (OllamaModelReadiness?)null,
        };
        if (unavailable is not null)
        {
            return new OllamaModelReadinessResult(
                unavailable.Value,
                Error: discovery.Error);
        }

        var canonical = CanonicalModelId(modelId);
        var local = discovery.LocalModels.FirstOrDefault(
            model => string.Equals(
                CanonicalModelId(model.Id),
                canonical,
                StringComparison.OrdinalIgnoreCase));
        if (local is not null)
        {
            return new OllamaModelReadinessResult(OllamaModelReadiness.Ready, local);
        }

        if (discovery.RemoteModelIds.Any(id => string.Equals(
                CanonicalModelId(id),
                canonical,
                StringComparison.OrdinalIgnoreCase)))
        {
            return new OllamaModelReadinessResult(
                OllamaModelReadiness.RemoteModelDisallowed,
                Error: LocalError(AppErrorCode.PolishRemoteModelDisallowed, canRetry: false));
        }

        return new OllamaModelReadinessResult(
            OllamaModelReadiness.ModelMissing,
            Error: LocalError(AppErrorCode.PolishModelUnavailable, canRetry: false));
    }

    internal async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

    internal static string CanonicalModelId(string modelId) =>
        modelId.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? modelId[..^":latest".Length]
            : modelId;

    private static bool TryReadModel(
        JsonElement row,
        out OllamaModelInfo? model,
        out bool isRemote,
        out bool? supportsCompletion)
    {
        model = null;
        isRemote = false;
        supportsCompletion = null;
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("name", out var nameValue) ||
            nameValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameValue.GetString()))
        {
            return false;
        }

        if (row.TryGetProperty("remote_host", out var remoteHost) &&
            remoteHost.ValueKind == JsonValueKind.String)
        {
            isRemote = !string.IsNullOrWhiteSpace(remoteHost.GetString());
        }

        bool? thinks = null;
        if (row.TryGetProperty("capabilities", out var capabilities) &&
            capabilities.ValueKind == JsonValueKind.Array)
        {
            var reported = capabilities.EnumerateArray()
                .Where(capability => capability.ValueKind == JsonValueKind.String)
                .Select(capability => capability.GetString())
                .Where(capability => capability is not null)
                .ToArray();
            thinks = reported.Any(capability =>
                string.Equals(capability, "thinking", StringComparison.OrdinalIgnoreCase));
            supportsCompletion = reported.Any(capability =>
                string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase));
        }

        model = new OllamaModelInfo(nameValue.GetString()!, thinks);
        return true;
    }

    private static OllamaDiscoveryResult DiscoveryFailure(
        OllamaHealth health,
        AppErrorCode code,
        bool canRetry = true) =>
        new(health, [], [], LocalError(code, canRetry));

    private static AppError LocalError(AppErrorCode code, bool canRetry = true) =>
        new(code, AppErrorStage.LocalPolish, canRetry);

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
