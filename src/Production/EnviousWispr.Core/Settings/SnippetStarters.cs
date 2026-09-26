namespace EnviousWispr.Core.Settings;

/// <summary>The example snippets a brand-new install starts with (macOS <c>SnippetStarters</c>, founder 2026-09-01).</summary>
/// <remarks>
/// A FEATURE WHOSE FIRST SCREEN IS EMPTY HAS TO BE EXPLAINED; one whose first screen is already full of
/// working examples explains itself. These six reach a person only through <see cref="AppSettings.FreshInstall"/>,
/// which the settings store hands back when there is NO settings file at all. The app writes that file on the
/// same launch, so from then on they are ordinary snippets: editable, deletable, and never put back. An
/// existing profile, a migrated one, an unreadable one and a reset one all start from
/// <see cref="AppSettings.Default"/>, which carries none - an example injected into somebody's own list would
/// be a stranger's email address among theirs.
///
/// EVERY ONE OF THEM FIRES (founder: a decorative example the user cannot try is a screenshot). Every trigger
/// begins with "my" and ends in one common word, because the matcher compares words literally and a trigger the
/// engine might run together would be an example that silently does not work.
///
/// THE TEXT IS VISIBLY FICTIONAL, copied from macOS as it ships: <c>example.com</c> is reserved for exactly this
/// (RFC 2606), 555 is not a working area code, and "John Doe" is the stock placeholder name, so a starter pasted
/// into a real message by mistake reads as a placeholder rather than as somebody's real address.
///
/// STORED IN NAME ORDER, NOT THE MAC'S ORDER. The Windows list is kept sorted by trigger on every save
/// (<c>VocabularyPresenter.AddSnippetAsync</c>), so seeding in any other order would reshuffle the list the
/// first time somebody added a snippet.
/// </remarks>
public static class SnippetStarters
{
    /// <summary>The six, as macOS ships them (trigger, then text).</summary>
    private static readonly SnippetEntry[] Catalog =
    [
        new("my email", "john.doe@example.com"),
        new("my phone", "(555) 010-4477"),
        new("my address", "1600 Example Way, Suite 200, Springfield, IL 62704"),
        new("my calendar", "https://cal.example.com/john-doe"),
        new("my signature", "Thanks so much. John Doe, Product at Example Co."),
        new("my intro", "Hi, I'm John Doe. I lead product at Example Co, and I'm happy to help however I can."),
    ];

    /// <summary>The starters as a fresh install stores them: sorted by trigger, as every save keeps the list.</summary>
    public static IReadOnlyList<SnippetEntry> All { get; } = Catalog
        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>True while this snippet is still exactly a starter as it shipped: the row shows "Example".</summary>
    /// <remarks>
    /// DERIVED, NOT STORED, so the badge needs no field and no migration: the first edit to either the trigger or
    /// the text clears it, and nothing has to remember to. Both halves are compared, because a person who kept the
    /// trigger and typed their own address has made it theirs - the one moment the badge must be gone.
    /// </remarks>
    public static bool IsUneditedExample(SnippetEntry snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        return Catalog.Any(starter =>
            string.Equals(starter.Name, snippet.Name, StringComparison.Ordinal) &&
            string.Equals(starter.Body, snippet.Body, StringComparison.Ordinal));
    }
}
