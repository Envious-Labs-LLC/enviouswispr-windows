using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Diagnostics;

/// <summary>Which app a word import read, as a closed list. Never a path or a name the app did not ship with.</summary>
public enum DiagnosticWordImportSource
{
    WisprFlow,
    Handy,
}

/// <summary>How one word import attempt from another app ended.</summary>
public enum DiagnosticWordImportOutcome
{
    Completed,
    NothingFound,
    NothingCompatible,
    NothingNew,
    Cancelled,
    Stale,
    Failed,
}

/// <summary>One word import attempt as the diagnostic log keeps it: the source, the outcome, and COUNTS.</summary>
/// <remarks>
/// THE SNIPPET IMPORT REPORT'S SHAPE (<see cref="DiagnosticSnippetImport"/>) for words: pairs offered for review, added,
/// already had, corrected differently (offered for replacement, not taken), that could not be stored, and left out
/// by the source. Every member is a fixed enum or a bounded number, so nothing here can carry a word, a spelling, a
/// path or another app's content. A report whose members are out of range is dropped whole
/// (<see cref="IsWithinBounds"/>; a method, so it is never written to the log as a field), not trimmed.
/// </remarks>
public sealed record DiagnosticWordImport(
    DiagnosticWordImportSource Source,
    DiagnosticWordImportOutcome Outcome,
    WordImportFailure? Failure = null,
    int Candidates = 0,
    int Added = 0,
    int AlreadyHad = 0,
    int Conflicts = 0,
    int Unstorable = 0,
    int Excluded = 0)
{
    /// <summary>No count can exceed this; a rival app's whole store is scanned at most 25,001 rows.</summary>
    public const int MaximumCount = 100_000;

    /// <summary>Every enum is a defined member and every count is within 0 and <see cref="MaximumCount"/>.</summary>
    public bool IsWithinBounds() =>
        Enum.IsDefined(Source) &&
        Enum.IsDefined(Outcome) &&
        (Failure is null || Enum.IsDefined(Failure.Value)) &&
        new[] { Candidates, Added, AlreadyHad, Conflicts, Unstorable, Excluded }
            .All(count => count is >= 0 and <= MaximumCount);

    /// <summary>The source an app names, as its closed category; an identifier this list does not know is refused.</summary>
    public static DiagnosticWordImportSource SourceFor(string appId) => appId switch
    {
        "wispr_flow" => DiagnosticWordImportSource.WisprFlow,
        "handy" => DiagnosticWordImportSource.Handy,
        _ => throw new ArgumentOutOfRangeException(nameof(appId), "Not an app a word import reads."),
    };

    /// <summary>The counts of a plan as it stood when the attempt ended.</summary>
    public static DiagnosticWordImport ForPlan(
        DiagnosticWordImportSource source,
        DiagnosticWordImportOutcome outcome,
        int excluded,
        CustomWordImportPlan plan,
        int added,
        WordImportFailure? failure = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new DiagnosticWordImport(
            source,
            outcome,
            failure,
            Candidates: plan.Lines.Count,
            Added: added,
            AlreadyHad: plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.AlreadyPresent),
            Conflicts: plan.ConflictCount,
            Unstorable: plan.UnreadableCount,
            Excluded: excluded);
    }
}
