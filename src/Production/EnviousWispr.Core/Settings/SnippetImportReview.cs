namespace EnviousWispr.Core.Settings;

/// <summary>How one imported snippet relates to the list a person already has.</summary>
public enum SnippetImportRowStatus
{
    /// <summary>Nobody's words yet: offered with a tick, ticked by default.</summary>
    New,

    /// <summary>The person already has a snippet on these spoken words. Skip only: an import never edits a snippet.</summary>
    Existing,

    /// <summary>An earlier row in this same batch already claims these spoken words. Skip only.</summary>
    DuplicateInBatch,
}

/// <summary>One row of the review screen: the candidate, its status, and whether it will be added.</summary>
/// <param name="ExistingTrigger">For <see cref="SnippetImportRowStatus.Existing"/>, the person's own trigger as they typed it.</param>
public sealed record SnippetImportReviewRow(
    SnippetImportCandidate Candidate,
    SnippetImportRowStatus Status,
    string? ExistingTrigger,
    bool Add)
{
    /// <summary>Only a genuinely new snippet can be ticked; every other status is skip only, so the screen never offers what the store would refuse.</summary>
    public bool IsAddable => Status == SnippetImportRowStatus.New;

    /// <summary>The "you already have this" line under a skip-only row (macOS strings).</summary>
    public string? StatusNote => Status switch
    {
        SnippetImportRowStatus.Existing => $"You have this, as \u201C{ExistingTrigger}\u201D.",
        SnippetImportRowStatus.DuplicateInBatch => "Already listed above.",
        _ => null,
    };

    /// <summary>The label in place of a tick on a skip-only row (macOS strings).</summary>
    public string? SkipLabel => Status switch
    {
        SnippetImportRowStatus.Existing => "You have this",
        SnippetImportRowStatus.DuplicateInBatch => "Skipped",
        _ => null,
    };

    /// <summary>The same row with the person's choice, refused for a skip-only row: the model, not the screen, is the authority.</summary>
    public SnippetImportReviewRow WithAdd(bool add) => IsAddable ? this with { Add = add } : this;
}

/// <summary>What confirming a review did.</summary>
public enum SnippetImportCommitKind
{
    /// <summary>The approved snippets were written, in one save.</summary>
    Committed,

    /// <summary>Nothing was ticked. Nothing was written.</summary>
    NothingApproved,

    /// <summary>The list changed while the review was open. Nothing was written; the review is rebuilt against <see cref="SnippetImportCommitOutcome.Current"/>.</summary>
    Stale,

    /// <summary>An approved row could not be stored. Nothing was written; <see cref="SnippetImportCommitOutcome.Message"/> says why.</summary>
    Refused,
}

/// <summary>The answer to one commit, worked out inside the settings gate.</summary>
/// <param name="Current">The snippets as they stood inside the gate, so a stale review is rebuilt from the truth.</param>
public sealed record SnippetImportCommitOutcome(
    SnippetImportCommitKind Kind,
    int Added,
    IReadOnlyList<SnippetEntry> Current,
    string? Message = null);

