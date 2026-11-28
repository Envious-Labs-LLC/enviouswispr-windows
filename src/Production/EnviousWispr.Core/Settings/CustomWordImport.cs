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

    private static readonly char[] LineBreaks = ['\r', '\n'];

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

        var known = Index(existing);

        // Spoken forms already claimed by an EARLIER line of this same import. Without this, a file
        // listing one word twice adds it twice, and the list gains a duplicate the user never typed.
        var claimed = new Dictionary<string, CustomWordEntry>(StringComparer.OrdinalIgnoreCase);

        var lines = new List<ImportedWordLine>();
        var raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headerMode = DetectHeaderMode(raw);
        for (var index = 0; index < raw.Length; index++)
        {
            lines.Add(IsHeader(raw[index])
                ? new ImportedWordLine(index + 1, raw[index], null, ImportedWordOutcome.Ignored)
                : ReadLine(raw[index], index + 1, headerMode, known, claimed));
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
        var includeCategory = entries.Any(entry => entry.Category != WordCategory.General);
        var lines = new List<string> { includeCategory ? HeaderWithCategory : Header };
        foreach (var entry in entries)
        {
            var categorySuffix = includeCategory
                ? $",{WordCategoryFormatting.Label(entry.Category).ToLowerInvariant()}"
                : string.Empty;
            if (entry.Aliases.Count > 0)
            {
                foreach (var alias in entry.Aliases)
                {
                    lines.Add($"{alias},{entry.Replacement},{Spell(entry.Strictness)}{categorySuffix}");
                }
            }
            else
            {
                lines.Add($"{entry.SpokenForm},{entry.Replacement},{Spell(entry.Strictness)}{categorySuffix}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The line that says this file has a third column, and names all three.</summary>
    /// <remarks>
    /// IT EARNS ITS PLACE TWICE. It tells the reader what the columns are, in the same words the
    /// app uses, in a file whose whole purpose is to be edited in a spreadsheet. And it is what
    /// makes a third field mean anything at all, so a file written before today cannot have one
    /// line reinterpreted.
    /// </remarks>
    public const string Header = "when I say,write,how closely";

    public const string HeaderWithCategory = "when I say,write,how closely,category";

    private enum HeaderMode
    {
        None,
        Strictness,
        StrictnessAndCategory,
    }

    private static HeaderMode ParseHeader(string line)
    {
        var trimmed = line.Trim();
        if (string.Equals(trimmed, HeaderWithCategory, StringComparison.OrdinalIgnoreCase))
        {
            return HeaderMode.StrictnessAndCategory;
        }

        if (string.Equals(trimmed, Header, StringComparison.OrdinalIgnoreCase))
        {
            return HeaderMode.Strictness;
        }

        return HeaderMode.None;
    }

    private static bool IsHeader(string line) => ParseHeader(line) != HeaderMode.None;

    /// <remarks>
    /// THE FIRST LINE THAT SAYS ANYTHING, not any line anywhere. A header found halfway down would
    /// let a file turn its own earlier lines into three-column lines retrospectively, which is the
    /// exact reinterpretation the header exists to prevent.
    /// </remarks>
    private static HeaderMode DetectHeaderMode(string[] lines) =>
        lines
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) is { } first
            ? ParseHeader(first)
            : HeaderMode.None;

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

        // THE FILE'S WHOLE ANSWER FOR THAT WORD, not only its replacement. Taking the replacement
        // and leaving the strictness behind would hand back a word corrected to the file's spelling
        // by the user's own rule, which is neither of the two things anybody chose.
        var incoming = new Dictionary<string, CustomWordEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in replacements)
        {
            foreach (var alias in entry.Aliases)
            {
                incoming[alias] = entry;
            }

            if (entry.Aliases.Count == 0)
            {
                incoming[entry.SpokenForm] = entry;
            }
        }

        var merged = new List<CustomWordEntry>(existing.Count);
        foreach (var entry in existing)
        {
            if (entry.Aliases.Count == 0)
            {
                merged.Add(incoming.TryGetValue(entry.SpokenForm, out var direct)
                    ? entry with { Replacement = direct.Replacement, Strictness = direct.Strictness, Category = direct.Category }
                    : entry);
                continue;
            }

            var remaining = new List<string>(entry.Aliases.Count);
            var matched = new List<(string Alias, CustomWordEntry Taken)>();
            foreach (var alias in entry.Aliases)
            {
                if (incoming.TryGetValue(alias, out var taken))
                {
                    matched.Add((alias, taken));
                }
                else
                {
                    remaining.Add(alias);
                }
            }

            if (matched.Count == 0)
            {
                merged.Add(entry);
                continue;
            }

            if (remaining.Count == 0 &&
                matched.All(m =>
                    string.Equals(m.Taken.Replacement, matched[0].Taken.Replacement, StringComparison.Ordinal) &&
                    m.Taken.Strictness == matched[0].Taken.Strictness &&
                    m.Taken.Category == matched[0].Taken.Category))
            {
                var first = matched[0].Taken;
                merged.Add(entry with { Replacement = first.Replacement, Strictness = first.Strictness, Category = first.Category });
                continue;
            }

            if (remaining.Count > 0)
            {
                var preservedLearned = CustomWordEntry.NormalizeLearnedAliases(
                    entry.EffectiveLearnedAliases(),
                    remaining);
                merged.Add(new CustomWordEntry(
                    remaining,
                    entry.Replacement,
                    entry.Strictness,
                    entry.Category,
                    learnedAliases: preservedLearned,
                    learnedAt: preservedLearned.Count > 0 ? entry.LearnedAt : null));
            }

            foreach (var group in matched.GroupBy(m => (m.Taken.Replacement, m.Taken.Strictness, m.Taken.Category)))
            {
                merged.Add(new CustomWordEntry(
                    group.Select(m => m.Alias).ToArray(),
                    group.Key.Replacement,
                    group.Key.Strictness,
                    group.Key.Category));
            }
        }

        return merged.ToArray();
    }

    /// <summary>The word a person reads in the third column, and types back into it.</summary>
    /// <remarks>
    /// SPELLED OUT RATHER THAN NUMBERED. This file opens in a spreadsheet and gets edited by hand,
    /// and a column reading 0, 1 and 2 asks the reader to look something up before they can change
    /// it. Written for every row including the ordinary one, so the column never appears and
    /// disappear depending on which words are in the list.
    /// </remarks>
    private static string Spell(MatchStrictness strictness) => strictness switch
    {
        MatchStrictness.Loose => "loose",
        MatchStrictness.Strict => "strict",
        _ => "default",
    };

    private static bool TryReadStrictness(string value, out MatchStrictness strictness)
    {
        // AN EMPTY FIELD IS NOT "DEFAULT", IT IS A FIELD SOMEBODY DELETED HALF OF. "word,written,"
        // reading as the ordinary rule would quietly replace a Loose or Strict word's choice with
        // the one it did not have, on a line whose third column is visibly blank in the spreadsheet
        // the person was editing.
        switch (value.Trim().ToLowerInvariant())
        {
            case "loose":
                strictness = MatchStrictness.Loose;
                return true;
            case "strict":
                strictness = MatchStrictness.Strict;
                return true;
            case "default":
                strictness = MatchStrictness.Default;
                return true;
            default:
                strictness = MatchStrictness.Default;
                return false;
        }
    }

    private static bool TryReadCategory(string value, out WordCategory category)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "general":
                category = WordCategory.General;
                return true;
            case "person":
                category = WordCategory.Person;
                return true;
            case "brand":
                category = WordCategory.Brand;
                return true;
            case "acronym":
                category = WordCategory.Acronym;
                return true;
            case "domain":
                category = WordCategory.Domain;
                return true;
            default:
                category = WordCategory.General;
                return false;
        }
    }

    private static ImportedWordLine ReadLine(
        string raw,
        int lineNumber,
        HeaderMode headerMode,
        Dictionary<string, CustomWordEntry> known,
        Dictionary<string, CustomWordEntry> claimed)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return new ImportedWordLine(lineNumber, raw, null, ImportedWordOutcome.Ignored);
        }

        var expectedColumns = headerMode switch
        {
            HeaderMode.StrictnessAndCategory => 4,
            HeaderMode.Strictness => 3,
            _ => 2,
        };
        var parts = trimmed.Split(Separators);
        if (parts.Length != expectedColumns)
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

        var strictness = MatchStrictness.Default;
        if (headerMode != HeaderMode.None && !TryReadStrictness(parts[2], out strictness))
        {
            return new ImportedWordLine(lineNumber, raw, null, ImportedWordOutcome.Unreadable);
        }

        var category = WordCategory.General;
        if (headerMode == HeaderMode.StrictnessAndCategory && !TryReadCategory(parts[3], out category))
        {
            return new ImportedWordLine(lineNumber, raw, null, ImportedWordOutcome.Unreadable);
        }

        return Classify(new CustomWordEntry([spoken], replacement, strictness, category), lineNumber, raw, known, claimed);
    }

    /// <summary>Plans words another app already holds as pairs, by exactly the rules a pasted line meets.</summary>
    /// <param name="incoming">The pairs, in the source's order. A pair that could not survive a round trip through
    /// <see cref="Write"/> is refused as unreadable rather than stored, as a pasted line would be.</param>
    /// <param name="existing">Words the user already has, so collisions can be reported.</param>
    /// <remarks>
    /// ONE CLASSIFICATION, TWO WAYS IN. A word read out of another app's store is not text, so it does not go through
    /// the line parser - a comma inside it would split it - but WHAT HAPPENS to it (added, already had, corrected
    /// differently, listed twice) is decided by the same code a pasted line reaches, so the two cannot drift. A field
    /// holding a separator or a line break is refused here for the reason <see cref="Write"/> gives: the list must
    /// export and import back unchanged, and refusing on the way in is the half a person can see.
    /// </remarks>
    public static CustomWordImportPlan Plan(
        IReadOnlyList<CustomWordEntry> incoming,
        IReadOnlyList<CustomWordEntry> existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existing);
        var known = Index(existing);
        var claimed = new Dictionary<string, CustomWordEntry>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<ImportedWordLine>(incoming.Count);
        for (var index = 0; index < incoming.Count; index++)
        {
            var entry = incoming[index];
            var spoken = entry.SpokenForm.Trim();
            var replacement = entry.Replacement.Trim();
            lines.Add(IsStorableField(spoken) && IsStorableField(replacement) && Enum.IsDefined(entry.Strictness) && Enum.IsDefined(entry.Category)
                ? Classify(new CustomWordEntry([spoken], replacement, entry.Strictness, entry.Category), index + 1, spoken, known, claimed)
                : new ImportedWordLine(index + 1, spoken, null, ImportedWordOutcome.Unreadable));
        }

        return new CustomWordImportPlan(lines);
    }

    /// <summary>The words already there, by spoken form; the LAST row claiming a spoken form is the one that counts.</summary>
    /// <remarks>
    /// A LIST CAN HOLD TWO ROWS FOR ONE SPOKEN FORM - a profile or settings file written elsewhere is not deduplicated on
    /// load - and building this with a dictionary constructor threw on exactly that list, from inside the settings gate.
    /// Last wins because that is the row the corrector uses (<c>CustomWordCorrector.Correct</c>, ordinary rows).
    /// </remarks>
    private static Dictionary<string, CustomWordEntry> Index(IReadOnlyList<CustomWordEntry> existing)
    {
        var known = new Dictionary<string, CustomWordEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existing)
        {
            if (entry.Aliases.Count > 0)
            {
                foreach (var alias in entry.Aliases)
                {
                    known[alias] = entry;
                }
            }
            else
            {
                known[entry.SpokenForm] = entry;
            }
        }

        return known;
    }

    private static bool IsStorableField(string value) =>
        value.Length > 0 &&
        value.Length <= MaximumFieldLength &&
        value.IndexOfAny(Separators) < 0 &&
        value.IndexOfAny(LineBreaks) < 0;

    private static ImportedWordLine Classify(
        CustomWordEntry entry,
        int lineNumber,
        string raw,
        Dictionary<string, CustomWordEntry> known,
        Dictionary<string, CustomWordEntry> claimed)
    {
        if (TryFindExisting(entry.SpokenForm, known, claimed, out var current))
        {
            return new ImportedWordLine(
                lineNumber,
                raw,
                entry,
                string.Equals(current.Replacement, entry.Replacement, StringComparison.Ordinal)
                    && current.Strictness == entry.Strictness
                    && current.Category == entry.Category
                    ? ImportedWordOutcome.AlreadyPresent
                    : ImportedWordOutcome.Conflict);
        }

        claimed[entry.SpokenForm] = entry;
        return new ImportedWordLine(lineNumber, raw, entry, ImportedWordOutcome.Added);
    }

    private static bool TryFindExisting(
        string spoken,
        Dictionary<string, CustomWordEntry> known,
        Dictionary<string, CustomWordEntry> claimed,
        out CustomWordEntry current) =>
        known.TryGetValue(spoken, out current!) || claimed.TryGetValue(spoken, out current!);
}
