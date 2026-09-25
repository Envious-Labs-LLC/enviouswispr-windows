namespace EnviousWispr.Core.Settings;

/// <summary>The comparison form for snippet matching, and the ONE place it is defined.</summary>
/// <remarks>
/// IN CORE RATHER THAN BESIDE THE MATCHER, because three callers need the identical answer and they
/// live in different projects: the expander (matching), the Snippets page (refusing a duplicate
/// trigger) and the stored-data rules. A second implementation is how two of them come to disagree
/// about whether "My Email." and "my email" are the same trigger. Ported from the macOS
/// <c>SnippetText</c> (Sources/EnviousWisprCore/Snippet.swift): lowercase, drop a leading opening
/// bracket or quote, drop trailing punctuation. Everything else compares literally - deliberately NOT
/// fuzzy, unlike custom-word correction, or "my email address" spoken in ordinary prose would start
/// pasting an address.
/// </remarks>
public static class SnippetText
{
    /// <summary>Punctuation dropped from the FRONT of a spoken token before comparison.</summary>
    public static IReadOnlySet<char> Leading { get; } =
        new HashSet<char> { '(', '[', '{', '"', '\'', '“', '‘' };

    /// <summary>Punctuation that CLOSES rather than terminates: it can sit AFTER a full stop and hide it.</summary>
    public static IReadOnlySet<char> Closing { get; } =
        new HashSet<char> { ')', ']', '}', '"', '\'', '”', '’' };

    /// <summary>Punctuation that ENDS a sentence. A comma inside a matched phrase is noise; a full stop is a boundary.</summary>
    public static IReadOnlySet<char> SentenceEnding { get; } = new HashSet<char> { '.', '!', '?' };

    /// <summary>Punctuation dropped from the END of a spoken token before comparison.</summary>
    /// <remarks>
    /// COMPOSED FROM THE THREE ROLES RATHER THAN SPELLED OUT, so the roles cannot drift apart. The
    /// expander re-attaches the same run after the expansion, so a full stop that clung to the last
    /// trigger word survives; a token stripped here and re-attached from a different list would drift.
    /// </remarks>
    public static IReadOnlySet<char> Trailing { get; } =
        new HashSet<char>(SentenceEnding.Concat(Closing).Concat([',', ';', ':']));

    /// <summary>A spoken token reduced to its comparison form.</summary>
    /// <remarks>
    /// TRIMMED AT BOTH ENDS, AND THAT IS NOT REDUNDANT WITH THE TOKENISER: the keyword arrives from a
    /// text box the person types into. Without the trim a keyword of "   " normalises to itself, reads
    /// as armed, and the expansion runs against a word nobody can speak. Trim, strip, trim again, so
    /// ' "hello" ' and '"hello"' reach the same form.
    /// </remarks>
    public static string Normalize(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var s = token.ToLowerInvariant().AsSpan().Trim();
        while (s.Length > 0 && Leading.Contains(s[0]))
        {
            s = s[1..];
        }

        while (s.Length > 0 && Trailing.Contains(s[^1]))
        {
            s = s[..^1];
        }

        return s.Trim().ToString();
    }

    /// <summary>True when this token ends a sentence, read THROUGH trailing whitespace and closing marks.</summary>
    /// <remarks>
    /// <c>my.”</c> ends a sentence; its last character is the quote. Reading only the final character
    /// let a snippet span a real sentence break and hid that a saved expansion already ended itself.
    /// Whitespace first, because a saved expansion is deliberately NOT trimmed: a trailing newline the
    /// person typed must not hide the full stop before it.
    /// </remarks>
    public static bool EndsSentence(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var s = token.AsSpan().Trim();
        while (s.Length > 0 && Closing.Contains(s[^1]))
        {
            s = s[..^1];
        }

        return s.Length > 0 && SentenceEnding.Contains(s[^1]);
    }

    /// <summary>The length of the maximal run of trailing punctuation starting at <paramref name="start"/>.</summary>
    /// <remarks>
    /// THE MIRROR OF <see cref="TrailingPunctuation"/>, which reads from the END of a token. This reads
    /// FORWARD from a sentinel into text a model wrote, and must see the WHOLE run before deciding: a
    /// loop that stopped at the first non-terminator cannot see a full stop standing behind a bracket.
    /// </remarks>
    public static int PunctuationRunLength(string text, int start)
    {
        ArgumentNullException.ThrowIfNull(text);
        var end = start;
        while (end < text.Length && Trailing.Contains(text[end]))
        {
            end++;
        }

        return end - start;
    }

    /// <summary>A trailing-punctuation run with its sentence terminators removed and everything else kept, in order.</summary>
    /// <remarks>
    /// THE ONE ANSWER TO "A TERMINATOR CAN HIDE BEHIND A CLOSING MARK", and every site that decides about
    /// a run calls it. macOS grew three versions of that question and two got it wrong the same way.
    /// </remarks>
    public static string DroppingSentenceEndings(string run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return string.Concat(run.Where(character => !SentenceEnding.Contains(character)));
    }

    /// <summary>The leading punctuation run on a token, in source order.</summary>
    public static string LeadingPunctuation(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var length = 0;
        while (length < token.Length && Leading.Contains(token[length]))
        {
            length++;
        }

        return token[..length];
    }