/// <summary>The review and the one atomic write, ported from macOS <c>SnippetImportRowBuilder</c> and <c>SnippetsManager.importSnippets</c>.</summary>
public static class SnippetImportReview
{
    /// <summary>Review rows for a validated batch against the list as it is now. One key per snippet, one lookup per candidate.</summary>
    public static IReadOnlyList<SnippetImportReviewRow> BuildRows(
        IReadOnlyList<SnippetImportCandidate> candidates,
        IReadOnlyList<SnippetEntry> existing)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(existing);
        var owners = OwnersByKey(existing);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<SnippetImportReviewRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // Validated() refused every candidate with no key, so the fallback is never taken; an empty key
            // would still land as a duplicate rather than as new.
            var key = SnippetText.CollisionKey(candidate.Trigger) ?? string.Empty;
            if (owners.TryGetValue(key, out var owner))
            {
                rows.Add(new SnippetImportReviewRow(candidate, SnippetImportRowStatus.Existing, owner, Add: false));
            }
            else if (!seen.Add(key))
            {
                rows.Add(new SnippetImportReviewRow(candidate, SnippetImportRowStatus.DuplicateInBatch, null, Add: false));
            }
            else
            {
                rows.Add(new SnippetImportReviewRow(candidate, SnippetImportRowStatus.New, null, Add: true));
            }
        }

        return rows;
    }

    /// <summary>Applies the approved rows to <paramref name="current"/>, or refuses without changing anything.</summary>
    /// <param name="current">The snippets as they are inside the gate, at the moment of writing.</param>
    /// <param name="baseline">The snippets the review was built against.</param>
    /// <param name="approved">The ticked rows' candidates, in review order.</param>
    /// <returns>The new list (null when nothing is to be written) and the outcome.</returns>
    /// <remarks>
    /// THE LIST MUST STILL BE THE ONE THE PERSON REVIEWED, compared as a COUNTED multiset of trigger and text, so a
    /// hand-edited file holding one entry twice does not read as equal to the list with it once. A change in
    /// between - by any route - refuses the whole write, so a snippet that arrived during the review can be
    /// neither duplicated nor silently overwritten.
    ///
    /// THEN EVERY ADDITION IS JUDGED against the current list AND the additions before it, by the same three rules
    /// as the page (a trigger nobody can say, an empty text, a trigger already answered to). The first failure
    /// refuses the whole batch. Then the store's own ceiling. Only then is anything written, once.
    ///
    /// THE KEYWORD IS NOT TOUCHED: this returns snippets, and the caller keeps the keyword it has.
    ///
    /// SORTED BY TRIGGER, NOT PUT AT THE FRONT AS ON THE MAC, because the Windows list is kept in trigger order on
    /// every save and an import that broke the order would reshuffle on the next add.
    /// </remarks>
    public static (IReadOnlyList<SnippetEntry>? Next, SnippetImportCommitOutcome Outcome) Commit(
        IReadOnlyList<SnippetEntry> current,
        IReadOnlyList<SnippetEntry> baseline,
        IReadOnlyList<SnippetImportCandidate> approved)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(approved);
        if (!SameList(current, baseline))
        {
            return (null, new SnippetImportCommitOutcome(SnippetImportCommitKind.Stale, 0, current, SnippetImportMessages.StaleNotice));
        }

        if (approved.Count == 0)
        {
            return (null, new SnippetImportCommitOutcome(SnippetImportCommitKind.NothingApproved, 0, current));
        }

        var owners = OwnersByKey(current);
        var accepted = new List<SnippetEntry>(approved.Count);
        foreach (var candidate in approved)
        {
            var entry = new SnippetEntry(candidate.Trigger.Trim(), candidate.Expansion);
            var key = SnippetText.CollisionKey(entry.Name);
            string? refusal = null;
            if (key is null || string.IsNullOrWhiteSpace(entry.Body))
            {
                refusal = SnippetImportMessages.UnusableTrigger(entry.Name);
            }
            else if (owners.TryGetValue(key, out var owner))
            {
                refusal = $"You already have a snippet for those words: \"{owner}\". Nothing was imported.";
            }
            else if (entry.Name.Length > AppSettingsValidator.MaximumSnippetTriggerLength ||
                entry.Body.Length > AppSettingsValidator.MaximumSnippetBodyLength)
            {
                refusal = SnippetImportMessages.ExpansionTooLong(entry.Name, AppSettingsValidator.MaximumSnippetBodyLength);
            }

            if (refusal is not null)
            {
                return (null, new SnippetImportCommitOutcome(SnippetImportCommitKind.Refused, 0, current, refusal));
            }

            owners[key!] = entry.Name;
            accepted.Add(entry);
        }

        if (current.Count + accepted.Count > AppSettingsValidator.MaximumSnippets)
        {
            return (null, new SnippetImportCommitOutcome(
                SnippetImportCommitKind.Refused, 0, current, SnippetImportMessages.WouldExceedStore(AppSettingsValidator.MaximumSnippets)));
        }

        var next = current
            .Concat(accepted)
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return (next, new SnippetImportCommitOutcome(SnippetImportCommitKind.Committed, accepted.Count, next));
    }

    /// <summary>Whether two lists hold the same snippets the same number of times, in any order.</summary>
    public static bool SameList(IReadOnlyList<SnippetEntry> left, IReadOnlyList<SnippetEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var counts = new Dictionary<SnippetEntry, int>();
        foreach (var entry in left)
        {
            counts[entry] = counts.GetValueOrDefault(entry) + 1;
        }

        foreach (var entry in right)
        {
            if (!counts.TryGetValue(entry, out var count) || count == 0)
            {
                return false;
            }

            counts[entry] = count - 1;
        }

        return true;
    }

    /// <summary>Collision key to the trigger that owns it; the FIRST owner is kept, as the page's duplicate check names it.</summary>
    private static Dictionary<string, string> OwnersByKey(IReadOnlyList<SnippetEntry> snippets)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snippet in snippets)
        {
            if (SnippetText.CollisionKey(snippet.Name) is { } key)
            {
                owners.TryAdd(key, snippet.Name);
            }
        }

        return owners;
    }
}

