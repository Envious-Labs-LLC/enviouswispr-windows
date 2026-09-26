using System.Globalization;
using System.Text;

namespace EnviousWispr.Core.Settings;

// Shared contract for snippet import, ported from macOS SnippetImportContract.swift (#2997).
//
// Every source - a paste, a chosen file, another app - produces a SnippetImportBatch, and nothing downstream
// (the review, the commit) knows which source produced a candidate. Sentences are macOS's, word for word,
// with "Mac" read as "PC" where it appears.

/// <summary>One snippet a source found: the words to say and the text to paste. Never the keyword.</summary>
/// <remarks>The trigger is trimmed when the batch is validated; the text is kept exactly as the source held it.</remarks>
public sealed record SnippetImportCandidate(string Trigger, string Expansion);

/// <summary>Counts a source hands back beside its candidates. COUNTS ONLY, never content.</summary>
public abstract record SnippetImportNotice(int Count)
{
    /// <summary>Source rows deliberately refused: disabled, deleted, placeholder, or empty text.</summary>
    public sealed record IncompatibleSourceEntriesExcluded(int Count) : SnippetImportNotice(Count);

    /// <summary>Pasted or file lines that did not parse into a trigger and some text.</summary>
    public sealed record LinesSkipped(int Count) : SnippetImportNotice(Count);
}

/// <summary>The ceilings every snippet source honours, in one place so the sources cannot drift apart.</summary>
/// <remarks>
/// DELIBERATELY TIGHTER THAN THE MAC WHERE THE WINDOWS STORE IS TIGHTER. macOS reviews up to 5,000 candidates,
/// with triggers to 512 characters and text to 20,000. The Windows settings store keeps at most
/// <see cref="AppSettingsValidator.MaximumSnippets"/> snippets, triggers to
/// <see cref="AppSettingsValidator.MaximumSnippetTriggerLength"/> and text to
/// <see cref="AppSettingsValidator.MaximumSnippetBodyLength"/>, so a review offering more than that would end in a
/// save the store refuses. The limit is stated where it can still be read, before review.
/// </remarks>
public static class SnippetImportLimits
{
    /// <summary>More candidates than the store could ever hold is refused before review.</summary>
    public const int MaximumCandidates = AppSettingsValidator.MaximumSnippets;

    /// <summary>A pasted list, a CSV or a plain list: a snippet list is small, so anything larger is a mistaken selection.</summary>
    public const int MaximumImportFileBytes = 16 * 1024 * 1024;

    /// <summary>Our own export gets a higher, still finite ceiling (macOS <c>maximumExportedFileBytes</c>).</summary>
    public const int MaximumExportedFileBytes = 64 * 1024 * 1024;

    /// <summary>Rows another app may hold where it keeps snippets, counting the ones that cannot be snippets here (macOS value).</summary>
    public const int MaximumSourceEntries = 5_000;

    public const int MaximumTriggerLength = AppSettingsValidator.MaximumSnippetTriggerLength;

    public const int MaximumExpansionLength = AppSettingsValidator.MaximumSnippetBodyLength;

    /// <summary>Total text (every trigger plus every expansion) one batch may carry (macOS value).</summary>
    public const int MaximumStoredCharacters = 4_000_000;
}

/// <summary>Why an import failed, as a closed category the diagnostic log can carry (macOS <c>SnippetImportTelemetryFailure</c>).</summary>
public enum SnippetImportFailure
{
    UnsupportedType,
    TooLarge,
    Unreadable,
    NotOurs,
    NewerVersion,
    Malformed,
    TooMany,
    UnusableEntry,
    AppNotFound,
    AppStoreUnreadable,
    WriteFailed,
    InvariantViolation,
}

