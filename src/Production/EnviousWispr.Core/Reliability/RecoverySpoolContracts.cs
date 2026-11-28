using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Core.Reliability;

/// <summary>Constants and thresholds for encrypted live dictation audio recovery spools.</summary>
public static class RecoverySpoolConstants
{
    public const string DirectoryName = "recovery-spools";
    public const string FileExtension = ".ewrec";
    public const int FormatVersion = 1;
    public const int DefaultSampleRate = 16_000;
    public const int DefaultChannels = 1;

    /// <summary>Minimum spoken duration (500 ms at 16 kHz) required before an orphaned spool is replayed.</summary>
    public const int MinimumSpokenSamples = 8_000;

    /// <summary>Maximum captured audio duration capped per spool (60 minutes at 16 kHz mono).</summary>
    public const long MaximumSpoolSamples = 16_000L * 60 * 60;

    /// <summary>Maximum sample count accepted in a single encrypted PCM chunk frame.</summary>
    public const int MaximumFrameSamples = 16_000 * 30;
}

/// <summary>Lifecycle state recorded inside an encrypted recovery spool.</summary>
public enum RecoverySpoolStatus
{
    Recording = 1,
    Finalizing = 2,
}

/// <summary>Authenticated session snapshot stored inside each encrypted recovery spool header.</summary>
public sealed record RecoverySpoolHeader(
    DictationSessionId SessionId,
    int SampleRate,
    int Channels,
    string? LanguageCode,
    bool AutoLanguageDetection,
    DateTimeOffset CreatedAt,
    int Version = RecoverySpoolConstants.FormatVersion);

/// <summary>Metadata discovered by inspecting an orphaned recovery spool without transcribing it.</summary>
public sealed record RecoverySpoolMetadata(
    DictationSessionId SessionId,
    string FilePath,
    DateTimeOffset CreatedAt,
    int SampleRate,
    int Channels,
    string? LanguageCode,
    bool AutoLanguageDetection,
    RecoverySpoolStatus Status,
    long SampleCount,
    long FileSizeBytes);

/// <summary>Result of decrypting and verifying an encrypted recovery spool from disk.</summary>
public sealed record RecoverySpoolReadResult(
    bool Succeeded,
    RecoverySpoolHeader? Header,
    float[] Samples,
    RecoverySpoolStatus Status,
    bool TruncatedTail,
    string? ErrorMessage = null,
    long SampleCount = 0);

/// <summary>Result reported after replaying an orphaned recovery spool batch on startup.</summary>
public sealed record RecoverySpoolReplaySummary(
    int FoundCount,
    int ReplayedCount,
    int SavedCount,
    int DiscardedCount,
    int FailedCount,
    bool YieldedToLiveDictation);

/// <summary>Background-serialized streaming writer for one active dictation's encrypted recovery spool.</summary>
public interface IRecoverySpoolWriter : IAsyncDisposable
{
    DictationSessionId SessionId { get; }

    string FilePath { get; }

    long SamplesWritten { get; }

    /// <summary>Queues a live mono 16 kHz PCM packet without blocking the audio callback thread.</summary>
    void EnqueueSamples(ReadOnlyMemory<float> samples);

    /// <summary>Waits until every packet queued before this call has been encrypted and flushed to disk.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Flushes queued audio, writes an authenticated finalizing marker, and closes the writer handle.</summary>
    Task MarkFinalizingAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels pending writes, closes handles, and deletes the spool file immediately.</summary>
    Task DiscardAndDeleteAsync();
}

/// <summary>Manages DPAPI-protected encrypted live audio recovery spools on local disk.</summary>
public interface IRecoverySpoolStore
{
    string DirectoryPath { get; }

    /// <summary>Opens a new per-session encrypted spool if recovery persistence is admitted.</summary>
    Task<IRecoverySpoolWriter?> OpenSessionAsync(
        RecoverySpoolHeader header,
        bool canPersistRecovery,
        CancellationToken cancellationToken = default);

    /// <summary>Returns orphaned spools currently on disk, ordered oldest-first, excluding active writers.</summary>
    IReadOnlyList<RecoverySpoolMetadata> ListOrphanSpools();

    /// <summary>Decrypts and authenticates an orphaned spool, recovering the valid prefix if truncated on crash.</summary>
    Task<RecoverySpoolReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes the spool associated with <paramref name="sessionId"/> if present.</summary>
    Task<bool> DeleteSessionAsync(DictationSessionId sessionId);

    /// <summary>Deletes a specific spool file path if present.</summary>
    Task<bool> DeleteFileAsync(string filePath);
}
