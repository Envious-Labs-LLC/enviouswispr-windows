using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace EnviousWispr.Services.Runtime;

public sealed class RuntimeWorkerSupervisor : IRuntimeWorkerSupervisor
{
    private const int ProtocolVersion = 1;

    private readonly string _workerExecutable;
    private readonly string[] _workerArguments;
    private readonly int _maximumRestarts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task<string>? _stderrDrain;
    private int _restartCount;
    private bool _disposed;

    public RuntimeWorkerSupervisor(
        string workerExecutable,
        IEnumerable<string>? workerArguments = null,
        int maximumRestarts = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutable);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRestarts);
        _workerExecutable = Path.GetFullPath(workerExecutable);
        _workerArguments = workerArguments?.ToArray() ?? [];
        _maximumRestarts = maximumRestarts;
    }

    public RuntimeWorkerState State { get; private set; } = RuntimeWorkerState.Stopped;

    public int? WorkerProcessId => _process is { HasExited: false } process ? process.Id : null;

    public async Task<RuntimeWorkerResult> StartAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false } && State == RuntimeWorkerState.Ready)
            {
                return Success(RuntimeWorkerState.Ready);
            }

            _restartCount = 0;
            return await StartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeWorkerResult> CheckHealthAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await CheckHealthCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeWorkerResult> EnsureHealthyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var health = await CheckHealthCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (health.Succeeded)
            {
                return health;
            }

            if (_restartCount >= _maximumRestarts)
            {
                return Failure();
            }

            _restartCount++;
            return await StartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<RuntimeWorkerResponse?> TranscribeAsync(
        RuntimeWorkerTranscriptionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeout(timeout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is null || _process.HasExited || State is not RuntimeWorkerState.Ready)
            {
                var start = await StartCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (!start.Succeeded)
                {
                    return null;
                }
            }

            var response = await SendRawRequestAsync(
                "transcribe",
                request,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                await StopCoreAsync().ConfigureAwait(false);
                State = RuntimeWorkerState.Faulted;
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = RuntimeWorkerState.Faulted;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return Success(RuntimeWorkerState.Disposed);
            }

            await StopCoreAsync().ConfigureAwait(false);
            State = RuntimeWorkerState.Stopped;
            return Success(State);
        }
        finally
        {
            _gate.Release();
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

            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
            State = RuntimeWorkerState.Disposed;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<RuntimeWorkerResult> StartCoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await StopCoreAsync().ConfigureAwait(false);
        State = RuntimeWorkerState.Starting;
        if (!File.Exists(_workerExecutable))
        {
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _workerExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in _workerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                State = RuntimeWorkerState.Faulted;
                return Failure();
            }

            _process = process;
            _stderrDrain = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var health = await SendRequestAsync("health", timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!health.Succeeded)
            {
                await StopCoreAsync().ConfigureAwait(false);
                State = RuntimeWorkerState.Faulted;
                return Failure();
            }

            State = RuntimeWorkerState.Ready;
            return Success(State);
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidOperationException)
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }
    }

    private async Task<RuntimeWorkerResult> CheckHealthCoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited || State is not RuntimeWorkerState.Ready)
        {
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }

        var result = await SendRequestAsync("health", timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = RuntimeWorkerState.Faulted;
            return Failure();
        }

        State = RuntimeWorkerState.Ready;
        return Success(State);
    }

    private async Task<RuntimeWorkerResult> SendRequestAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var response = await SendRawRequestAsync(
            command,
            transcription: null,
            timeout,
            cancellationToken).ConfigureAwait(false);
        var expectedStatus = command == "shutdown" ? "stopping" : "ready";
        return response is not null &&
            string.Equals(response.Status, expectedStatus, StringComparison.Ordinal)
            ? Success(State)
            : Failure();
    }

    private async Task<RuntimeWorkerResponse?> SendRawRequestAsync(
        string command,
        RuntimeWorkerTranscriptionRequest? transcription,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return null;
        }

        var requestId = Guid.NewGuid();
        var request = new RuntimeWorkerRequest(ProtocolVersion, requestId, command, transcription);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request))
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<RuntimeWorkerResponse>(line);
            return response is not null &&
                response.ProtocolVersion == ProtocolVersion &&
                response.RequestId == requestId
                ? response
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            return null;
        }
    }

    private async Task StopCoreAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await TryRequestShutdownAsync(process).ConfigureAwait(false);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
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
            if (_stderrDrain is not null)
            {
                try
                {
                    await _stderrDrain.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or OperationCanceledException or TimeoutException)
                {
                    // Stderr is intentionally discarded and cannot block teardown.
                }

                _stderrDrain = null;
            }
        }
    }

    private static async Task TryRequestShutdownAsync(Process process)
    {
        var request = new RuntimeWorkerRequest(ProtocolVersion, Guid.NewGuid(), "shutdown");
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request))
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or TimeoutException)
        {
            // The worker is already gone or wedged; process-tree kill is the fallback.
        }
    }

    private static void ValidateTimeout(TimeSpan timeout) =>
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

    private static RuntimeWorkerResult Success(RuntimeWorkerState state) => new(true, state);

    private static RuntimeWorkerResult Failure() => new(
        Succeeded: false,
        RuntimeWorkerState.Faulted,
        new AppError(
            AppErrorCode.RuntimeWorkerFailed,
            AppErrorStage.RuntimeWorker,
            CanRetry: true));

}
