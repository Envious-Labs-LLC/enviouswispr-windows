namespace EnviousWispr.Services.Input;

/// <summary>
/// Shared asynchronous clipboard ownership arbiter preventing background post-paste cleanup
/// from racing with QuickAdd selection copies, explicit Copy operations, or subsequent deliveries.
/// </summary>
public sealed class ClipboardBoardOwnership : IDisposable
{
    private readonly SemaphoreSlim _boardGate = new(1, 1);
    private readonly object _preemptSync = new();
    private Func<Task>? _activePreempt;
    private long _generation;

    public static ClipboardBoardOwnership Shared { get; } = new();

    public void Dispose() => _boardGate.Dispose();

    public long CurrentGeneration => Volatile.Read(ref _generation);

    public bool IsSuperseded(long registeredGeneration) =>
        Volatile.Read(ref _generation) != registeredGeneration;

    /// <summary>
    /// Registers an active background cleanup preemption callback and returns the takeover generation
    /// token under which it remains valid to touch the clipboard.
    /// </summary>
    internal long RegisterCleanup(Func<Task> preemptCallback)
    {
        ArgumentNullException.ThrowIfNull(preemptCallback);
        lock (_preemptSync)
        {
            _activePreempt = preemptCallback;
            return Volatile.Read(ref _generation);
        }
    }

    internal void ClearCleanup(long generation, Func<Task> preemptCallback)
    {
        lock (_preemptSync)
        {
            if (ReferenceEquals(_activePreempt, preemptCallback) &&
                Volatile.Read(ref _generation) == generation)
            {
                _activePreempt = null;
            }
        }
    }

    /// <summary>
    /// Preempts any pending background post-paste cleanup and acquires exclusive clipboard ownership
    /// across the caller's asynchronous clipboard operation (e.g., QuickAdd 400 ms Copy window or Paste).
    /// </summary>
    public async Task<IAsyncDisposable> BeginTakeoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Func<Task>? preempt;
        lock (_preemptSync)
        {
            Interlocked.Increment(ref _generation);
            preempt = _activePreempt;
            _activePreempt = null;
        }

        if (preempt is not null)
        {
            try
            {
                await preempt().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
            }
        }

        await _boardGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    /// <summary>
    /// Acquires a short exclusive slot for background restore or unmarked rewrite if the takeover
    /// generation has not been superseded since the paste was dispatched.
    /// </summary>
    internal async Task<IAsyncDisposable?> TryBeginRestoreAsync(
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        if (IsSuperseded(expectedGeneration))
        {
            return null;
        }

        await _boardGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (IsSuperseded(expectedGeneration))
        {
            _boardGate.Release();
            return null;
        }

        return new Lease(this);
    }

    private sealed class Lease(ClipboardBoardOwnership owner) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._boardGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