/// <summary>The sentences around an import that depend on counts, whole and by count (macOS <c>SnippetImportResultCopy</c>).</summary>
public static class SnippetImportCopy
{
    public static string Completed(int added) => added == 1
        ? "Added 1 snippet. Say your keyword, then the trigger, and it's pasted."
        : $"Added {added} snippets. Say your keyword, then the trigger, and it's pasted.";

    public const string NothingFound = "No snippets were found, and nothing was changed.";

    public static string NothingCompatible(int found) => found == 1
        ? "Found 1 entry, but none could be imported. Nothing was changed."
        : $"Found {found} entries, but none could be imported. Nothing was changed.";

    public const string NothingApproved = "You skipped everything, so nothing was changed.";

    public static string ConfirmTitle(int approved) => approved switch
    {
        0 => "Add nothing",
        1 => "Add 1 snippet",
        _ => $"Add {approved} snippets",
    };

    public static string PasteSummary(int found, int skipped)
    {
        var snippets = found == 1 ? "1 snippet found" : $"{found} snippets found";
        return skipped switch
        {
            0 => $"{snippets}.",
            1 => $"{snippets}, 1 line skipped.",
            _ => $"{snippets}, {skipped} lines skipped.",
        };
    }

    public static string ReviewSummary(int newCount, int existing, int duplicates)
    {
        var parts = new List<string>();
        if (newCount == 1) { parts.Add("1 new snippet"); }
        else if (newCount > 0) { parts.Add($"{newCount} new snippets"); }
        if (existing > 0) { parts.Add($"{existing} you already have"); }
        if (duplicates > 0) { parts.Add($"{duplicates} listed twice"); }
        return parts.Count == 0 ? "Nothing to review." : string.Join(", ", parts) + ".";
    }

    public static string Notice(SnippetImportNotice notice) => notice switch
    {
        SnippetImportNotice.IncompatibleSourceEntriesExcluded { Count: 1 } => "1 entry was left out because EnviousWispr can't use it.",
        SnippetImportNotice.IncompatibleSourceEntriesExcluded excluded => $"{excluded.Count} entries were left out because EnviousWispr can't use them.",
        SnippetImportNotice.LinesSkipped { Count: 1 } => "1 line skipped because it has no trigger and text.",
        _ => $"{notice.Count} lines skipped because they have no trigger and text.",
    };

    /// <summary>The count above the list: "N of M" while searching, else the whole count.</summary>
    public static string CountLabel(int shown, int total, bool searching) =>
        searching ? $"{shown} of {total}" : total == 1 ? "1 snippet" : $"{total} snippets";
}

/// <summary>Search over saved snippets (macOS <c>SnippetsCoordinator.filtered</c>).</summary>
public static class SnippetSearch
{
    /// <summary>The snippets whose trigger or text contains the query, ignoring case; every snippet for a blank query.</summary>
    public static IReadOnlyList<SnippetEntry> Filter(IReadOnlyList<SnippetEntry> snippets, string? query)
    {
        ArgumentNullException.ThrowIfNull(snippets);
        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length == 0)
        {
            return snippets;
        }

        return snippets
            .Where(snippet =>
                snippet.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                snippet.Body.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
