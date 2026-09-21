using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

/// <summary>What a stop established: the work is finished, or it is still running past the deadline and stays owned.</summary>
/// <remarks>
/// A TIMEOUT IS NOT A TERMINATION. A loop that did not finish inside the deadline is still running,
/// still holding whatever it holds, and the owner that started it keeps it - the token source is
/// not disposed, the task is not dropped, the engine under it is not stopped out from under it. A
/// later stop joins the same work again. The report is what lets a shutdown say honestly what it
/// left behind, rather than waiting without limit or pretending.
/// </remarks>
public enum StopOutcome
{
    /// <summary>The work finished; nothing of it is still running.</summary>
    Completed,

    /// <summary>The work was cancelled but had not finished when the deadline passed; it is still owned.</summary>
    StillRunning,
}

/// <summary>What stopping the background work around a recording established, owner by owner.</summary>
public sealed record BackgroundStopReport(StopOutcome Streaming, StopOutcome AutoStop, StopOutcome Preview)
{
    public static BackgroundStopReport AllCompleted { get; } =
        new(StopOutcome.Completed, StopOutcome.Completed, StopOutcome.Completed);

    /// <summary>Whether every owner finished.</summary>
    public bool Completed =>
        Streaming == StopOutcome.Completed && AutoStop == StopOutcome.Completed && Preview == StopOutcome.Completed;
}

/// <summary>Words for the preview screen, tagged with the dictation they belong to and whether their screen is still open.</summary>
/// <param name="IsCurrent">Asked at the draw: false once the preview that produced the frame has been closed.</param>
public sealed record LivePreviewFrame(DictationSessionId SessionId, string Text, Func<bool> IsCurrent);

/// <summary>What is left of a stop's deadline, phase by phase; null when the stop has no deadline.</summary>
internal sealed class StopBudget(TimeSpan? deadline, TimeProvider clock)
{
    private readonly long _started = clock.GetTimestamp();

    /// <summary>The time left; zero once the deadline has passed, null without a deadline.</summary>
    /// <remarks>
    /// NEVER INVENTED. A budget that has run out hands on zero, not a millisecond: a join given zero
    /// still observes - it reports a loop that has finished as finished and one that has not as still
    /// running - and takes no time doing it, so the phases of a stop together never exceed what the
    /// stop was given.
    /// </remarks>
    public TimeSpan? Remaining
    {
        get
        {
            if (deadline is not { } limit)
            {
                return null;
            }

            var remaining = limit - clock.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>The time left of a budget that has a deadline.</summary>
    public TimeSpan Left => Remaining ?? throw new InvalidOperationException("The stop has no deadline.");

    /// <summary>Takes the gate inside what is left of the budget; false when the budget ran out first.</summary>
    public async Task<bool> TryEnterAsync(SemaphoreSlim gate)
    {
        if (Remaining is not { } remaining)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            return true;
        }

        // A FREE GATE IS TAKEN WHATEVER IS LEFT: a budget already spent still lets the stop through
        // to observe; it only refuses to wait.
        if (gate.Wait(0))
        {
            return true;
        }

        if (remaining == TimeSpan.Zero)
        {
            return false;
        }

        using var patience = new CancellationTokenSource(remaining, clock);
        try
        {
            await gate.WaitAsync(patience.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Joins a loop that has been asked to stop: without limit, or for as long as a deadline allows.</summary>
internal static class BoundedJoin
{
    /// <summary>Waits for the work; a cancelled ending counts as completion, a deadline that passes first does not.</summary>
    public static async Task<StopOutcome> JoinAsync(Task work, TimeSpan? deadline, TimeProvider clock)
    {
        try
        {
            if (deadline is { } limit)
            {
                await work.WaitAsync(limit, clock).ConfigureAwait(false);
            }
            else
            {
                await work.ConfigureAwait(false);
            }

            return StopOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            return StopOutcome.Completed;
        }
        catch (TimeoutException)
        {
            return StopOutcome.StillRunning;
        }
    }
}
