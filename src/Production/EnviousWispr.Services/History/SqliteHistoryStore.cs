using System.Globalization;
using System.Security;
using System.Text;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace EnviousWispr.Services.History;

/// <summary>
/// SQLite + FTS5 history store with single-writer serialization, safe migration from legacy
/// <c>history.json</c>, staged audio cleanup parity, and background-friendly keyset/limit queries.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore, IHistoryQueryStore, IDisposable
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

    private HistoryPageResult QueryPageCore(HistoryPageQuery query, CancellationToken cancellationToken)
    {
        var openStatus = EnsureInitializedForReadCore(out var connection, out _, cancellationToken);
        if (openStatus is not null)
        {
            return new HistoryPageResult(
                [],
                openStatus.Status,
                0,
                0,
                EmptyDayCounts,
                openStatus.Error);
        }

        if (connection is null)
        {
            return new HistoryPageResult(
                [],
                HistoryLoadStatus.Missing,
                0,
                0,
                EmptyDayCounts);
        }

        try
        {
            RecoverStagedDeletesIfNeeded(connection);
            var removedAudio = PruneExpiredAndOverflowCore(connection, query.RetentionDays, query.Now);
            _audioJournal.DrainPendingAudioDeletes(removedAudio);

            var totalStoredCount = ScalarInt(connection, "SELECT COUNT(*) FROM history_entries");

            var whereSql = BuildFilterWhereClause(query, out var parameters);
            var matchingTicks = new List<long>();
            using (var ticksCmd = connection.CreateCommand())
            {
                ticksCmd.CommandText = $"SELECT e.created_at_utc_ticks FROM history_entries e {whereSql}";
                AddParameters(ticksCmd, parameters);
                using var reader = ticksCmd.ExecuteReader();
                while (reader.Read())
                {
                    matchingTicks.Add(reader.GetInt64(0));
                }
            }

            var totalMatchingCount = matchingTicks.Count;
            var dayCounts = CountDaysFromUtcTicks(matchingTicks, query.LocalTimeZone ?? TimeZoneInfo.Local);

            var limit = Math.Max(0, query.Limit);
            var entries = new List<DictationHistoryEntry>(Math.Min(limit, totalMatchingCount));
            if (limit > 0 && totalMatchingCount > 0)
            {
                using var pageCmd = connection.CreateCommand();
                pageCmd.CommandText =
                    $"""
                    SELECT e.id, e.created_at_iso, e.text, e.engine_id, e.was_polished, e.was_delivered,
                           e.expires_at_iso, e.original_text, e.language_code, e.polish_provider_id,
                           e.polish_model_id, e.audio_file_name, e.audio_duration_ms
                    FROM history_entries e
                    {whereSql}
                    ORDER BY e.created_at_utc_ticks DESC, e.rowid DESC
                    LIMIT $limit;
                    """;
                AddParameters(pageCmd, parameters);
                pageCmd.Parameters.AddWithValue("$limit", limit);
                using var reader = pageCmd.ExecuteReader();
                while (reader.Read())
                {
                    entries.Add(ReadEntry(reader));
                }
            }

            return new HistoryPageResult(
                entries,
                HistoryLoadStatus.Loaded,
                totalStoredCount,
                totalMatchingCount,
                dayCounts);
        }
        catch (Exception exception)
        {
            var mapped = MapLoadException(exception);
            return new HistoryPageResult([], mapped.Status, 0, 0, EmptyDayCounts, mapped.Error);
        }
    }

    private List<Guid> QueryMatchingIdsCore(HistoryPageQuery query, CancellationToken cancellationToken)
    {
        var openStatus = EnsureInitializedForReadCore(out var connection, out _, cancellationToken);
        if (openStatus is not null || connection is null)
        {
            return [];
        }

        try
        {
            _ = PruneExpiredAndOverflowCore(connection, query.RetentionDays, query.Now);
            var whereSql = BuildFilterWhereClause(query, out var parameters);
            var ids = new List<Guid>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT e.id
                FROM history_entries e
                {whereSql}
                ORDER BY e.created_at_utc_ticks DESC, e.rowid DESC;
                """;
            AddParameters(cmd, parameters);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParseExact(reader.GetString(0), "D", out var id) || Guid.TryParse(reader.GetString(0), out id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    private DictationHistoryEntry? GetCore(Guid id, CancellationToken cancellationToken)
    {
        var openStatus = EnsureInitializedForReadCore(out var connection, out _, cancellationToken);
        if (openStatus is not null || connection is null)
        {
            return null;
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, created_at_iso, text, engine_id, was_polished, was_delivered,
                       expires_at_iso, original_text, language_code, polish_provider_id,
                       polish_model_id, audio_file_name, audio_duration_ms
                FROM history_entries
                WHERE id = $id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private HistoryOperationResult AddCore(
        DictationHistoryEntry entry,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = EnsureOpenedForMutationCore(out var migrationError, cancellationToken);
            if (connection is null)
            {
                return new HistoryOperationResult(false, migrationError);
            }

            RecoverStagedDeletesIfNeeded(connection);
            var removedAudio = new List<string>();
            using var tx = connection.BeginTransaction();

            // Read any prior audio filename first (ExecuteReader only executes the first statement).
            using (var prevCmd = connection.CreateCommand())
            {
                prevCmd.Transaction = tx;
                prevCmd.CommandText = "SELECT audio_file_name FROM history_entries WHERE id = $id LIMIT 1;";
                prevCmd.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
                if (prevCmd.ExecuteScalar() is string prevAudio
                    && !string.IsNullOrWhiteSpace(prevAudio)
                    && !string.Equals(prevAudio, entry.AudioFileName, StringComparison.OrdinalIgnoreCase))
                {
                    removedAudio.Add(prevAudio);
                }
            }

            using (var deleteOld = connection.CreateCommand())
            {
                deleteOld.Transaction = tx;
                deleteOld.CommandText =
                    """
                    DELETE FROM history_fts WHERE entry_id = $id;
                    DELETE FROM history_entries WHERE id = $id;
                    """;
                deleteOld.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
                deleteOld.ExecuteNonQuery();
            }

            InsertEntryCore(connection, tx, entry);
            tx.Commit();

            removedAudio.AddRange(PruneExpiredAndOverflowCore(connection, retentionDays, now));
            _audioJournal.DrainPendingAudioDeletes(removedAudio);
            return new HistoryOperationResult(true);
        }
        catch (Exception exception)
        {
            return MapSaveException(exception);
        }
    }

    private HistoryRepolishUpdateResult UpdateRepolishedCore(
        Guid id,
        string expectedCurrentText,
        string newText,
        bool wasPolished,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var openStatus = EnsureInitializedForReadCore(out var connection, out _, cancellationToken);
            if (openStatus is not null)
            {
                return HistoryRepolishUpdateResult.Failed(openStatus.Error);
            }

            if (connection is null)
            {
                return HistoryRepolishUpdateResult.NotFound();
            }

            var existing = GetCore(id, cancellationToken);
            if (existing is null)
            {
                return HistoryRepolishUpdateResult.NotFound();
            }

            if (existing.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            {
                return HistoryRepolishUpdateResult.Expired();
            }

            if (!string.Equals(existing.Text, expectedCurrentText, StringComparison.Ordinal))
            {
                return HistoryRepolishUpdateResult.Stale();
            }

            var updated = existing with
            {
                Text = newText,
                WasPolished = wasPolished,
                OriginalText = existing.OriginalText ?? existing.Text,
                PolishProviderId = providerId,
                PolishModelId = modelId,
            };

            if (!updated.IsValid)
            {
                return HistoryRepolishUpdateResult.Failed(
                    new AppError(AppErrorCode.InvalidData, AppErrorStage.HistorySave, CanRetry: false));
            }

            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    UPDATE history_entries
                    SET text = $text,
                        was_polished = $wasPolished,
                        original_text = $originalText,
                        polish_provider_id = $providerId,
                        polish_model_id = $modelId,
                        search_labels = $searchLabels
                    WHERE id = $id;
                    DELETE FROM history_fts WHERE entry_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", updated.Id.ToString("D"));
                cmd.Parameters.AddWithValue("$text", updated.Text);
                cmd.Parameters.AddWithValue("$wasPolished", updated.WasPolished ? 1 : 0);
                cmd.Parameters.AddWithValue("$originalText", (object?)updated.OriginalText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$providerId", (object?)updated.PolishProviderId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$modelId", (object?)updated.PolishModelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$searchLabels", BuildSearchLabels(updated));
                cmd.ExecuteNonQuery();
            }

            InsertFtsCore(connection, tx, updated);
            tx.Commit();
            return HistoryRepolishUpdateResult.Updated(updated);
        }
        catch (Exception exception)
        {
            var err = MapSaveException(exception).Error;
            return HistoryRepolishUpdateResult.Failed(err);
        }
    }

    private HistoryOperationResult DeleteManyCore(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        try
        {
            var openStatus = EnsureInitializedForReadCore(out var connection, out _, cancellationToken);
            if (openStatus is not null)
            {
                return new HistoryOperationResult(false, openStatus.Error);
            }

            if (connection is null)
            {
                LocalDictationAudioArchive.RecoverStagedDeletes(
                    _recordingsDirectory,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                _audioJournal.DrainPendingAudioDeletes([]);
                return new HistoryOperationResult(true);
            }

            RecoverStagedDeletesIfNeeded(connection);
            var targetIdStrings = ids.Select(id => id.ToString("D")).Distinct(StringComparer.Ordinal).ToArray();
            var targetAudioFiles = CollectAudioFilesForIds(connection, targetIdStrings);
            // Only remove WAVs that will no longer be referenced by any surviving row.
            var remainingAudioFiles = CollectRemainingAudioFilesExcludingIds(connection, targetIdStrings);
            var filesToStage = targetAudioFiles
                .Where(file => !remainingAudioFiles.Contains(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var deleteError = LocalDictationAudioArchive.TryStageDeletes(
                _recordingsDirectory,
                filesToStage,
                includeAllWaveFiles: false,
                out var stagedDeletes);
            if (deleteError is { } errorCode)
            {
                return Failure(errorCode, canRetry: errorCode == AppErrorCode.StorageUnavailable);
            }

            try
            {
                using var tx = connection.BeginTransaction();
                foreach (var idText in targetIdStrings)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        """
                        DELETE FROM history_fts WHERE entry_id = $id;
                        DELETE FROM history_entries WHERE id = $id;
                        """;
                    cmd.Parameters.AddWithValue("$id", idText);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch
            {
                LocalDictationAudioArchive.RollbackStagedDeletes(stagedDeletes);
                throw;
            }

            LocalDictationAudioArchive.CommitStagedDeletes(
                _recordingsDirectory,
                stagedDeletes,
                cleanRemainingTempFiles: false);
            _audioJournal.DrainPendingAudioDeletes([]);
            return new HistoryOperationResult(true);
        }
        catch (Exception exception)
        {
            return MapSaveException(exception);
        }
    }

    private HistoryOperationResult KeepCore(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var openStatus = EnsureInitializedForReadCore(out var connection, out _, cancellationToken);
            if (openStatus is not null)
            {
                return new HistoryOperationResult(false, openStatus.Error);
            }

            if (connection is null)
            {
                return new HistoryOperationResult(true);
            }

            RecoverStagedDeletesIfNeeded(connection);
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    UPDATE history_entries
                    SET expires_at_utc_ticks = NULL,
                        expires_at_iso = NULL
                    WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString("D"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return new HistoryOperationResult(true);
        }
        catch (Exception exception)
        {
            return MapSaveException(exception);
        }
    }

    private HistoryOperationResult ClearCore(CancellationToken cancellationToken)
    {
        try
        {
            var openStatus = EnsureInitializedForReadCore(out var connection, out _, cancellationToken);
            if (openStatus is not null)
            {
                return new HistoryOperationResult(false, openStatus.Error);
            }

            if (connection is not null)
            {
                RecoverStagedDeletesIfNeeded(connection);
            }

            IEnumerable<string> targetFiles = connection is not null
                ? CollectAllAudioFiles(connection)
                : Array.Empty<string>();
            var deleteError = LocalDictationAudioArchive.TryStageDeletes(
                _recordingsDirectory,
                targetFiles,
                includeAllWaveFiles: true,
                out var stagedDeletes);
            if (deleteError is { } errorCode)
            {
                return Failure(errorCode, canRetry: errorCode == AppErrorCode.StorageUnavailable);
            }

            if (connection is not null)
            {
                try
                {
                    using var tx = connection.BeginTransaction();
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText =
                            """
                            DELETE FROM history_fts;
                            DELETE FROM history_entries;
                            """;
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    using var checkpoint = connection.CreateCommand();
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    checkpoint.ExecuteNonQuery();
                }
                catch
                {
                    LocalDictationAudioArchive.RollbackStagedDeletes(stagedDeletes);
                    throw;
                }
            }

            LocalDictationAudioArchive.CommitStagedDeletes(
                _recordingsDirectory,
                stagedDeletes,
                cleanRemainingTempFiles: true);
            _audioJournal.DeletePendingDeletesManifest();
            if (_legacyJsonPath is not null)
            {
                TryDeleteFileQuietly(_legacyJsonPath);
            }

            return new HistoryOperationResult(true);
        }
        catch (Exception exception)
        {
            return MapSaveException(exception);
        }
    }

    private HistoryLoadResult? EnsureInitializedForReadCore(
        out SqliteConnection? connection,
        out AppError? error,
        CancellationToken cancellationToken)
    {
        connection = _connection;
        error = null;
        if (connection is not null)
        {
            return null;
        }

        var dbExists = File.Exists(_databasePath);
        if (!dbExists)
        {
            if (_legacyJsonPath is not null && File.Exists(_legacyJsonPath))
            {
                return MigrateLegacyJsonCore(out connection, out error, cancellationToken);
            }

            LocalDictationAudioArchive.RecoverStagedDeletes(
                _recordingsDirectory,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _recoveredStagedOnInstance = true;
            _audioJournal.DrainPendingAudioDeletes([]);
            return null;
        }

        try
        {
            connection = OpenAndEnsureSchemaCore();
            _connection = connection;
            if (_legacyJsonPath is not null && File.Exists(_legacyJsonPath))
            {
                if (!HasCompletedLegacyMigration(connection))
                {
                    return CompletePendingLegacyMigrationCore(connection, out error, cancellationToken);
                }

                TryDeleteFileQuietly(_legacyJsonPath);
            }

            return null;
        }
        catch (Exception exception)
        {
            CloseConnection();
            var mapped = MapLoadException(exception);
            error = mapped.Error;
            return mapped;
        }
    }

    private SqliteConnection? EnsureOpenedForMutationCore(
        out AppError? error,
        CancellationToken cancellationToken)
    {
        var readResult = EnsureInitializedForReadCore(out var connection, out error, cancellationToken);
        if (readResult is not null && readResult.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
        {
            error = readResult.Error;
            return null;
        }

        if (connection is not null)
        {
            return connection;
        }

        try
        {
            connection = OpenAndEnsureSchemaCore();
            _connection = connection;
            return connection;
        }
        catch (Exception exception)
        {
            CloseConnection();
            error = MapSaveException(exception).Error;
            return null;
        }
    }

    private HistoryLoadResult? MigrateLegacyJsonCore(
        out SqliteConnection? connection,
        out AppError? error,
        CancellationToken cancellationToken)
    {
        connection = null;
        error = null;
        using var legacyStore = new JsonHistoryStore(_legacyJsonPath!, _recordingsDirectory);
        // Load with retentionDays = 0 (keep non-expired) so migration preserves existing history intact;
        // the caller's requested retention window is enforced immediately afterwards on SQLite.
        var legacyResult = legacyStore
            .LoadAsync(RetentionDays.HistoryMinimum, DateTimeOffset.UtcNow, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (legacyResult.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
        {
            error = legacyResult.Error;
            return legacyResult;
        }

        try
        {
            connection = OpenAndEnsureSchemaCore();
            var result = CompletePendingLegacyMigrationCore(connection, out error, cancellationToken, preloadedLegacy: legacyResult);
            if (result is not null)
            {
                CloseConnection();
                DeleteDatabaseArtifactsQuietly();
                return result;
            }

            _connection = connection;
            return null;
        }
        catch (Exception exception)
        {
            CloseConnection();
            DeleteDatabaseArtifactsQuietly();
            var mapped = MapLoadException(exception);
            error = mapped.Error;
            return mapped;
        }
    }

    private HistoryLoadResult? CompletePendingLegacyMigrationCore(
        SqliteConnection connection,
        out AppError? error,
        CancellationToken cancellationToken,
        HistoryLoadResult? preloadedLegacy = null)
    {
        error = null;
        HistoryLoadResult legacyResult;
        if (preloadedLegacy is not null)
        {
            legacyResult = preloadedLegacy;
        }
        else
        {
            using var legacyStore = new JsonHistoryStore(_legacyJsonPath!, _recordingsDirectory);
            legacyResult = legacyStore
                .LoadAsync(RetentionDays.HistoryMinimum, DateTimeOffset.UtcNow, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        if (legacyResult.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unavailable)
        {
            error = legacyResult.Error;
            return legacyResult;
        }

        using var tx = connection.BeginTransaction();
        // Insert oldest-to-newest so rowid DESC preserves newest-first tie ordering identical to JsonHistoryStore.
        for (var i = legacyResult.Entries.Count - 1; i >= 0; i--)
        {
            var entry = legacyResult.Entries[i];
            if (!EntryExistsCore(connection, tx, entry.Id))
            {
                InsertEntryCore(connection, tx, entry);
            }
        }

        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText =
                """
                INSERT OR REPLACE INTO history_meta (key, value)
                VALUES ('legacy_json_migrated', '1');
                """;
            meta.ExecuteNonQuery();
        }

        tx.Commit();
        TryDeleteFileQuietly(_legacyJsonPath!);
        return null;
    }

    private static bool HasCompletedLegacyMigration(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM history_meta WHERE key = 'legacy_json_migrated' LIMIT 1;";
        return string.Equals(cmd.ExecuteScalar() as string, "1", StringComparison.Ordinal);
    }

    private static bool EntryExistsCore(SqliteConnection connection, SqliteTransaction tx, Guid id)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM history_entries WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        return cmd.ExecuteScalar() is not null;
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    private SqliteConnection OpenAndEnsureSchemaCore()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            connection.CreateFunction(
                "ew_contains",
                (string? haystack, string? needle) =>
                    !string.IsNullOrEmpty(haystack)
                    && !string.IsNullOrEmpty(needle)
                    && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase),
                isDeterministic: true);

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText =
                    """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA busy_timeout=5000;
                    PRAGMA temp_store=MEMORY;
                    """;
                pragma.ExecuteNonQuery();
            }

            var userVersion = ScalarInt(connection, "PRAGMA user_version;");
            if (userVersion < CurrentSchemaVersion)
            {
                using var tx = connection.BeginTransaction();
                using var ddl = connection.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText =
                    $"""
                    CREATE TABLE IF NOT EXISTS history_meta (
                        key TEXT PRIMARY KEY NOT NULL,
                        value TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS history_entries (
                        id TEXT PRIMARY KEY NOT NULL,
                        created_at_utc_ticks INTEGER NOT NULL,
                        created_at_iso TEXT NOT NULL,
                        text TEXT NOT NULL,
                        engine_id TEXT NOT NULL,
                        was_polished INTEGER NOT NULL,
                        was_delivered INTEGER NOT NULL,
                        expires_at_utc_ticks INTEGER NULL,
                        expires_at_iso TEXT NULL,
                        original_text TEXT NULL,
                        language_code TEXT NULL,
                        polish_provider_id TEXT NULL,
                        polish_model_id TEXT NULL,
                        audio_file_name TEXT NULL,
                        audio_duration_ms INTEGER NULL,
                        is_file_transcription INTEGER NOT NULL,
                        search_labels TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_history_created
                        ON history_entries (created_at_utc_ticks DESC);
                    CREATE INDEX IF NOT EXISTS ix_history_kind_created
                        ON history_entries (is_file_transcription, created_at_utc_ticks DESC);
                    CREATE INDEX IF NOT EXISTS ix_history_expires
                        ON history_entries (expires_at_utc_ticks)
                        WHERE expires_at_utc_ticks IS NOT NULL;
                    CREATE VIRTUAL TABLE IF NOT EXISTS history_fts USING fts5(
                        entry_id UNINDEXED,
                        text,
                        original_text,
                        engine_id,
                        polish_model_id,
                        polish_provider_id,
                        search_labels,
                        tokenize='trigram'
                    );
                    PRAGMA user_version = {CurrentSchemaVersion};
                    """;
                ddl.ExecuteNonQuery();
                tx.Commit();
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void RecoverStagedDeletesIfNeeded(SqliteConnection connection)
    {
        if (_recoveredStagedOnInstance && !HasPendingTempDeletes())
        {
            return;
        }

        LocalDictationAudioArchive.RecoverStagedDeletes(
            _recordingsDirectory,
            CollectAllAudioFiles(connection));
        _recoveredStagedOnInstance = true;
    }

    private bool HasPendingTempDeletes()
    {
        if (!Directory.Exists(_recordingsDirectory))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(_recordingsDirectory, "*.deltmp").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static List<string> PruneExpiredAndOverflowCore(
        SqliteConnection connection,
        int retentionDays,
        DateTimeOffset now)
    {
        var nowTicks = now.UtcTicks;
        var hasCutoff = retentionDays > 0;
        var cutoffTicks = hasCutoff ? (now - TimeSpan.FromDays(retentionDays)).UtcTicks : long.MinValue;

        var needsPrune = false;
        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = hasCutoff
                ? """
                  SELECT EXISTS(
                      SELECT 1 FROM history_entries
                      WHERE (expires_at_utc_ticks IS NOT NULL AND expires_at_utc_ticks <= $nowTicks)
                         OR created_at_utc_ticks < $cutoffTicks
                  ) OR (SELECT COUNT(*) FROM history_entries) > $maxEntries;
                  """
                : """
                  SELECT EXISTS(
                      SELECT 1 FROM history_entries
                      WHERE expires_at_utc_ticks IS NOT NULL AND expires_at_utc_ticks <= $nowTicks
                  ) OR (SELECT COUNT(*) FROM history_entries) > $maxEntries;
                  """;
            checkCmd.Parameters.AddWithValue("$nowTicks", nowTicks);
            if (hasCutoff)
            {
                checkCmd.Parameters.AddWithValue("$cutoffTicks", cutoffTicks);
            }

            checkCmd.Parameters.AddWithValue("$maxEntries", MaximumEntries);
            needsPrune = Convert.ToInt64(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }

        if (!needsPrune)
        {
            return [];
        }

        var doomedIds = new List<string>();
        var candidateAudio = new List<string>();
        using var tx = connection.BeginTransaction();

        var expiredSql = hasCutoff
            ? """
              SELECT id, audio_file_name FROM history_entries
              WHERE (expires_at_utc_ticks IS NOT NULL AND expires_at_utc_ticks <= $nowTicks)
                 OR created_at_utc_ticks < $cutoffTicks;
              """
            : """
              SELECT id, audio_file_name FROM history_entries
              WHERE expires_at_utc_ticks IS NOT NULL AND expires_at_utc_ticks <= $nowTicks;
              """;
        using (var expiredCmd = connection.CreateCommand())
        {
            expiredCmd.Transaction = tx;
            expiredCmd.CommandText = expiredSql;
            expiredCmd.Parameters.AddWithValue("$nowTicks", nowTicks);
            if (hasCutoff)
            {
                expiredCmd.Parameters.AddWithValue("$cutoffTicks", cutoffTicks);
            }

            using var reader = expiredCmd.ExecuteReader();
            while (reader.Read())
            {
                doomedIds.Add(reader.GetString(0));
                if (!reader.IsDBNull(1))
                {
                    candidateAudio.Add(reader.GetString(1));
                }
            }
        }

        foreach (var idText in doomedIds)
        {
            using var delCmd = connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText =
                """
                DELETE FROM history_fts WHERE entry_id = $id;
                DELETE FROM history_entries WHERE id = $id;
                """;
            delCmd.Parameters.AddWithValue("$id", idText);
            delCmd.ExecuteNonQuery();
        }

        var countAfterExpiry = ScalarInt(connection, "SELECT COUNT(*) FROM history_entries;", tx);
        if (countAfterExpiry > MaximumEntries)
        {
            var excess = countAfterExpiry - MaximumEntries;
            var overflowIds = new List<string>(excess);
            using (var overflowCmd = connection.CreateCommand())
            {
                overflowCmd.Transaction = tx;
                overflowCmd.CommandText =
                    """
                    SELECT id, audio_file_name FROM history_entries
                    ORDER BY created_at_utc_ticks ASC, rowid ASC
                    LIMIT $excess;
                    """;
                overflowCmd.Parameters.AddWithValue("$excess", excess);
                using var reader = overflowCmd.ExecuteReader();
                while (reader.Read())
                {
                    overflowIds.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1))
                    {
                        candidateAudio.Add(reader.GetString(1));
                    }
                }
            }

            foreach (var idText in overflowIds)
            {
                using var delCmd = connection.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText =
                    """
                    DELETE FROM history_fts WHERE entry_id = $id;
                    DELETE FROM history_entries WHERE id = $id;
                    """;
                delCmd.Parameters.AddWithValue("$id", idText);
                delCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();

        if (candidateAudio.Count == 0)
        {
            return [];
        }

        var survivingAudio = CollectAllAudioFiles(connection);
        return candidateAudio
            .Where(file => !survivingAudio.Contains(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DictationHistoryEntry> ReadAllEntriesCore(SqliteConnection connection)
    {
        var entries = new List<DictationHistoryEntry>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, created_at_iso, text, engine_id, was_polished, was_delivered,
                   expires_at_iso, original_text, language_code, polish_provider_id,
                   polish_model_id, audio_file_name, audio_duration_ms
            FROM history_entries
            ORDER BY created_at_utc_ticks DESC, rowid DESC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private static string BuildFilterWhereClause(
        HistoryPageQuery query,
        out List<(string Name, object Value)> parameters)
    {
        parameters = [];
        var clauses = new List<string>();

        if (query.KindFilter == HistoryEntryKindFilter.Dictation)
        {
            clauses.Add("e.is_file_transcription = 0");
        }
        else if (query.KindFilter == HistoryEntryKindFilter.TranscribeFile)
        {
            clauses.Add("e.is_file_transcription = 1");
        }

        if (query.CreatedFromUtc is { } fromUtc)
        {
            clauses.Add("e.created_at_utc_ticks >= $fromUtcTicks");
            parameters.Add(("$fromUtcTicks", fromUtc.UtcTicks));
        }

        if (query.CreatedToUtcExclusive is { } toUtc)
        {
            clauses.Add("e.created_at_utc_ticks < $toUtcTicks");
            parameters.Add(("$toUtcTicks", toUtc.UtcTicks));
        }

        var trimmed = (query.SearchQuery ?? string.Empty).Trim();
        if (trimmed.Length > 0)
        {
            if (trimmed.Length >= 3 && Ascii.IsValid(trimmed))
            {
                var escapedFtsPhrase = "\"" + trimmed.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                clauses.Add("e.id IN (SELECT entry_id FROM history_fts WHERE history_fts MATCH $ftsQuery)");
                parameters.Add(("$ftsQuery", escapedFtsPhrase));
            }

            clauses.Add(
                "(ew_contains(e.text, $q) OR ew_contains(e.original_text, $q) OR ew_contains(e.engine_id, $q) OR ew_contains(e.polish_model_id, $q) OR ew_contains(e.polish_provider_id, $q) OR ew_contains(e.search_labels, $q))");
            parameters.Add(("$q", trimmed));
        }

        return clauses.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", clauses);
    }

    private static void AddParameters(
        SqliteCommand cmd,
        List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
    }

    private static Dictionary<DateOnly, int> CountDaysFromUtcTicks(
        List<long> utcTicks,
        TimeZoneInfo zone)
    {
        var counts = new Dictionary<DateOnly, int>();
        foreach (var ticks in utcTicks)
        {
            var dto = new DateTimeOffset(ticks, TimeSpan.Zero);
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(dto, zone).DateTime);
            counts[day] = counts.TryGetValue(day, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }

    private static void InsertEntryCore(
        SqliteConnection connection,
        SqliteTransaction tx,
        DictationHistoryEntry entry)
    {
        var searchLabels = BuildSearchLabels(entry);
        var isFile = IsFileTranscription(entry) ? 1 : 0;

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO history_entries (
                    id, created_at_utc_ticks, created_at_iso, text, engine_id,
                    was_polished, was_delivered, expires_at_utc_ticks, expires_at_iso,
                    original_text, language_code, polish_provider_id, polish_model_id,
                    audio_file_name, audio_duration_ms, is_file_transcription, search_labels
                ) VALUES (
                    $id, $createdTicks, $createdIso, $text, $engineId,
                    $wasPolished, $wasDelivered, $expiresTicks, $expiresIso,
                    $originalText, $languageCode, $providerId, $modelId,
                    $audioFile, $audioMs, $isFile, $searchLabels
                );
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$createdTicks", entry.CreatedAt.UtcTicks);
            cmd.Parameters.AddWithValue("$createdIso", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$text", entry.Text);
            cmd.Parameters.AddWithValue("$engineId", entry.EngineId);
            cmd.Parameters.AddWithValue("$wasPolished", entry.WasPolished ? 1 : 0);
            cmd.Parameters.AddWithValue("$wasDelivered", entry.WasDelivered ? 1 : 0);
            cmd.Parameters.AddWithValue("$expiresTicks", entry.ExpiresAt is { } exp ? exp.UtcTicks : DBNull.Value);
            cmd.Parameters.AddWithValue("$expiresIso", entry.ExpiresAt is { } expIso ? expIso.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$originalText", (object?)entry.OriginalText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$languageCode", (object?)entry.LanguageCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$providerId", (object?)entry.PolishProviderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$modelId", (object?)entry.PolishModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$audioFile", (object?)entry.AudioFileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$audioMs", entry.AudioDurationMs is { } ms ? ms : DBNull.Value);
            cmd.Parameters.AddWithValue("$isFile", isFile);
            cmd.Parameters.AddWithValue("$searchLabels", searchLabels);
            cmd.ExecuteNonQuery();
        }

        InsertFtsCore(connection, tx, entry, searchLabels);
    }

    private static void InsertFtsCore(
        SqliteConnection connection,
        SqliteTransaction tx,
        DictationHistoryEntry entry,
        string? precomputedSearchLabels = null)
    {
        var searchLabels = precomputedSearchLabels ?? BuildSearchLabels(entry);
        using var fts = connection.CreateCommand();
        fts.Transaction = tx;
        fts.CommandText =
            """
            INSERT INTO history_fts (
                entry_id, text, original_text, engine_id, polish_model_id, polish_provider_id, search_labels
            ) VALUES (
                $id, $text, $originalText, $engineId, $modelId, $providerId, $searchLabels
            );
            """;
        fts.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        fts.Parameters.AddWithValue("$text", entry.Text);
        fts.Parameters.AddWithValue("$originalText", entry.OriginalText ?? string.Empty);
        fts.Parameters.AddWithValue("$engineId", entry.EngineId);
        fts.Parameters.AddWithValue("$modelId", entry.PolishModelId ?? string.Empty);
        fts.Parameters.AddWithValue("$providerId", entry.PolishProviderId ?? string.Empty);
        fts.Parameters.AddWithValue("$searchLabels", searchLabels);
        fts.ExecuteNonQuery();
    }

    private static DictationHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        var id = Guid.Parse(reader.GetString(0));
        var createdAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var text = reader.GetString(2);
        var engineId = reader.GetString(3);
        var wasPolished = reader.GetInt64(4) != 0;
        var wasDelivered = reader.GetInt64(5) != 0;
        DateTimeOffset? expiresAt = reader.IsDBNull(6)
            ? null
            : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var originalText = reader.IsDBNull(7) ? null : reader.GetString(7);
        var languageCode = reader.IsDBNull(8) ? null : reader.GetString(8);
        var providerId = reader.IsDBNull(9) ? null : reader.GetString(9);
        var modelId = reader.IsDBNull(10) ? null : reader.GetString(10);
        var audioFileName = reader.IsDBNull(11) ? null : reader.GetString(11);
        long? audioDurationMs = reader.IsDBNull(12) ? null : reader.GetInt64(12);

        return new DictationHistoryEntry(
            id,
            createdAt,
            text,
            engineId,
            wasPolished,
            wasDelivered,
            expiresAt,
            originalText,
            languageCode,
            providerId,
            modelId,
            audioFileName,
            audioDurationMs);
    }

    private static bool IsFileTranscription(DictationHistoryEntry entry) =>
        entry.EngineId.Contains("file", StringComparison.OrdinalIgnoreCase)
        || entry.EngineId.Contains("transcribe", StringComparison.OrdinalIgnoreCase);

    internal static string BuildSearchLabels(DictationHistoryEntry entry)
    {
        var sb = new StringBuilder();
        void Append(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(value.Trim());
        }

        if (!string.IsNullOrWhiteSpace(entry.EngineId))
        {
            Append(TranscriptionEngineNames.DisplayName(entry.EngineId));
            if (entry.EngineId.Contains("parakeet", StringComparison.OrdinalIgnoreCase))
            {
                Append(TranscriptionEngineNames.Parakeet);
            }
            else if (entry.EngineId.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            {
                Append(TranscriptionEngineNames.Whisper);
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.PolishModelId))
        {
            var normalizedModel = entry.PolishModelId.Trim().ToLowerInvariant();
            Append(normalizedModel switch
            {
                "eg-1" or "eg1" => "EG-1",
                "eg-1-mini" or "eg1-mini" => "EG-1 Mini",
                "eg-1-micro" or "eg1-micro" => "EG-1 Micro",
                _ => entry.PolishModelId,
            });
        }

        if (!string.IsNullOrWhiteSpace(entry.PolishProviderId))
        {
            var normalizedProvider = entry.PolishProviderId.Trim().ToLowerInvariant();
            Append(normalizedProvider switch
            {
                "eg_one" or "egone" or "enviousegone" => "EG-1",
                "ollama" => "Ollama",
                "openai" => "OpenAI",
                "anthropic" => "Anthropic",
                "gemini" => "Gemini",
                _ => null,
            });
        }

        if (!string.IsNullOrWhiteSpace(entry.LanguageCode))
        {
            Append(LanguageCatalog.DisplayNameFor(entry.LanguageCode));
        }

        return sb.ToString();
    }

    private static HashSet<string> CollectAllAudioFiles(SqliteConnection connection)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT audio_file_name FROM history_entries WHERE audio_file_name IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var file = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(file))
            {
                files.Add(file);
            }
        }

        return files;
    }

    private static List<string> CollectAudioFilesForIds(SqliteConnection connection, IReadOnlyList<string> ids)
    {
        var files = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT audio_file_name FROM history_entries WHERE id = $id AND audio_file_name IS NOT NULL;";
        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids)
        {
            idParam.Value = id;
            if (cmd.ExecuteScalar() is string file && !string.IsNullOrWhiteSpace(file))
            {
                files.Add(file);
            }
        }

        return files;
    }

    private static HashSet<string> CollectRemainingAudioFilesExcludingIds(
        SqliteConnection connection,
        IReadOnlyList<string> excludedIds)
    {
        var excludedSet = new HashSet<string>(excludedIds, StringComparer.Ordinal);
        var surviving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, audio_file_name FROM history_entries WHERE audio_file_name IS NOT NULL;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!excludedSet.Contains(reader.GetString(0)))
            {
                var file = reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(file))
                {
                    surviving.Add(file);
                }
            }
        }

        return surviving;
    }

    private static int ScalarInt(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    private void DeleteDatabaseArtifactsQuietly()
    {
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
            }
        }
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
