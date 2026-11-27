using System.Security;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace EnviousWispr.Services.History;

public sealed partial class SqliteHistoryStore
{
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
        // Ensure migrated WAL frames are durably flushed to the main database file before deleting the legacy JSON source.
        using (var durable = connection.CreateCommand())
        {
            durable.CommandText =
                """
                PRAGMA synchronous=FULL;
                PRAGMA wal_checkpoint(TRUNCATE);
                PRAGMA synchronous=NORMAL;
                """;
            durable.ExecuteNonQuery();
        }

        TryDeleteFileQuietly(_legacyJsonPath!);
        return null;
    }

    private static bool HasCompletedLegacyMigration(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM history_meta WHERE key = 'legacy_json_migrated' LIMIT 1;";
        return string.Equals(cmd.ExecuteScalar() as string, "1", StringComparison.Ordinal);
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
}
