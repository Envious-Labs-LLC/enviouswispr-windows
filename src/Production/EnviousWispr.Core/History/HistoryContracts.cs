using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.History;

public sealed record DictationHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Text,
    string EngineId,
    bool WasPolished,
    bool WasDelivered,
    DateTimeOffset? ExpiresAt = null,
    string? OriginalText = null,
    string? LanguageCode = null,
    string? PolishProviderId = null,
    string? PolishModelId = null,
    string? AudioFileName = null,
    long? AudioDurationMs = null)
{
    public const int MaximumTextLength = 100_000;

    public static DictationHistoryEntry Create(
        DateTimeOffset createdAt,
        string text,
        string engineId,
        bool wasPolished,
        bool wasDelivered,
        DateTimeOffset? expiresAt = null,
        string? originalText = null,
        string? languageCode = null,
        string? polishProviderId = null,
        string? polishModelId = null,
        string? audioFileName = null,
        long? audioDurationMs = null) => new(
            Guid.NewGuid(),
            createdAt,
            text,
            engineId,
            wasPolished,
            wasDelivered,
            expiresAt,
            originalText,
            languageCode,
            polishProviderId,
            polishModelId,
            audioFileName,
            audioDurationMs);

    public bool IsValid =>
        Id != Guid.Empty &&
        CreatedAt != default &&
        !string.IsNullOrWhiteSpace(Text) &&
        Text.Length <= MaximumTextLength &&
        !string.IsNullOrWhiteSpace(EngineId) &&
        EngineId.Length <= 256 &&
        (ExpiresAt is null || ExpiresAt > CreatedAt) &&
        (OriginalText is null || (OriginalText.Length <= MaximumTextLength && !string.IsNullOrWhiteSpace(OriginalText))) &&
        (LanguageCode is null || LanguageCode.Length <= 64) &&
        (PolishProviderId is null || PolishProviderId.Length <= 128) &&
        (PolishModelId is null || PolishModelId.Length <= 128) &&
        (AudioFileName is null || (
            !string.IsNullOrWhiteSpace(AudioFileName) &&
            AudioFileName.Length <= 256 &&
            !Path.IsPathRooted(AudioFileName) &&
            string.Equals(Path.GetFileName(AudioFileName), AudioFileName, StringComparison.Ordinal))) &&
        (AudioDurationMs is null or >= 0);
}

/// <summary>Metadata for a locally archived dictation recording paired with a history entry.</summary>
public sealed record ArchivedDictationAudio(string FileName, long DurationMs);

/// <summary>Local-only dictation recording archive paired with History entries.</summary>
public interface IDictationAudioArchive
{
    ArchivedDictationAudio? SaveForEntry(Guid entryId, CapturedAudio audio);

    void DeleteFile(string? fileName);
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

public enum HistoryRepolishUpdateStatus
{
    Updated,
    NotFound,
    Expired,
    Stale,
    Failed,
}

public sealed record HistoryRepolishUpdateResult(
    HistoryRepolishUpdateStatus Status,
    DictationHistoryEntry? UpdatedEntry = null,
    AppError? Error = null)
{
    public bool Succeeded => Status == HistoryRepolishUpdateStatus.Updated;

    public static HistoryRepolishUpdateResult Updated(DictationHistoryEntry entry) =>
        new(HistoryRepolishUpdateStatus.Updated, entry);

    public static HistoryRepolishUpdateResult NotFound() =>
        new(HistoryRepolishUpdateStatus.NotFound);

    public static HistoryRepolishUpdateResult Expired() =>
        new(HistoryRepolishUpdateStatus.Expired);

    public static HistoryRepolishUpdateResult Stale() =>
        new(HistoryRepolishUpdateStatus.Stale);

    public static HistoryRepolishUpdateResult Failed(AppError? error = null) =>
        new(HistoryRepolishUpdateStatus.Failed, Error: error);
}

public enum HistoryRepolishStatus
{
    Succeeded,
    NoProviderConfigured,
    EntryNotFound,
    EntryExpired,
    EscapeRecoveryEntryMustBeKept,
    StaleEntry,
    PolishRefused,
    Failed,
    Closing,
}

public sealed record HistoryRepolishResult(
    HistoryRepolishStatus Status,
    DictationHistoryEntry? Entry = null,
    bool CopiedToClipboard = false)
{
    public bool Succeeded => Status == HistoryRepolishStatus.Succeeded;

    public static HistoryRepolishResult Success(DictationHistoryEntry entry, bool copied = false) =>
        new(HistoryRepolishStatus.Succeeded, entry, copied);

    public static HistoryRepolishResult NoProvider() =>
        new(HistoryRepolishStatus.NoProviderConfigured);

    public static HistoryRepolishResult NotFound() =>
        new(HistoryRepolishStatus.EntryNotFound);

    public static HistoryRepolishResult Expired() =>
        new(HistoryRepolishStatus.EntryExpired);

    public static HistoryRepolishResult MustBeKept() =>
        new(HistoryRepolishStatus.EscapeRecoveryEntryMustBeKept);

    public static HistoryRepolishResult Stale() =>
        new(HistoryRepolishStatus.StaleEntry);

    public static HistoryRepolishResult Refused() =>
        new(HistoryRepolishStatus.PolishRefused);

    public static HistoryRepolishResult FailedResult() =>
        new(HistoryRepolishStatus.Failed);

    public static HistoryRepolishResult ClosingResult() =>
        new(HistoryRepolishStatus.Closing);
}

public interface IHistoryRepolishService
{
    Task<HistoryRepolishResult> RepolishAsync(
        Guid entryId,
        bool copyToClipboard = false,
        CancellationToken cancellationToken = default);

    Task<HistoryRepolishResult> RepolishAsync(
        DictationHistoryEntry entry,
        bool copyToClipboard = false,
        CancellationToken cancellationToken = default);
}

public interface IHistoryStore
{
    Task<HistoryLoadResult> LoadAsync(
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<DictationHistoryEntry?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<DictationHistoryEntry?>(null);

    Task<HistoryOperationResult> AddAsync(
        DictationHistoryEntry entry,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<HistoryRepolishUpdateResult> UpdateRepolishedAsync(
        Guid id,
        string expectedCurrentText,
        string newText,
        bool wasPolished,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HistoryRepolishUpdateResult.Failed());

    Task<HistoryOperationResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes all history entries matching <paramref name="ids"/>.</summary>
    async Task<HistoryOperationResult> DeleteManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            var result = await DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return result;
            }
        }

        return new HistoryOperationResult(true);
    }

    Task<HistoryOperationResult> KeepAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default);
}
