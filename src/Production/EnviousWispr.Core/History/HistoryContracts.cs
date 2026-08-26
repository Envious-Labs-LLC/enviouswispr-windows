using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.History;

public sealed record DictationHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Text,
    string EngineId,
    bool WasPolished,
    bool WasDelivered)
{
    public const int MaximumTextLength = 100_000;

    public static DictationHistoryEntry Create(
        DateTimeOffset createdAt,
        string text,
        string engineId,
        bool wasPolished,
        bool wasDelivered) => new(
            Guid.NewGuid(),
            createdAt,
            text,
            engineId,
            wasPolished,
            wasDelivered);

    public bool IsValid =>
        Id != Guid.Empty &&
        CreatedAt != default &&
        !string.IsNullOrWhiteSpace(Text) &&
        Text.Length <= MaximumTextLength &&
        !string.IsNullOrWhiteSpace(EngineId) &&
        EngineId.Length <= 256;
}

public enum HistoryLoadStatus
{
    Loaded,
    Missing,
    Invalid,
    Unavailable,
}

public sealed record HistoryLoadResult(
    IReadOnlyList<DictationHistoryEntry> Entries,
    HistoryLoadStatus Status,
    AppError? Error = null);

public sealed record HistoryOperationResult(bool Succeeded, AppError? Error = null);

public interface IHistoryStore
{
    Task<HistoryLoadResult> LoadAsync(
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<HistoryOperationResult> AddAsync(
        DictationHistoryEntry entry,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<HistoryOperationResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default);
}
