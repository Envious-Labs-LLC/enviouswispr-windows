namespace EnviousWispr.Pipeline;

/// <summary>What the session's teardown established, owner by owner.</summary>
public sealed record SessionTeardownReport(StopOutcome Watchdog, BackgroundStopReport Background)
{
    /// <summary>A teardown with nothing to tear down: an executor that owns no session.</summary>
    public static SessionTeardownReport Nothing { get; } = new(StopOutcome.Completed, BackgroundStopReport.AllCompleted);

    /// <summary>Whether every owner finished inside its deadline.</summary>
    public bool Completed => Watchdog == StopOutcome.Completed && Background.Completed;
}

/// <summary>How the shutdown ended.</summary>
public enum ShutdownOutcome
{
    /// <summary>Nothing was running, nothing held the session, and the teardown ran under it.</summary>
    Quiescent,

    /// <summary>Something outlived the budget: a command, an expiry notification or a hold. The teardown did not run beside it.</summary>
    Unclean,
}

/// <summary>What the shutdown established.</summary>
/// <remarks>
/// TEARDOWN NEVER RUNS BESIDE A RESOURCE USER. Cancellation is not quiescence: a command asked to
/// stop is still running until it says it has stopped, and a hold is still held until it is given
/// back. The shutdown closes admission at once, cancels what can be cancelled, waits its budget for
/// completion, and tears the session down only once nothing is using it. What did not finish is
/// named here rather than disposed under; the shell decides what to do with an unclean end.
/// </remarks>
/// <param name="Outcome">Quiescent or unclean.</param>
/// <param name="CommandOutstanding">A command was still running when the budget ran out.</param>
/// <param name="ExpiriesOutstanding">An interruption's expiry notification was still in flight, or one threw.</param>
/// <param name="HoldsOutstanding">How many holds on the session were still out.</param>
/// <param name="Teardown">What the teardown established, or null when it did not run.</param>
public sealed record ShutdownReport(
    ShutdownOutcome Outcome,
    bool CommandOutstanding,
    bool ExpiriesOutstanding,
    int HoldsOutstanding,
    SessionTeardownReport? Teardown)
{
    /// <summary>Quiescent, no notification faulted, and a teardown in which every owner finished.</summary>
    public bool Clean => Outcome == ShutdownOutcome.Quiescent && !ExpiriesOutstanding && Teardown is { Completed: true };
}
