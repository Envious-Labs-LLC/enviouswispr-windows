namespace EnviousWispr.Services.AppImport;

/// <summary>Where a private copy of another app's store lives while it is read, and how it is taken away again.</summary>
/// <remarks>
/// A COPY OF SOMEBODY'S WORDS MUST NOT OUTLIVE THE READ. Every copy goes into its own folder named
/// <c>import-&lt;guid&gt;</c> under ONE app-owned parent in the temp folder (<see cref="DefaultParent"/>), and is
/// removed as soon as the read ends. A virus scanner or the search indexer can hold a just-written file open for a
/// moment, so each deletion is retried briefly; if a file still cannot go, the folder stays behind, and the next
/// launch's <see cref="SweepLeftovers()"/> removes it.
///
/// NEVER A RECURSIVE DELETE. The sweep lists the parent's own <c>import-</c> folders and their numbered attempt
/// folders, deletes each file it finds by name, then each emptied folder, and finally ASKS THE DISK whether
/// anything it meant to remove is still there. It never follows a link out of the parent (a link is removed as the
/// link, never walked), never touches an entry in the parent that it did not name, and never touches anything
/// outside the parent.
/// </remarks>
public static class AppImportScratch
{
    internal const string FolderPrefix = "import-";

    private const int DeleteAttempts = 5;
    private static readonly TimeSpan DeletePause = TimeSpan.FromMilliseconds(100);

    /// <summary>The one parent every scratch copy lives under: <c>%TEMP%\EnviousWispr-app-import</c>.</summary>
    public static string DefaultParent { get; } = Path.Combine(Path.GetTempPath(), "EnviousWispr-app-import");

    /// <summary>A new, not yet created, scratch folder path for one read.</summary>
    internal static string NewFolder(string parent) =>
        Path.Combine(parent, FolderPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>Removes one scratch folder; true when nothing of it is left on disk.</summary>
    internal static bool Remove(string scratch)
    {
        if (!Directory.Exists(scratch))
        {
            return true;
        }

        foreach (var attempt in SafeDirectories(scratch))
        {
            foreach (var file in SafeFiles(attempt))
            {
                Retry(() => File.Delete(file));
            }

            Retry(() => Directory.Delete(attempt, recursive: false));
        }

        foreach (var file in SafeFiles(scratch))
        {
            Retry(() => File.Delete(file));
        }

        Retry(() => Directory.Delete(scratch, recursive: false));
        return !Directory.Exists(scratch);
    }

    /// <summary>Removes every leftover scratch folder under <see cref="DefaultParent"/>; false when one could not be removed.</summary>
    public static bool SweepLeftovers() => SweepLeftovers(DefaultParent);

    internal static bool SweepLeftovers(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return true;
        }

        var clean = true;
        foreach (var folder in SafeDirectories(parent))
        {
            if (!Path.GetFileName(folder).StartsWith(FolderPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            clean &= Remove(folder);
        }

        return clean;
    }

    /// <summary>Child folders that are real folders; a link among them is removed as a link, never entered.</summary>
    private static string[] SafeDirectories(string path)
    {
        try
        {
            var result = new List<string>();
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                if (new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Retry(() => Directory.Delete(child, recursive: false));
                    continue;
                }

                result.Add(child);
            }

            return [.. result];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
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
