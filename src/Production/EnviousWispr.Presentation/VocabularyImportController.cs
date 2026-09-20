using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>Reads a word list into the dictionary and offers what it could not decide, without the page.</summary>
/// <remarks>
/// ONE PATH FOR EVERY WORD LIST, HOWEVER IT ARRIVED. A file, a paste and a bundled pack are the
/// same list with three ways in; the page owns the ways in (the picker, the clipboard, the menu)
/// and this owns what the list does to the words.
///
/// NO DECISION OUTSIDE THE GATE AT ALL, INCLUDING "THERE IS NOTHING TO ADD". Reading the words first
/// to decide whether to bother meant a removal finishing in between could leave the page saying
/// "no new words" about a list that would in fact have gained some. The plan is computed once,
/// inside the gate, against the words that are there when it is applied, and it comes back so the
/// page describes what was actually stored.
///
/// A CONFLICT IS OFFERED RATHER THAN DECIDED. A word the list corrects differently from the way the
/// person already does is neither overwritten nor silently dropped: the plan carries it, the page
/// offers a button, and <see cref="ReplaceConflictsAsync"/> merges the list's version in - again
/// inside the gate, against the words that are there then.
/// </remarks>
public sealed class VocabularyImportController
{
    private readonly VocabularyPresenter _vocabulary;

    public VocabularyImportController(VocabularyPresenter vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = vocabulary;
    }

    /// <summary>Adds what is new in the list and answers with the whole plan: added, already had, conflicts, unreadable.</summary>
    public Task<SettingsSaveResult<CustomWordImportPlan>> ImportAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _vocabulary.ChangeAsync(data =>
        {
            var plan = CustomWordImport.Read(text, data.CustomWords);
            return (new ReusableUserData([.. data.CustomWords, .. plan.Additions], data.Snippets), plan);
        });
    }

    /// <summary>Installs a bundled pack through exactly the path an imported file takes.</summary>
    /// <remarks>
    /// Same reader, same collision rules, same description. A pack that merged by its own route would
    /// be a second implementation of adding words, and the two would drift - so a person who already
    /// corrects one of these words their own way keeps their version and is told, exactly as they
    /// would be for a file they chose themselves.
    /// </remarks>
    public Task<SettingsSaveResult<CustomWordImportPlan>> ApplyPackAsync(VocabularyPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return ImportAsync(pack.Words);
    }

    /// <summary>Takes the list's version of the words the person corrects differently.</summary>
    /// <remarks>
    /// MERGED INSIDE THE GATE, against the words that are there when it happens. Merging outside built
    /// a list from a snapshot, and saving it put back whatever had changed since.
    /// </remarks>
    public Task<SettingsSaveResult> ReplaceConflictsAsync(IReadOnlyList<CustomWordEntry> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        return _vocabulary.ChangeAsync(data => new ReusableUserData(
            CustomWordImport.Merge(data.CustomWords, replacements),
            data.Snippets));
    }
}
