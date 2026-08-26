namespace EnviousWispr.Core.Errors;

public enum AppErrorCode
{
    InvalidData,
    NewerSchema,
    StorageUnavailable,
    AccessDenied,
    Cancelled,
    InvalidTransition,
    AudioDeviceUnavailable,
    AudioDeviceLost,
    AudioFormatUnsupported,
    CaptureAlreadyActive,
    HotkeyInvalid,
    HotkeyConflict,
    HotkeyUnavailable,
    TargetUnavailable,
    HardwareProbeFailed,
    RuntimeProviderUnavailable,
    RuntimeProviderIncompatible,
    ModelPackUnavailable,
    RuntimeWorkerFailed,
    RuntimeResourceBusy,
}

public enum AppErrorStage
{
    Session,
    SettingsLoad,
    SettingsSave,
    SettingsReset,
    ProfileImport,
    ProfileExport,
    AudioDeviceEnumeration,
    AudioCapture,
    HotkeyConfiguration,
    HotkeyHook,
    TargetCapture,
    HardwareDiscovery,
    RuntimeSelection,
    RuntimeWorker,
    RuntimeResource,
}

public sealed record AppError(AppErrorCode Code, AppErrorStage Stage, bool CanRetry);
