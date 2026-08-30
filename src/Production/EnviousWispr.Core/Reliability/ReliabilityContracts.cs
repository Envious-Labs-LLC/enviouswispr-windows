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

/// <param name="PreviousRunWasDictating">
/// Whether the previous run ended with a dictation still in flight.
/// </param>
/// <remarks>
/// THE COUNT OF INTERRUPTED RUNS IS DELIBERATELY NOT HERE, AND ITS ABSENCE IS THE POINT. It used to
/// be, and Home spent it on a warning reading "That has now happened N times in a row" - a number
/// nothing in this app can justify, because a closed laptop, a Restart from the Start menu, a log
/// off and Task Manager all leave the trace a crash leaves. On the test machine it reached nineteen,
/// almost all of it a build script releasing a file lock. The store still keeps the count for a
/// diagnostics export; not returning it here is what makes putting it back on screen impossible
/// rather than merely discouraged.
///
/// WHAT REPLACES IT IS A FACT THE APP ACTUALLY KNOWS. Recovery text is written only after
/// transcription completes, so a stop DURING a dictation loses it with nothing to restore - and that
/// is the one case where somebody genuinely needs telling, because they have to say it again.
/// </remarks>
public sealed record ApplicationRunStartResult(
    Guid RunId,
    RunStateLoadStatus Status,
    bool PreviousRunWasDictating,
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

    /// <summary>Records whether a dictation is in flight right now.</summary>
    /// <remarks>
    /// WRITTEN AT EVERY EDGE RATHER THAN SAMPLED, because the heartbeat runs once a minute and a
    /// dictation lasts seconds. A sampled flag would miss almost every one, which would be a signal
    /// that reads as reliable and is not.
    /// </remarks>
    Task<bool> SetDictationActiveAsync(
        Guid runId,
        bool active,
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
