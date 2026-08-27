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
    /// A polish result was refused as nonsense and the cleaned transcript was kept instead.
    /// </summary>
    /// <remarks>
    /// Distinct from the polish FAILING. A refused result is one the model returned confidently,
    /// so without this event it is indistinguishable in the log from polish that ran and chose to
    /// change nothing - and those want opposite responses.
    /// </remarks>
    PolishOutputRefused,

    /// <summary>The watcher ended a recording because the speaker had stopped.</summary>
    AutoStopTriggered,
    DictationCompleted,
    UnhandledFailure,
}
