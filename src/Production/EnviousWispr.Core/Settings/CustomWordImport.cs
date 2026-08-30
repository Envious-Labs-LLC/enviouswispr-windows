namespace EnviousWispr.Core.Settings;

/// <summary>One line of an imported word list, and what became of it.</summary>
public readonly record struct ImportedWordLine(
    int LineNumber,
    string RawText,
    CustomWordEntry? Entry,
    ImportedWordOutcome Outcome);

/// <summary>What happened to one line.</summary>
public enum ImportedWordOutcome
{
    /// <summary>A new pair, ready to add.</summary>
    Added,

    /// <summary>Blank, or a comment. Not a problem and not a word.</summary>
    Ignored,

    /// <summary>The line has no separator, so there is nothing to correct it TO.</summary>
    Unreadable,

    /// <summary>The same spoken form already exists, with the same replacement.</summary>
    AlreadyPresent,

    /// <summary>The same spoken form already exists with a DIFFERENT replacement.</summary>
    Conflict,
}

/// <summary>The whole result of reading a word list, before anything is changed.</summary>
public sealed record CustomWordImportPlan(IReadOnlyList<ImportedWordLine> Lines)
{
    public IReadOnlyList<CustomWordEntry> Additions => Lines
        .Where(line => line.Outcome == ImportedWordOutcome.Added && line.Entry is not null)
        .Select(line => line.Entry!)
        .ToArray();

    /// <summary>The words the file corrects differently from the way the user already does.</summary>
    /// <remarks>
    /// CARRIED, NOT JUST COUNTED, AND THAT IS THE WHOLE POINT OF THE ROW. Reporting "3 left alone"
    /// tells someone their curated list was ignored and gives them nothing to do about it except
    /// retype three words they already have written down. macOS lets the user decide; this is the
    /// value that decision needs.
    ///
    /// Still not applied by reading a plan. A plan reads and never writes, so choosing to take
    /// these remains a separate, deliberate act at the call site.
    /// </remarks>
    public IReadOnlyList<CustomWordEntry> Conflicts => Lines
        .Where(line => line.Outcome == ImportedWordOutcome.Conflict && line.Entry is not null)
        .Select(line => line.Entry!)
        .ToArray();

    public int ConflictCount => Lines.Count(line => line.Outcome == ImportedWordOutcome.Conflict);

    public int UnreadableCount => Lines.Count(line => line.Outcome == ImportedWordOutcome.Unreadable);
}

/// <summary>
/// Reads a word list a person typed or exported from somewhere else.
/// </summary>
/// <remarks>
/// WHY A PLAN RATHER THAN A LIST. Importing can collide with words the user already has, and a
/// collision is the one thing they must be told about rather than have decided for them: silently
/// overwriting a correction someone tuned by hand is worse than importing nothing. So reading and
/// applying are separate, and this half only ever reads.
///
/// A LINE WITH NO SEPARATOR IS UNREADABLE, NOT IGNORED, and the distinction is the whole reason the
/// outcome is an enum rather than a bool. "Ignored" is a blank line and means nothing went wrong;
/// "unreadable" is a line the user meant as a word and which cannot become one. Collapsing the two
/// would drop half a file silently and report success.
///
/// THE SEPARATORS ARE THE ONES PEOPLE ACTUALLY PRODUCE. A comma, a tab, or an equals sign, because
/// a word list arrives as a spreadsheet export, a text file, or something pasted out of a rival
/// app. A quoted CSV field containing a comma is NOT handled, and that is stated rather than
/// silently mangled - see <see cref="Read"/>.
/// </remarks>
public static class CustomWordImport
{
    private static readonly char[] Separators = [',', '\t', '='];

    /// <summary>Longest a spoken form or replacement may be before the line is refused.</summary>
    /// <remarks>
    /// A pasted document with no separators would otherwise become one enormous "word". The limit
    /// is generous for a phrase and far below anything anyone would type deliberately.
    /// </remarks>
    public const int MaximumFieldLength = 200;

