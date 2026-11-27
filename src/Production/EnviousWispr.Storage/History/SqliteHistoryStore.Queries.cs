using System.Globalization;
using System.Security;
using System.Text;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using Microsoft.Data.Sqlite;

namespace EnviousWispr.Services.History;

public sealed partial class SqliteHistoryStore
{
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
            var removedAudio = PruneExpiredAndOverflowCore(connection, query.RetentionDays, query.Now);
            _audioJournal.DrainPendingAudioDeletes(removedAudio);
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

    private static int ScalarInt(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
