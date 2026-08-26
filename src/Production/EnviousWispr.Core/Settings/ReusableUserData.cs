namespace EnviousWispr.Core.Settings;

public sealed record CustomWordEntry(string SpokenForm, string Replacement);

public sealed record SnippetEntry(string Name, string Body);

public sealed class ReusableUserData : IEquatable<ReusableUserData>
{
    public ReusableUserData(
        IReadOnlyList<CustomWordEntry> customWords,
        IReadOnlyList<SnippetEntry> snippets)
    {
        ArgumentNullException.ThrowIfNull(customWords);
        ArgumentNullException.ThrowIfNull(snippets);
        CustomWords = customWords.ToArray();
        Snippets = snippets.ToArray();
    }

    public IReadOnlyList<CustomWordEntry> CustomWords { get; }

    public IReadOnlyList<SnippetEntry> Snippets { get; }

    public static ReusableUserData Empty { get; } = new(
        Array.Empty<CustomWordEntry>(),
        Array.Empty<SnippetEntry>());

    public bool Equals(ReusableUserData? other) =>
        other is not null &&
        CustomWords.SequenceEqual(other.CustomWords) &&
        Snippets.SequenceEqual(other.Snippets);

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

        return hash.ToHashCode();
    }
}

public sealed record PortableProfile(
    int SchemaVersion,
    UserPreferences Preferences,
    ReusableUserData UserData)
{
    public const int CurrentSchemaVersion = 4;
}
