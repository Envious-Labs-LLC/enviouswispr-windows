namespace EnviousWispr.Core.Reliability;

/// <summary>
/// The commit protocol between a publication and the exit that may abandon it: the publication
/// commits under the fence, the exit abandons under the fence, and whichever comes first is the one
/// that happened - never both.
/// </summary>
/// <remarks>
/// A BOUNDED WAIT IS NOT A FENCE. An exit that stops waiting for a write has not stopped the write:
/// the temporary file is already on disk, and the replacement that makes it the record can land
/// after the exit has reported the completion outstanding - a clean run published by an exit that
/// was not clean. So the write's last act, the replacement, is asked for through <see cref="TryCommit"/>,
/// which refuses once the exit has abandoned; and the exit's abandonment is asked for through
/// <see cref="TryAbandon"/>, which refuses once the replacement has happened - so the exit knows,
/// on either answer, exactly what the next launch will read.
/// </remarks>
public sealed class PublicationFence
{
    private readonly object _lock = new();
    private bool _committed;
    private bool _abandoned;

    /// <summary>The instant before the commit is asked for: a seam for a test to stand in with the exit deciding meanwhile. Nothing in production sets it.</summary>
    internal Action? BeforeCommit { get; set; }

    /// <summary>The instant after the commit ran and the fence let go: a seam for a test to hold the writer's tail with the exit deciding meanwhile. Nothing in production sets it.</summary>
    internal Action? AfterCommit { get; set; }

    /// <summary>Whether the publication committed.</summary>
    public bool Committed
    {
        get
        {
            lock (_lock)
            {
                return _committed;
            }
        }
    }

    /// <summary>Runs the commit unless the publication has been abandoned; true when it ran.</summary>
    public bool TryCommit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        BeforeCommit?.Invoke();
        lock (_lock)
        {
            if (_abandoned || _committed)
            {
                return false;
            }

            commit();
            _committed = true;
        }

        AfterCommit?.Invoke();
        return true;
    }

    /// <summary>Abandons the publication unless it has committed; true when it was abandoned, false when the commit had already happened.</summary>
    public bool TryAbandon()
    {
        lock (_lock)
        {
            if (_committed)
            {
                return false;
            }

            _abandoned = true;
            return true;
        }
    }
}
