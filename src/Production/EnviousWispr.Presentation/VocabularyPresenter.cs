using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>How a snippet save went: refused and why, with the snippet it clashed with, or stored.</summary>
public readonly record struct SnippetSaveOutcome(SnippetRefusal? Refusal, SnippetEntry? Clash, bool TargetMissing = false);

/// <summary>Why a word edit was refused, if it could not be applied.</summary>
public enum WordEditRefusal
{
    TargetMissing,
    DuplicateSpokenForm,
    DuplicateReplacement,
}

/// <summary>How a word edit went: refused (missing target or spoken-form clash) or stored.</summary>
public readonly record struct WordEditOutcome(WordEditRefusal? Refusal, CustomWordEntry? Clash);

/// <summary>How a keyword save went: refused and why, or the word now stored.</summary>
public readonly record struct SnippetKeywordOutcome(SnippetRefusal? Refusal, string? Keyword);

/// <summary>The dictionary and snippet mutations, without the page that offers them.</summary>
/// <remarks>
/// EVERY CHANGE IS A FUNCTION OF THE WORDS AS THEY ARE WHEN THE GATE OPENS, applied through the
/// same serialised writer as every other settings change, so two edits landing together both
/// survive and a removal made against a list that changed under it removes only what is still
/// there. The page keeps selection, the dialogs, the suggestion chips and the boxes; what moves
/// here is what a word or a snippet does to the list: a twin is replaced rather than duplicated,
/// the list stays sorted, a removal matches rows by identity, and the answer says what actually
/// happened.
/// </remarks>
public enum LearnedSaveStatus
{
    Saved,
    AlreadyCovered,
    TargetGone,
    AliasOwnedElsewhere,
    Refused,
}

public readonly record struct LearnedSaveOutcome(
    LearnedSaveStatus Status,
    CustomWordEntry? Before,
    CustomWordEntry? After);

public enum LearnedUndoWriteStatus
{
    Applied,
    NotApplied,
    Refused,
}

public sealed class VocabularyPresenter
{
    private readonly SettingsPresenter _settings;

    public VocabularyPresenter(SettingsPresenter settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Adds a word under the strictness it carries, replacing any word spoken the same way.</summary>
    /// <remarks>
    /// ONE SPOKEN FORM, ONE ROW. A word added twice with different replacements would otherwise
    /// leave two rows fighting over the same sound; the later one wins and the list stays sorted.
    /// </remarks>
    public Task<SettingsSaveResult> AddWordAsync(CustomWordEntry word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var claimedAliases = (word.Aliases.Count > 0 ? word.Aliases : [word.SpokenForm])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ChangeAsync(data =>
        {
            var next = new List<CustomWordEntry>(data.CustomWords.Count + 1);
            foreach (var entry in data.CustomWords)
            {
                if (entry.Aliases.Count == 0)
                {
                    if (!claimedAliases.Contains(entry.SpokenForm))
                    {
                        next.Add(entry);
                    }

                    continue;
                }

                var overlaps = entry.Aliases.Any(claimedAliases.Contains);
                if (!overlaps)
                {
                    next.Add(entry);
                    continue;
                }

                var remainingAliases = entry.Aliases
                    .Where(alias => !claimedAliases.Contains(alias))
                    .ToArray();
                if (remainingAliases.Length > 0)
                {
                    next.Add(new CustomWordEntry(
                        remainingAliases,
                        entry.Replacement,
                        entry.Strictness,
                        entry.Category,
                        entry.Id));
                }
            }

            next.Add(word);
            return data.WithCustomWords(
                next.OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray());
        });
    }

    /// <summary>Adds a word from plain inputs, replacing any word spoken the same way.</summary>
    public Task<SettingsSaveResult> AddWordAsync(
        string spokenForm,
        string replacement,
        MatchStrictness strictness = MatchStrictness.Default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spokenForm);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        return AddWordAsync(new CustomWordEntry(spokenForm.Trim(), replacement.Trim(), strictness));
    }

