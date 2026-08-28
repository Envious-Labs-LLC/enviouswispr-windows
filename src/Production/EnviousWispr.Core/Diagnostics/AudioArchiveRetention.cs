namespace EnviousWispr.Core.Diagnostics;

/// <summary>
/// Decides which archived dictation recordings to delete, oldest first.
/// </summary>
/// <remarks>
/// THE ARCHIVE IS A DEBUG-BUILD TOOL FOR REPRODUCING A BAD TRANSCRIPT. Without it, "the app heard
/// that wrong" is unreproducible: the audio is gone the moment the dictation finishes, so the only
/// evidence is the wrong text and a memory of what was said.
///
/// IT IS BOUNDED BY COUNT RATHER THAN BY AGE, and the difference matters on a machine nobody is
/// watching. An age bound keeps everything from a busy afternoon and can fill a disk; a count bound
/// has a maximum size that can be stated in advance and does not depend on how much the user
/// dictated. A debug tool that fills someone's disk is a worse bug than the one it was helping to
/// find.
///
/// PRIVACY: this is audio, so it is the most sensitive thing the app touches. It never leaves the
/// machine, it exists only in DEBUG builds, and it is bounded so it cannot quietly accumulate. The
/// network boundary is untouched - nothing here is uploaded, and the archive is not readable by
/// anything that talks to a network.
/// </remarks>
public static class AudioArchiveRetention
{
    /// <summary>How many recordings the archive keeps.</summary>
    /// <remarks>
    /// Enough to cover a debugging session - a handful of attempts at reproducing one problem -
    /// and small enough that the archive's maximum size is a number somebody can hold in their
    /// head rather than a function of how the machine has been used.
    /// </remarks>
    public const int MaximumRecordings = 20;

    /// <summary>
    /// Which files to delete so the archive stays within its bound, oldest first.
    /// </summary>
    /// <param name="existing">
    /// The archive's files with their write times, in any order.
    /// </param>
    /// <remarks>
    /// Returns the files to DELETE rather than the ones to keep, deliberately. A caller handed the
    /// keepers has to work out the complement itself, and the obvious way to do that - delete
    /// everything not in the list - deletes anything that appeared between the two operations.
    /// </remarks>
    public static IReadOnlyList<string> ToDelete(IReadOnlyList<(string Path, DateTimeOffset Written)> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.Count <= MaximumRecordings)
        {
            return [];
        }

        return existing
            .OrderByDescending(file => file.Written)
            // Ordered by time and then by PATH, because two recordings can share a write time on a
            // machine with a coarse clock, and an unstable order would make the same archive delete
            // different files on different runs - which is the shape of bug nobody reproduces.
            .ThenByDescending(file => file.Path, StringComparer.Ordinal)
            .Skip(MaximumRecordings)
            .Select(file => file.Path)
            .ToArray();
    }
}
