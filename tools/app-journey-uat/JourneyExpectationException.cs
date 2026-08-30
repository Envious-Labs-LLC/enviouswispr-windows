namespace EnviousWispr.AppJourney.Uat;

/// <summary>The journey did not go the way this harness expected.</summary>
/// <remarks>
/// ITS OWN TYPE, SO A FAILING TEST STOPS LOOKING LIKE A CRASHING MACHINE. Every expectation in this
/// harness used to throw <see cref="InvalidOperationException"/> or <see cref="TimeoutException"/>
/// and nothing caught them, so a failed expectation terminated the process on an unhandled
/// exception - which Windows records in the event log exactly as it records an application fault.
///
/// **MEASURED COST, WHICH IS WHY THIS IS NOT TIDINESS.** On 2026-08-28 eleven entries from this
/// harness were read as evidence that the development machine was unstable, and were counted
/// alongside real hypervisor faults while somebody worked out whether the hardware was failing. They
/// were failing tests.
///
/// The distinction is the whole point of the type: this is caught and reported, and anything else -
/// a null reference, an out-of-memory, a genuine fault in the app under test - still propagates and
/// still looks like the crash it is.
///
/// IT COVERS EVERY REASON THIS HARNESS CAN NAME, not only assertions about the app. A missing window,
/// speech synthesis being unavailable, a refusal to delete a directory it does not recognise: all of
/// them are the harness stopping and able to say why, and none is a runtime fault. Converting only
/// the obvious assertions left the rest crashing, which is how the first attempt at this was found
/// wanting - a failing run still terminated, from a helper defined outside the block that had been
/// converted.
/// </remarks>
internal sealed class JourneyExpectationException : Exception
{
    public JourneyExpectationException(string message)
        : base(message)
    {
    }

    /// <summary>Carries the underlying failure, where one caused this.</summary>
    /// <remarks>
    /// SEVERAL CALL SITES ALREADY HAD AN INNER EXCEPTION AND WOULD HAVE LOST IT. A clipboard or
    /// playback failure is explained by the exception underneath it, and a harness that reports
    /// "the fixture could not be played" without saying what Windows said is a harness that sends
    /// somebody looking with nothing to look at.
    /// </remarks>
    public JourneyExpectationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
