namespace EnviousWispr.Core.Diagnostics;

public enum AppFailureCategory
{
    None,
    AccessDenied,
    InvalidData,
    StorageUnavailable,
    Recovery,
    ResourcePressure,
    SystemLifecycle,
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
    TextDelivery,
    Observability,
    ModelDelivery,
    Unknown,
}
