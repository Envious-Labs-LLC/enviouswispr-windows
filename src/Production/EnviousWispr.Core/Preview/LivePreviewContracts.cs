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
