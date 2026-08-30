namespace EnviousWispr.Core.Settings;

/// <summary>Removes exactly the rows a person chose, and no others.</summary>
/// <remarks>
/// A RECORD COMPARES BY VALUE, WHICH IS WRONG FOR "THE ROW I CLICKED". Two identical entries are
/// equal to each other, so filtering the stored list by value removes BOTH when a person selected
/// one - silently, and only for somebody who has a duplicate. Nothing in the app forbids one:
/// importing a profile or a word list does not require uniqueness.
///
/// IDENTITY, NOT EQUALITY. The list the view is bound to holds the same instances the selection
/// hands back, so comparing by reference removes the chosen row and leaves its twin alone.
///
/// SEPARATED FROM THE BUTTON SO IT CAN BE TESTED. The defect is invisible in the running app unless
/// somebody happens to have a duplicate and happens to notice both disappear.
/// </remarks>
public static class CustomWordRemoval
{
    /// <summary>The list without the selected rows, keeping any value-equal twins.</summary>
    public static IReadOnlyList<CustomWordEntry> Without(
        IReadOnlyList<CustomWordEntry> words,
        IReadOnlyList<CustomWordEntry> selected)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(selected);

        if (selected.Count == 0)
        {
            return words;
        }

        var removing = new HashSet<CustomWordEntry>(selected, ReferenceEqualityComparer.Instance);
        return words.Where(entry => !removing.Contains(entry)).ToArray();
    }

    /// <summary>Compares by identity, so a duplicate is a different row rather than the same one.</summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<CustomWordEntry>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(CustomWordEntry? left, CustomWordEntry? right) => ReferenceEquals(left, right);

        public int GetHashCode(CustomWordEntry entry) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry);
    }
}
