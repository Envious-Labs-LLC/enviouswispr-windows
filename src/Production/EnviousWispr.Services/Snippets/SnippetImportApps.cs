using EnviousWispr.Core.Settings;
using EnviousWispr.Services.AppImport;

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
    public static string SupportedNames => ImportAppNames.Join([.. All.Select(app => app.DisplayName)]);
}

/// <summary>Wispr Flow's snippets: rows of its <c>Dictionary</c> table flagged <c>isSnippet</c>, read from a private copy.</summary>
/// <remarks>
/// <c>phrase</c> is the trigger and <c>replacement</c> the text (macOS, measured on real rows; the same columns in
/// the Windows app, read from a private copy of a real store). <c>isDeleted</c> rows are soft deletes, and importing
/// them would resurrect a snippet the person removed; <c>isSnippet = 0</c> rows are words. Both are COUNTED rather
/// than hidden by a WHERE clause, so a store holding only words reads as "found N, none compatible" and not as
/// empty. <c>LIMIT</c> is one past the ceiling so "too many" is knowable. How the private copy is made, and why it
/// can be trusted, is <see cref="WisprFlowDatabase"/>, shared with the word import.
/// </remarks>
public sealed class WisprFlowSnippetApp : ISnippetImportApp
{
    private readonly string _databasePath;

    public WisprFlowSnippetApp(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>Where Wispr Flow for Windows keeps its database: <c>%APPDATA%\Wispr Flow\flow.sqlite</c>.</summary>
    public static string DefaultDatabasePath => WisprFlowDatabase.DefaultPath;

    public string Id => "wispr_flow";

    public string DisplayName => "Wispr Flow";

    public bool IsInstalled => File.Exists(_databasePath);

    public SnippetImportBatch Load()
    {
        if (!IsInstalled)
        {
            throw new SnippetImportException(SnippetImportFailure.AppNotFound, SnippetImportMessages.AppNotFound(DisplayName));
        }

        var sql =
            "SELECT phrase, replacement, isDeleted, isSnippet FROM Dictionary " +
            $"ORDER BY id COLLATE BINARY ASC LIMIT {SnippetImportLimits.MaximumSourceEntries + 1}";
        List<SnippetImportCandidate> rows;
        int excluded;
        try
        {
            (rows, excluded) = WisprFlowDatabase.Read(_databasePath, sql, row =>
            {
                var phrase = row.RequiredText(0);
                var replacement = row.OptionalText(1);
                var isDeleted = row.RequiredBoolean(2);
                var isSnippet = row.RequiredBoolean(3);
                return isDeleted || !isSnippet ? null : Literal(phrase, replacement);
            });
        }
        catch (RivalStoreUnreadableException)
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
}
