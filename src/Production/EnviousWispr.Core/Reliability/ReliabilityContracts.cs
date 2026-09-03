using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Reliability;

public enum RunStateLoadStatus
{
    Started,
    PreviousRunInterrupted,
    InvalidStateRecovered,
    Unavailable,

    /// <summary>Windows ended the previous run: a shutdown, a restart, or a log off.</summary>
    /// <remarks>
    /// "WE DO NOT KNOW" AND "WINDOWS ENDED IT" ARE DIFFERENT FACTS AND USED TO BE THE SAME RECORD.
    /// A run only records a clean exit when the app completes one itself, and Windows shutting the
    /// machine down terminates the process before that can happen - so a deliberate Restart from the
    /// Start menu was written down as an interruption, indistinguishable from a fault.
    ///
    /// NOTHING A USER SEES DEPENDS ON THIS. The banner that once accused the product on this basis
    /// is gone, and what replaced it asks whether a dictation was actually in flight. This is about
    /// a diagnostics export being honest: somebody reading a run history should not have to treat a
    /// week of ordinary restarts as a week of crashes. Ref: #93.
    ///
    /// IT DOES NOT COVER Task Manager, a forced kill, or the power going out. Those genuinely cannot
    /// be told from a crash by the process they end, and they stay `PreviousRunInterrupted`.
    /// </remarks>
    PreviousRunEndedByWindows,
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
    /// <remarks>
    /// A WINDOWS ENDING IS DELIBERATELY NOT ONE OF THESE. It is a known ending rather than an
    /// unexplained one, which is the entire reason for telling them apart.
    /// </remarks>
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

    /// <summary>Records that Windows is ending this run, before it gets the chance to end itself.</summary>
    /// <remarks>
    /// NOT `CompleteRunAsync`, AND THE DIFFERENCE IS THE POINT. A clean completion says the app tore
    /// itself down and everything in that teardown succeeded. Windows ending the session says only
    /// that the ending was expected; the process may still be killed part-way through whatever it was
    /// doing. Writing the stronger claim here would make every shutdown look like a clean exit and
    /// lose the distinction this exists to record. Ref: #93.
    /// </remarks>
    Task<bool> NoteSystemEndingAsync(
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

    /// <summary>Windows is shutting down, restarting, or logging the user off.</summary>
    SessionEnding,
}

public interface ISystemLifecycleMonitor : IDisposable
{
    event EventHandler<SystemLifecycleTransition>? Transitioned;
}
