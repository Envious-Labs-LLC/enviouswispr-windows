using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.History;

namespace EnviousWispr.Services.History;

internal sealed class HistoryAudioDeleteJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _recordingsDirectory;
    private readonly string _pendingAudioDeletesPath;

    public HistoryAudioDeleteJournal(string recordingsDirectory, string pendingAudioDeletesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingAudioDeletesPath);
        _recordingsDirectory = Path.GetFullPath(recordingsDirectory);
        _pendingAudioDeletesPath = Path.GetFullPath(pendingAudioDeletesPath);
    }

    public string RecordingsDirectory => _recordingsDirectory;

    public static HashSet<string> BuildReferencedAudioSet(IEnumerable<DictationHistoryEntry> entries)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.AudioFileName))
            {
                referenced.Add(entry.AudioFileName);
            }
        }

        return referenced;
    }

    public static List<string> CollectRemovedAudioFileNames(
        IReadOnlyList<DictationHistoryEntry> before,
        IReadOnlyList<DictationHistoryEntry> after)
    {
        if (before.Count == 0)
        {
            return [];
        }

        var survivingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in after)
        {
            if (!string.IsNullOrWhiteSpace(entry.AudioFileName))
            {
                survivingFiles.Add(entry.AudioFileName);
            }
        }

        var removed = new List<string>();
        foreach (var entry in before)
        {
            if (!string.IsNullOrWhiteSpace(entry.AudioFileName)
                && !survivingFiles.Contains(entry.AudioFileName))
            {
                removed.Add(entry.AudioFileName);
            }
        }

        return removed;
    }

    public void DrainPendingAudioDeletes(IEnumerable<string> newlyRemovedFileNames)
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in ReadPendingDeletesManifest())
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                pending.Add(fileName);
            }
        }

        foreach (var fileName in newlyRemovedFileNames)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                pending.Add(fileName);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var stillBlocked = new List<string>();
        foreach (var fileName in pending)
        {
            if (LocalDictationAudioArchive.TryDeleteNamedFileOutcome(_recordingsDirectory, fileName) is not null)
            {
                stillBlocked.Add(fileName);
            }
        }

        if (stillBlocked.Count == 0)
        {
            DeletePendingDeletesManifest();
        }
        else
        {
            WritePendingDeletesManifest(stillBlocked);
        }
    }

    public void DeletePendingDeletesManifest()
    {
        try
        {
            if (File.Exists(_pendingAudioDeletesPath))
            {
                File.Delete(_pendingAudioDeletesPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    private string[] ReadPendingDeletesManifest()
    {
        try
        {
            if (!File.Exists(_pendingAudioDeletesPath))
            {
                return [];
            }

            var json = File.ReadAllText(_pendingAudioDeletesPath);
            return JsonSerializer.Deserialize<string[]>(json, SerializerOptions) ?? [];
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    private void WritePendingDeletesManifest(IReadOnlyList<string> fileNames)
    {
        try
        {
            var directory = Path.GetDirectoryName(_pendingAudioDeletesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(fileNames, SerializerOptions);
            File.WriteAllText(_pendingAudioDeletesPath, json);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }
}
