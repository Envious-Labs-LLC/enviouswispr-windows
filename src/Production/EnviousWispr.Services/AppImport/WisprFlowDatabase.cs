namespace EnviousWispr.Services.AppImport;

/// <summary>How Wispr Flow's one database is read, shared by the word import and the snippet import (macOS <c>WisprFlowDatabase</c>).</summary>
/// <remarks>
/// ONE DATABASE, TWO FEATURES, ONE READ. Wispr Flow keeps words and snippets as rows of the same <c>Dictionary</c> table
/// in <c>%APPDATA%\Wispr Flow\flow.sqlite</c> (measured on the Windows app: same table and columns as the Mac). What
/// differs between the two imports is only the SQL and the row mapping, which the caller passes in; acquiring a copy
/// that is safe to read is the same question for both and has one answer here.
///
/// NOTHING IS OPENED IN WISPR FLOW'S FOLDER. The database and its write-ahead log are COPIED into a folder of our own
/// and only the copy is opened - the Windows counterpart of the Mac's private clone, since Windows has no general
/// copy-on-write clone. The copy is trusted only when the source's parts (main file, log, index and a rollback
/// journal) look identical before and after it, so a checkpoint Wispr Flow ran during the copy is caught rather than
/// read as a database that never existed. A rollback journal means a transaction is half written and the read is
/// refused. Three attempts, then <see cref="RivalStoreUnreadableException"/>; each caller turns that into its own
/// sentence, which offers quitting Wispr Flow as something that can help and never asserts it as the cause (founder
/// decision 2026-09-18).
/// </remarks>
internal static class WisprFlowDatabase
{
    private const int Attempts = 3;

    /// <summary>The copy is refused above this size; the Mac measured stores near 1 GB and this leaves room for more.</summary>
    private const long MaximumCopyBytes = 2L * 1024 * 1024 * 1024;

    private const string FileName = "flow.sqlite";

    private static readonly string[] MonitoredSuffixes = ["", "-wal", "-shm", "-journal"];
    private static readonly string[] CopiedSuffixes = ["", "-wal"];

    /// <summary>Where Wispr Flow for Windows keeps its database: <c>%APPDATA%\Wispr Flow\flow.sqlite</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", FileName);

    /// <summary>Runs <paramref name="sql"/> against a stable private copy of the database; null from the mapper is a counted exclusion.</summary>
    /// <exception cref="RivalStoreUnreadableException">No stable copy could be made, or the copy could not be read whole.</exception>
    public static (List<T> Rows, int Excluded) Read<T>(string databasePath, string sql, Func<WindowsSqlite.Row, T?> map)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var scratch = Path.Combine(Path.GetTempPath(), "ew-app-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var copy = AcquireStableCopy(databasePath, scratch);
            try
            {
                return WindowsSqlite.ReadRows(copy, sql, map);
            }
            catch (Exception exception) when (exception is SqliteReadException or DllNotFoundException or EntryPointNotFoundException)
            {
                throw new RivalStoreUnreadableException();
            }
        }
        finally
        {
            RemoveScratch(scratch);
        }
    }

    private static string AcquireStableCopy(string databasePath, string scratch)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var before = ReadParts(databasePath);
            if (before is null)
            {
                continue;
            }

            var directory = Path.Combine(scratch, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var copy = Path.Combine(directory, FileName);
            try
            {
                // INSIDE THE GUARD: a temp folder Windows will not create is an unreadable attempt, never an
                // exception escaping to the page.
                Directory.CreateDirectory(directory);
                long total = 0;
                foreach (var suffix in CopiedSuffixes)
                {
                    var source = databasePath + suffix;
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    total += CopyShared(source, copy + suffix, MaximumCopyBytes - total);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (ReadParts(databasePath) is { } after && after.SequenceEqual(before))
            {
                return copy;
            }
        }

        throw new RivalStoreUnreadableException();
    }

    /// <summary>Every monitored part's identity, or null when the source cannot be copied safely right now.</summary>
    private static (bool Present, long Length, DateTime Written)[]? ReadParts(string databasePath)
    {
        var parts = new (bool, long, DateTime)[MonitoredSuffixes.Length];
        for (var index = 0; index < MonitoredSuffixes.Length; index++)
        {
            var suffix = MonitoredSuffixes[index];
            var info = new FileInfo(databasePath + suffix);
            try
            {
                info.Refresh();
                if (!info.Exists)
                {
                    if (suffix.Length == 0)
                    {
                        return null;
                    }

                    parts[index] = (false, 0, default);
                    continue;
                }

                // A half-written transaction, or a link that would lead the copy back out of our folder.
                if (suffix == "-journal" || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return null;
                }

                parts[index] = (true, info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return parts;
    }

    /// <summary>Copies a file another app holds open, sharing it the way that app does, up to a byte budget.</summary>
    private static long CopyShared(string source, string destination, long budget)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > budget)
            {
                throw new IOException("The store is larger than the copy allows.");
            }

            output.Write(buffer, 0, read);
        }

        return copied;
    }

    /// <summary>Removes our own scratch folder file by file: only the names this reader could have made.</summary>
    private static void RemoveScratch(string scratch)
    {
        if (!Directory.Exists(scratch))
        {
            return;
        }

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var directory = Path.Combine(scratch, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var suffix in MonitoredSuffixes)
            {
                TryDelete(Path.Combine(directory, FileName + suffix));
            }

            TryDeleteDirectory(directory);
        }

        TryDeleteDirectory(scratch);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Another app's store could not be read safely and whole. Each import turns it into its own sentence.</summary>
internal sealed class RivalStoreUnreadableException : Exception
{
}
