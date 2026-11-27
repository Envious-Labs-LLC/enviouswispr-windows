using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.LLM;

public sealed record EgOneServerOptions(
    string ServerExecutable,
    string ModelFile,
    int ContextTokens = 8_192,
    int? GpuLayers = null,
    TimeSpan? StartupTimeout = null,
    int? Threads = null,
    bool AllowCpuFallback = false,
    int? BatchThreads = null,
    int? SleepIdleSeconds = null)
{
    public TimeSpan EffectiveStartupTimeout => StartupTimeout ?? TimeSpan.FromSeconds(60);
}

public sealed record EgOneEndpoint(int Port, string AuthToken, int ContextTokens)
{
    public Uri HealthUri => new($"http://127.0.0.1:{Port}/health", UriKind.Absolute);

    public Uri SlotsUri => new($"http://127.0.0.1:{Port}/slots", UriKind.Absolute);

    public Uri ChatCompletionsUri =>
        new($"http://127.0.0.1:{Port}/v1/chat/completions", UriKind.Absolute);
}

internal interface IEgOneRuntime : IAsyncDisposable
{
    int? OwnedProcessId { get; }

    int? SleepIdleSeconds { get; }

    Task<EgOneEndpoint?> EnsureReadyAsync(CancellationToken cancellationToken);

    /// <summary>Returns the live loopback endpoint only if the server is already running and ready; never cold-starts it.</summary>
    EgOneEndpoint? TryGetReadyEndpoint();

    Task<bool> TryPreWakeAsync(CancellationToken cancellationToken);

    void TerminateImmediately();
}

public sealed class EgOneServerManager : IEgOneRuntime
{
    private readonly EgOneServerOptions _options;
    private readonly HttpClient _healthClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Process? _process;
    private WindowsProcessJob? _processJob;
    private volatile EgOneEndpoint? _endpoint;
    private bool _hasReachedReady;
    private bool _restartUsed;
    private int _failedStarts;
    private volatile bool _disposed;
    private int _preWakeInFlight;
    private volatile bool _isUsingAccelerator;

