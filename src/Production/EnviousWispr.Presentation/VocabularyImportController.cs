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
            var effective = data.EffectiveCustomWords();
            var plan = CustomWordImport.Read(text, effective);
            var normalizedAdditions = CustomWordEntry.NormalizeLegacyEntries(plan.Additions);
            return (ApplyAdditionsWithBuiltinAwareness(data, normalizedAdditions), plan);
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

    /// <summary>Adds a reviewed import from another app if the list is still the one reviewed; otherwise writes nothing.</summary>
    /// <remarks>
    /// THE SAME PLAN AS A PASTED LIST (<see cref="CustomWordImport.Plan"/>), DECIDED INSIDE THE SAME GATE; what differs is
    /// that the person reviewed it first, so a list that moved during the review is refused rather than merged into
    /// (<see cref="CustomWordAppImport.Commit"/>). A refusal hands the writer back the settings it was given, and the
    /// writer does not save a value it already holds. Conflicts are still offered afterwards through
    /// <see cref="ReplaceConflictsAsync"/>.
    /// </remarks>
    public Task<SettingsSaveResult<WordImportCommitOutcome>> CommitFromAppAsync(
        IReadOnlyList<CustomWordEntry> baseline,
        IReadOnlyList<CustomWordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(entries);
        var baselineCopy = baseline.ToArray();
        var entriesCopy = entries.ToArray();
        return _vocabulary.ChangeAsync(data =>
        {
            var effective = data.EffectiveCustomWords();
            var (next, outcome) = CustomWordAppImport.Commit(effective, baselineCopy, entriesCopy);
            if (next is null)
            {
                return (data, outcome);
            }

            var updatedData = ApplyAdditionsWithBuiltinAwareness(data, outcome.Plan.Additions);
            return (updatedData, outcome with { Current = updatedData.EffectiveCustomWords() });
        });
    }

    /// <summary>Takes the list's version of the words the person corrects differently.</summary>
    /// <remarks>
    /// MERGED INSIDE THE GATE, against the words that are there when it happens. Merging outside built
    /// a list from a snapshot, and saving it put back whatever had changed since.
    /// </remarks>
    public Task<SettingsSaveResult> ReplaceConflictsAsync(IReadOnlyList<CustomWordEntry> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        return _vocabulary.ChangeAsync(data =>
        {
            var effective = data.EffectiveCustomWords();
            var mergedEffective = CustomWordImport.Merge(effective, replacements);
            var nextUserWords = new List<CustomWordEntry>(mergedEffective.Count);
            var nextDeleted = data.DeletedBuiltinWordIds;

            foreach (var entry in mergedEffective)
            {
                if (entry.Origin == CustomWordOrigin.Builtin)
                {
                    continue;
                }

                nextUserWords.Add(entry);
            }

            foreach (var effectiveEntry in effective)
            {
                if (effectiveEntry.Origin == CustomWordOrigin.Builtin &&
                    BuiltinWords.FindByEntryId(effectiveEntry.Id) is { } builtin &&
                    !mergedEffective.Any(m =>
                        m.Origin == CustomWordOrigin.Builtin &&
                        string.Equals(m.Id, effectiveEntry.Id, StringComparison.Ordinal)) &&
                    !nextUserWords.Any(u =>
                        string.Equals(u.Replacement, builtin.Entry.Replacement, StringComparison.OrdinalIgnoreCase)))
                {
                    nextDeleted = BuiltinWords.WithTombstone(nextDeleted, builtin.BuiltinId);
                }
            }

            return data.WithCustomWordsAndDeletedBuiltins(nextUserWords.ToArray(), nextDeleted);
        });
    }

    private static ReusableUserData ApplyAdditionsWithBuiltinAwareness(
        ReusableUserData data,
        IReadOnlyList<CustomWordEntry> additions)
    {
        if (additions.Count == 0)
        {
            return data;
        }

        var nextUserWords = new List<CustomWordEntry>(data.CustomWords);
        var nextDeleted = data.DeletedBuiltinWordIds;

        foreach (var addition in additions)
        {
            var builtin = BuiltinWords.FindByCanonical(addition.Replacement);
            if (builtin is null)
            {
                nextUserWords.Add(addition.OwnedByUser());
                continue;
            }

            var wasTombstoned = nextDeleted.Contains(builtin.BuiltinId, StringComparer.OrdinalIgnoreCase);
            if (wasTombstoned)
            {
                nextDeleted = BuiltinWords.WithoutTombstone(nextDeleted, builtin.BuiltinId);
            }

            var existingUserIndex = nextUserWords.FindIndex(entry =>
                string.Equals(entry.Replacement, builtin.Entry.Replacement, StringComparison.OrdinalIgnoreCase));
            if (existingUserIndex >= 0)
            {
                var existing = nextUserWords[existingUserIndex];
                nextUserWords[existingUserIndex] = new CustomWordEntry(
                    CustomWordEntry.NormalizeAliases(existing.Aliases.Concat(addition.Aliases)),
                    existing.Replacement,
                    existing.Strictness,
                    existing.Category,
                    existing.Id,
                    existing.LearnedAliases,
                    existing.LearnedAt,
                    CustomWordOrigin.User);
                continue;
            }

            var mergedAliases = wasTombstoned
                ? addition.Aliases
                : CustomWordEntry.NormalizeAliases(addition.Aliases.Concat(builtin.Entry.Aliases));
            nextUserWords.Add(new CustomWordEntry(
                mergedAliases,
                builtin.Entry.Replacement,
                addition.Strictness != MatchStrictness.Default ? addition.Strictness : builtin.Entry.Strictness,
                addition.Category != WordCategory.General ? addition.Category : builtin.Entry.Category,
                id: builtin.Entry.Id,
                learnedAliases: wasTombstoned ? [] : builtin.Entry.LearnedAliases,
                learnedAt: null,
                origin: CustomWordOrigin.User));
        }

        return data.WithCustomWordsAndDeletedBuiltins(
            CustomWordEntry.NormalizeLegacyEntries(nextUserWords),
            nextDeleted);
    }
}
