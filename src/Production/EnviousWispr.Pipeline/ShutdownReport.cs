namespace EnviousWispr.Pipeline;

/// <summary>What the session's teardown established, owner by owner, then the session's disposal.</summary>
/// <remarks>
/// THE DISPOSAL RUNS ONLY BEHIND FINISHED OWNERS. The capture, the controller and the delivery route
/// are what the background loops and the finalisation use; a loop still running past its deadline
/// still uses them, so the disposal is not run and <see cref="Disposal"/> is null. Run, it is the
/// executor's sequence - the shell's observers off the capture, the controller and its capture, the
/// shell's references, the delivery route - joined under what is left of the same deadline and
/// reported like the owners are; a step that threw is over, not outstanding, and is reported apart.
/// </remarks>
/// <param name="Watchdog">The recording watchdog's stop.</param>
/// <param name="Background">The streaming, auto-stop and preview stops.</param>
/// <param name="Disposal">The session's disposal; null when it was not run because an owner had not finished.</param>
/// <param name="DisposalFaulted">A step of the disposal threw; the steps after it still ran.</param>
public sealed record SessionTeardownReport(
    StopOutcome Watchdog,
    BackgroundStopReport Background,
    StopOutcome? Disposal,
    bool DisposalFaulted = false)
{
    /// <summary>A teardown with nothing to tear down: an executor that owns no session.</summary>
    public static SessionTeardownReport Nothing { get; } =
        new(StopOutcome.Completed, BackgroundStopReport.AllCompleted, StopOutcome.Completed);

    /// <summary>Whether every owner finished inside the deadline and the disposal ran and finished inside it too.</summary>
    public bool Completed =>
        Watchdog == StopOutcome.Completed && Background.Completed && Disposal == StopOutcome.Completed;
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
/// named here rather than disposed under; the shell decides what to do with an unclean end, and
/// <see cref="SessionQuiescent"/> is the question it asks before it disposes anything the session
/// uses.
/// </remarks>
/// <param name="Outcome">Quiescent or unclean.</param>
/// <param name="CommandOutstanding">A command was still running when the budget ran out.</param>
/// <param name="ExpiriesOutstanding">An interruption's expiry notification was still in flight when the budget ran out.</param>
/// <param name="ExpiryFaulted">An expiry notification threw; it is over, but the shell's status line faulted.</param>
/// <param name="HoldsOutstanding">How many holds on the session were still out.</param>
/// <param name="Teardown">What the teardown established, or null when it did not run.</param>
public sealed record ShutdownReport(
    ShutdownOutcome Outcome,
    bool CommandOutstanding,
    bool ExpiriesOutstanding,
    bool ExpiryFaulted,
    int HoldsOutstanding,
    SessionTeardownReport? Teardown)
{
    /// <summary>Nothing is using the session any more: quiescent, and every owner and the session's disposal finished. What the session used may be disposed.</summary>
    public bool SessionQuiescent => Outcome == ShutdownOutcome.Quiescent && Teardown is { Completed: true };

    /// <summary>Session quiescent, no notification faulted, and no step of the disposal threw.</summary>
    public bool Clean => SessionQuiescent && !ExpiryFaulted && Teardown is { DisposalFaulted: false };
}
