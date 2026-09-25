namespace EnviousWispr.Core.Settings;

public sealed record CustomWordEntry(
    string SpokenForm,
    string Replacement,
    MatchStrictness Strictness = MatchStrictness.Default)
{
    /// <summary>
    /// What a screen reader announces for this row.
    /// </summary>
    /// <remarks>
    /// A record's generated ToString emits its type name and brace syntax, and a list row with no
    /// explicit automation name falls back to exactly that. Measured on the running app, the row
    /// announced "CustomWordEntry open brace SpokenForm equals zzz test entry comma Replacement
    /// equals ZZZTestEntry close brace" before reaching anything a user cares about.
    ///
    /// The row's child text elements were already clean; only the container was wrong, and only
    /// once a row was bound - which is why an audit of an EMPTY list found nothing.
    /// </remarks>
    public override string ToString() => Strictness switch
    {
        MatchStrictness.Loose => $"{SpokenForm} becomes {Replacement}, matched loosely",
        MatchStrictness.Strict => $"{SpokenForm} becomes {Replacement}, matched strictly",
        _ => $"{SpokenForm} becomes {Replacement}",
    };
}

/// <summary>One saved snippet.</summary>
/// <param name="Name">
/// THE TRIGGER: the words spoken after the keyword, matched word for word after
/// <see cref="SnippetText.Normalize"/>, never fuzzily. Named Name because that is what it was called on
/// disk before snippets fired, and renaming a stored member would break every saved file.
/// </param>
/// <param name="Body">The text delivered in the trigger's place, exactly as typed, line breaks and fill-ins included.</param>
/// <remarks>
/// NO COMPUTED PROPERTIES HERE. This record is written to the settings file as it stands, and the
/// store refuses a member it does not know - a derived property would be serialised by one build and
/// refused by the next. The trigger's tokens and collision key live on <see cref="SnippetText"/>.
/// </remarks>
public sealed record SnippetEntry(string Name, string Body)
{
    /// <summary>What a screen reader announces for this row. See <see cref="CustomWordEntry"/>.</summary>
    public override string ToString() => $"{Name}: {Body}";
}

public sealed class ReusableUserData : IEquatable<ReusableUserData>
{
    /// <param name="customWords">The person's corrections.</param>
    /// <param name="snippets">The person's snippets.</param>
    /// <param name="snippetKeyword">
    /// The word spoken before a snippet's trigger. Null or blank is the default, "backslash": a file
    /// written before the keyword existed has no such member, and an absent member must read as the
    /// word every snippet has always needed. The constructor's parameter is named for the member so
    /// the settings file binds to it.
    /// </param>
    /// <remarks>
    /// THE KEYWORD LIVES WITH THE SNIPPETS, as it does on macOS (<c>SnippetVocabulary.keyword</c>, saved
    /// in the snippet store), so a portable profile carries it with the snippets it belongs to. A change
    /// to the words or the snippets goes through <see cref="WithCustomWords"/> or
    /// <see cref="WithSnippets"/>, which keep it: rebuilding this with two arguments would quietly put a
    /// changed keyword back to the default.
    /// </remarks>
    public ReusableUserData(
        IReadOnlyList<CustomWordEntry> customWords,
        IReadOnlyList<SnippetEntry> snippets,
        string? snippetKeyword = null)
    {
        ArgumentNullException.ThrowIfNull(customWords);
        ArgumentNullException.ThrowIfNull(snippets);
        CustomWords = customWords.ToArray();
        Snippets = snippets.ToArray();
        SnippetKeyword = string.IsNullOrWhiteSpace(snippetKeyword)
            ? SnippetVocabulary.DefaultKeyword
            : snippetKeyword;
    }

    public IReadOnlyList<CustomWordEntry> CustomWords { get; }

    public IReadOnlyList<SnippetEntry> Snippets { get; }

    /// <summary>The word spoken immediately before a snippet's trigger; "backslash" unless the person chose another.</summary>
    public string SnippetKeyword { get; }

    /// <summary>The same data with other words, keeping the snippets and the keyword.</summary>
    public ReusableUserData WithCustomWords(IReadOnlyList<CustomWordEntry> customWords) =>
        new(customWords, Snippets, SnippetKeyword);

    /// <summary>The same data with other snippets, keeping the words and the keyword.</summary>
    public ReusableUserData WithSnippets(IReadOnlyList<SnippetEntry> snippets) =>
        new(CustomWords, snippets, SnippetKeyword);

    /// <summary>The same data with another keyword, keeping the words and the snippets.</summary>
    public ReusableUserData WithSnippetKeyword(string snippetKeyword) =>
        new(CustomWords, Snippets, snippetKeyword);

    public static ReusableUserData Empty { get; } = new(
        Array.Empty<CustomWordEntry>(),
        Array.Empty<SnippetEntry>());

    public bool Equals(ReusableUserData? other) =>
        other is not null &&
        CustomWords.SequenceEqual(other.CustomWords) &&
        Snippets.SequenceEqual(other.Snippets) &&
        string.Equals(SnippetKeyword, other.SnippetKeyword, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ReusableUserData);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in CustomWords)
        {
            hash.Add(entry);
        }

        foreach (var entry in Snippets)
        {
            hash.Add(entry);
        }

        hash.Add(SnippetKeyword, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed record PortableProfile(
    int SchemaVersion,
    UserPreferences Preferences,
    ReusableUserData UserData)
{
    public const int CurrentSchemaVersion = 12;
}