    /// <summary>
    /// Reads a word list without changing anything.
    /// </summary>
    /// <param name="text">The file's contents, or what the user pasted.</param>
    /// <param name="existing">Words the user already has, so collisions can be reported.</param>
    /// <remarks>
    /// KNOWN LIMIT, STATED RATHER THAN SILENTLY WRONG: a quoted CSV field containing a comma splits
    /// at that comma. Handling it properly means a real CSV parser, and half of one is worse than
    /// none - it would succeed on the easy cases and mangle exactly the rows a user could not
    /// predict. A line that splits into more than two parts is refused as unreadable rather than
    /// guessed at, so the failure is visible.
    /// </remarks>
    public static CustomWordImportPlan Read(string text, IReadOnlyList<CustomWordEntry> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (string.IsNullOrEmpty(text))
        {
            return new CustomWordImportPlan([]);
        }

        var known = existing.ToDictionary(
            entry => entry.SpokenForm,
            entry => entry.Replacement,
            StringComparer.OrdinalIgnoreCase);

        // Spoken forms already claimed by an EARLIER line of this same import. Without this, a file
        // listing one word twice adds it twice, and the list gains a duplicate the user never typed.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = new List<ImportedWordLine>();
        var raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < raw.Length; index++)
        {
            lines.Add(ReadLine(raw[index], index + 1, known, claimed));
        }

        return new CustomWordImportPlan(lines);
    }

    /// <summary>
    /// Writes a word list a person can read, edit, and import again.
    /// </summary>
    /// <remarks>
    /// Comma-separated, because that is what opens in a spreadsheet. A field containing a comma
    /// would break the round trip, so it is refused at IMPORT rather than escaped at export - one
    /// of the two has to give, and refusing on the way in is the half a user can see and fix.
    /// </remarks>
    public static string Write(IReadOnlyList<CustomWordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return string.Join(
            Environment.NewLine,
            entries.Select(entry => $"{entry.SpokenForm},{entry.Replacement}"));
    }

    /// <summary>Takes the imported version of words the user already corrects differently.</summary>
    /// <remarks>
    /// IN CORE RATHER THAN IN THE WINDOW, SO IT CAN BE MEASURED. The rest of importing is a value
    /// that a test can drive with no windowing present, and the one step that CHANGES a user's list
    /// is the one worth holding to that standard hardest.
    ///
    /// REPLACED IN PLACE, NOT REMOVED AND APPENDED. A user who has ordered their list has ordered
    /// it, and an import that quietly moved three words to the bottom would be a change nobody
    /// asked for arriving alongside one they did.
    ///
    /// A replacement whose spoken form is not already present is IGNORED rather than added. This
    /// answers one question - take their version of a word I already have - and adding is the other
    /// question, which the plan's additions already answer.
    /// </remarks>
    public static IReadOnlyList<CustomWordEntry> Merge(
        IReadOnlyList<CustomWordEntry> existing,
        IReadOnlyList<CustomWordEntry> replacements)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            return existing;
        }

        var incoming = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in replacements)
        {
            incoming[entry.SpokenForm] = entry.Replacement;
        }

        return existing
            .Select(entry => incoming.TryGetValue(entry.SpokenForm, out var replacement)
                ? entry with { Replacement = replacement }
                : entry)
            .ToArray();
    }

    private static ImportedWordLine ReadLine(
        string raw,
        int lineNumber,
        Dictionary<string, string> known,
        Dictionary<string, string> claimed)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return new ImportedWordLine(lineNumber, raw, null, ImportedWordOutcome.Ignored);
        }

        var parts = trimmed.Split(Separators);
        if (parts.Length != 2)
        {
            return new ImportedWordLine(lineNumber, raw, null, ImportedWordOutcome.Unreadable);
        }

        var spoken = parts[0].Trim();
        var replacement = parts[1].Trim();
        if (spoken.Length == 0 ||
            replacement.Length == 0 ||
            spoken.Length > MaximumFieldLength ||
            replacement.Length > MaximumFieldLength)
        {
            return new ImportedWordLine(lineNumber, raw, null, ImportedWordOutcome.Unreadable);
        }

        var entry = new CustomWordEntry(spoken, replacement);
        if (TryFindExisting(spoken, known, claimed, out var current))
        {
            return new ImportedWordLine(
                lineNumber,
                raw,
                entry,
                string.Equals(current, replacement, StringComparison.Ordinal)
                    ? ImportedWordOutcome.AlreadyPresent
                    : ImportedWordOutcome.Conflict);
        }

        claimed[spoken] = replacement;
        return new ImportedWordLine(lineNumber, raw, entry, ImportedWordOutcome.Added);
    }

    private static bool TryFindExisting(
        string spoken,
        Dictionary<string, string> known,
        Dictionary<string, string> claimed,
        out string current) =>
        known.TryGetValue(spoken, out current!) || claimed.TryGetValue(spoken, out current!);
}
