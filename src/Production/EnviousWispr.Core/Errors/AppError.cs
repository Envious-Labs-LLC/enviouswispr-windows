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
}

public sealed record AppError(AppErrorCode Code, AppErrorStage Stage, bool CanRetry);
