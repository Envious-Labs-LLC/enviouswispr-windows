using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>How a snippet save went: refused and why, with the snippet it clashed with, or stored.</summary>
public readonly record struct SnippetSaveOutcome(SnippetRefusal? Refusal, SnippetEntry? Clash, bool TargetMissing = false);

/// <summary>Why a word edit was refused, if it could not be applied.</summary>
public enum WordEditRefusal
{
    TargetMissing,
    DuplicateSpokenForm,
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
        return ChangeAsync(data => data.WithCustomWords(
            data.CustomWords
                .Where(entry => !string.Equals(entry.SpokenForm, word.SpokenForm, StringComparison.OrdinalIgnoreCase))
                .Append(word)
                .OrderBy(entry => entry.SpokenForm, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()));
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

        var updated = new CustomWordEntry(spokenForm.Trim(), replacement.Trim(), strictness);
        return ChangeAsync(data =>
        {
            var originalIndex = FindWordIndex(data.CustomWords, original);
            if (originalIndex < 0)
            {
                return (data, new WordEditOutcome(WordEditRefusal.TargetMissing, null));
            }

            if (!string.Equals(original.SpokenForm, updated.SpokenForm, StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < data.CustomWords.Count; i++)
                {
                    if (i != originalIndex &&
                        string.Equals(data.CustomWords[i].SpokenForm, updated.SpokenForm, StringComparison.OrdinalIgnoreCase))
                    {
                        return (data, new WordEditOutcome(WordEditRefusal.DuplicateSpokenForm, data.CustomWords[i]));
                    }
                }
            }

            return (
                data.WithCustomWords(ReplaceWord(data.CustomWords, original, updated)),
                new WordEditOutcome(null, null));
        });
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

        return -1;
    }

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
            .SelectMany(p => CustomWordImport.Read(p.Words, []).Additions)
            .Select(e => GetSignature(e.SpokenForm, e.Replacement))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static string GetSignature(string spoken, string replacement) =>
        $"{spoken.Trim()}\0{replacement.Trim()}";

    /// <summary>Checks whether a custom word entry matches an entry from the built-in vocabulary packs.</summary>
    public static bool IsPackWord(CustomWordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return PackWordSignatures.Value.Contains(GetSignature(entry.SpokenForm, entry.Replacement));
    }

    /// <summary>Filters custom word entries by category ("all", "custom", "packs") and optional search query.</summary>
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

        if (!string.IsNullOrWhiteSpace(query))
        {
            var trimmed = query.Trim();
            filtered = filtered.Where(w =>
                w.SpokenForm.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || w.Replacement.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        return filtered.ToList();
    }
}
