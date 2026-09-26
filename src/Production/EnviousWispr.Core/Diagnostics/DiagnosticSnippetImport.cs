using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Diagnostics;

/// <summary>Where a snippet import came from, as a closed list (macOS <c>SnippetImportTelemetrySource</c>). Never a file name.</summary>
public enum DiagnosticSnippetImportSource
{
    Paste,
    FileJson,
    FileCsv,
    FileText,
    FileOther,
    WisprFlow,
}

/// <summary>How one import attempt ended (macOS <c>SnippetImportTelemetryOutcome</c>).</summary>
public enum DiagnosticSnippetImportOutcome
{
    Completed,
    NothingFound,
    NothingCompatible,
    NothingApproved,
    Stale,
    Failed,
}

/// <summary>One snippet import attempt as the diagnostic log keeps it: the source, the outcome, and COUNTS.</summary>
/// <remarks>
/// THE MAC'S ATTEMPT REPORT, FIELD FOR FIELD (<c>SnippetImportAttemptReport</c>): candidates found, added, skipped
/// because the person already had them, skipped because the batch listed them twice, unticked, and left out by the
/// source. Every member is a fixed enum or a bounded number, so nothing here can carry a trigger, a snippet's text, a
/// file name or another app's content. A report whose members are out of range is dropped whole
/// (<see cref="IsWithinBounds"/>; a method, so it is never written to the log as a field), not trimmed.
/// </remarks>
public sealed record DiagnosticSnippetImport(
    DiagnosticSnippetImportSource Source,
    DiagnosticSnippetImportOutcome Outcome,
    SnippetImportFailure? Failure = null,
    int Candidates = 0,
    int Added = 0,
    int SkippedExisting = 0,
    int SkippedDuplicateBatch = 0,
    int SkippedUnticked = 0,
    int Excluded = 0)
{
    /// <summary>No count can exceed this; a rival app's whole table is scanned at most 5,001 rows, a file at most 1,001 snippets.</summary>
    public const int MaximumCount = 100_000;

    /// <summary>Every enum is a defined member and every count is within 0 and <see cref="MaximumCount"/>.</summary>
    public bool IsWithinBounds() =>
        Enum.IsDefined(Source) &&
        Enum.IsDefined(Outcome) &&
        (Failure is null || Enum.IsDefined(Failure.Value)) &&
        new[] { Candidates, Added, SkippedExisting, SkippedDuplicateBatch, SkippedUnticked, Excluded }
            .All(count => count is >= 0 and <= MaximumCount);

    /// <summary>The source a batch names, or the file kind an extension names, as its closed category.</summary>
    public static DiagnosticSnippetImportSource SourceFor(string sourceIdOrExtension) =>
        sourceIdOrExtension.ToLowerInvariant() switch
        {
            "paste" => DiagnosticSnippetImportSource.Paste,
            "file_json" or ".json" => DiagnosticSnippetImportSource.FileJson,
            "file_csv" or ".csv" => DiagnosticSnippetImportSource.FileCsv,
            "file_text" or ".txt" or ".text" or ".md" or ".list" => DiagnosticSnippetImportSource.FileText,
            "wispr_flow" => DiagnosticSnippetImportSource.WisprFlow,
            _ => DiagnosticSnippetImportSource.FileOther,
        };

    /// <summary>The counts of a review as it stood when it ended: what the rows said, not what anyone meant to do.</summary>
    public static DiagnosticSnippetImport ForReview(
        DiagnosticSnippetImportSource source,
        DiagnosticSnippetImportOutcome outcome,
        int excluded,
        IReadOnlyList<SnippetImportReviewRow> rows,
        int added,
        SnippetImportFailure? failure = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new DiagnosticSnippetImport(
            source,
            outcome,
            failure,
            Candidates: rows.Count,
            Added: added,
            SkippedExisting: rows.Count(row => row.Status == SnippetImportRowStatus.Existing),
            SkippedDuplicateBatch: rows.Count(row => row.Status == SnippetImportRowStatus.DuplicateInBatch),
            SkippedUnticked: rows.Count(row => row.Status == SnippetImportRowStatus.New && !row.Add),
            Excluded: excluded);
    }
}
