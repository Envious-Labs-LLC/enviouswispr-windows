using System.Diagnostics.CodeAnalysis;

namespace EnviousWispr.Presentation;

/// <summary>
/// The gate every presentation operation enters before it touches a store, a provider or a device,
/// and the close that shuts it: nothing new admitted, everything inside told to stop, and one task
/// that completes when the last of them has left.
/// </summary>
/// <remarks>
/// WHAT THE EXIT DISPOSES MUST HAVE NOBODY INSIDE IT. A history load still reading when the history
/// store's semaphore is disposed faults the load; a microphone test still listening when the app
/// exits leaves the device open; a model discovery still out when the model source is disposed
/// pulls its client from under an HTTP call. The dictation session's quiescence says nothing about
/// any of these - they are the window's work, not the pipeline's - so the presentation keeps its
/// own count. An operation takes a lease on the way in and returns it on the way out; the close
/// refuses new leases, cancels the token every lease carries, and waits for the count to reach zero.
/// A close asked for again is the first close's task.
/// </remarks>
public sealed class PresentationAdmission : IDisposable
{
    private readonly object _lock = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _outstanding;
    private bool _closed;

    /// <summary>Cancelled by the close; every operation inside the gate runs under it.</summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>Whether the close has begun; nothing is admitted after it.</summary>
    public bool Closed
    {
        get
        {
            lock (_lock)
            {
                return _closed;
            }
        }
    }

    /// <summary>How many operations hold a lease right now.</summary>
    public int Outstanding
    {
        get
        {
            lock (_lock)
            {
                return _outstanding;
            }
        }
    }

    /// <summary>Takes a lease for one operation, or refuses because the close has begun.</summary>
    public bool TryEnter([NotNullWhen(true)] out Lease? lease)
    {
        lock (_lock)
        {
            if (_closed)
            {
                lease = null;
                return false;
            }

            _outstanding++;
            lease = new Lease(this);
            return true;
        }
    }

    /// <summary>Shuts the gate, cancels what is inside, and completes when the last lease is returned.</summary>
    public Task CloseAsync()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return _drained.Task;
            }

            _closed = true;
            if (_outstanding == 0)
            {
                _drained.TrySetResult();
            }
        }

        // CANCELLED OUTSIDE THE LOCK, ONCE: a registration runs on this thread and may return a lease.
        _closing.Cancel();
        return _drained.Task;
    }

    /// <summary>Releases the token source. Only once the close has completed; leases still out would find their token gone.</summary>
    public void Dispose() => _closing.Dispose();

    private void Leave()
    {
        lock (_lock)
        {
            _outstanding--;
            if (_closed && _outstanding == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>One operation's place inside the gate; returned by disposing it, once.</summary>
    public sealed class Lease : IDisposable
    {
        private PresentationAdmission? _owner;

        internal Lease(PresentationAdmission owner)
        {
            _owner = owner;
        }

        /// <summary>The gate's closing token, for the operation to run under.</summary>
        public CancellationToken Closing => (_owner ?? throw new ObjectDisposedException(nameof(Lease))).Closing;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}
