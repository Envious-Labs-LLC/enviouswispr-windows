using System.Diagnostics;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Polish;
using EnviousWispr.LLM;
using EnviousWispr.Presentation;

namespace EnviousWispr.App;

/// <summary>The Ollama models block's host: the wire client per call, and the log. Ref: #213.</summary>
/// <remarks>
/// A CLIENT PER CALL, against the endpoint the page holds at that moment, under the same loopback policy the polish
/// provider uses - as <see cref="PolishModelSource"/> does for the picker. Nothing here is kept between calls.
///
/// THE LOG SAYS WHAT HAPPENED AND NOTHING ABOUT WHICH. A model id is a string the privacy contract forbids in the log
/// (docs/privacy/observability.md), so a download is Started, Downloaded, Stopped or Failed with an error code, and
/// that is all.
/// </remarks>
internal sealed class OllamaModelHost(IAppLogger logger, Func<string?> activeModelId) : IOllamaModelHost
{
    public string? ActiveModelId => activeModelId();

    public async Task<OllamaInventory> InspectAsync(string? endpoint, CancellationToken cancellationToken)
    {
        await using var client = new OllamaApiClient(endpoint, readinessTimeout: TimeSpan.FromSeconds(3));
        var discovery = await client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var server = discovery.Health switch
        {
            OllamaHealth.Ready => OllamaServerState.Ready,
            OllamaHealth.NoLocalModels => OllamaServerState.NoModels,
            OllamaHealth.EndpointInvalid => OllamaServerState.EndpointInvalid,
            OllamaHealth.ServerUnavailable when discovery.ConnectionRefused => OllamaServerState.NotListening,
            _ => OllamaServerState.NotResponding,
        };
        return new OllamaInventory(
            server,
            discovery.LocalModels.Select(model => new OllamaInstalledModel(model.Id, model.SizeBytes, model.ParameterSize)).ToArray(),
            OllamaFound: server != OllamaServerState.NotListening || OllamaIsInstalled());
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
        await using (var client = new OllamaApiClient(endpoint))
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

    public async Task<OllamaDeleteOutcome> DeleteAsync(string? endpoint, string modelId, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        OllamaDeleteOutcome outcome;
        await using (var client = new OllamaApiClient(endpoint))
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

    /// <summary>
    /// The Ollama app where its Windows installer puts it (per user), or ollama.exe on PATH. Decides which instructions
    /// the page gives when nothing answers; the app never starts it.
    /// </summary>
    private static bool OllamaIsInstalled()
    {
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama.exe");
        if (File.Exists(installed))
        {
            return true;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "ollama.exe")))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters no path can hold is not where Ollama is.
            }
        }

        return false;
    }

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
}
