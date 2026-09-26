namespace EnviousWispr.Core.Settings;

// Words brought over from another dictation app, ported from macOS SmartImportSource.swift (#1686, #1773).
//
// Every adapter reads its own store into AppWords and a count of rows it refused on purpose; everything after that
// - the ceilings, the trimming, the mapping onto this app's pairs, the review and the commit - is the same for every
// app and lives here. Sentences are macOS's, word for word, with "Mac" read as "PC".

/// <summary>One word another app holds: the spelling it writes, and the misspellings it corrects from, if it records any.</summary>
/// <remarks>
/// macOS <c>SmartImportWord</c> without its case-sensitivity flag: only TypeWhisper supplies one, TypeWhisper is not
/// read on Windows, and this app's words have no case-sensitive setting to carry it into.
/// </remarks>
public sealed record ImportedAppWord(string Canonical, IReadOnlyList<string> Aliases)
{
    public ImportedAppWord(string canonical)
        : this(canonical, [])
    {
    }
}

/// <summary>What an adapter read, and how many source rows it refused on purpose (a COUNT, never their content).</summary>
public sealed record ImportedAppWords(IReadOnlyList<ImportedAppWord> Words, int Excluded);

/// <summary>An app's words as this app's pairs, ready to review.</summary>
/// <param name="SourceId">The closed identifier of the app (<c>wispr_flow</c>, <c>handy</c>), never a display name.</param>
/// <param name="SourceDisplayName">What the person sees.</param>
/// <param name="Entries">The pairs, one spoken form each, first in the source's order.</param>
/// <param name="Excluded">Source rows left out: refused by the adapter, a blank word, or a spoken form listed twice.</param>
public sealed record CustomWordAppBatch(
    string SourceId,
    string SourceDisplayName,
    IReadOnlyList<CustomWordEntry> Entries,
    int Excluded);

/// <summary>Why a word import from another app could not go ahead, as a closed category the diagnostic log can carry.</summary>
public enum WordImportFailure
{
    AppNotFound,
    AppStoreUnreadable,
    TooMany,
    WouldExceedStore,
    WriteFailed,
}

/// <summary>Why a word import could not go ahead. The message is the sentence the person reads; the failure is what the log keeps.</summary>
public sealed class WordImportException : Exception
{
    public WordImportException(WordImportFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public WordImportFailure Failure { get; }
}

/// <summary>The ceilings an app import honours.</summary>
public static class WordImportLimits
{
    /// <summary>Rows another app may hold where it keeps words, counting the ones it refuses (macOS <c>maximumCandidates</c>).</summary>
    public const int MaximumSourceEntries = 25_000;

    /// <summary>Pairs one import may offer: never more than the list itself may hold.</summary>
    public const int MaximumEntries = AppSettingsValidator.MaximumCustomWords;
}

/// <summary>Every sentence a word import from another app shows (macOS <c>SmartImportError</c> and the import sheet).</summary>
public static class WordImportMessages
{
    public static string AppNotFound(string app) => $"Couldn't find any {app} words on this PC.";

    /// <summary>"Can help", not "the cause": every reader failure lands here (founder decision 2026-09-18).</summary>
    public static string AppUnreadable(string app) =>
        $"Couldn't read your {app} words, so nothing was imported. If {app} is running, quitting it and trying again can help.";

    public static string TooManySourceEntries(string app, int limit) =>
        $"{app} has more than {limit} dictionary entries, including entries it may hide or disable. EnviousWispr stopped without importing anything.";

    /// <summary>Windows only: the list's own ceiling, reached by what the import would add to the list as it stands.</summary>
    public static string WouldExceedStore(int limit) =>
        $"That would make more than {limit} words, which is more than EnviousWispr can keep. Nothing was imported.";

    public const string StaleNotice =
        "Your word list changed while you were reviewing. Nothing was imported. Here is the updated list.";

    public const string NothingFound = "No words were found, and nothing was changed.";

    public static string NothingCompatible(int found) => found == 1
        ? "Found 1 entry, but none were compatible. Nothing was changed."
        : $"Found {found} entries, but none were compatible. Nothing was changed.";

