using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Reliability;

public enum RunStateLoadStatus
{
    Started,
    PreviousRunInterrupted,
    InvalidStateRecovered,
    Unavailable,
}

public sealed record ApplicationRunStartResult(
    Guid RunId,
    RunStateLoadStatus Status,
    int ConsecutiveInterruptedRuns,
    AppError? Error = null)
{
    public bool RecoveredInterruptedRun => Status is
        RunStateLoadStatus.PreviousRunInterrupted or
        RunStateLoadStatus.InvalidStateRecovered;
}

public interface IApplicationRunStateStore
{
    Task<ApplicationRunStartResult> BeginRunAsync(
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default);

    Task<bool> HeartbeatAsync(
        Guid runId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteRunAsync(
        Guid runId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default);
}

public enum RecoveryTextLoadStatus
{
    Missing,
    Found,
    Invalid,
    Unavailable,
}

public sealed record RecoveryTextRecord(
    DictationSessionId SessionId,
    DateTimeOffset CreatedAt,
    string Text);

public sealed record RecoveryTextLoadResult(
    RecoveryTextLoadStatus Status,
    RecoveryTextRecord? Record = null,
    AppError? Error = null);

public interface IRecoveryTextStore
{
    Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<bool> SaveAsync(
        RecoveryTextRecord record,
        CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record SystemResourceSnapshot(
    long AvailableDiskBytes,
    ulong AvailablePhysicalMemoryBytes,
    uint MemoryLoadPercent,
    bool IsAvailable = true);

public enum DictationAdmissionStatus
{
    Ready,
    LowDisk,
    LowMemory,
    Unavailable,
}

public sealed record DictationAdmissionResult(
    DictationAdmissionStatus Status,
    bool CanStart,
    bool CanPersistRecovery,
    AppError? Error = null);

public interface ISystemResourceProbe
{
    SystemResourceSnapshot Probe();
}

public enum SystemLifecycleTransition
{
    Suspending,
    Resumed,
    SessionLocked,
    SessionUnlocked,
}

public interface ISystemLifecycleMonitor : IDisposable
{
    event EventHandler<SystemLifecycleTransition>? Transitioned;
}
