using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;

namespace EnviousWispr.Services.History;

/// <summary>
/// Persists local-only 16-bit PCM WAV dictation recordings under <see cref="RecordingsDirectory"/>
/// using bounded count and byte retention.
/// </summary>
public sealed class LocalDictationAudioArchive : IDictationAudioArchive
{
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
            EnforceRetentionExcluding(fileName, bytes.LongLength);

            var durationMs = WaveFile.ComputeDurationMs(audio.Samples.Length, audio.SampleRate);
            return new ArchivedDictationAudio(fileName, durationMs);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            TryDeletePath(tempPath);
        }
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

    private string? ResolveSafePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(RecordingsDirectory, fileName));
        var rootWithSep = RecordingsDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? RecordingsDirectory
            : RecordingsDirectory + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private void EnforceRetentionExcluding(string currentFileName, long currentFileBytes)
    {
        if (!Directory.Exists(RecordingsDirectory))
        {
            return;
        }

        var existing = new List<(string Path, DateTimeOffset Written, long Bytes)>();
        foreach (var path in Directory.EnumerateFiles(RecordingsDirectory, "*.wav"))
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, currentFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                existing.Add((path, info.LastWriteTimeUtc, info.Length));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var remainingCount = _maxRecordings - 1;
        var remainingBytes = Math.Max(0L, _maxTotalBytes - Math.Max(0L, currentFileBytes));
        foreach (var stale in DictationRecordingRetention.ToDelete(existing, remainingCount, remainingBytes))
        {
            TryDeletePath(stale);
        }
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