    public static string LeftOut(int count) => count == 1
        ? "1 entry was left out because EnviousWispr can't use it."
        : $"{count} entries were left out because EnviousWispr can't use them.";

    /// <summary>The line under the app list, before anything is read (macOS import sheet).</summary>
    public const string CarriesSpellings = "New words come across with the alternate spellings you set up in the other app.";

    public static string NoAppsFound(string supportedNames) =>
        $"No supported dictation apps found on this PC. EnviousWispr can read words from {supportedNames}.";

    public static string ConfirmTitle(int additions) => additions switch
    {
        0 => "Add nothing",
        1 => "Add 1 word",
        _ => $"Add {additions} words",
    };

    /// <summary>What the commit did, naming every outcome that occurred (the pasted list's message, in an app's terms).</summary>
    public static string Result(CustomWordImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>();
        var already = plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.AlreadyPresent);
        if (plan.Additions.Count > 0)
        {
            parts.Add($"{plan.Additions.Count} added");
        }

        if (already > 0)
        {
            parts.Add($"{already} you already had");
        }

        if (plan.ConflictCount > 0)
        {
            parts.Add($"{plan.ConflictCount} left alone because you already correct them differently");
        }

        if (plan.UnreadableCount > 0)
        {
            parts.Add($"{plan.UnreadableCount} could not be stored");
        }

        return parts.Count == 0 ? NothingFound : string.Join(". ", parts) + ".";
    }

    /// <summary>One line naming every outcome the review found, never only the good ones.</summary>
    public static string ReviewSummary(CustomWordImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new List<string>();
        var added = plan.Additions.Count;
        var already = plan.Lines.Count(line => line.Outcome == ImportedWordOutcome.AlreadyPresent);
        if (added > 0)
        {
            parts.Add(added == 1 ? "1 new word" : $"{added} new words");
        }

        if (already > 0)
        {
            parts.Add($"{already} you already have");
        }

        if (plan.ConflictCount > 0)
        {
            parts.Add($"{plan.ConflictCount} you already correct differently");
        }

        if (plan.UnreadableCount > 0)
        {
            parts.Add($"{plan.UnreadableCount} EnviousWispr can't store");
        }

        return parts.Count == 0 ? "Nothing to review." : string.Join(", ", parts) + ".";
    }
}

/// <summary>Turns another app's words into this app's pairs, and commits a reviewed import in one write.</summary>
/// <remarks>
/// THE MAPPING IS THE PORT'S ONE REAL DECISION. macOS keeps a word as a spelling with alternate spellings that correct
/// to it; this app keeps pairs, "when I say" and "write". A word with alternate spellings becomes one pair per
/// spelling, each writing the word (Wispr Flow's <c>phrase</c> "anthropic" with <c>replacement</c> "Anthropic" is
/// "anthropic" writes "Anthropic"). A word with none becomes the pair that writes itself, which is how this app's own
/// word lists already carry a plain term ("kubernetes,Kubernetes"): the corrector matches a one-word written form as
/// well as its spoken one, so the word is recognised and spelled as the other app had it.
///
/// A SPOKEN FORM IS OWNED ONCE. Two source rows claiming one spoken form for different words is the same collision
/// macOS resolves "earlier wins"; here the first keeps it and the later ones are counted as left out, so the review
/// never offers to replace a word the import itself has just added.
///
/// A CONFLICT WITH THE PERSON'S OWN LIST IS OFFERED, NOT DECIDED - the Windows word import's rule
/// (<see cref="CustomWordImport.Plan"/>), not the Mac's skip-only one. A pair that says something different from a
/// correction they already have is left alone at commit and offered afterwards as "Replace my N corrections".
/// </remarks>
public static class CustomWordAppImport
{
    /// <summary>Applies the ceilings, drops blanks, and maps words onto pairs (macOS <c>SmartImportSource.loadRawCandidates</c>).</summary>
    /// <exception cref="WordImportException">The source is bigger than an import may be.</exception>
    public static CustomWordAppBatch Build(string sourceId, string sourceDisplayName, ImportedAppWords read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDisplayName);
        ArgumentNullException.ThrowIfNull(read);

