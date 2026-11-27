using System.Globalization;
using System.Security;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using Microsoft.Data.Sqlite;

namespace EnviousWispr.Services.History;

public sealed partial class SqliteHistoryStore
{
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

            if (connection is not null)
            {
                try
                {
                    using var checkpoint = connection.CreateCommand();
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    checkpoint.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                }
            }

            return new HistoryOperationResult(true);
        }
        catch (Exception exception)
        {
            return MapSaveException(exception);
        }
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
}
