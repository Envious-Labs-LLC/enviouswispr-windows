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
    Unknown,
}
