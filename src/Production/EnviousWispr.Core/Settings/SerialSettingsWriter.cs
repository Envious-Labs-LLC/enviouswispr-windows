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
/// <summary>What a settings change produced, and what stopped it if anything did.</summary>
/// <param name="Failure">The exception that prevented the save, or null.</param>
/// <param name="Value">What the change worked out while it held the gate.</param>
public readonly record struct SettingsUpdateOutcome<T>(Exception? Failure, T Value);

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
        var outcome = await UpdateAsync<object?>(
            current => (change(current), null), cancellationToken).ConfigureAwait(false);
        return outcome.Failure;
    }

    /// <summary>Applies a change that also has something to say about what it did.</summary>
    /// <remarks>
    /// THE DECISION AND THE ANSWER BOTH BELONG INSIDE THE GATE. An import works out what it will add
    /// and what it conflicts with, and both of those are questions about the CURRENT words - so
    /// computing them outside means the plan describes a list that may already have changed, and
    /// saving that plan's result then overwrites whatever changed it. Returning the value lets the
    /// caller report what actually happened rather than what it expected to happen.
    /// </remarks>
    public async Task<SettingsUpdateOutcome<T>> UpdateAsync<T>(
        Func<AppSettings, (AppSettings Settings, T Value)> change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (next, value) = change(_current);
            await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _current = next;
            return new SettingsUpdateOutcome<T>(null, value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // THE STORED VALUE IS NOT ADVANCED ON A FAILURE, so the next change still derives from
            // what is really on disk rather than from something that was never written.
            return new SettingsUpdateOutcome<T>(exception, default!);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Waits for any save in progress, then stops accepting new ones.</summary>
    /// <remarks>
    /// AWAITED AT EXIT, NOT DISPOSED DURING TEARDOWN. Synchronous shutdown that disposes the gate
    /// makes an in-flight save release a disposed semaphore; abandoning it instead lets the process
    /// end mid-write. Taking the gate one last time is what proves nothing is still inside it.
    /// </remarks>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _disposed = true;
        _gate.Release();
        _gate.Dispose();
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