    public EgOneServerManager(EgOneServerOptions options, HttpMessageHandler? healthHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServerExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ContextTokens, 1_024);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.EffectiveStartupTimeout, TimeSpan.Zero);
        if (options.SleepIdleSeconds is { } sleepSeconds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sleepSeconds, 0);
        }
        _options = options;
        _isUsingAccelerator = options.GpuLayers is > 0;
        _healthClient = healthHandler is null
            ? new HttpClient()
            : new HttpClient(healthHandler, disposeHandler: false);
    }

    public bool IsUsingAccelerator => _isUsingAccelerator;

    public int? SleepIdleSeconds => _options.SleepIdleSeconds is > 0 ? _options.SleepIdleSeconds : null;

    public int? OwnedProcessId
    {
        get
        {
            var process = _process;
            return process is { HasExited: false } ? process.Id : null;
        }
    }

    public async Task<EgOneEndpoint?> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_endpoint is not null && _process is { HasExited: false })
            {
                return _endpoint;
            }

            if (_failedStarts >= 2)
            {
                return null;
            }

            if (_hasReachedReady)
            {
                if (_restartUsed)
                {
                    return null;
                }

                _restartUsed = true;
            }

            await StopOwnedProcessAsync().ConfigureAwait(false);
            var endpoint = await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            if (endpoint is null)
            {
                _failedStarts++;
            }

            return endpoint;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> TryPreWakeAsync(CancellationToken cancellationToken)
    {
        if (_disposed ||
            _options.SleepIdleSeconds is not > 0 ||
            _endpoint is not { } endpoint ||
            _process is not { HasExited: false })
        {
            return Task.FromResult(false);
        }

        return SendPreWakeAsync(endpoint, cancellationToken);
    }

    internal async Task<bool> SendPreWakeAsync(EgOneEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (_disposed || Interlocked.CompareExchange(ref _preWakeInFlight, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.SlotsUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            using var response = await _healthClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TimeoutException
                or OperationCanceledException
                or ObjectDisposedException
                or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            Volatile.Write(ref _preWakeInFlight, 0);
        }
    }

    internal static IReadOnlyList<string> CreateArguments(
        EgOneServerOptions options,
        int port,
        string authToken)
    {
        var arguments = new List<string>
        {
            "--model", options.ModelFile,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "-c", options.ContextTokens.ToString(CultureInfo.InvariantCulture),
            "-np", "1",
            "--no-warmup",
            "--api-key", authToken,
            "-fa", "on",
            "--cache-type-k", "q8_0",
            "--cache-type-v", "q8_0",
        };
        if (options.GpuLayers is { } gpuLayers)
        {
            arguments.Add("--gpu-layers");
            arguments.Add(gpuLayers.ToString(CultureInfo.InvariantCulture));
        }

        if (options.Threads is { } threads && threads > 0)
        {
            arguments.Add("--threads");
            arguments.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        if (options.BatchThreads is { } batchThreads && batchThreads > 0)
        {
            arguments.Add("--threads-batch");
            arguments.Add(batchThreads.ToString(CultureInfo.InvariantCulture));
        }

        if (options.SleepIdleSeconds is { } sleepSeconds && sleepSeconds > 0)
        {
            arguments.Add("--sleep-idle-seconds");
            arguments.Add(sleepSeconds.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    internal static bool ShouldRetryOnCpuFallback(
        EgOneServerOptions options,
        EgOneEndpoint? gpuEndpoint,
        bool isCancellationRequested) =>
        gpuEndpoint is null &&
        !isCancellationRequested &&
        options.AllowCpuFallback &&
        options.GpuLayers is > 0;

    internal static TimeSpan ResolveInitialAttemptTimeout(EgOneServerOptions options)
    {
        var effective = options.EffectiveStartupTimeout;
        if (options.AllowCpuFallback && options.GpuLayers is > 0 && effective > TimeSpan.FromSeconds(30))
        {
            return TimeSpan.FromSeconds(30);
        }

        return effective;
    }

    private async Task<EgOneEndpoint?> StartCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ServerExecutable) || !File.Exists(_options.ModelFile))
        {
            return null;
        }

        var initialOptions = _options with { StartupTimeout = ResolveInitialAttemptTimeout(_options) };
        var endpoint = await StartWithOptionsAsync(initialOptions, cancellationToken).ConfigureAwait(false);
        if (ShouldRetryOnCpuFallback(_options, endpoint, cancellationToken.IsCancellationRequested))
        {
            var cpuOptions = _options with { GpuLayers = 0, AllowCpuFallback = false };
            endpoint = await StartWithOptionsAsync(cpuOptions, cancellationToken).ConfigureAwait(false);
        }

        return endpoint;
    }

    public EgOneEndpoint? TryGetReadyEndpoint()
    {
        if (_disposed)
        {
            return null;
        }

        var endpoint = _endpoint;
        var process = _process;
        if (endpoint is null || process is null)
        {
            return null;
        }

        try
        {
            return process.HasExited ? null : endpoint;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<EgOneEndpoint?> StartWithOptionsAsync(
        EgOneServerOptions attemptOptions,
        CancellationToken cancellationToken)
    {
        var port = FindFreePort();
        var authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo
        {
            FileName = attemptOptions.ServerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in CreateArguments(attemptOptions, port, authToken))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            var processJob = WindowsProcessJob.TryCreateFor(process);
            if (processJob is null)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                process.Dispose();
                return null;
            }

            _process = process;
            _processJob = processJob;
            var endpoint = new EgOneEndpoint(port, authToken, attemptOptions.ContextTokens);
            var ready = await AwaitReadyAsync(
                endpoint,
                attemptOptions.EffectiveStartupTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                await StopOwnedProcessAsync().ConfigureAwait(false);
                return null;
            }

            _endpoint = endpoint;
            _isUsingAccelerator = attemptOptions.GpuLayers is > 0;
            _hasReachedReady = true;
            return endpoint;
        }
        catch (OperationCanceledException)
        {
            await StopOwnedProcessAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidOperationException)
        {
            await StopOwnedProcessAsync().ConfigureAwait(false);
            return null;
        }
    }

    private async Task<bool> AwaitReadyAsync(
        EgOneEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
            {
                return false;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.HealthUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            try
            {
                using var response = await _healthClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TimeoutException or TaskCanceledException)
            {
                if (exception is TaskCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static int FindFreePort()
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port >= 8_082)
            {
                return port;
            }
        }
    }

    private async Task StopOwnedProcessAsync()
    {
        _endpoint = null;
        var process = Interlocked.Exchange(ref _process, null);
        var processJob = Interlocked.Exchange(ref _processJob, null);
        if (process is null)
        {
            processJob?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            process.Dispose();
            processJob?.Dispose();
        }
    }

    public void TerminateImmediately()
    {
        _endpoint = null;
        var process = Interlocked.Exchange(ref _process, null);
        var processJob = Interlocked.Exchange(ref _processJob, null);
        if (process is null)
        {
            processJob?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The exact owned process already exited between the state check and the kill.
        }
        finally
        {
            process.Dispose();
            processJob?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopOwnedProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _healthClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
