using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Polish;
using EnviousWispr.LLM;
using EnviousWispr.Presentation;

namespace EnviousWispr.App;

/// <summary>The Ollama models block's host: the wire client per call, the guarded launcher, and the log. Ref: #213.</summary>
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
    private static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StartPoll = TimeSpan.FromMilliseconds(500);

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
            server == OllamaServerState.NotListening ? OllamaLauncher.Assess(client.Endpoint) : OllamaStartability.Startable);
    }

    public async Task<OllamaStartOutcome> StartAsync(string? endpoint, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        OllamaStartOutcome outcome;
        await using (var client = new OllamaApiClient(endpoint, readinessTimeout: TimeSpan.FromSeconds(3)))
        {
            // ASKED AGAIN, RIGHT BEFORE LAUNCHING. The page's look may be minutes old; something may have started
            // Ollama since, and a second launch beside it is the case the guards exist for.
            var now = await client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            if (now.Health != OllamaHealth.ServerUnavailable || !now.ConnectionRefused ||
                OllamaLauncher.Assess(client.Endpoint) != OllamaStartability.Startable ||
                OllamaLauncher.FindDesktopApp() is not { } desktopApp)
            {
                outcome = OllamaStartOutcome.NotAllowed;
            }
            else if (!OllamaLauncher.Launch(desktopApp))
            {
                outcome = OllamaStartOutcome.LaunchFailed;
            }
            else
            {
                outcome = OllamaStartOutcome.NoAnswer;
                while (timer.Elapsed < StartPatience)
                {
                    await Task.Delay(StartPoll, cancellationToken).ConfigureAwait(false);
                    var answer = await client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    if (answer.Health is OllamaHealth.Ready or OllamaHealth.NoLocalModels)
                    {
                        outcome = OllamaStartOutcome.Started;
                        break;
                    }
                }
            }
        }

        logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            outcome == OllamaStartOutcome.Started ? AppEventCode.OllamaStarted : AppEventCode.OllamaStartFailed,
            outcome == OllamaStartOutcome.Started ? AppFailureCategory.None : AppFailureCategory.LocalPolish,
            timer.ElapsedMilliseconds,
            DiagnosticProvider.Ollama,
            outcome == OllamaStartOutcome.Started ? null : AppErrorCode.PolishProviderUnavailable));
        return outcome;
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

/// <summary>Finds the Ollama desktop app and decides whether EnviousWispr may start it. Windows only.</summary>
/// <remarks>
/// GUARDED, BECAUSE THE DESKTOP APP IS NOT GENTLE (design review of #213, against Ollama 0.34.4's own source): on start
/// it cleans up earlier servers and can terminate one another tool started, and it merges its own settings - including
/// how widely the server listens - into the server's environment. So it is started only in the plain case, and every
/// other case gets instructions instead of a launch.
/// </remarks>
internal static class OllamaLauncher
{
    private const int DefaultPort = 11434;

    /// <summary>Whether starting Ollama is safe for this endpoint on this machine right now.</summary>
    public static OllamaStartability Assess(Uri? endpoint)
    {
        if (endpoint is null || endpoint.Port != DefaultPort ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return OllamaStartability.CustomEndpoint;
        }

        if (FindDesktopApp() is null)
        {
            return OllamaStartability.LauncherNotFound;
        }

        if (!HostSettingStaysOnThisPc())
        {
            return OllamaStartability.ExposedHost;
        }

        if (IsElevated())
        {
            return OllamaStartability.Elevated;
        }

        return AnOllamaProcessIsRunning() ? OllamaStartability.AlreadyRunning : OllamaStartability.Startable;
    }

    /// <summary>The desktop app where the installer puts it (per user), or beside an ollama.exe on PATH.</summary>
    public static string? FindDesktopApp()
    {
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama app.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "ollama.exe")) &&
                    Path.Combine(directory, "ollama app.exe") is var beside && File.Exists(beside))
                {
                    return beside;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters no path can hold is not where Ollama is.
            }
        }

        return null;
    }

    public static bool Launch(string desktopApp)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(desktopApp)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(desktopApp) ?? string.Empty,
            });
            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// OLLAMA_HOST, in this process and in the account's and the machine's settings, names nothing beyond this PC and
    /// no port but the default. Unset means Ollama's own default, which is loopback.
    /// </summary>
    internal static bool HostSettingStaysOnThisPc() =>
        new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(target => Environment.GetEnvironmentVariable("OLLAMA_HOST", target))
            .All(OllamaEndpointPolicy.IsLoopbackHostSetting);

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Read by name, never acted on: its presence is only a reason not to start a second one.</summary>
    private static bool AnOllamaProcessIsRunning()
    {
        foreach (var name in new[] { "ollama", "ollama app" })
        {
            var found = Process.GetProcessesByName(name);
            try
            {
                if (found.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in found)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}
