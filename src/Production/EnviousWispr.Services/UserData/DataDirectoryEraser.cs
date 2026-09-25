using System.Security;
using System.Text.Json;

namespace EnviousWispr.Services.UserData;

/// <summary>What one erasure of the data directory did, counted, never named.</summary>
/// <param name="Removed">Entries deleted: files, folders and links.</param>
/// <param name="Remaining">Entries still under the root when the pass finished, counted by walking it again afterwards.</param>
/// <param name="Refused">Entries not touched because they did not resolve strictly inside the root, or the root itself was refused.</param>
public sealed record DataDeletionReport(int Removed, int Remaining, int Refused)
{
    /// <summary>True only when the walk afterwards found the root empty and nothing was refused.</summary>
    public bool Complete => Remaining == 0 && Refused == 0;
}

/// <summary>What the last erasure could not remove, as the next launch reads it.</summary>
/// <param name="Remaining">How many entries were left in the data directory.</param>
/// <param name="CredentialsRemaining">Whether stored API keys could not be removed from Credential Manager.</param>
public sealed record DataDeletionLeftover(int Remaining, bool CredentialsRemaining);

/// <summary>
/// Empties the app's data directory, one entry at a time, for "Delete all EnviousWispr data".
/// </summary>
/// <remarks>
/// NEVER A RECURSIVE DELETE OF THE ROOT. `Directory.Delete(root, true)` answers a question about a
/// path, and the set of ways a junction, a symbolic link or a mount can sit under that path is not
/// something a caller can enumerate by testing the path first. So the root is walked: every entry is
/// resolved to a full path and must sit STRICTLY inside the root before it is touched; a reparse point
/// is deleted as the link itself and never entered, so its target survives whatever it points at; a
/// real folder is emptied the same way and then removed on its own. Then the root is walked again and
/// whatever is still there is counted, because the answer is the world afterwards, not the exit codes.
///
/// THE ROOT ITSELF IS KEPT, AND REFUSED WHEN IT IS NOT A PLAIN FOLDER. A data directory that is itself
/// a link or a drive root is not something this app created, and emptying it would reach data the
/// link's author put somewhere else. Nothing is deleted then, and the report says so.
///
/// A LOCKED FILE IS COUNTED, NOT RETRIED FOREVER. The caller decides whether to walk again; the report
/// never claims an empty directory the second walk did not find.
/// </remarks>
public static class DataDirectoryEraser
{
    /// <summary>The note an incomplete erasure leaves for the next launch, in the data directory it could not empty.</summary>
    public const string LeftoverFileName = "data-deletion-incomplete.json";

    private const int LeftoverSchemaVersion = 1;

    // EVERY ATTRIBUTE IS SEEN. The enumeration's defaults skip hidden and system entries in some
    // overloads, and a skipped entry is one the walk afterwards would also skip - an entry left behind
    // that nothing counts.
    private static readonly EnumerationOptions EveryEntry = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static readonly JsonSerializerOptions LeftoverJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Deletes every entry inside <paramref name="root"/>, keeping the root, and reports what the walk afterwards found.</summary>
    public static DataDeletionReport Erase(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Normalize(root);
        switch (Classify(fullRoot))
        {
            case RootKind.Missing:
                return new DataDeletionReport(Removed: 0, Remaining: 0, Refused: 0);
            case RootKind.Refused:
                // NOT WALKED, NOT EVEN TO COUNT. Counting through a link would read its target, and a
                // refused root is one this app does not own the inside of. Refused alone makes the
                // report incomplete.
                return new DataDeletionReport(Removed: 0, Remaining: 0, Refused: 1);
        }

        var removed = 0;
        var refused = 0;
        EmptyDirectory(fullRoot, fullRoot, ref removed, ref refused);
        return new DataDeletionReport(removed, CountRemaining(fullRoot, fullRoot, isRoot: true), refused);
    }

