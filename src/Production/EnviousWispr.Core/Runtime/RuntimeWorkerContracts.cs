using EnviousWispr.Core.Errors;

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
