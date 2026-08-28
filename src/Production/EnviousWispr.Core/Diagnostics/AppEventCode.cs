namespace EnviousWispr.Core.Diagnostics;

public enum AppEventCode
{
    ApplicationStarting,
    ApplicationRunRecovered,
    ApplicationHeartbeatFailed,
    ApplicationCleanShutdown,
    DuplicateInstanceRejected,
    DuplicateInstanceActivated,
    SettingsLoaded,
    SettingsCreated,
    SettingsMigrated,
    SettingsRecovered,
    SettingsReset,
    SettingsNewerVersionPreserved,
    ShellShown,
    ShellClosed,
    HotkeyReady,
    HotkeyFailed,
    QuickAddRequested,
    QuickAddPrepared,
    DictationRecordingStarted,

    /// <summary>
    /// How a key press split between opening the microphone and starting the stream.
    /// </summary>
    /// <remarks>
    /// EMITTED TO DECIDE WHETHER HOLDING THE DEVICE OPEN BETWEEN DICTATIONS IS WORTH BUILDING. That
    /// idea only helps if OPENING is the slow half, and nobody has measured which half is slow. If
    /// opening turns out to be cheap, the feature buys nothing and the privacy question it raises -
    /// whether an open microphone lights the Windows in-use indicator - never has to be asked.
    ///
    /// TWO CODES RATHER THAN ONE EMITTED TWICE. The same name on consecutive lines with two numbers
    /// is a puzzle for whoever reads the log, and the order is the only thing distinguishing them -
    /// which is exactly the sort of detail that survives until someone reverses it.
    /// </remarks>
    CaptureDeviceOpened,

    /// <summary>How long the stream took to start, after the device was already open.</summary>
    /// <remarks>The half that warming CANNOT remove, and therefore the floor on a key press.</remarks>
    CaptureStreamStarted,
    DictationCaptureFinalized,
    DictationTranscriptionStarted,
    DictationTranscriptionCompleted,
    DictationTranscriptionDegraded,
    DictationTranscriptionFailed,
    DeterministicProcessingStarted,
    DeterministicProcessingCompleted,
    DeterministicProcessingDegraded,
    PolishStarted,
    PolishCompleted,
    PolishDegraded,
    PolishRuntimeStarted,
    PolishRuntimeReady,
    PolishRuntimeDegraded,
    TextDeliveryStarted,
    TextDeliveryCompleted,
    TextDeliveryClipboardFallback,
    TextDeliveryRefused,
    TextDeliveryFailed,
    DictationCancelled,
    DictationSessionFailed,
    DictationSessionRecovered,
    RecoveryTextSaved,
    RecoveryTextCleared,
    RecoveryTextUnavailable,
    ResourcePressureDetected,
    SystemSuspending,
    SystemResumed,
    SessionLocked,
    SessionUnlocked,
    AudioDevicesChanged,
    LivePreviewStarted,
    LivePreviewUpdated,
    LivePreviewStopped,
    LivePreviewFailed,
    RuntimeSelectionObserved,
    DiagnosticsExported,
    DiagnosticsExportFailed,
    TelemetryConsentEnabled,
    TelemetryConsentDisabled,
    /// <summary>
    /// One dictation, from the moment the user stopped speaking to the moment their text existed
    /// somewhere they could use it. Its ElapsedMilliseconds is the ONLY number that answers "how
    /// long did I wait".
    /// </summary>
    /// <remarks>
    /// Every stage already logged its own elapsed time and none of them answered that question.
    /// Four numbers in four lines cannot be added up afterwards: nothing says which dictation each
    /// belongs to, and a sum silently reports zero for everything BETWEEN the stages, which is
    /// where an unexplained wait would hide. This is measured by one stopwatch spanning the path.
    /// </remarks>
    /// <summary>
    /// Quick Add ran and the app had nothing selected.
    /// </summary>
    /// <remarks>
    /// Split from QuickAddPrepared because the two shared one event and the log could not answer
    /// the only question a support case asks: did the user get their word. The MESSAGE distinguished
    /// them from the first version; the log did not, which is the half nobody sees until they need
    /// it. Found by measuring the log rather than the screen.
    /// </remarks>
    QuickAddSelectionEmpty,

    /// <summary>
    /// Quick Add declined to borrow the clipboard, because the app was busy.
    /// </summary>
    /// <remarks>
    /// Distinct from QuickAddPrepared with nothing found. A refusal is a DECISION and an empty
    /// selection is a FACT about the other app, and they have different fixes - one is "wait a
    /// moment", the other is "select something". A single event for both would make them
    /// indistinguishable in the one place anyone would look afterwards.
    /// </remarks>
    QuickAddRefused,

    /// <summary>
    /// A polish result was refused as nonsense and the cleaned transcript was kept instead.
    /// </summary>
    /// <remarks>
    /// Distinct from the polish FAILING. A refused result is one the model returned confidently,
    /// so without this event it is indistinguishable in the log from polish that ran and chose to
    /// change nothing - and those want opposite responses.
    /// </remarks>
    PolishOutputRefused,

    /// <summary>
    /// A release used text recognised while the user was still speaking, and transcribed only the
    /// tail.
    /// </summary>
    /// <remarks>
    /// The event that says streaming actually PAID. Segments being committed says the loop ran;
    /// only this says the release was shorter for it, and the two can differ - every commit can
    /// succeed and the head start still be refused at the last check.
    /// </remarks>
    StreamingHeadStartUsed,

    /// <summary>A stretch of a running recording was transcribed before the user finished.</summary>
    StreamingSegmentCommitted,

    /// <summary>
    /// Streaming gave up its head start, and the release will transcribe the whole recording.
    /// </summary>
    /// <remarks>
    /// Never a user-visible failure - the dictation completes exactly as it did before streaming
    /// existed. It is logged because a run of these is the difference between a feature that is
    /// helping and one that is silently costing the machine work for nothing.
    /// </remarks>
    StreamingAbandoned,

    /// <summary>The watcher ended a recording because the speaker had stopped.</summary>
    AutoStopTriggered,
    DictationCompleted,
    UnhandledFailure,
}
