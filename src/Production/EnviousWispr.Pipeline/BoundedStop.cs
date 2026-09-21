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
