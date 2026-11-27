using System.Security;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace EnviousWispr.Services.History;

/// <summary>
/// SQLite + FTS5 history store with single-writer serialization, safe migration from legacy
/// <c>history.json</c>, staged audio cleanup parity, and background-friendly keyset/limit queries.
/// </summary>
public sealed partial class SqliteHistoryStore : IHistoryStore, IHistoryQueryStore, IDisposable
{
    public const int MaximumEntries = 50_000;
    private const int CurrentSchemaVersion = 1;
    private const int SqliteErrorCantOpen = 14;
    private const int SqliteErrorCorrupt = 11;
    private const int SqliteErrorNotADb = 26;
    private const int SqliteErrorPerm = 3;
    private const int SqliteErrorReadOnly = 8;
    private const int SqliteErrorAuth = 23;

    private static readonly IReadOnlyDictionary<DateOnly, int> EmptyDayCounts =
        new Dictionary<DateOnly, int>();

    private readonly string _databasePath;
    private readonly string? _legacyJsonPath;
    private readonly string _recordingsDirectory;
    private readonly HistoryAudioDeleteJournal _audioJournal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _recoveredStagedOnInstance;
    private bool _disposed;

    public SqliteHistoryStore(
        string databasePath,
        string? legacyJsonPath = null,
        string? recordingsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _legacyJsonPath = string.IsNullOrWhiteSpace(legacyJsonPath)
            ? null
            : Path.GetFullPath(legacyJsonPath);
        var dbDirectory = Path.GetDirectoryName(_databasePath) ?? AppContext.BaseDirectory;
        _recordingsDirectory = !string.IsNullOrWhiteSpace(recordingsDirectory)
            ? Path.GetFullPath(recordingsDirectory)
            : Path.Combine(dbDirectory, "recordings", "dictations");
        var pendingPath = Path.Combine(
            dbDirectory,
            $".{Path.GetFileName(_databasePath)}.pending-audio-deletes.json");
        _audioJournal = new HistoryAudioDeleteJournal(_recordingsDirectory, pendingPath);
    }

    public async Task<HistoryLoadResult> LoadAsync(
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateRetention(retentionDays);
        ThrowIfDisposed();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => LoadCore(retentionDays, now, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryPageResult> QueryPageAsync(
        HistoryPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRetention(query.RetentionDays);
        ThrowIfDisposed();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => QueryPageCore(query, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> QueryMatchingIdsAsync(
        HistoryPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRetention(query.RetentionDays);
        ThrowIfDisposed();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => QueryMatchingIdsCore(query, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DictationHistoryEntry?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => GetCore(id, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryOperationResult> AddAsync(
        DictationHistoryEntry entry,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateRetention(retentionDays);
        ThrowIfDisposed();
        if (!entry.IsValid)
        {
            return Failure(AppErrorCode.InvalidData, canRetry: false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => AddCore(entry, retentionDays, now, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryRepolishUpdateResult> UpdateRepolishedAsync(
        Guid id,
        string expectedCurrentText,
        string newText,
        bool wasPolished,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentText);
        ArgumentException.ThrowIfNullOrWhiteSpace(newText);
        ThrowIfDisposed();
        if (newText.Length > DictationHistoryEntry.MaximumTextLength)
        {
            return HistoryRepolishUpdateResult.Failed(
                new AppError(AppErrorCode.InvalidData, AppErrorStage.HistorySave, CanRetry: false));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(
                    () => UpdateRepolishedCore(
                        id,
                        expectedCurrentText,
                        newText,
                        wasPolished,
                        providerId,
                        modelId,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<HistoryOperationResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        DeleteManyAsync([id], cancellationToken);

    public async Task<HistoryOperationResult> DeleteManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfDisposed();
        if (ids.Count == 0)
        {
            return new HistoryOperationResult(true);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => DeleteManyCore(ids, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryOperationResult> KeepAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => KeepCore(id, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => ClearCore(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseConnection();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private HistoryLoadResult LoadCore(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var openStatus = EnsureInitializedForReadCore(out var connection, out var loadError, cancellationToken);
        if (openStatus is not null)
        {
            return openStatus;
        }

        if (connection is null)
        {
            return new HistoryLoadResult([], HistoryLoadStatus.Missing);
        }

        try
        {
            RecoverStagedDeletesIfNeeded(connection);
            var removedAudio = PruneExpiredAndOverflowCore(connection, retentionDays, now);
            var entries = ReadAllEntriesCore(connection);
            _audioJournal.DrainPendingAudioDeletes(removedAudio);
            return new HistoryLoadResult(entries, HistoryLoadStatus.Loaded);
        }
        catch (Exception exception)
        {
            return MapLoadException(exception);
        }
    }

    private void CloseConnection()
    {
        if (_connection is not null)
        {
            try
            {
                _connection.Close();
            }
            catch
            {
            }

            _connection.Dispose();
            _connection = null;
        }

        SqliteConnection.ClearAllPools();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

    private static void ValidateRetention(int retentionDays)
    {
        if (retentionDays is < RetentionDays.HistoryMinimum or > RetentionDays.HistoryMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }
    }

    private static HistoryLoadResult MapLoadException(Exception exception) => exception switch
    {
        SqliteException sqlite when sqlite.SqliteErrorCode is SqliteErrorCorrupt or SqliteErrorNotADb =>
            new HistoryLoadResult([], HistoryLoadStatus.Invalid, new AppError(AppErrorCode.InvalidData, AppErrorStage.HistoryLoad, CanRetry: false)),
        SqliteException sqlite when sqlite.SqliteErrorCode is SqliteErrorPerm or SqliteErrorReadOnly or SqliteErrorAuth =>
            new HistoryLoadResult([], HistoryLoadStatus.Unavailable, new AppError(AppErrorCode.AccessDenied, AppErrorStage.HistoryLoad, CanRetry: true)),
        SqliteException =>
            new HistoryLoadResult([], HistoryLoadStatus.Unavailable, new AppError(AppErrorCode.StorageUnavailable, AppErrorStage.HistoryLoad, CanRetry: true)),
        UnauthorizedAccessException or SecurityException =>
            new HistoryLoadResult([], HistoryLoadStatus.Unavailable, new AppError(AppErrorCode.AccessDenied, AppErrorStage.HistoryLoad, CanRetry: true)),
        IOException =>
            new HistoryLoadResult([], HistoryLoadStatus.Unavailable, new AppError(AppErrorCode.StorageUnavailable, AppErrorStage.HistoryLoad, CanRetry: true)),
        _ => throw exception,
    };

    private static HistoryOperationResult MapSaveException(Exception exception) => exception switch
    {
        SqliteException sqlite when sqlite.SqliteErrorCode is SqliteErrorCorrupt or SqliteErrorNotADb =>
            Failure(AppErrorCode.InvalidData, canRetry: false),
        SqliteException sqlite when sqlite.SqliteErrorCode is SqliteErrorPerm or SqliteErrorReadOnly or SqliteErrorAuth =>
            Failure(AppErrorCode.AccessDenied, canRetry: false),
        SqliteException =>
            Failure(AppErrorCode.StorageUnavailable, canRetry: true),
        UnauthorizedAccessException or SecurityException =>
            Failure(AppErrorCode.AccessDenied, canRetry: false),
        IOException =>
            Failure(AppErrorCode.StorageUnavailable, canRetry: true),
        _ => throw exception,
    };

    private static HistoryOperationResult Failure(AppErrorCode code, bool canRetry = true) => new(
        false,
        new AppError(code, AppErrorStage.HistorySave, canRetry));
}
