using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Core.Runtime;

public enum RuntimeWorkerState
{
    Stopped,
    Starting,
    Ready,
    Faulted,
    Disposed,

    /// <summary>Aborted for the shutdown: the worker was killed and nothing will start another.</summary>
    Aborted,
}

/// <summary>What an abort found, and whether the worker's exit was seen.</summary>
public enum RuntimeWorkerAbortOutcome
{
    /// <summary>There was no worker to abort.</summary>
    NoWorker,

    /// <summary>The worker was killed and its exit observed inside the deadline.</summary>
    Exited,

    /// <summary>The worker was killed but its exit was not observed inside the deadline.</summary>
    StillRunning,
}

/// <summary>The result of an abort: the outcome and the worker it was aimed at.</summary>
public sealed record RuntimeWorkerAbortResult(RuntimeWorkerAbortOutcome Outcome, int? WorkerProcessId);

public sealed record RuntimeWorkerResult(
    bool Succeeded,
    RuntimeWorkerState State,
    AppError? Error = null);

public interface IRuntimeWorkerSupervisor : IAsyncDisposable
{
    RuntimeWorkerState State { get; }

    int? WorkerProcessId { get; }

    Task<RuntimeWorkerResult> StartAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<RuntimeWorkerResult> CheckHealthAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<RuntimeWorkerResult> EnsureHealthyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Kills the worker now, for a shutdown, without waiting for a request in flight; terminal.</summary>
    /// <remarks>
    /// NOT BEHIND THE REQUEST GATE. A wedged transcription holds the gate for as long as its timeout,
    /// and a shutdown cannot wait that long; the abort takes the worker of the current generation
    /// out from under the request, which then ends as a failed one, and refuses every start after.
    /// The deadline bounds how long the exit is waited for; the outcome says whether it was seen.
    /// </remarks>
    Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline);
}

public enum RuntimeResourceKind
{
    Cpu,
    Accelerator,
}

public enum RuntimeWorkloadKind
{
    LivePreview,
    FinalAsr,
    LocalPolish,
}

public sealed record RuntimeResourceAcquireResult(
    bool Succeeded,
    IAsyncDisposable? Lease = null,
    AppError? Error = null);

/// <summary>
/// Admission to a shared runtime resource for one workload. The narrow face of the arbiter that the
/// dictation pipeline needs: ask, and either hold a lease until disposed or be told why not.
/// </summary>
public interface IRuntimeResourceAdmission
{
    Task<RuntimeResourceAcquireResult> AcquireAsync(
        RuntimeResourceKind resource,
        RuntimeWorkloadKind workload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeWorkerRequest(
    int ProtocolVersion,
    Guid RequestId,
    string Command,
    RuntimeWorkerTranscriptionRequest? Transcription = null);

public sealed record RuntimeWorkerResponse(
    int ProtocolVersion,
    Guid RequestId,
    string Status,
    RuntimeWorkerTranscript? Transcript = null,
    AppError? Error = null);

public sealed record RuntimeWorkerTranscriptionRequest(
    Guid SessionId,
    string MemoryMapName,
    int SampleCount);

public sealed record RuntimeWorkerTranscript(
    Guid SessionId,
    string Text,
    string EngineId,
    IReadOnlyList<TranscriptTokenTiming> TokenTimings,
    bool UsedFallback,
    AppError? DegradedError = null,
    string? DetectedLanguage = null);
