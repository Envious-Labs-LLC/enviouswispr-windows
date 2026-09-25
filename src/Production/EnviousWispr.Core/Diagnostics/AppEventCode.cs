namespace EnviousWispr.Core.Diagnostics;

public enum AppEventCode
{
    ApplicationStarting,
    ApplicationRunRecovered,
    ApplicationHeartbeatFailed,

    /// <summary>
    /// The run state could not record whether a dictation was in flight.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM THE HEARTBEAT, because they fail for the same reason and mean different things.
    /// A missed heartbeat costs a timestamp. A missed dictation edge costs the ONE signal that
    /// separates "your words are gone" from "your computer restarted", so a run that logs this is a
    /// run whose lost-dictation warning cannot be trusted in either direction.
    /// </remarks>
    ApplicationRunStateEdgeFailed,
    ApplicationCleanShutdown,

    /// <summary>The shutdown ended with something still running - a command, a notification, a hold - and tore nothing down beside it.</summary>
    ApplicationShutdownUnclean,

    /// <summary>The exit budget ran out with work still outstanding; what it used was kept, and the host was told to end.</summary>
    ApplicationExitEscalated,
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
    /// A push-to-talk signal had to wait for the command ahead of it, or for an outside holder of the
    /// session gate, before it ran.
    /// </summary>
    /// <remarks>
    /// THE EVIDENCE THAT THE WINDOW WAS ENTERED. A key-up that lands while the press is still starting
    /// used to be discarded; it is now queued (#86, #148). A journey that wants to prove the queue did
    /// its job needs the app to say the signal actually waited, because from outside the process a
    /// quick tap that happened to land after the press finished looks identical to one that did not.
    /// </remarks>
    DictationSignalQueued,

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

    /// <summary>One deterministic cleanup stage reported what it did.</summary>
    /// <remarks>
    /// SEPARATE FROM THE SUMMARY PAIR ABOVE, which say only that the whole pass finished and
    /// how long it took. A pass that skipped all five stages and one that did five jobs
    /// quickly are the same line there, and "do custom words work" is exactly the question
    /// that difference answers. The pipeline has always produced a receipt per stage; this is
    /// where it stops being thrown away.
    /// </remarks>
    DeterministicStageObserved,
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

    /// <summary>Windows is shutting down, restarting, or logging the user off.</summary>
    SystemSessionEnding,
    AudioDevicesChanged,
    LivePreviewStarted,
    LivePreviewUpdated,
    LivePreviewStopped,
    LivePreviewFailed,
    /// <summary>The preview worker was still starting when the dictation ended; nothing was shown.</summary>
    LivePreviewStartupCancelled,

    /// <summary>The preview's worker refused its stop at the release and was ended by force, its exit seen, before the final transcription.</summary>
    LivePreviewAborted,
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

    /// <summary>A speech-model download began, from the bundled manifest.</summary>
    ModelDeliveryStarted,

    /// <summary>Every file arrived, matched its published hash, and the model was activated.</summary>
    ModelDeliveryCompleted,

    /// <summary>The download stopped short; the category says whether the network, the disk, or the manifest was the reason.</summary>
    ModelDeliveryFailed,

    // PASTE AND COPY LAST DICTATION (#206): one event per outcome, the same closed set as
    // LastDictationOutcome, so a reader can tell every ending apart without the words.

    /// <summary>Paste last dictation wrote the words into the window the person was in.</summary>
    LastDictationPasted,

    /// <summary>Copy last dictation put the words on the clipboard.</summary>
    LastDictationCopied,

    /// <summary>The target refused the paste and the clipboard caught the words; route and reason ride on the entry.</summary>
    LastDictationKeptOnClipboard,

    /// <summary>Nothing could be reused: history empty or off, or only a take the person cancelled.</summary>
    LastDictationNothingToReuse,

    /// <summary>Declined: a dictation was recording or being delivered, and owns the clipboard next.</summary>
    LastDictationDeclinedDictationInProgress,

    /// <summary>Declined: another reuse was still running.</summary>
    LastDictationDeclinedBusy,

    /// <summary>Declined: no window to paste into, or it had gone.</summary>
    LastDictationDeclinedNoTarget,

    /// <summary>Declined: the window in front was EnviousWispr's own.</summary>
    LastDictationDeclinedOwnWindow,

    /// <summary>The delivery wrote nothing and the clipboard did not catch the words.</summary>
    LastDictationReuseFailed,

    // TRANSCRIBE A FILE (#211): what happened to a file, never which file or what it said.

    /// <summary>A file transcription began.</summary>
    FileTranscriptionStarted,

    /// <summary>Every piece of the file was transcribed.</summary>
    FileTranscriptionCompleted,

    /// <summary>The file held no speech.</summary>
    FileTranscriptionEmpty,

    /// <summary>The person stopped it; the words so far were kept.</summary>
    FileTranscriptionCancelled,

    /// <summary>The file could not be read as audio, or a piece failed; any words so far were kept.</summary>
    FileTranscriptionFailed,

    /// <summary>A file was not started: a dictation or an update had the session.</summary>
    FileTranscriptionRefused,

    // OLLAMA MODELS (#213): what happened to a download or a removal - never which model, never where.

    /// <summary>A model download began.</summary>
    OllamaModelDownloadStarted,

    /// <summary>Ollama said the download succeeded.</summary>
    OllamaModelDownloaded,

    /// <summary>The person stopped a download.</summary>
    OllamaModelDownloadStopped,

    /// <summary>A download ended without success; the error code says how.</summary>
    OllamaModelDownloadFailed,

    /// <summary>A model was removed, or was already gone.</summary>
    OllamaModelRemoved,

    /// <summary>A removal was refused or went unanswered.</summary>
    OllamaModelRemoveFailed,

    UnhandledFailure,
}
