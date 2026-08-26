using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Services.Runtime;

public sealed class RuntimeResourceArbiter : IDisposable
{
    private readonly SemaphoreSlim _cpu = new(1, 1);
    private readonly SemaphoreSlim _accelerator = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<RuntimeResourceKind, RuntimeWorkloadKind> _owners = [];
    private bool _disposed;

    public IReadOnlyDictionary<RuntimeResourceKind, RuntimeWorkloadKind> ActiveOwners
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<RuntimeResourceKind, RuntimeWorkloadKind>(_owners);
            }
        }
    }

    public async Task<RuntimeResourceAcquireResult> AcquireAsync(
        RuntimeResourceKind resource,
        RuntimeWorkloadKind workload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var semaphore = SemaphoreFor(resource);
        if (!await semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return new RuntimeResourceAcquireResult(
                Succeeded: false,
                Error: new AppError(
                    AppErrorCode.RuntimeResourceBusy,
                    AppErrorStage.RuntimeResource,
                    CanRetry: true));
        }

        lock (_gate)
        {
            _owners[resource] = workload;
        }

        return new RuntimeResourceAcquireResult(
            Succeeded: true,
            new ResourceLease(this, semaphore, resource));
    }

    private SemaphoreSlim SemaphoreFor(RuntimeResourceKind resource) => resource switch
    {
        RuntimeResourceKind.Cpu => _cpu,
        RuntimeResourceKind.Accelerator => _accelerator,
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    };

    private void Release(SemaphoreSlim semaphore, RuntimeResourceKind resource)
    {
        lock (_gate)
        {
            _owners.Remove(resource);
        }

        semaphore.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cpu.Dispose();
        _accelerator.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class ResourceLease(
        RuntimeResourceArbiter owner,
        SemaphoreSlim semaphore,
        RuntimeResourceKind resource) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(semaphore, resource);
            }

            return ValueTask.CompletedTask;
        }
    }
}
