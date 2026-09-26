using System.Security.Cryptography;

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
/// journal) look identical before and after it - the copied parts by the SHA-256 of their bytes, which the copy's own
/// hash must match as well - so a checkpoint Wispr Flow ran during the copy is caught rather than read as a database
/// that never existed. A rollback journal means a transaction is half written and the read is refused. Three
/// attempts, then <see cref="RivalStoreUnreadableException"/>; each caller turns that into its own sentence, which
/// offers quitting Wispr Flow and trying again as something that can help and never asserts it as the cause (founder
/// decision 2026-09-18). The copy lives under <see cref="AppImportScratch"/> and is removed when the read ends.
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
        where T : class =>
        Read(databasePath, sql, map, AppImportScratch.DefaultParent, afterCopy: null);

    /// <param name="afterCopy">Tests only: runs between the copy and the second look at the source, where another app's
    /// write would land. It can only make a read FAIL; nothing it does can make an unstable copy pass.</param>
    internal static (List<T> Rows, int Excluded) Read<T>(
        string databasePath,
        string sql,
        Func<WindowsSqlite.Row, T?> map,
        string scratchParent,
        Action? afterCopy)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var scratch = AppImportScratch.NewFolder(scratchParent);
        try
        {
            var copy = AcquireStableCopy(databasePath, scratch, afterCopy);
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
            // A folder a scanner still holds stays behind for the next launch's sweep (AppImportScratch).
            _ = AppImportScratch.Remove(scratch);
        }
    }

    private static string AcquireStableCopy(string databasePath, string scratch, Action? afterCopy)
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
            var copied = new string?[CopiedSuffixes.Length];
            try
            {
                // INSIDE THE GUARD: a temp folder Windows will not create is an unreadable attempt, never an
                // exception escaping to the page.
                Directory.CreateDirectory(directory);
                long total = 0;
                for (var index = 0; index < CopiedSuffixes.Length; index++)
                {
                    var source = databasePath + CopiedSuffixes[index];
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    var (length, hash) = CopyShared(source, copy + CopiedSuffixes[index], MaximumCopyBytes - total);
                    copied[index] = hash;
                    total += length;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            afterCopy?.Invoke();

            // THE CONTENT, NOT ONLY ITS SIZE AND TIME. A checkpoint can rewrite the log in place at the same length
            // and leave the time as it was, and a size-and-time check calls that unchanged. The source is hashed
            // before and after, and the copy's own hash must equal both, so the bytes read are bytes that existed.
            if (ReadParts(databasePath) is { } after && after.SequenceEqual(before) && CopiedMatch(before, copied))
            {
                return copy;
            }
        }

        throw new RivalStoreUnreadableException();
    }

    /// <summary>Every copied part's hash equals the source's, and no copied part appeared or vanished in between.</summary>
    private static bool CopiedMatch((bool Present, long Length, DateTime Written, string? Hash)[] source, string?[] copied)
    {
        for (var index = 0; index < CopiedSuffixes.Length; index++)
        {
            var part = source[Array.IndexOf(MonitoredSuffixes, CopiedSuffixes[index])];
            if (part.Present != (copied[index] is not null) || !string.Equals(part.Hash, copied[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every monitored part's identity, content included for the copied parts; null when the source cannot be copied safely right now.</summary>
    private static (bool Present, long Length, DateTime Written, string? Hash)[]? ReadParts(string databasePath)
    {
        var parts = new (bool, long, DateTime, string?)[MonitoredSuffixes.Length];
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

                    parts[index] = (false, 0, default, null);
                    continue;
                }

                // A half-written transaction, or a link that would lead the copy back out of our folder.
                if (suffix == "-journal" || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return null;
                }

                // The index (-shm) is not copied - SQLite rebuilds it beside the copy - so only its presence, size
                // and time are watched; the copied parts are watched by content.
                parts[index] = (true, info.Length, info.LastWriteTimeUtc, CopiedSuffixes.Contains(suffix) ? Hash(info.FullName) : null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return parts;
    }

    /// <summary>SHA-256 of a file another app holds open, streamed and read shared so that app is never blocked.</summary>
    private static string Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    /// <summary>Copies a file another app holds open, sharing it the way that app does, up to a byte budget; answers the length and the hash of what was written.</summary>
    private static (long Length, string Hash) CopyShared(string source, string destination, long budget)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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
            hash.AppendData(buffer, 0, read);
        }

        return (copied, Convert.ToHexString(hash.GetHashAndReset()));
    }
}

/// <summary>Another app's store could not be read safely and whole. Each import turns it into its own sentence.</summary>
internal sealed class RivalStoreUnreadableException : Exception
{
}
