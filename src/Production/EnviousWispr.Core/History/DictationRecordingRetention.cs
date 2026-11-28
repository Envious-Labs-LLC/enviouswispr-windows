namespace EnviousWispr.Core.History;

/// <summary>
/// Decides which local dictation WAV recordings to remove so storage stays bounded on disk.
/// </summary>
public static class DictationRecordingRetention
{
    /// <summary>Default maximum number of saved dictation WAV recordings retained locally.</summary>
    public const int DefaultMaximumRecordings = 100;

    /// <summary>Default maximum total byte budget retained locally for dictation WAV recordings.</summary>
    public const long DefaultMaximumTotalBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Returns the paths to delete, oldest first, so both count and total byte budgets hold.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(
        IReadOnlyList<(string Path, DateTimeOffset Written, long Bytes)> existing,
        int maxRecordings = DefaultMaximumRecordings,
        long maxTotalBytes = DefaultMaximumTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.Count == 0)
        {
            return [];
        }

        var countLimit = Math.Max(0, maxRecordings);
        var byteLimit = Math.Max(0L, maxTotalBytes);

        var ordered = existing
            .OrderByDescending(file => file.Written)
            .ThenByDescending(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        var keptCount = 0;
        var keptBytes = 0L;
        var toDelete = new List<string>();
        foreach (var file in ordered)
        {
            var fileBytes = Math.Max(0L, file.Bytes);
            var fitsCount = keptCount < countLimit;
            var fitsBytes = keptBytes + fileBytes <= byteLimit;
            if (fitsCount && fitsBytes)
            {
                keptCount++;
                keptBytes += fileBytes;
            }
            else
            {
                toDelete.Add(file.Path);
            }
        }

        return toDelete;
    }
}
