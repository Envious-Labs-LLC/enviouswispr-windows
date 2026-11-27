using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Networking;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.LLM;
using EnviousWispr.Presentation;
using EnviousWispr.Services.AppImport;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.App;

/// <summary>The Ollama models block's host: the wire client per call, the safe launcher, the cloud catalog, and the log. Ref: #71, #213.</summary>
internal sealed class OllamaModelHost(
    IAppLogger logger,
    Func<string?> activeModelId,
    OllamaCloudCatalogClient? cloudCatalog = null,
    IRuntimeResourceAdmission? resourceAdmission = null,
    Func<bool>? isDictationBusy = null) : IOllamaModelHost, IDisposable
{
    private readonly OllamaCloudCatalogClient _cloudCatalog = cloudCatalog ?? new OllamaCloudCatalogClient();
    private readonly bool _ownsCloudCatalog = cloudCatalog is null;

    public string? ActiveModelId => activeModelId();

    public async Task<OllamaInventory> InspectAsync(string? endpoint, CancellationToken cancellationToken)
    {
        await using var client = new OllamaApiClient(
            endpoint,
            SharedHttpClients.GetHandler(HttpClientProfile.NoRedirect),
            readinessTimeout: TimeSpan.FromSeconds(3));
        var discovery = await client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var server = discovery.Health switch
        {
            OllamaHealth.Ready => OllamaServerState.Ready,
            OllamaHealth.NoLocalModels => OllamaServerState.NoModels,
            OllamaHealth.EndpointInvalid => OllamaServerState.EndpointInvalid,
            OllamaHealth.ServerUnavailable when discovery.ConnectionRefused => OllamaServerState.NotListening,
            _ => OllamaServerState.NotResponding,
        };
        IReadOnlyList<string> cloudCatalogIds = [];
        if (server is OllamaServerState.Ready or OllamaServerState.NoModels)
        {
            cloudCatalogIds = await _cloudCatalog.TryGetCatalogIdsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return new OllamaInventory(
            server,
            discovery.AllModels
                .Select(model => new OllamaInstalledModel(
                    model.Id,
                    model.SizeBytes,
                    model.ParameterSize,
                    model.IsRemote,
                    model.SupportsThinking))
                .ToArray(),
            OllamaFound: server != OllamaServerState.NotListening || OllamaIsInstalled(),
            CloudCatalogIds: cloudCatalogIds);
    }

    public OllamaLaunchSafety CheckLaunchSafety() =>
        OllamaLocalConfigProbe.CheckLaunchSafety();

    public bool TryOpenOllama()
    {
        if (CheckLaunchSafety() != OllamaLaunchSafety.SafeLocalOnly)
        {
            return false;
        }

        var target = ResolveOllamaLaunchTarget(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH"));
        if (target is null)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = target.FileName,
                UseShellExecute = target.UseShellExecute,
                CreateNoWindow = target.CreateNoWindow,
            };
            if (!string.IsNullOrWhiteSpace(target.Arguments))
            {
                startInfo.Arguments = target.Arguments;
            }

            _ = Process.Start(startInfo);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            return false;
        }
    }

    public async Task<OllamaPullOutcome> PullAsync(
        string? endpoint,
        string modelId,
        IProgress<OllamaPullUpdate> progress,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.OllamaModelDownloadStarted, Provider: DiagnosticProvider.Ollama));
        OllamaPullOutcome outcome;
        await using (var client = new OllamaApiClient(endpoint, SharedHttpClients.GetHandler(HttpClientProfile.NoRedirect)))
        {
            outcome = await client.PullAsync(modelId, progress, cancellationToken).ConfigureAwait(false);
        }

        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            outcome switch
            {
                OllamaPullOutcome.Succeeded => AppEventCode.OllamaModelDownloaded,
                OllamaPullOutcome.Cancelled => AppEventCode.OllamaModelDownloadStopped,
                _ => AppEventCode.OllamaModelDownloadFailed,
            },
            outcome is OllamaPullOutcome.Succeeded or OllamaPullOutcome.Cancelled ? AppFailureCategory.None : AppFailureCategory.LocalPolish,
            timer.ElapsedMilliseconds,
            DiagnosticProvider.Ollama,
            ErrorCodeFor(outcome)));
        return outcome;
    }

    public async Task<OllamaHostedAddResult> ResolveAndAddHostedModelAsync(
        string? endpoint,
        string advertisedId,
        IProgress<OllamaPullUpdate> progress,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.OllamaModelDownloadStarted, Provider: DiagnosticProvider.Ollama));
        OllamaHostedAddResult result;
        await using (var client = new OllamaApiClient(endpoint, SharedHttpClients.GetHandler(HttpClientProfile.NoRedirect)))
        {
            result = await client.ResolveAndAddHostedModelAsync(advertisedId, progress, cancellationToken).ConfigureAwait(false);
        }

        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            result.Outcome switch
            {
                OllamaHostedAddOutcome.Succeeded => AppEventCode.OllamaModelDownloaded,
                OllamaHostedAddOutcome.Cancelled => AppEventCode.OllamaModelDownloadStopped,
                _ => AppEventCode.OllamaModelDownloadFailed,
            },
            result.Outcome is OllamaHostedAddOutcome.Succeeded or OllamaHostedAddOutcome.Cancelled ? AppFailureCategory.None : AppFailureCategory.LocalPolish,
            timer.ElapsedMilliseconds,
            DiagnosticProvider.Ollama,
            result.PullOutcome is { } pullOutcome ? ErrorCodeFor(pullOutcome) : ErrorCodeForHosted(result.Outcome)));
        return result;
    }

    public async Task<OllamaWarmupOutcome> WarmUpAsync(
        string? endpoint,
        string modelId,
        CancellationToken cancellationToken)
    {
        if (isDictationBusy?.Invoke() == true)
        {
            return OllamaWarmupOutcome.Cancelled;
        }

        CancellationTokenSource? linked = null;
        IAsyncDisposable? lease = null;
        try
        {
            var effectiveToken = cancellationToken;
            if (resourceAdmission is RuntimeResourceArbiter arbiter)
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    arbiter.CurrentBackgroundToken);
                effectiveToken = linked.Token;
            }

            if (resourceAdmission is not null)
            {
                var acquired = await resourceAdmission.AcquireAsync(
                    RuntimeResourceKind.Cpu,
                    RuntimeWorkloadKind.LocalPolishBackground,
                    TimeSpan.Zero,
                    effectiveToken).ConfigureAwait(false);
                if (!acquired.Succeeded || acquired.Lease is null)
                {
                    return OllamaWarmupOutcome.Cancelled;
                }

                lease = acquired.Lease;
            }

            if (isDictationBusy?.Invoke() == true)
            {
                return OllamaWarmupOutcome.Cancelled;
            }

            await using var client = new OllamaApiClient(endpoint, SharedHttpClients.GetHandler(HttpClientProfile.NoRedirect));
            return await client.WarmUpAsync(modelId, effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OllamaWarmupOutcome.Cancelled;
        }
        catch (ObjectDisposedException)
        {
            return OllamaWarmupOutcome.Cancelled;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            linked?.Dispose();
        }
    }

    public async Task<OllamaDeleteOutcome> DeleteAsync(string? endpoint, string modelId, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        OllamaDeleteOutcome outcome;
        await using (var client = new OllamaApiClient(endpoint, SharedHttpClients.GetHandler(HttpClientProfile.NoRedirect)))
        {
            outcome = await client.DeleteAsync(modelId, cancellationToken).ConfigureAwait(false);
        }

        var removed = outcome is OllamaDeleteOutcome.Deleted or OllamaDeleteOutcome.NotFound;
        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            removed ? AppEventCode.OllamaModelRemoved : AppEventCode.OllamaModelRemoveFailed,
            removed ? AppFailureCategory.None : AppFailureCategory.LocalPolish,
            timer.ElapsedMilliseconds,
            DiagnosticProvider.Ollama,
            outcome switch
            {
                OllamaDeleteOutcome.Deleted or OllamaDeleteOutcome.NotFound => null,
                OllamaDeleteOutcome.EndpointInvalid => AppErrorCode.PolishEndpointInvalid,
                OllamaDeleteOutcome.ServerUnavailable => AppErrorCode.PolishProviderUnavailable,
                _ => AppErrorCode.PolishProviderServerError,
            }));
        return outcome;
    }

    internal static OllamaLaunchTarget? ResolveOllamaLaunchTarget(
        string? localAppDataPath,
        string? pathEnvironment,
        Func<string, bool>? fileExists = null) =>
        OllamaModelCatalog.ResolveOllamaLaunchTarget(localAppDataPath, pathEnvironment, fileExists);

    private static bool OllamaIsInstalled() =>
        ResolveOllamaLaunchTarget(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH")) is not null;

    private static AppErrorCode? ErrorCodeFor(OllamaPullOutcome outcome) => outcome switch
    {
        OllamaPullOutcome.Succeeded or OllamaPullOutcome.Cancelled => null,
        OllamaPullOutcome.DiskFull => AppErrorCode.StorageUnavailable,
        OllamaPullOutcome.NotFound => AppErrorCode.PolishModelUnavailable,
        OllamaPullOutcome.NetworkFailed or OllamaPullOutcome.ServerUnavailable => AppErrorCode.PolishProviderUnavailable,
        OllamaPullOutcome.EndpointInvalid => AppErrorCode.PolishEndpointInvalid,
        OllamaPullOutcome.Interrupted => AppErrorCode.PolishFailed,
        _ => AppErrorCode.PolishProviderServerError,
    };

    private static AppErrorCode? ErrorCodeForHosted(OllamaHostedAddOutcome outcome) => outcome switch
    {
        OllamaHostedAddOutcome.Succeeded or OllamaHostedAddOutcome.Cancelled => null,
        OllamaHostedAddOutcome.NoUsableName or OllamaHostedAddOutcome.Ambiguous => AppErrorCode.PolishModelUnavailable,
        OllamaHostedAddOutcome.EndpointInvalid => AppErrorCode.PolishEndpointInvalid,
        OllamaHostedAddOutcome.ServerUnavailable or OllamaHostedAddOutcome.Unreachable => AppErrorCode.PolishProviderUnavailable,
        _ => AppErrorCode.PolishProviderServerError,
    };

    public void Dispose()
    {
        if (_ownsCloudCatalog)
        {
            _cloudCatalog.Dispose();
        }
    }
}