        // THE SCANNED COUNT, survivors plus the adapter's own exclusions: a source of 25,001 rows filtered down to one
        // must not look like a one-row source, and a ceiling checked after shrinking loses its signal.
        if (read.Words.Count + read.Excluded > WordImportLimits.MaximumSourceEntries)
        {
            throw new WordImportException(
                WordImportFailure.TooMany,
                WordImportMessages.TooManySourceEntries(sourceDisplayName, WordImportLimits.MaximumSourceEntries));
        }

        var entries = new List<CustomWordEntry>();
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = read.Excluded;
        foreach (var word in read.Words)
        {
            // A BLANK WORD IS AN EXCLUSION TOO. Leaving it uncounted would let a source of nothing but blanks report
            // "no words were found", which is the untruth the count exists to stop.
            var canonical = word.Canonical.Trim();
            if (canonical.Length == 0)
            {
                excluded++;
                continue;
            }

            var spokenForms = word.Aliases
                .Select(alias => alias.Trim())
                .Where(alias => alias.Length > 0)
                .ToList();
            if (spokenForms.Count == 0)
            {
                spokenForms.Add(canonical);
            }

            foreach (var spoken in spokenForms)
            {
                if (!owned.Add(spoken))
                {
                    excluded++;
                    continue;
                }

                entries.Add(new CustomWordEntry(spoken, canonical));
            }
        }

        // A SECOND CEILING, ON WHAT WOULD BE STORED: one word with a long list of spellings is many pairs, which the
        // row count above cannot see. Refused before review, because a review offering more than the list can hold
        // ends in a save the store refuses.
        if (entries.Count > WordImportLimits.MaximumEntries)
        {
            throw new WordImportException(
                WordImportFailure.TooMany,
                WordImportMessages.WouldExceedStore(WordImportLimits.MaximumEntries));
        }

        return new CustomWordAppBatch(sourceId, sourceDisplayName, entries, excluded);
    }

    /// <summary>Adds what is new if the list is still <paramref name="baseline"/>; otherwise writes nothing and says so.</summary>
    /// <remarks>
    /// REFUSES RATHER THAN MERGES WHEN THE LIST MOVED (macOS "Your word list changed while you were reviewing"). The
    /// person approved a review drawn against one list; adding to a different one would add words they never saw
    /// classified against it. Compared as a sequence, so a duplicate or a changed strictness is a change.
    /// </remarks>
    public static (IReadOnlyList<CustomWordEntry>? Next, WordImportCommitOutcome Outcome) Commit(
        IReadOnlyList<CustomWordEntry> current,
        IReadOnlyList<CustomWordEntry> baseline,
        IReadOnlyList<CustomWordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(entries);
        if (!current.SequenceEqual(baseline))
        {
            return (null, new WordImportCommitOutcome(WordImportCommitKind.Stale, CustomWordImport.Plan(entries, current), current));
        }

        var plan = CustomWordImport.Plan(entries, current);
        if (plan.Additions.Count == 0)
        {
            return (null, new WordImportCommitOutcome(WordImportCommitKind.NothingNew, plan, current));
        }

        if (current.Count + plan.Additions.Count > AppSettingsValidator.MaximumCustomWords)
        {
            return (null, new WordImportCommitOutcome(WordImportCommitKind.WouldExceedStore, plan, current));
        }

        var next = current.Concat(plan.Additions).ToArray();
        return (next, new WordImportCommitOutcome(WordImportCommitKind.Committed, plan, next));
    }
}

/// <summary>How a reviewed word import ended.</summary>
public enum WordImportCommitKind
{
    Committed,
    NothingNew,
    Stale,
    WouldExceedStore,
}

/// <summary>What the commit did, the plan it acted on, and the list as it now stands.</summary>
/// <param name="Plan">For <see cref="WordImportCommitKind.Stale"/>, the plan against the list as it now stands, ready for the next review.</param>
public sealed record WordImportCommitOutcome(
    WordImportCommitKind Kind,
    CustomWordImportPlan Plan,
    IReadOnlyList<CustomWordEntry> Current);
