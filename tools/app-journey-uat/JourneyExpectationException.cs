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

    /// <summary>The HARNESS could not do its job; nothing here is a verdict about the app.</summary>
    /// <remarks>
    /// AN INSTRUMENT FAILURE REPORTED AS A PRODUCT VERDICT IS THE DEFECT CLASS THIS EXISTS TO STOP.
    /// On 2026-09-04 and 05 two sessions here concluded that injected input was inert on the
    /// development machine and built five explanations on top of it; the injector had been handed an
    /// all-zero struct and had never injected anything. The macOS harness carries the same lesson from
    /// 2026-08-10, when a resolver pressed a key nothing was listening for and the FAIL was believed.
    ///
    /// So a refusal to press, a key that could not be resolved, a payload that failed its own
    /// precondition, or a control window too short to mean anything ends the run with its own exit
    /// code and the words INSTRUMENT INVALID, and a runner can never average it into a pass rate.
    /// </remarks>
    public bool InstrumentInvalid { get; private init; }

    /// <summary>A reason the harness cannot proceed that says nothing about the product.</summary>
    public static JourneyExpectationException Instrument(string message) =>
        new(message) { InstrumentInvalid = true };
}