    /// <summary>The trailing punctuation run on a token, in source order: what <see cref="Normalize"/> removed from the end.</summary>
    public static string TrailingPunctuation(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var start = token.Length;
        while (start > 0 && Trailing.Contains(token[start - 1]))
        {
            start--;
        }

        return token[start..];
    }

    /// <summary>A trigger's comparison form: the tokens the matcher compares against, in order.</summary>
    public static IReadOnlyList<string> TriggerTokens(string trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return trigger
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(token => token.Length > 0)
            .ToArray();
    }

    /// <summary>The ONE definition of "the same spoken words", or null for a trigger nobody can say.</summary>
    /// <remarks>
    /// Tokens joined by one space: injective, because every token is non-empty and whitespace-free.
    /// NULL RATHER THAN EMPTY so an empty trigger never collides with another empty trigger: a snippet
    /// nobody can say is refused, not matched.
    /// </remarks>
    public static string? CollisionKey(string trigger)
    {
        var tokens = TriggerTokens(trigger);
        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }
}

/// <summary>What the expansion step fires against: the saved snippets and the word that must come first.</summary>
/// <param name="Snippets">The saved snippets; <see cref="SnippetEntry.Name"/> is the trigger.</param>
/// <param name="Keyword">The word spoken immediately before a trigger. Compared through <see cref="SnippetText.Normalize"/>.</param>
public sealed record SnippetVocabulary(IReadOnlyList<SnippetEntry> Snippets, string Keyword)
{
    /// <summary>The fresh-install keyword, the macOS default (<c>SnippetVocabulary.defaultKeyword</c>, founder 2026-09-01).</summary>
    public const string DefaultKeyword = "backslash";

    /// <summary>Nothing saved and nothing to say: the step is skipped.</summary>
    public static SnippetVocabulary Empty { get; } = new([], string.Empty);

    /// <summary>
    /// Nothing can fire without both halves, so the step is skipped rather than run as a no-op, and a
    /// person with no snippets takes a byte-identical path.
    /// </summary>
    public bool CanFire => Snippets.Count > 0 && SnippetText.Normalize(Keyword).Length > 0;

    /// <summary>The vocabulary a person's saved data describes.</summary>
    public static SnippetVocabulary From(ReusableUserData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new SnippetVocabulary(data.Snippets, data.SnippetKeyword);
    }
}

/// <summary>Why a snippet or a keyword was not saved. One sentence per member, on the page.</summary>
public enum SnippetRefusal
{
    /// <summary>The trigger is only punctuation or space, so nothing can ever match it.</summary>
    TriggerEmpty,

    /// <summary>There is no text to paste, so the snippet would delete the words that were said.</summary>
    ExpansionEmpty,

    /// <summary>Another snippet is matched by the same spoken words.</summary>
    DuplicateTrigger,

    /// <summary>The keyword is more than one word, so it could never match the one token it is compared against.</summary>
    KeywordNotOneWord,
}

/// <summary>The save-time rules, stated once so the page and the stored-data writer cannot disagree.</summary>
public static class SnippetRules
{
    /// <summary>Whether <paramref name="candidate"/> may be saved beside <paramref name="existing"/>, and which snippet it clashes with.</summary>
    /// <remarks>
    /// A DUPLICATE TRIGGER IS REFUSED AT THE DOOR, as the macOS editor refuses it
    /// (<c>SnippetsManager.validate</c>), which is what makes the matcher's tie-break unreachable
    /// rather than merely unlikely. A snippet saved under the SAME name as an existing one, ignoring
    /// case, is not a clash: that is how this page edits a snippet, by replacing it - the same row, as
    /// macOS's same-id exemption says.
    /// </remarks>
    public static SnippetRefusal? Validate(SnippetEntry candidate, IReadOnlyList<SnippetEntry> existing, out SnippetEntry? clash)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);
        clash = null;
        var key = SnippetText.CollisionKey(candidate.Name);
        if (key is null)
        {
            return SnippetRefusal.TriggerEmpty;
        }

        if (string.IsNullOrWhiteSpace(candidate.Body))
        {
            return SnippetRefusal.ExpansionEmpty;
        }

        clash = existing.FirstOrDefault(entry =>
            !string.Equals(entry.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SnippetText.CollisionKey(entry.Name), key, StringComparison.Ordinal));
        return clash is null ? null : SnippetRefusal.DuplicateTrigger;
    }

    /// <summary>The keyword as it will be stored: its comparison form, or the default when the box was cleared.</summary>
    /// <remarks>
    /// A BLANK KEYWORD RESTORES THE DEFAULT RATHER THAN BEING REFUSED (macOS <c>setKeyword</c>): the
    /// person is clearing a text box, not asking to switch the feature off, and an empty keyword would
    /// silently stop every snippet. A keyword of more than one word is refused, because the matcher
    /// compares the keyword against ONE spoken token and "hey wispr" would read as armed and never fire.
    /// </remarks>
    public static SnippetRefusal? CleanKeyword(string typed, out string keyword)
    {
        ArgumentNullException.ThrowIfNull(typed);
        var cleaned = SnippetText.Normalize(typed);
        if (cleaned.Length == 0)
        {
            keyword = SnippetVocabulary.DefaultKeyword;
            return null;
        }

        if (cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 1)
        {
            keyword = cleaned;
            return SnippetRefusal.KeywordNotOneWord;
        }

        keyword = cleaned;
        return null;
    }
}
