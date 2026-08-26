using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Reliability;

public static class SystemResourceAdmissionPolicy
{
    public const long MinimumRecoveryDiskBytes = 64L * 1024 * 1024;
    public const ulong MinimumDictationMemoryBytes = 384UL * 1024 * 1024;

    public static DictationAdmissionResult Evaluate(SystemResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsAvailable)
        {
            return new DictationAdmissionResult(
                DictationAdmissionStatus.Unavailable,
                CanStart: true,
                CanPersistRecovery: true,
                new AppError(
                    AppErrorCode.StorageUnavailable,
                    AppErrorStage.ResourceHealth,
                    CanRetry: true));
        }

        if (snapshot.AvailablePhysicalMemoryBytes < MinimumDictationMemoryBytes)
        {
            return new DictationAdmissionResult(
                DictationAdmissionStatus.LowMemory,
                CanStart: false,
                CanPersistRecovery: snapshot.AvailableDiskBytes >= MinimumRecoveryDiskBytes,
                new AppError(
                    AppErrorCode.LowMemory,
                    AppErrorStage.ResourceHealth,
                    CanRetry: true));
        }

        if (snapshot.AvailableDiskBytes < MinimumRecoveryDiskBytes)
        {
            return new DictationAdmissionResult(
                DictationAdmissionStatus.LowDisk,
                CanStart: true,
                CanPersistRecovery: false,
                new AppError(
                    AppErrorCode.LowDiskSpace,
                    AppErrorStage.ResourceHealth,
                    CanRetry: true));
        }

        return new DictationAdmissionResult(
            DictationAdmissionStatus.Ready,
            CanStart: true,
            CanPersistRecovery: true);
    }
}
