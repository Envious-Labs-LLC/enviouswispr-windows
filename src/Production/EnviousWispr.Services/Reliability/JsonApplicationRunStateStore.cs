using System.Security;
using System.Text.Json;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Services.Reliability;

public sealed class JsonApplicationRunStateStore : IApplicationRunStateStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumStateBytes = 16 * 1024;

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RunStateFile? _current;
    private bool _disposed;

    public JsonApplicationRunStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<ApplicationRunStartResult> BeginRunAsync(
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var runId = Guid.NewGuid();
            var status = RunStateLoadStatus.Started;
            var consecutiveInterruptedRuns = 0;
            var previousRunWasDictating = false;

            if (File.Exists(_path))
            {
                RunStateFile? previous;
                try
                {
                    var info = new FileInfo(_path);
                    if (info.Length is <= 0 or > MaximumStateBytes)
                    {
                        previous = null;
                    }
                    else
                    {
                        var json = await File.ReadAllTextAsync(_path, cancellationToken)
                            .ConfigureAwait(false);
                        previous = JsonSerializer.Deserialize<RunStateFile>(
                            json,
                            JsonSettingsStore.SerializerOptions);
                    }
                }
                catch (JsonException)
                {
                    previous = null;
                }

                if (!IsValid(previous))
                {
                    if (!TryPreserveInvalidSource())
                    {
                        return Unavailable(runId, AppErrorCode.StorageUnavailable);
                    }

                    status = RunStateLoadStatus.InvalidStateRecovered;
                    consecutiveInterruptedRuns = 1;
                }
                else if (!previous!.CleanShutdown)
                {
                    status = RunStateLoadStatus.PreviousRunInterrupted;
                    consecutiveInterruptedRuns = Math.Min(
                        int.MaxValue,
                        previous.ConsecutiveInterruptedRuns + 1);
                    previousRunWasDictating = previous.DictationActive;
                }
            }

            _current = new RunStateFile(
                CurrentSchemaVersion,
                runId,
                timestamp,
                timestamp,
                CleanShutdown: false,
                consecutiveInterruptedRuns,
                DictationActive: false);
            await JsonSettingsStore.WriteAtomicallyAsync(_current, _path, cancellationToken)
                .ConfigureAwait(false);
            return new ApplicationRunStartResult(
                runId,
                status,
                previousRunWasDictating,
                status == RunStateLoadStatus.Started
                    ? null
                    : new AppError(
                        AppErrorCode.PreviousRunInterrupted,
                        AppErrorStage.RunState,
                        CanRetry: true));
        }
        catch (IOException)
        {
            return Unavailable(Guid.NewGuid(), AppErrorCode.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(Guid.NewGuid(), AppErrorCode.AccessDenied);
        }
        catch (SecurityException)
        {
            return Unavailable(Guid.NewGuid(), AppErrorCode.AccessDenied);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> HeartbeatAsync(
        Guid runId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(runId, timestamp, cleanShutdown: false, dictationActive: null, cancellationToken);

    public Task<bool> SetDictationActiveAsync(
        Guid runId,
        bool active,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(runId, timestamp, cleanShutdown: false, dictationActive: active, cancellationToken);

    public Task<bool> CompleteRunAsync(
        Guid runId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(runId, timestamp, cleanShutdown: true, dictationActive: false, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task<bool> UpdateAsync(
        Guid runId,
        DateTimeOffset timestamp,
        bool cleanShutdown,
        bool? dictationActive,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current?.RunId != runId || _current.CleanShutdown)
            {
                return false;
            }

            // NULL MEANS LEAVE IT ALONE. A heartbeat says nothing about whether somebody is
            // dictating, and writing false there would clear the flag a second after a dictation set
            // it - which is the whole signal, gone, on a timer.
            var next = _current with
            {
                LastHeartbeatAt = timestamp,
                CleanShutdown = cleanShutdown,
                DictationActive = dictationActive ?? _current.DictationActive,
            };
            await JsonSettingsStore.WriteAtomicallyAsync(next, _path, cancellationToken)
                .ConfigureAwait(false);
            _current = next;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryPreserveInvalidSource()
    {
        try
        {
            File.Copy(_path, _path + ".previous", overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    private static bool IsValid(RunStateFile? state) => state is
    {
        SchemaVersion: CurrentSchemaVersion,
        ConsecutiveInterruptedRuns: >= 0,
    } &&
        state.RunId != Guid.Empty &&
        state.StartedAt != default &&
        state.LastHeartbeatAt >= state.StartedAt;

    private static ApplicationRunStartResult Unavailable(Guid runId, AppErrorCode code) => new(
        runId,
        RunStateLoadStatus.Unavailable,
        PreviousRunWasDictating: false,
        new AppError(code, AppErrorStage.RunState, CanRetry: true));

    private sealed record RunStateFile(
        int SchemaVersion,
        Guid RunId,
        DateTimeOffset StartedAt,
        DateTimeOffset LastHeartbeatAt,
        bool CleanShutdown,
        int ConsecutiveInterruptedRuns,
        // DEFAULTED, SO A FILE WRITTEN BY AN OLDER BUILD STILL READS. It deserialises to false,
        // which is the honest answer: that build never recorded whether a dictation was running.
        bool DictationActive = false);
}
