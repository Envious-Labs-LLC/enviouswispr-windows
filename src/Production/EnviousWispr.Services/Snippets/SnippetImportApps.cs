using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Snippets;

/// <summary>Another dictation app EnviousWispr can read snippets out of, read only and only when the person picks it.</summary>
public interface ISnippetImportApp
{
    /// <summary>A closed identifier (<c>wispr_flow</c>), never a display name.</summary>
    string Id { get; }

    /// <summary>What the person sees.</summary>
    string DisplayName { get; }

    /// <summary>Whether the app's store is on this PC. Asked only once the person is looking at the app list.</summary>
    bool IsInstalled { get; }

    /// <summary>Reads every snippet, refusing and COUNTING what cannot become a literal snippet here. Not yet validated.</summary>
    SnippetImportBatch Load();
}

/// <summary>The apps "From another app" offers (macOS <c>SnippetImportAppRegistry</c>).</summary>
/// <remarks>
/// WISPR FLOW ONLY ON WINDOWS, AND THE ABSENCE OF TYPEWHISPER IS A DECISION, NOT A GAP. macOS reads TypeWhisper's
/// Core Data store (<c>~/Library/Application Support/TypeWhisper/snippets.store</c>). TypeWhisper's Windows app is a
/// separate program that keeps its snippets as JSON inside a per-profile data folder, a location this build could
/// not verify on a real install, and a reader aimed at a guessed path would either find nothing or read the wrong
/// file. It is left out until that location is measured. Wispr Flow was measured: the Windows app keeps the same
/// <c>flow.sqlite</c>, same <c>Dictionary</c> table and columns as the Mac, under the roaming application-data
/// folder.
/// </remarks>
public static class SnippetImportApps
{
    public static IReadOnlyList<ISnippetImportApp> All { get; } = [new WisprFlowSnippetApp(WisprFlowSnippetApp.DefaultDatabasePath)];

    /// <summary>The names for the "none found" sentence, joined the way a person writes a list.</summary>
    public static string SupportedNames => All.Count switch
    {
        1 => All[0].DisplayName,
        2 => $"{All[0].DisplayName} and {All[1].DisplayName}",
        _ => string.Join(", ", All.Take(All.Count - 1).Select(app => app.DisplayName)) + ", and " + All[^1].DisplayName,
    };
}

/// <summary>Wispr Flow's snippets: rows of its <c>Dictionary</c> table flagged <c>isSnippet</c>, read from a private copy.</summary>
/// <remarks>
/// <c>phrase</c> is the trigger and <c>replacement</c> the text (macOS, measured on real rows; the same columns in
/// the Windows app, read from a private copy of a real store). <c>isDeleted</c> rows are soft deletes, and importing
/// them would resurrect a snippet the person removed; <c>isSnippet = 0</c> rows are words. Both are COUNTED rather
/// than hidden by a WHERE clause, so a store holding only words reads as "found N, none compatible" and not as
/// empty. <c>LIMIT</c> is one past the ceiling so "too many" is knowable.
///
/// NOTHING IS OPENED IN WISPR FLOW'S FOLDER. The database and its write-ahead log are COPIED into a folder of our
/// own and only the copy is opened - the Windows counterpart of the Mac's private clone, since Windows has no
/// general copy-on-write clone. The copy is trusted only when the source's parts (main file, log, index and a
/// rollback journal) look identical before and after it, so a checkpoint Wispr Flow ran during the copy is caught
/// rather than read as a database that never existed. A rollback journal means a transaction is half written and
/// the read is refused. Three attempts, then the unreadable sentence, which offers quitting Wispr Flow as
/// something that can help and never asserts it as the cause (founder decision 2026-09-18).
/// </remarks>
public sealed class WisprFlowSnippetApp : ISnippetImportApp
{
    private const int Attempts = 3;

    /// <summary>The copy is refused above this size; the Mac measured stores near 1 GB and this leaves room for more.</summary>
    private const long MaximumCopyBytes = 2L * 1024 * 1024 * 1024;

    private static readonly string[] MonitoredSuffixes = ["", "-wal", "-shm", "-journal"];
    private static readonly string[] CopiedSuffixes = ["", "-wal"];

    private readonly string _databasePath;