/// <summary>Why a snippet import could not go ahead. The message is the sentence the person reads; the failure is what the log keeps.</summary>
public sealed class SnippetImportException : Exception
{
    public SnippetImportException(SnippetImportFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public SnippetImportFailure Failure { get; }
}

/// <summary>What one source produced: its candidates, which source it was, and what it left out.</summary>
/// <param name="SourceId">A closed vocabulary ("paste", "file_json", "wispr_flow", ...), never a file name.</param>
/// <param name="SourceDisplayName">What the person sees, such as "CSV file" or "Wispr Flow".</param>
public sealed record SnippetImportBatch(
    string SourceId,
    string SourceDisplayName,
    IReadOnlyList<SnippetImportCandidate> Candidates,
    IReadOnlyList<SnippetImportNotice> Notices)
{
    /// <summary>How many lines or rows the source left out, summed over its notices.</summary>
    public int ExcludedCount => Notices.Sum(notice => notice.Count);

    /// <summary>The whole batch in stored form (triggers trimmed), or a refusal naming the entry that cannot be stored.</summary>
    /// <remarks>
    /// ALL OR NOTHING, as on macOS: silently dropping bad rows would show a review that quietly disagrees with the
    /// file, and importing them would put invisible characters inside a trigger nobody can then say. Every source
    /// goes through this, so a new source gets the rules by existing.
    /// </remarks>
    public SnippetImportBatch Validated()
    {
        if (Candidates.Count > SnippetImportLimits.MaximumCandidates)
        {
            throw new SnippetImportException(SnippetImportFailure.TooMany, SnippetImportMessages.TooManySnippets(SnippetImportLimits.MaximumCandidates));
        }

        var surface = 0L;
        var stored = new List<SnippetImportCandidate>(Candidates.Count);
        foreach (var raw in Candidates)
        {
            var candidate = raw with { Trigger = raw.Trigger.Trim() };
            if (candidate.Trigger.Length > SnippetImportLimits.MaximumTriggerLength)
            {
                throw new SnippetImportException(SnippetImportFailure.UnusableEntry, SnippetImportMessages.TriggerTooLong(SnippetImportLimits.MaximumTriggerLength));
            }

            // A trigger must have spoken words (the rule the page applies), and it must be storable text.
            if (SnippetText.CollisionKey(candidate.Trigger) is null ||
                !SnippetImportTextPolicy.IsAcceptableStoredValue(candidate.Trigger))
            {
                throw new SnippetImportException(SnippetImportFailure.UnusableEntry, SnippetImportMessages.UnusableTrigger(candidate.Trigger));
            }

            if (candidate.Expansion.Length > SnippetImportLimits.MaximumExpansionLength)
            {
                throw new SnippetImportException(
                    SnippetImportFailure.UnusableEntry,
                    SnippetImportMessages.ExpansionTooLong(candidate.Trigger, SnippetImportLimits.MaximumExpansionLength));
            }

            if (!SnippetImportTextPolicy.IsAcceptableMultilineStoredValue(candidate.Expansion))
            {
                throw new SnippetImportException(SnippetImportFailure.UnusableEntry, SnippetImportMessages.UnusableExpansion(candidate.Trigger));
            }

            surface += candidate.Trigger.Length + candidate.Expansion.Length;
            if (surface > SnippetImportLimits.MaximumStoredCharacters)
            {
                throw new SnippetImportException(SnippetImportFailure.TooLarge, SnippetImportMessages.TooMuchText(SnippetImportLimits.MaximumStoredCharacters));
            }

            stored.Add(candidate);
        }

        return this with { Candidates = stored };
    }
}

/// <summary>Whether imported text is text a person could see and the app can store (macOS <c>CustomWordsImportTextPolicy</c>).</summary>
/// <remarks>
/// ASKS UNICODE RATHER THAN HAND-ROLLING RANGES. Refused: controls (C0 and C1), surrogates, private use, unassigned
/// code points, format characters other than the two word-forming joiners (a bidi override makes a word render as
/// something it is not; a stray byte-order mark is invisible), and the line and paragraph separators U+2028 and
/// U+2029. Line breaks and tabs are content in snippet TEXT and separators in a TRIGGER.
/// </remarks>
public static class SnippetImportTextPolicy
{
    private const int ZeroWidthNonJoiner = 0x200C;
    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>A trigger: acceptable characters only, no line breaks or tabs, and something visible.</summary>
    public static bool IsAcceptableStoredValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().Length > 0 &&
            HasVisibleContent(value) &&
            value.EnumerateRunes().All(rune => rune.Value is not ('\n' or '\r' or '\t') && IsAcceptable(rune));
    }

