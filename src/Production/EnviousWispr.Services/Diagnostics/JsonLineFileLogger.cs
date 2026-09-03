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
        // The dictation is read from the ambient scope rather than from the entry, so a caller does
        // not have to know about it and cannot forget it. Outside a dictation there is none, and the
        // field is simply absent from the line.
        WriteRecord(PrivacySafeDiagnosticRecord.From(entry), DictationScope.Current);
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

    internal void WriteRecord(PrivacySafeDiagnosticRecord record) =>
        WriteRecord(record, DictationScope.Current);

    internal void WriteRecord(PrivacySafeDiagnosticRecord record, Guid? dictationId)
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

                var line = Serialize(LocalDiagnosticLine.From(record, dictationId))
                    + Environment.NewLine;
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

    /// <summary>Reads one line back, keeping everything the line said.</summary>
    /// <remarks>
    /// THE LOCAL TYPE ALL THE WAY THROUGH, BECAUSE PRUNING REWRITES THE FILE. Retention and the
    /// size trim both read every line, drop the ones they do not want, and write the rest back. Parse
    /// as one type and serialise as another and that rewrite strips the field it does not know about,
    /// so every dictation id would vanish on the first prune - two weeks later, or the first time the
    /// log grew, with nothing to say it had happened.
    /// </remarks>
    internal static bool TryParseRecord(string line, out LocalDiagnosticLine? record)
    {
        try
        {
            record = JsonSerializer.Deserialize<LocalDiagnosticLine>(line, SerializerOptions);
            return record is not null &&
                Enum.IsDefined(record.Event) &&
                Enum.IsDefined(record.Failure) &&
                (record.Provider is null || Enum.IsDefined(record.Provider.Value)) &&
                (record.ErrorCode is null || Enum.IsDefined(record.ErrorCode.Value)) &&
                (record.Engine is null || Enum.IsDefined(record.Engine.Value)) &&
                (record.HardwareClass is null || Enum.IsDefined(record.HardwareClass.Value)) &&
                // JsonStringEnumConverter ACCEPTS INTEGERS unless told otherwise, so a corrupted or
                // hand-edited line reading "stage":97 deserialises happily and would then survive a
                // prune and reach an export. Every other enum on this line is checked here; leaving
                // two out would make the strict read-back rule true only of the fields somebody
                // remembered.
                (record.Stage is null || Enum.IsDefined(record.Stage.Value)) &&
                (record.StageStatus is null || Enum.IsDefined(record.StageStatus.Value)) &&
                (record.RuntimeSelection is null || Enum.IsDefined(record.RuntimeSelection.Value)) &&
                record.ElapsedMilliseconds is null or
                    (>= 0 and <= PrivacySafeDiagnosticRecord.MaximumElapsedMilliseconds);
        }
        catch (JsonException)
        {
            record = null;
            return false;
        }
    }

    internal static string Serialize(LocalDiagnosticLine record) =>
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
        Func<LocalDiagnosticLine, bool> keep,
        long maximumBytes,
        bool newestFirst = false)
    {
        var records = File.ReadLines(_path)
            .Select(line => TryParseRecord(line, out var record) ? record : null)
            .Where(record => record is not null && keep(record))
            .Cast<LocalDiagnosticLine>()
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