    /// <summary>Adds a multi-alias categorized word after checking alias and canonical conflicts.</summary>
    public Task<SettingsSaveResult<WordEditOutcome>> AddMultiAliasWordAsync(
        IReadOnlyList<string> aliases,
        string replacement,
        MatchStrictness strictness,
        WordCategory category)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);

        var normalizedAliases = CustomWordEntry.NormalizeAliases(aliases);
        var trimmedReplacement = replacement.Trim();
        return ChangeAsync(data =>
        {
            if (FindAliasClash(data.CustomWords, normalizedAliases, excludeIndex: -1) is { } aliasClash)
            {
                return (data, new WordEditOutcome(WordEditRefusal.DuplicateSpokenForm, aliasClash));
            }

            if (FindCanonicalClash(data.CustomWords, trimmedReplacement, strictness, category, excludeIndex: -1) is { } canonicalClash)
            {
                return (data, new WordEditOutcome(WordEditRefusal.DuplicateReplacement, canonicalClash));
            }

            var created = new CustomWordEntry(
                normalizedAliases,
                trimmedReplacement,
                strictness,
                category,
                id: Guid.NewGuid().ToString("D"));
            var next = data.CustomWords
                .Append(created)
                .OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return (data.WithCustomWords(next), new WordEditOutcome(null, null));
        });
    }

    /// <summary>Replaces <paramref name="original"/> in place with updated values while preserving all other words.</summary>
    public Task<SettingsSaveResult<WordEditOutcome>> UpdateWordAsync(
        CustomWordEntry original,
        string spokenForm,
        string replacement,
        MatchStrictness strictness)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentException.ThrowIfNullOrWhiteSpace(spokenForm);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        return UpdateMultiAliasWordAsync(
            original,
            [spokenForm.Trim()],
            replacement.Trim(),
            strictness,
            original.Category);
    }

    /// <summary>Replaces <paramref name="original"/> with updated aliases, canonical replacement, category, and strictness.</summary>
    public Task<SettingsSaveResult<WordEditOutcome>> UpdateMultiAliasWordAsync(
        CustomWordEntry original,
        IReadOnlyList<string> aliases,
        string replacement,
        MatchStrictness strictness,
        WordCategory category)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);

        var normalizedAliases = CustomWordEntry.NormalizeAliases(aliases);
        var trimmedReplacement = replacement.Trim();
        var preservedLearned = string.Equals(original.Replacement, trimmedReplacement, StringComparison.OrdinalIgnoreCase)
            ? original.LearnedAliases
                .Where(alias => normalizedAliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                .ToArray()
            : [];
        var preservedLearnedAt = preservedLearned.Length > 0 ? original.LearnedAt : null;
        var updated = new CustomWordEntry(
            normalizedAliases,
            trimmedReplacement,
            strictness,
            category,
            original.Id,
            preservedLearned,
            preservedLearnedAt);
        return ChangeAsync(data =>
        {
            var originalIndex = FindWordIndex(data.CustomWords, original);
            if (originalIndex < 0)
            {
                return (data, new WordEditOutcome(WordEditRefusal.TargetMissing, null));
            }

            if (FindAliasClash(data.CustomWords, normalizedAliases, originalIndex) is { } aliasClash)
            {
                return (data, new WordEditOutcome(WordEditRefusal.DuplicateSpokenForm, aliasClash));
            }

            if (FindCanonicalClash(data.CustomWords, trimmedReplacement, strictness, category, originalIndex) is { } canonicalClash)
            {
                return (data, new WordEditOutcome(WordEditRefusal.DuplicateReplacement, canonicalClash));
            }

            return (
                data.WithCustomWords(ReplaceWord(data.CustomWords, original, updated)),
                new WordEditOutcome(null, null));
        });
    }

    /// <summary>Enables or disables an isolated vocabulary pack without mutating CustomWords.</summary>
    public Task<SettingsSaveResult> SetVocabularyPackEnabledAsync(string packId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        var pack = VocabularyPacks.FindById(packId)
            ?? throw new ArgumentException($"Unknown vocabulary pack '{packId}'.", nameof(packId));

        return ChangeAsync(data =>
        {
            var current = data.EnabledVocabularyPackIds;
            var hasPack = current.Any(id => string.Equals(id, pack.Id, StringComparison.OrdinalIgnoreCase));
            if (hasPack == enabled)
            {
                return data;
            }

            IReadOnlyList<string> next = enabled
                ? [.. current, pack.Id]
                : current.Where(id => !string.Equals(id, pack.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            return data.WithEnabledVocabularyPackIds(VocabularyPacks.NormalizePackIds(next));
        });
    }

    /// <summary>Sets the full list of enabled vocabulary pack IDs without mutating CustomWords.</summary>
    public Task<SettingsSaveResult> SetEnabledVocabularyPacksAsync(IReadOnlyList<string> packIds)
    {
        ArgumentNullException.ThrowIfNull(packIds);
        var normalized = VocabularyPacks.NormalizePackIds(packIds);
        return ChangeAsync(data => data.WithEnabledVocabularyPackIds(normalized));
    }

    private static CustomWordEntry? FindAliasClash(
        IReadOnlyList<CustomWordEntry> words,
        IReadOnlyList<string> incomingAliases,
        int excludeIndex)
    {
        if (incomingAliases.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < words.Count; i++)
        {
            if (i == excludeIndex)
            {
                continue;
            }

            var other = words[i];
            foreach (var alias in incomingAliases)
            {
                if (other.Aliases.Any(existing => string.Equals(existing, alias, StringComparison.OrdinalIgnoreCase)))
                {
                    return other;
                }
            }
        }

        return null;
    }

    private static CustomWordEntry? FindCanonicalClash(
        IReadOnlyList<CustomWordEntry> words,
        string replacement,
        MatchStrictness strictness,
        WordCategory category,
        int excludeIndex)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (i == excludeIndex)
            {
                continue;
            }

            var other = words[i];
            if (string.Equals(other.Replacement, replacement, StringComparison.Ordinal) &&
                other.Strictness == strictness &&
                other.Category == category)
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>Replaces <paramref name="original"/> with <paramref name="updated"/> and keeps the word list sorted.</summary>
    public static IReadOnlyList<CustomWordEntry> ReplaceWord(
        IReadOnlyList<CustomWordEntry> existing,
        CustomWordEntry original,
        CustomWordEntry updated)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);

        var index = FindWordIndex(existing, original);
        if (index < 0)
        {
            return existing;
        }

        var list = existing.ToList();
        list.RemoveAt(index);
        return list
            .Append(updated)
            .OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static int FindWordIndex(IReadOnlyList<CustomWordEntry> words, CustomWordEntry target)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (ReferenceEquals(words[i], target))
            {
                return i;
            }
        }

        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] == target)
            {
                return i;
            }
        }

        if (!string.IsNullOrWhiteSpace(target.Id))
        {
            for (var i = 0; i < words.Count; i++)
            {
                if (string.Equals(words[i].Id, target.Id, StringComparison.Ordinal) &&
                    HasSameContent(words[i], target))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool HasSameContent(CustomWordEntry left, CustomWordEntry right) =>
        string.Equals(left.Replacement, right.Replacement, StringComparison.Ordinal) &&
        left.Strictness == right.Strictness &&
        left.Category == right.Category &&
        left.Aliases.SequenceEqual(right.Aliases, StringComparer.Ordinal);

    /// <summary>Removes the selected rows and says how many really went.</summary>
    /// <remarks>
    /// THE COUNT IS WHAT WAS REALLY REMOVED, NOT WHAT WAS SELECTED. Removal matches by identity, so
    /// a row that another change replaced while this was waiting is no longer the row that was
    /// chosen - it is left alone, correctly, and the page can say so rather than tell somebody a
    /// word is gone when it is still there.
    /// </remarks>
    public Task<SettingsSaveResult<int>> RemoveWordsAsync(IReadOnlyList<CustomWordEntry> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return ChangeAsync(data =>
        {
            var remaining = CustomWordRemoval.Without(data.CustomWords, selected);
            return (data.WithCustomWords(remaining), data.CustomWords.Count - remaining.Count);
        });
    }

    /// <summary>Removes the selected rows and returns the exact entries removed by reference identity.</summary>
    public Task<SettingsSaveResult<IReadOnlyList<CustomWordEntry>>> RemoveWordsAndReportAsync(IReadOnlyList<CustomWordEntry> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return ChangeAsync<IReadOnlyList<CustomWordEntry>>(data =>
        {
            var remaining = CustomWordRemoval.Without(data.CustomWords, selected);
            var remainingSet = new HashSet<CustomWordEntry>(remaining, ReferenceEqualityComparer.Instance);
            var removed = data.CustomWords.Where(entry => !remainingSet.Contains(entry)).ToArray();
            return (data.WithCustomWords(remaining), removed);
        });
    }

    /// <summary>
    /// Atomically saves a learned correction (<paramref name="original"/> -> <paramref name="corrected"/>)
    /// inside the serialized settings gate, verifying target identity and alias ownership against live words.
    /// </summary>
    public Task<SettingsSaveResult<LearnedSaveOutcome>> SaveLearnedWordAsync(
        string original,
        string corrected,
        Core.Learning.LearnTargetState expectedTarget,
        IReadOnlyList<CustomWordEntry> packTerms,
        DateTimeOffset learnedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);
        ArgumentException.ThrowIfNullOrWhiteSpace(corrected);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        ArgumentNullException.ThrowIfNull(packTerms);

        var trimmedOriginal = original.Trim();
        var trimmedCorrected = corrected.Trim();
        var originalKey = Core.Learning.CorrectionPairKey.Normalise(trimmedOriginal);
        var correctedKey = Core.Learning.CorrectionPairKey.Normalise(trimmedCorrected);

        return ChangeAsync(data =>
        {
            var liveWords = data.CustomWords;
            var userTarget = liveWords.FirstOrDefault(w =>
                string.Equals(Core.Learning.CorrectionPairKey.Normalise(w.Replacement), correctedKey, StringComparison.Ordinal));
            var packTarget = userTarget is null
                ? packTerms.FirstOrDefault(w =>
                    string.Equals(Core.Learning.CorrectionPairKey.Normalise(w.Replacement), correctedKey, StringComparison.Ordinal))
                : null;
            var target = userTarget ?? packTarget;

            if (expectedTarget is Core.Learning.LearnTargetState.ExistingWord expected)
            {
                if (target is null || !string.Equals(target.Id, expected.WordId, StringComparison.Ordinal))
                {
                    return (data, new LearnedSaveOutcome(LearnedSaveStatus.TargetGone, null, null));
                }
            }

            if (target is not null &&
                (string.Equals(Core.Learning.CorrectionPairKey.Normalise(target.Replacement), originalKey, StringComparison.Ordinal) ||
                 target.Aliases.Any(a => string.Equals(Core.Learning.CorrectionPairKey.Normalise(a), originalKey, StringComparison.Ordinal))))
            {
                return (data, new LearnedSaveOutcome(LearnedSaveStatus.AlreadyCovered, target, target));
            }

            // Verify neither the original trigger nor corrected replacement is owned by another entry.
            foreach (var entry in liveWords.Concat(packTerms))
            {
                if (target is not null && string.Equals(entry.Id, target.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.Aliases.Any(a =>
                    string.Equals(Core.Learning.CorrectionPairKey.Normalise(a), originalKey, StringComparison.Ordinal) ||
                    string.Equals(Core.Learning.CorrectionPairKey.Normalise(a), correctedKey, StringComparison.Ordinal)))
                {
                    return (data, new LearnedSaveOutcome(LearnedSaveStatus.AliasOwnedElsewhere, null, null));
                }

                var entryCanonKey = Core.Learning.CorrectionPairKey.Normalise(entry.Replacement);
                if ((!entryCanonKey.Contains(' ', StringComparison.Ordinal) || entry.Aliases.Count == 0) &&
                    (string.Equals(entryCanonKey, originalKey, StringComparison.Ordinal) ||
                     string.Equals(entryCanonKey, correctedKey, StringComparison.Ordinal)))
                {
                    return (data, new LearnedSaveOutcome(LearnedSaveStatus.AliasOwnedElsewhere, null, null));
                }
            }

            if (userTarget is not null)
            {
                var nextAliases = CustomWordEntry.NormalizeAliases(userTarget.Aliases.Append(trimmedOriginal));
                var nextLearned = CustomWordEntry.NormalizeLearnedAliases(
                    userTarget.LearnedAliases.Append(trimmedOriginal),
                    nextAliases);
                var updated = userTarget with
                {
                    Aliases = nextAliases,
                    LearnedAliases = nextLearned,
                    LearnedAt = userTarget.LearnedAt ?? learnedAt,
                };
                var nextList = ReplaceWord(liveWords, userTarget, updated);
                return (data.WithCustomWords(nextList), new LearnedSaveOutcome(LearnedSaveStatus.Saved, userTarget, updated));
            }

            var category = packTarget?.Category ?? WordCategoryFormatting.Classify(trimmedCorrected);
            CustomWordEntry created;
            if (packTarget is not null)
            {
                var baseAliases = CustomWordEntry.NormalizeAliases(packTarget.Aliases.Append(trimmedOriginal));
                created = new CustomWordEntry(
                    baseAliases,
                    packTarget.Replacement,
                    packTarget.Strictness,
                    category,
                    id: packTarget.Id,
                    learnedAliases: [trimmedOriginal],
                    learnedAt: learnedAt);
            }
            else
            {
                created = new CustomWordEntry(
                    aliases: [trimmedOriginal],
                    replacement: trimmedCorrected,
                    strictness: MatchStrictness.Default,
                    category: category,
                    id: Guid.NewGuid().ToString("D"),
                    learnedAliases: [trimmedOriginal],
                    learnedAt: learnedAt);
            }

            var addedList = liveWords
                .Append(created)
                .OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return (data.WithCustomWords(addedList), new LearnedSaveOutcome(LearnedSaveStatus.Saved, null, created));
        });
    }

    /// <summary>
    /// Atomically reverts a previously learned change ONLY if the live word still equals <paramref name="expectedPostSave"/>.
    /// </summary>
    public Task<SettingsSaveResult<LearnedUndoWriteStatus>> UndoLearnedWordAsync(
        CustomWordEntry expectedPostSave,
        CustomWordEntry? restorePreSave)
    {
        ArgumentNullException.ThrowIfNull(expectedPostSave);

        return ChangeAsync(data =>
        {
            var live = data.CustomWords.FirstOrDefault(w => string.Equals(w.Id, expectedPostSave.Id, StringComparison.Ordinal));
            if (live is null || !live.Equals(expectedPostSave))
            {
                return (data, LearnedUndoWriteStatus.NotApplied);
            }

            if (restorePreSave is null)
            {
                var remaining = CustomWordRemoval.Without(data.CustomWords, [live]);
                return (data.WithCustomWords(remaining), LearnedUndoWriteStatus.Applied);
            }

            var restored = ReplaceWord(data.CustomWords, live, restorePreSave);
            return (data.WithCustomWords(restored), LearnedUndoWriteStatus.Applied);
        });
    }

    /// <summary>Adds a snippet, replacing any snippet of the same name; the list stays sorted by name.</summary>
    /// <remarks>
    /// JUDGED INSIDE THE GATE, against the snippets that are there when it happens, by
    /// <see cref="SnippetRules.Validate"/>: a trigger nobody can say, an empty text, or a trigger another
    /// snippet already answers to is refused and nothing is stored - a duplicate would make which snippet
    /// fires a tie nobody chose. The answer names the clash so the page can say which one.
    /// </remarks>
    public Task<SettingsSaveResult<SnippetSaveOutcome>> AddSnippetAsync(SnippetEntry snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        return ChangeAsync(data =>
        {
            var refusal = SnippetRules.Validate(snippet, data.Snippets, out var clash);
            if (refusal is not null)
            {
                return (data, new SnippetSaveOutcome(refusal, clash));
            }

            return (data.WithSnippets(
                    data.Snippets
                        .Where(entry => !string.Equals(entry.Name, snippet.Name, StringComparison.OrdinalIgnoreCase))
                        .Append(snippet)
                        .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray()),
                new SnippetSaveOutcome(null, null));
        });
    }

    /// <summary>Removes one snippet, matched by value.</summary>
    public Task<SettingsSaveResult> RemoveSnippetAsync(SnippetEntry snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        return ChangeAsync(data => data.WithSnippets(data.Snippets.Where(entry => entry != snippet).ToArray()));
    }

    /// <summary>Validates an edited snippet against <paramref name="existing"/>, ignoring <paramref name="original"/>.</summary>
    public static SnippetRefusal? ValidateSnippetEdit(
        SnippetEntry original,
        string trigger,
        string body,
        IReadOnlyList<SnippetEntry> existing,
        out SnippetEntry? clash)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(existing);
        clash = null;

        var trimmedTrigger = (trigger ?? string.Empty).Trim();
        var key = SnippetText.CollisionKey(trimmedTrigger);
        if (key is null)
        {
            return SnippetRefusal.TriggerEmpty;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return SnippetRefusal.ExpansionEmpty;
        }

        var originalIndex = FindSnippetIndex(existing, original);
        for (var i = 0; i < existing.Count; i++)
        {
            if (i == originalIndex)
            {
                continue;
            }

            if (string.Equals(SnippetText.CollisionKey(existing[i].Name), key, StringComparison.Ordinal))
            {
                clash = existing[i];
                return SnippetRefusal.DuplicateTrigger;
            }
        }

        return null;
    }

    /// <summary>Replaces <paramref name="original"/> with <paramref name="updated"/> while preserving list order.</summary>
    public static IReadOnlyList<SnippetEntry> ReplaceSnippet(
        IReadOnlyList<SnippetEntry> existing,
        SnippetEntry original,
        SnippetEntry updated)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);

        var list = existing.ToList();
        var index = FindSnippetIndex(list, original);
        if (index < 0)
        {
            return existing;
        }

        list[index] = updated;
        return list.ToArray();
    }

    /// <summary>Atomically validates and replaces <paramref name="original"/> while preserving snippet order.</summary>
    public Task<SettingsSaveResult<SnippetSaveOutcome>> UpdateSnippetAsync(
        SnippetEntry original,
        string trigger,
        string body)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(body);

        var updated = new SnippetEntry(trigger.Trim(), body);
        return ChangeAsync(data =>
        {
            if (FindSnippetIndex(data.Snippets, original) < 0)
            {
                return (data, new SnippetSaveOutcome(null, null, TargetMissing: true));
            }

            var refusal = ValidateSnippetEdit(original, updated.Name, updated.Body, data.Snippets, out var clash);
            if (refusal is not null)
            {
                return (data, new SnippetSaveOutcome(refusal, clash));
            }

            return (
                data.WithSnippets(ReplaceSnippet(data.Snippets, original, updated)),
                new SnippetSaveOutcome(null, null));
        });
    }

    private static int FindSnippetIndex(IReadOnlyList<SnippetEntry> snippets, SnippetEntry target)
    {
        for (var i = 0; i < snippets.Count; i++)
        {
            if (ReferenceEquals(snippets[i], target))
            {
                return i;
            }
        }

        for (var i = 0; i < snippets.Count; i++)
        {
            if (snippets[i] == target)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Stores the word said before a snippet, and answers with the word actually stored.</summary>
    /// <remarks>
    /// THE STORED WORD, NOT THE TYPED ONE, comes back so the box can show it: a cleared box stores the
    /// default and "Backslash." stores "backslash" (<see cref="SnippetRules.CleanKeyword"/>). A keyword
    /// of more than one word is refused before anything is written.
    /// </remarks>
    public async Task<SettingsSaveResult<SnippetKeywordOutcome>> SetSnippetKeywordAsync(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        var refusal = SnippetRules.CleanKeyword(typed, out var keyword);
        if (refusal is not null)
        {
            return new SettingsSaveResult<SnippetKeywordOutcome>(null, null, new SnippetKeywordOutcome(refusal, null));
        }

        var saved = await ChangeAsync(data => data.WithSnippetKeyword(keyword)).ConfigureAwait(false);
        return new SettingsSaveResult<SnippetKeywordOutcome>(
            saved.Refusal,
            saved.Failure,
            new SnippetKeywordOutcome(null, saved.Saved ? keyword : null));
    }

    /// <summary>Applies any change to the words and snippets, derived from what is current inside the gate.</summary>
    public Task<SettingsSaveResult> ChangeAsync(Func<ReusableUserData, ReusableUserData> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return _settings.SaveAsync(current => current with { UserData = change(current.UserData) });
    }

    /// <summary>Applies a change that also has something to say about what it did, worked out inside the gate.</summary>
    public Task<SettingsSaveResult<T>> ChangeAsync<T>(Func<ReusableUserData, (ReusableUserData Data, T Value)> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return _settings.SaveAsync(current =>
        {
            // THE SAME DATA BACK IS NO CHANGE, and the settings go back unchanged, which the writer does not save:
            // a refused snippet or import leaves the file alone rather than rewriting it with what it holds.
            var (data, value) = change(current.UserData);
            return (ReferenceEquals(data, current.UserData) ? current : current with { UserData = data }, value);
        });
    }

    private static readonly Lazy<HashSet<string>> PackWordSignatures = new(() =>
        VocabularyPacks.All
            .SelectMany(p => p.Entries)
            .SelectMany(e => e.Aliases.Select(alias => GetSignature(alias, e.Replacement)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static string GetSignature(string spoken, string replacement) =>
        $"{spoken.Trim()}\0{replacement.Trim()}";

    /// <summary>Checks whether a custom word entry matches an entry from the built-in vocabulary packs.</summary>
    public static bool IsPackWord(CustomWordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Aliases.Count > 0
            ? entry.Aliases.Any(alias => PackWordSignatures.Value.Contains(GetSignature(alias, entry.Replacement)))
            : PackWordSignatures.Value.Contains(GetSignature(entry.SpokenForm, entry.Replacement));
    }

    /// <summary>Filters custom word entries by category key ("all", "custom", "packs", or WordCategory name) and optional search query.</summary>
    public static IReadOnlyList<CustomWordEntry> FilterWords(
        IEnumerable<CustomWordEntry> words,
        string category,
        string? query = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        var filtered = words;

        if (string.Equals(category, "custom", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(w => !IsPackWord(w));
        }
        else if (string.Equals(category, "packs", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(IsPackWord);
        }
        else if (!string.IsNullOrWhiteSpace(category) &&
                 !string.Equals(category, "all", StringComparison.OrdinalIgnoreCase) &&
                 Enum.TryParse<WordCategory>(category, ignoreCase: true, out var parsedCategory))
        {
            filtered = filtered.Where(w => w.Category == parsedCategory);
        }

        return ApplyQueryFilter(filtered, query);
    }

    /// <summary>Filters custom word entries by typed <see cref="WordCategory"/> (null = All categories) and optional search query.</summary>
    public static IReadOnlyList<CustomWordEntry> FilterWords(
        IEnumerable<CustomWordEntry> words,
        WordCategory? category,
        string? query = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        var filtered = category is { } selected
            ? words.Where(w => w.Category == selected)
            : words;

        return ApplyQueryFilter(filtered, query);
    }

    private static List<CustomWordEntry> ApplyQueryFilter(
        IEnumerable<CustomWordEntry> entries,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries.ToList();
        }

        var trimmed = query.Trim();
        return entries
            .Where(w =>
                w.Replacement.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                w.Aliases.Any(alias => alias.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