    /// <summary>
    /// True when <paramref name="candidate"/>, resolved, is strictly inside <paramref name="root"/>:
    /// never the root itself, never a sibling whose name merely starts with the root's, never a path
    /// that climbs out with "..".
    /// </summary>
    internal static bool IsStrictlyInside(string root, string candidate)
    {
        var fullRoot = Normalize(root);
        var fullCandidate = Normalize(candidate);
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.Length > prefix.Length &&
            fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Leaves the next launch a count of what was left behind. No path, no name: a number and a flag.</summary>
    public static void WriteLeftover(string root, DataDeletionLeftover leftover)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        var fullRoot = Normalize(root);
        if (Classify(fullRoot) == RootKind.Refused)
        {
            // A ROOT ERASE REFUSED IS A ROOT THIS NEVER WRITES INTO EITHER: the note would land wherever
            // the link points. The caller logs the refusal instead.
            return;
        }

        try
        {
            Directory.CreateDirectory(fullRoot);
            var json = JsonSerializer.Serialize(
                new LeftoverDocument(LeftoverSchemaVersion, leftover.Remaining, leftover.CredentialsRemaining),
                LeftoverJson);
            File.WriteAllText(Path.Combine(fullRoot, LeftoverFileName), json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A NOTE THAT CANNOT BE WRITTEN IS A NOTE NOT LEFT. The erasure already happened; the
            // next launch then simply has nothing to say, which is the most this can promise from
            // a directory it could not empty.
        }
    }

    /// <summary>Reads and removes the note an incomplete erasure left, or null when there is none worth reading.</summary>
    public static DataDeletionLeftover? TakeLeftover(string root)
    {
        var fullRoot = Normalize(root);
        if (Classify(fullRoot) != RootKind.PlainFolder)
        {
            return null;
        }

        var path = Path.Combine(fullRoot, LeftoverFileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<LeftoverDocument>(File.ReadAllText(path), LeftoverJson);
            File.Delete(path);
            return document is { SchemaVersion: LeftoverSchemaVersion, Remaining: >= 0 }
                ? new DataDeletionLeftover(document.Remaining, document.CredentialsRemaining)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return null;
        }
    }

    private static void EmptyDirectory(string root, string directory, ref int removed, ref int refused)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            // MATERIALISED BEFORE ANYTHING IS DELETED, so the walk is over a list rather than a live
            // enumeration of a directory it is changing.
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EveryEntry).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(entry.FullName);
            if (!IsStrictlyInside(root, path))
            {
                refused++;
                continue;
            }

            try
            {
                // READ AGAIN AT THE MOMENT OF ACTING, not trusted from the listing: a folder swapped
                // for a link between the two must be treated as the link it now is.
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // THE LINK, NEVER ITS TARGET. Removing a junction or a directory symbolic link
                    // removes the name; a file symbolic link likewise. Neither call descends.
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        Directory.Delete(path, recursive: false);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }
                else if (attributes.HasFlag(FileAttributes.Directory))
                {
                    EmptyDirectory(root, path, ref removed, ref refused);
                    ClearReadOnly(path, attributes);
                    Directory.Delete(path, recursive: false);
                }
                else
                {
                    ClearReadOnly(path, attributes);
                    File.Delete(path);
                }

                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // IN USE OR DENIED. Left where it is; the walk afterwards counts it.
            }
        }
    }

    /// <summary>Counts what is still under the root, entering real folders only, so a link's target is never counted or reached.</summary>
    private static int CountRemaining(string root, string directory, bool isRoot)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EveryEntry).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A FOLDER THAT CANNOT BE LISTED IS NOT KNOWN TO BE EMPTY. Below the root it was already
            // counted as an entry of its parent; the root itself counts as one thing left.
            return isRoot ? 1 : 0;
        }

        var count = 0;
        foreach (var entry in entries)
        {
            count++;
            if (entry.Attributes.HasFlag(FileAttributes.Directory) &&
                !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                IsStrictlyInside(root, entry.FullName))
            {
                count += CountRemaining(root, Path.GetFullPath(entry.FullName), isRoot: false);
            }
        }

        return count;
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>True when the root is a plain folder or truly absent, the two cases Erase may act on or call empty.</summary>
    public static bool CanErase(string root) => Classify(Normalize(root)) != RootKind.Refused;

    /// <summary>What the root path is, read from the path's OWN attributes, which do not follow a link.</summary>
    /// <remarks>
    /// ONLY A PATH THAT IS NOT THERE AT ALL IS MISSING. `Directory.Exists` answers false for a file of
    /// that name, and treating that as "nothing to delete" reported an erasure complete that had removed
    /// nothing. Attributes are read from the path itself, never through a link, so a link (a dangling
    /// junction included) is refused; so is a file, a drive root, and anything unreadable.
    /// </remarks>
    private static RootKind Classify(string fullRoot)
    {
        if (IsDriveRoot(fullRoot))
        {
            return RootKind.Refused;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullRoot);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return RootKind.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // NOT KNOWN TO BE A PLAIN FOLDER, SO NOT TREATED AS ONE.
            return RootKind.Refused;
        }

        return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)
            ? RootKind.PlainFolder
            : RootKind.Refused;
    }

    private static bool IsDriveRoot(string fullPath) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullPath) ?? string.Empty),
            fullPath,
            StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private enum RootKind
    {
        Missing,
        PlainFolder,
        Refused,
    }

    private sealed record LeftoverDocument(int SchemaVersion, int Remaining, bool CredentialsRemaining);
}
