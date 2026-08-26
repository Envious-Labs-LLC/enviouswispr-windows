namespace EnviousWispr.Core.Diagnostics;

public enum AppFailureCategory
{
    None,
    AccessDenied,
    InvalidData,
    StorageUnavailable,
    AudioUnavailable,
    HotkeyConflict,
    HotkeyUnavailable,
    TargetUnavailable,
    AsrUnavailable,
    RuntimeProvider,
    RuntimeWorker,
    PostProcessing,
    LocalPolish,
    CloudPolish,
    Unknown,
}
