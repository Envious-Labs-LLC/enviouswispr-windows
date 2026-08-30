namespace EnviousWispr.Core.Settings;

/// <summary>Applies settings changes one at a time, each built on what is actually stored.</summary>
/// <remarks>
/// TWO WRITERS EACH SAVING A COMPLETE RECORD IS HOW A CHANGE DISAPPEARS. Every writer in the window
/// built a whole AppSettings and then saved it, so two overlapping saves produced two complete
/// snapshots and whichever finished last silently discarded the other. Saving atomically never
/// helped: each save was atomic AND complete, so it replaced everything the other had just written.
///
/// THE RULE IS TO DERIVE INSIDE THE GATE, WHICH IS WHY THIS TAKES A FUNCTION. A record built before
/// the wait is stale by the time the wait ends, and writing it back is exactly the loss being
/// prevented - so the change is expressed as "what to do to whatever is current" and evaluated once
/// the gate is held.
///
/// IT LIVES HERE RATHER THAN IN THE WINDOW SO THE PROPERTY CAN BE PROVEN. The defect only appears
/// when two saves overlap, which never happens in a test that drives a window one call at a time.
/// </remarks>
public sealed class SerialSettingsWriter : IDisposable
{
    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;
    private bool _disposed;

    public SerialSettingsWriter(ISettingsStore store, AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(current);
        _store = store;
        _current = current;
    }

    /// <summary>The settings as last successfully written.</summary>
    public AppSettings Current => _current;

    /// <summary>Applies a change and saves it, or reports the failure and leaves everything alone.</summary>
    /// <returns>Null when it worked, or the exception that stopped it.</returns>
    public async Task<Exception?> UpdateAsync(
        Func<AppSettings, AppSettings> change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = change(_current);
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _current = next;
            return null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // THE STORED VALUE IS NOT ADVANCED ON A FAILURE, so the next change still derives from
            // what is really on disk rather than from something that was never written.
            return exception;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
