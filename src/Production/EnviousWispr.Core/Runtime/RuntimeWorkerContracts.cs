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
}

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
    AppError? DegradedError = null);
