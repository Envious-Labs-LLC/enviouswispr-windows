using EnviousWispr.Core.Settings;

namespace EnviousWispr.Presentation;

/// <summary>How a snippet save went: refused and why, with the snippet it clashed with, or stored.</summary>
public readonly record struct SnippetSaveOutcome(SnippetRefusal? Refusal, SnippetEntry? Clash);

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
            var (data, value) = change(current.UserData);
            return (current with { UserData = data }, value);
        });
    }
}
