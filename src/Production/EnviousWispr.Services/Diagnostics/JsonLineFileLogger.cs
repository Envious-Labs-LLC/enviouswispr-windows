using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Diagnostics;

public sealed class JsonLineFileLogger : IAppLogger
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private const long TargetTrimmedBytes = 4 * 1024 * 1024;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _writeLock = new();
    private readonly string _path;
    private bool _enabled;
    private int _retentionDays;

    public JsonLineFileLogger(
        string path,
        bool enabled = true,
        int retentionDays = 14)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retentionDays, RetentionDays.DiagnosticMaximum);
        _path = Path.GetFullPath(path);
        _enabled = enabled;
        _retentionDays = retentionDays;
    }

    public void Write(AppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        WriteRecord(PrivacySafeDiagnosticRecord.From(entry));
    }

    public void Configure(ObservabilityPreferences preferences, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentOutOfRangeException.ThrowIfLessThan(preferences.DiagnosticRetentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            preferences.DiagnosticRetentionDays,
            RetentionDays.DiagnosticMaximum);

        lock (_writeLock)
        {
            _enabled = preferences.LocalDiagnosticsEnabled;
            _retentionDays = preferences.DiagnosticRetentionDays;
            try
            {
                PruneUnsafe(now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Diagnostics are best-effort and can never break settings or startup.
            }
        }
    }

    internal void WriteRecord(PrivacySafeDiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            lock (_writeLock)
            {
                if (!_enabled)
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var line = Serialize(record) + Environment.NewLine;
                File.AppendAllText(_path, line);
                if (new FileInfo(_path).Length > MaximumFileBytes)
                {
                    TrimToCapacityUnsafe();
                }
            }
        }
        catch (IOException)
        {
            // Diagnostics are best-effort and can never break dictation or startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics are best-effort and can never break dictation or startup.
        }
        catch (SecurityException)
        {
            // Diagnostics are best-effort and can never break dictation or startup.
        }
    }

    internal static bool TryParseRecord(string line, out PrivacySafeDiagnosticRecord? record)
    {
        try
        {
            record = JsonSerializer.Deserialize<PrivacySafeDiagnosticRecord>(line, SerializerOptions);
            return record is not null &&
                Enum.IsDefined(record.Event) &&
                Enum.IsDefined(record.Failure) &&
                (record.Provider is null || Enum.IsDefined(record.Provider.Value)) &&
                (record.ErrorCode is null || Enum.IsDefined(record.ErrorCode.Value)) &&
                (record.Engine is null || Enum.IsDefined(record.Engine.Value)) &&
                (record.HardwareClass is null || Enum.IsDefined(record.HardwareClass.Value)) &&
                record.ElapsedMilliseconds is null or
                    (>= 0 and <= PrivacySafeDiagnosticRecord.MaximumElapsedMilliseconds);
        }
        catch (JsonException)
        {
            record = null;
            return false;
        }
    }

    internal static string Serialize(PrivacySafeDiagnosticRecord record) =>
        JsonSerializer.Serialize(record, SerializerOptions);

    private void PruneUnsafe(DateTimeOffset now)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var cutoff = now - TimeSpan.FromDays(_retentionDays);
        RewriteValidRecordsUnsafe(record => record.Timestamp >= cutoff, long.MaxValue);
    }

    private void TrimToCapacityUnsafe() =>
        RewriteValidRecordsUnsafe(_ => true, TargetTrimmedBytes, newestFirst: true);

    private void RewriteValidRecordsUnsafe(
        Func<PrivacySafeDiagnosticRecord, bool> keep,
        long maximumBytes,
        bool newestFirst = false)
    {
        var records = File.ReadLines(_path)
            .Select(line => TryParseRecord(line, out var record) ? record : null)
            .Where(record => record is not null && keep(record))
            .Cast<PrivacySafeDiagnosticRecord>()
            .ToArray();
        if (newestFirst)
        {
            Array.Reverse(records);
        }

        var selected = new List<string>(records.Length);
        long bytes = 0;
        foreach (var record in records)
        {
            var line = Serialize(record);
            var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line + Environment.NewLine);
            if (bytes + lineBytes > maximumBytes)
            {
                break;
            }

            selected.Add(line);
            bytes += lineBytes;
        }

        if (newestFirst)
        {
            selected.Reverse();
        }

        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporaryPath, selected);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
