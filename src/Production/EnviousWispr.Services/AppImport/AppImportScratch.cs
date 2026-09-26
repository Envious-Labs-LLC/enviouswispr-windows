namespace EnviousWispr.Services.AppImport;

/// <summary>Where a private copy of another app's store lives while it is read, and how it is taken away again.</summary>
/// <remarks>
/// A COPY OF SOMEBODY'S WORDS MUST NOT OUTLIVE THE READ. Every copy goes into its own folder named
/// <c>import-&lt;guid&gt;</c> under ONE app-owned parent in the temp folder (<see cref="DefaultParent"/>), and is
/// removed as soon as the read ends. A virus scanner or the search indexer can hold a just-written file open for a
/// moment, so each deletion is retried briefly; if a file still cannot go, the folder stays behind, and the next
/// launch's <see cref="SweepLeftovers()"/> removes it.
///
/// NAME FIRST, THEN ACT, AT EVERY LEVEL. Nothing is deleted or unlinked unless its NAME is one this reader makes:
/// <c>import-</c> plus a 32-digit GUID in the parent, a numbered attempt folder inside that, and the copied database
/// files inside an attempt. An entry of any other name is left exactly as it is, link or not, and makes the folder
/// holding it count as not removed. A link with an owned name is removed as the link, never walked. Never a
/// recursive delete: entries are deleted one by one and the disk is asked afterwards whether anything is left.
///
/// A LISTING THAT FAILS IS A FAILURE, NOT AN EMPTY FOLDER. A folder that cannot be read may hold a copy, so an
/// access or I/O error while listing makes the removal or the sweep report that something may be left.
/// </remarks>
public static class AppImportScratch
{
    internal const string FolderPrefix = "import-";

    private const int DeleteAttempts = 5;
    private static readonly TimeSpan DeletePause = TimeSpan.FromMilliseconds(100);

    /// <summary>The only file names a copy is ever written or indexed under (<see cref="WisprFlowDatabase"/>).</summary>
    private static readonly HashSet<string> CopyFileNames =
        new(["flow.sqlite", "flow.sqlite-wal", "flow.sqlite-shm", "flow.sqlite-journal"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The one parent every scratch copy lives under: <c>%TEMP%\EnviousWispr-app-import</c>.</summary>
    public static string DefaultParent { get; } = Path.Combine(Path.GetTempPath(), "EnviousWispr-app-import");

    /// <summary>A new, not yet created, scratch folder path for one read.</summary>
    internal static string NewFolder(string parent) =>
        Path.Combine(parent, FolderPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>Removes one scratch folder; true only when nothing of it is left on disk.</summary>
    internal static bool Remove(string scratch)
    {
        if (!IsOwnedFolderName(Path.GetFileName(scratch)))
        {
            return false;
        }

        if (IsLink(scratch))
        {
            Retry(() => Directory.Delete(scratch, recursive: false));
            return !Exists(scratch);
        }

        switch (Probe(scratch))
        {
            case Presence.Absent:
                return true;
            case Presence.Unknown:
                return false;
        }

        if (List(scratch) is not { } attempts)
        {
            return false;
        }

        foreach (var attempt in attempts)
        {
            // Only a numbered attempt folder; anything else is not ours and stays, which keeps the folder too.
            if (!IsAttemptName(Path.GetFileName(attempt)) || File.Exists(attempt))
            {
                continue;
            }

            if (IsLink(attempt))
            {
                Retry(() => Directory.Delete(attempt, recursive: false));
                continue;
            }

            if (List(attempt) is not { } files)
            {
                return false;
            }

            foreach (var file in files)
            {
                if (CopyFileNames.Contains(Path.GetFileName(file)) && File.Exists(file))
                {
                    Retry(() => File.Delete(file));
                }
            }

            Retry(() => Directory.Delete(attempt, recursive: false));
        }

        Retry(() => Directory.Delete(scratch, recursive: false));
        return !Exists(scratch);
    }

    /// <summary>Removes every leftover scratch folder under <see cref="DefaultParent"/>; false when one may be left.</summary>
    public static bool SweepLeftovers() => SweepLeftovers(DefaultParent);

    internal static bool SweepLeftovers(string parent)
    {
        switch (Probe(parent))
        {
            case Presence.Absent:
                return true;
            case Presence.Unknown:
                return false;
        }

        if (List(parent) is not { } entries)
        {
            return false;
        }

        var clean = true;
        foreach (var entry in entries)
        {
            // FILTERED BY NAME BEFORE ANYTHING IS TOUCHED: a link or folder of any other name is not ours.
            if (!IsOwnedFolderName(Path.GetFileName(entry)))
            {
                continue;
            }

            clean &= Remove(entry);
        }

        return clean;
    }

    /// <summary><c>import-</c> followed by exactly the 32 hex digits <see cref="NewFolder"/> writes.</summary>
    internal static bool IsOwnedFolderName(string name) =>
        name.StartsWith(FolderPrefix, StringComparison.Ordinal) &&
        name.Length == FolderPrefix.Length + 32 &&
        Guid.TryParseExact(name.AsSpan(FolderPrefix.Length), "N", out _);

    private static bool IsAttemptName(string name) =>
        name.Length is > 0 and <= 2 && name.All(char.IsAsciiDigit);

    private static bool IsLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether anything is at the path. Asked so that it can say "could not tell", which counts as present.</summary>
    /// <remarks>
    /// NOT <c>Directory.Exists</c>, WHICH ANSWERS FALSE ON ANY ERROR: a folder the process may not look at would read
    /// as removed, and a sweep that could not see a copy would report that there was none.
    /// </remarks>
    private static Presence Probe(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return Presence.Present;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Presence.Absent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Presence.Unknown;
        }
    }

    private static bool Exists(string path) => Probe(path) != Presence.Absent;

    private enum Presence
    {
        Absent,
        Present,
        Unknown,
    }

    /// <summary>Every entry in a folder, or null when the folder could not be listed.</summary>
    private static string[]? List(string path)
    {
        try
        {
            return Directory.GetFileSystemEntries(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Retry(Action delete)
    {
        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < DeleteAttempts - 1)
                {
                    Thread.Sleep(DeletePause);
                }
            }
        }
    }
}
