namespace EnviousWispr.Core.Errors;

public enum AppErrorCode
{
    InvalidData,
    NewerSchema,
    StorageUnavailable,
    AccessDenied,
    Cancelled,
    InvalidTransition,
}

public enum AppErrorStage
{
    Session,
    SettingsLoad,
    SettingsSave,
    SettingsReset,
    ProfileImport,
    ProfileExport,
}

public sealed record AppError(AppErrorCode Code, AppErrorStage Stage, bool CanRetry);
