using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Core.Preview;

public sealed record LivePreviewUpdate(
    Guid SessionId,
    long Sequence,
    bool Succeeded,
    string Text,
    string? DetectedLanguage = null,
    AppError? Error = null);

public interface ILivePreviewEngine : IAsyncDisposable
{
    string EngineId { get; }

    Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default);

    Task<LivePreviewUpdate> PreviewAsync(
        AudioSnapshot snapshot,
        long sequence,
        CancellationToken cancellationToken = default);

    Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A preview engine whose worker can be ended by force when its stop is refused: the worker killed
/// and its exit waited for up to the deadline, the resource it held let go of once the exit is seen.
/// </summary>
/// <remarks>
/// FOR THE RELEASE, NOT ONLY THE SHUTDOWN. Preview must release its resources before the final
/// transcription begins; a stop the engine refuses - its worker still there, its lease still held -
/// would put the final engine behind a worker that is not going, so the session's owner ends the
/// worker rather than transcribing beside it. What was seen is all that is reported: an exit not
/// observed inside the deadline leaves the worker owned.
/// </remarks>
public interface IAbortableLivePreviewEngine : ILivePreviewEngine
{
    Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline);
}
