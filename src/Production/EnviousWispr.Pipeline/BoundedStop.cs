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

    /// <summary>The time left, floored at one millisecond so a join past the deadline still observes rather than skips; null without a deadline.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (deadline is not { } limit)
            {
                return null;
            }

            var remaining = limit - clock.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        }
    }

    /// <summary>Takes the gate inside what is left of the budget; false when the budget ran out first.</summary>
    public async Task<bool> TryEnterAsync(SemaphoreSlim gate)
    {
        if (Remaining is not { } remaining)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            return true;
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
