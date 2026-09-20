using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Diagnostics;

/// <summary>Which failure category a diagnostic line files an <see cref="AppError"/> under.</summary>
/// <remarks>
/// ONE TABLE, BECAUSE THE SHELL AND THE PIPELINE WRITE THE SAME LOG. This lived as a private method
/// on the app for as long as the app was the only thing that logged an error; the moment live preview
/// moved out of the shell, a second copy would have been the alternative, and two copies of a table
/// drift on the first edit that touches only one.
/// </remarks>
public static class AppFailureCategories
{
    public static AppFailureCategory For(AppError? error) => error?.Code switch
    {
        AppErrorCode.HotkeyConflict => AppFailureCategory.HotkeyConflict,
        AppErrorCode.HotkeyInvalid or AppErrorCode.HotkeyUnavailable =>
            AppFailureCategory.HotkeyUnavailable,
        AppErrorCode.TargetUnavailable => AppFailureCategory.TargetUnavailable,
        AppErrorCode.AccessDenied when error?.Stage == AppErrorStage.AudioCapture =>
            AppFailureCategory.AudioUnavailable,
        AppErrorCode.AudioDeviceUnavailable or AppErrorCode.AudioDeviceLost =>
            AppFailureCategory.AudioUnavailable,
        AppErrorCode.RuntimeProviderUnavailable or AppErrorCode.RuntimeProviderIncompatible =>
            AppFailureCategory.RuntimeProvider,
        AppErrorCode.RuntimeWorkerFailed => AppFailureCategory.RuntimeWorker,
        AppErrorCode.ModelPackUnavailable or AppErrorCode.TranscriptionFailed =>
            AppFailureCategory.AsrUnavailable,
        null => AppFailureCategory.None,
        _ => AppFailureCategory.Unknown,
    };
}