    public WisprFlowSnippetApp(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>Where Wispr Flow for Windows keeps its database: <c>%APPDATA%\Wispr Flow\flow.sqlite</c>.</summary>
    public static string DefaultDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "flow.sqlite");

    public string Id => "wispr_flow";

    public string DisplayName => "Wispr Flow";

    public bool IsInstalled => File.Exists(_databasePath);

    public SnippetImportBatch Load()
    {
        if (!IsInstalled)
        {
            throw new SnippetImportException(SnippetImportFailure.AppNotFound, SnippetImportMessages.AppNotFound(DisplayName));
        }

        var scratch = Path.Combine(Path.GetTempPath(), "ew-snippet-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var copy = AcquireStableCopy(scratch);
            var sql =
                "SELECT phrase, replacement, isDeleted, isSnippet FROM Dictionary " +
                $"ORDER BY id COLLATE BINARY ASC LIMIT {SnippetImportLimits.MaximumSourceEntries + 1}";
            List<SnippetImportCandidate> rows;
            int excluded;
            try
            {
                (rows, excluded) = WindowsSqlite.ReadRows(copy, sql, row =>
                {
                    var phrase = row.RequiredText(0);
                    var replacement = row.OptionalText(1);
                    var isDeleted = row.RequiredBoolean(2);
                    var isSnippet = row.RequiredBoolean(3);
                    return isDeleted || !isSnippet ? null : Literal(phrase, replacement);
                });
            }
            catch (Exception exception) when (exception is SqliteReadException or DllNotFoundException or EntryPointNotFoundException)
            {
                throw new SnippetImportException(SnippetImportFailure.AppStoreUnreadable, SnippetImportMessages.AppUnreadable(DisplayName));
            }

            // THE SCANNED COUNT, survivors plus exclusions: a store of 5,001 rows filtered down to one must not look
            // like a one-row source.
            if (rows.Count + excluded > SnippetImportLimits.MaximumSourceEntries)
            {
                throw new SnippetImportException(
                    SnippetImportFailure.TooMany,
                    SnippetImportMessages.TooManySourceEntries(DisplayName, SnippetImportLimits.MaximumSourceEntries));
            }

            return new SnippetImportBatch(
                Id,
                DisplayName,
                rows,
                excluded > 0 ? [new SnippetImportNotice.IncompatibleSourceEntriesExcluded(excluded)] : []);
        }
        finally
        {
            RemoveScratch(scratch);
        }
    }

    /// <summary>A row survives only as a literal snippet: a trigger with words and a text that is not blank; the text is kept as held.</summary>
    internal static SnippetImportCandidate? Literal(string trigger, string? expansion)
    {
        if (expansion is null || expansion.Trim().Length == 0)
        {
            return null;
        }

        var trimmed = trigger.Trim();
        return trimmed.Length == 0 ? null : new SnippetImportCandidate(trimmed, expansion);
    }

    private string AcquireStableCopy(string scratch)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var before = ReadParts();
            if (before is null)
            {
                continue;
            }

            var directory = Path.Combine(scratch, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var copy = Path.Combine(directory, "flow.sqlite");
            try
            {
                // INSIDE THE GUARD: a temp folder Windows will not create is an unreadable attempt, never an
                // exception escaping to the page.
                Directory.CreateDirectory(directory);
                long total = 0;
                foreach (var suffix in CopiedSuffixes)
                {
                    var source = _databasePath + suffix;
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

            if (ReadParts() is { } after && after.SequenceEqual(before))
            {
                return copy;
            }
        }

        throw new SnippetImportException(SnippetImportFailure.AppStoreUnreadable, SnippetImportMessages.AppUnreadable(DisplayName));
    }

    /// <summary>Every monitored part's identity, or null when the source cannot be copied safely right now.</summary>
    private (bool Present, long Length, DateTime Written)[]? ReadParts()
    {
        var parts = new (bool, long, DateTime)[MonitoredSuffixes.Length];
        for (var index = 0; index < MonitoredSuffixes.Length; index++)
        {
            var suffix = MonitoredSuffixes[index];
            var info = new FileInfo(_databasePath + suffix);
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
                TryDelete(Path.Combine(directory, "flow.sqlite" + suffix));
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
