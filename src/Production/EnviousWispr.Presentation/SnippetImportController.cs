using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Commits a reviewed snippet import in one write, without the page.</summary>
/// <remarks>
/// THE SAME GATE AS THE WORD IMPORT (<see cref="VocabularyImportController"/>), NOT A SECOND ONE. The decision is
/// made inside the serialised settings writer against the snippets that are there when it runs, and it comes back
/// so the page describes what was actually stored. What differs from words is the shape macOS gave snippets: a
/// review the person ticks through first, and a commit that REFUSES when the list is no longer the one they
/// reviewed rather than merging into whatever is there.
///
/// A REFUSAL WRITES NOTHING. The change hands the writer back the very settings it was given, and the writer does
/// not save a value it already holds.
///
/// Compared against the writer's own current value, not re-read from disk as macOS does: the Mac can have two
/// copies of the app writing one store, while this app runs one instance and every settings change goes through
/// this one writer, so the writer's value is the file's.
/// </remarks>
public sealed class SnippetImportController
{
    private readonly VocabularyPresenter _vocabulary;

    public SnippetImportController(VocabularyPresenter vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = vocabulary;
    }

    /// <summary>Adds the approved snippets if the list is still <paramref name="baseline"/>; the keyword is never changed.</summary>
    public Task<SettingsSaveResult<SnippetImportCommitOutcome>> CommitAsync(
        IReadOnlyList<SnippetEntry> baseline,
        IReadOnlyList<SnippetImportCandidate> approved)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(approved);
        var baselineCopy = baseline.ToArray();
        var approvedCopy = approved.ToArray();
        return _vocabulary.ChangeAsync(data =>
        {
            var (next, outcome) = SnippetImportReview.Commit(data.Snippets, baselineCopy, approvedCopy);
            return (next is null ? data : data.WithSnippets(next), outcome);
        });
    }
}
