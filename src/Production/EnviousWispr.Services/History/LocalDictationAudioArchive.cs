using System.Security;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;

namespace EnviousWispr.Services.History;

/// <summary>
/// Persists local-only 16-bit PCM WAV dictation recordings under <see cref="RecordingsDirectory"/>
/// using bounded count and byte retention.
/// </summary>
public sealed class LocalDictationAudioArchive : IDictationAudioArchive
{
    /// <summary>Grace window protecting in-flight atomic temp writes from orphan cleanup during pruning.</summary>
    public static readonly TimeSpan DefaultStaleTempFileAge = TimeSpan.FromMinutes(10);

    private readonly int _maxRecordings;
    private readonly long _maxTotalBytes;

    public LocalDictationAudioArchive(
        string recordingsDirectory,
        int maxRecordings = DictationRecordingRetention.DefaultMaximumRecordings,
        long maxTotalBytes = DictationRecordingRetention.DefaultMaximumTotalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsDirectory);
        RecordingsDirectory = Path.GetFullPath(recordingsDirectory);
        _maxRecordings = Math.Max(1, maxRecordings);
        _maxTotalBytes = Math.Max(0L, maxTotalBytes);
    }

    public string RecordingsDirectory { get; }

    public ArchivedDictationAudio? SaveForEntry(Guid entryId, CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (entryId == Guid.Empty || audio.SampleRate <= 0 || audio.Samples.IsEmpty)
        {
            return null;
        }

        var fileName = BuildFileName(entryId);
        var targetPath = ResolveSafePath(fileName);
        if (targetPath is null)
        {
            return null;
        }

        var tempPath = Path.Combine(
            RecordingsDirectory,
            $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(RecordingsDirectory);
            var bytes = WaveFile.EncodeMono(audio.Samples.Span, audio.SampleRate);
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, targetPath, overwrite: true);

            var durationMs = WaveFile.ComputeDurationMs(audio.Samples.Length, audio.SampleRate);
            return new ArchivedDictationAudio(fileName, durationMs);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return null;
        }
        finally
        {
            TryDeletePath(tempPath);
        }
    }

    public void PruneRetention(string? keepFileName = null, long keepBytes = 0)
    {
        try
        {
            if (!Directory.Exists(RecordingsDirectory))
            {
                return;
            }

            CleanupTempFiles(RecordingsDirectory, DefaultStaleTempFileAge, DateTimeOffset.UtcNow);

            if (keepFileName is { Length: > 0 } keptName && keepBytes <= 0)
            {
                var keptPath = ResolveSafePath(keptName);
                if (keptPath is not null && File.Exists(keptPath))
                {
                    try
                    {
                        keepBytes = new FileInfo(keptPath).Length;
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or SecurityException)
                    {
                    }
                }
            }

            var existing = new List<(string Path, DateTimeOffset Written, long Bytes)>();
            foreach (var path in Directory.EnumerateFiles(RecordingsDirectory, "*.wav"))
            {
                var name = Path.GetFileName(path);
                if (keepFileName is { Length: > 0 } protectedName
                    && string.Equals(name, protectedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    existing.Add((path, info.LastWriteTimeUtc, info.Length));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or SecurityException)
                {
                }
            }

            var hasProtected = keepFileName is { Length: > 0 };
            var remainingCount = hasProtected ? _maxRecordings - 1 : _maxRecordings;
            var remainingBytes = hasProtected
                ? Math.Max(0L, _maxTotalBytes - Math.Max(0L, keepBytes))
                : _maxTotalBytes;
            foreach (var stale in DictationRecordingRetention.ToDelete(existing, remainingCount, remainingBytes))
            {
                TryDeletePath(stale);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    public void ClearAll() => DeleteAllFiles(RecordingsDirectory);

    public static void DeleteAllFiles(string recordingsDirectory) =>
        _ = DeleteAllFilesWithOutcome(recordingsDirectory);

    internal static AppErrorCode? DeleteAllFilesWithOutcome(string recordingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(recordingsDirectory) || !Directory.Exists(recordingsDirectory))
        {
            return null;
        }

        AppErrorCode? firstError = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(recordingsDirectory, "*.wav"))
            {
                firstError ??= TryDeletePathOutcome(path);
            }

            firstError ??= CleanupTempFiles(recordingsDirectory, maxAge: null, DateTimeOffset.UtcNow);
        }
        catch (IOException)
        {
            firstError ??= AppErrorCode.StorageUnavailable;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            firstError ??= AppErrorCode.AccessDenied;
        }

        return firstError;
    }

    public void DeleteFile(string? fileName)
    {
        var path = ResolveSafePath(fileName);
        if (path is not null)
        {
            TryDeletePath(path);
        }
    }

    public string? ResolveExistingPath(string? fileName)
    {
        var path = ResolveSafePath(fileName);
        return path is not null && File.Exists(path) ? path : null;
    }

    public static string BuildFileName(Guid entryId) => $"{entryId:D}.wav";

    internal static AppErrorCode? TryDeleteNamedFileOutcome(string recordingsDirectory, string? fileName)
    {
        var path = ResolveSafePath(recordingsDirectory, fileName);
        return path is null ? null : TryDeletePathOutcome(path);
    }

    private static AppErrorCode? CleanupTempFiles(string recordingsDirectory, TimeSpan? maxAge, DateTimeOffset now)
    {
        AppErrorCode? firstError = null;
        foreach (var path in Directory.EnumerateFiles(recordingsDirectory, "*.tmp"))
        {
            if (maxAge is { } threshold)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || now - info.LastWriteTimeUtc < threshold)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or SecurityException)
                {
                    continue;
                }
            }

            firstError ??= TryDeletePathOutcome(path);
        }

        return firstError;
    }

    private string? ResolveSafePath(string? fileName) =>
        ResolveSafePath(RecordingsDirectory, fileName);

    internal static string? ResolveSafePath(string recordingsDirectory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(recordingsDirectory)
            || string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(recordingsDirectory);
        var full = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static void TryDeletePath(string path) => _ = TryDeletePathOutcome(path);

    internal static AppErrorCode? TryDeletePathOutcome(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return null;
        }
        catch (IOException)
        {
            return AppErrorCode.StorageUnavailable;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return AppErrorCode.AccessDenied;
        }
    }
}