    /// <summary>Snippet text: acceptable characters, line breaks and tabs allowed, and something visible.</summary>
    public static bool IsAcceptableMultilineStoredValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return HasVisibleContent(value) && value.EnumerateRunes().All(IsAcceptable);
    }

    /// <summary>Whether a value contains anything a person could see: not only spaces, joiners and variation selectors.</summary>
    public static bool HasVisibleContent(string value) =>
        value.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune) && !IsDefaultIgnorable(rune));

    /// <summary>Whether text read from a file looks like text at all rather than bytes read in the wrong encoding.</summary>
    public static bool IsPlausiblyText(string text) =>
        text.EnumerateRunes().All(rune =>
            rune.Value is '\n' or '\r' or '\t' ||
            Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned));

    /// <summary>A value as it can be shown inside an error message: anything invisible or refused is spelled as its code.</summary>
    public static string Displayable(string value)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var mustEscape = rune.Value is '\n' or '\r' or '\t' || !IsAcceptable(rune) || IsDefaultIgnorable(rune);
            builder.Append(mustEscape ? $"<U+{rune.Value:X4}>" : rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>How to name a rejected value in a sentence: quoted, or "a blank entry".</summary>
    public static string Describe(string value) =>
        value.Trim().Length > 0 ? $"\"{Displayable(value)}\"" : "a blank entry";

    private static bool IsAcceptable(Rune rune)
    {
        if (rune.Value is '\n' or '\r' or '\t' or ZeroWidthNonJoiner or ZeroWidthJoiner)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
    }

    /// <summary>The default-ignorable code points that matter here: format characters and variation selectors.</summary>
    private static bool IsDefaultIgnorable(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ||
        rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or 0x034F or 0x115F or 0x1160 or 0x3164;
}

/// <summary>Every sentence snippet import shows, in one place so a test can read them (macOS strings).</summary>
public static class SnippetImportMessages
{
    public static string TooManySnippets(int limit) =>
        $"That has more than {limit} snippets, which is more than EnviousWispr can import at once. Nothing was imported.";

    public static string TooMuchText(int limit) =>
        $"That has more than {limit} characters of snippet text in total, which is more than EnviousWispr can import at once. Nothing was imported.";

    public static string TriggerTooLong(int limit) =>
        $"That contains a trigger longer than {limit} characters, which is too long to say. Nothing was imported.";

    public static string ExpansionTooLong(string trigger, int limit) =>
        $"The text for {SnippetImportTextPolicy.Describe(trigger)} is longer than {limit} characters. Nothing was imported.";

    public static string UnusableTrigger(string trigger) =>
        $"That contains a trigger EnviousWispr can't use ({SnippetImportTextPolicy.Describe(trigger)}). Nothing was imported.";

    public static string UnusableExpansion(string trigger) =>
        $"The text for {SnippetImportTextPolicy.Describe(trigger)} contains characters EnviousWispr can't store. Nothing was imported.";

    public const string Unreadable = "That couldn't be read.";

    public const string TooLarge = "That is too big to be a snippet list. Check you picked the right one.";

    public static string UnsupportedType(string extension) =>
        $"EnviousWispr can't read {SnippetImportTextPolicy.Describe(extension)} files yet. Try the {SnippetsTransferDocument.DefaultFileName} you exported, a CSV, or a plain list.";

    public static string MalformedCsv(int line) =>
        $"That CSV has a quoting problem on line {line}. Nothing was imported.";

    public const string NotOurFile = "That file isn't an EnviousWispr snippets file.";

    public static string UnsupportedVersion(int version) =>
        $"That file was exported by a newer version of EnviousWispr (format {version}). Update the app, then try again.";

    public const string Damaged = "That file is damaged and can't be read.";

    public static string AppNotFound(string app) => $"Couldn't find any {app} snippets on this PC.";

    public static string AppUnreadable(string app) =>
        $"Couldn't read your {app} snippets, so nothing was imported. If {app} is running, quitting it and trying again can help.";

    public static string TooManySourceEntries(string app, int limit) =>
        $"{app} has more than {limit} entries where it keeps snippets, counting ones that can't be snippets here. EnviousWispr stopped without importing anything.";

    /// <summary>Windows only: the store's own ceiling, reached by adding the approved rows to the list as it stands.</summary>
    public static string WouldExceedStore(int limit) =>
        $"That would make more than {limit} snippets, which is more than EnviousWispr can keep. Nothing was imported.";

    public const string StaleNotice =
        "Your snippets changed while you were reviewing. Nothing was imported. Here is the updated list.";
}
