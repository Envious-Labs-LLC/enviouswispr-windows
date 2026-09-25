namespace EnviousWispr.Core.History;

/// <summary>Which dictation "Paste last dictation" and "Copy last dictation" mean. Ref: #206, macOS #3106.</summary>
/// <remarks>
/// ONE RULE FOR THE MENU'S PREVIEW AND FOR THE ACTION, applied at each read, so the two cannot disagree
/// about the same entry in the same state. The entry can still change between the menu opening and the
/// click - an entry deleted in between makes the action refuse rather than paste something else.
///
/// - An ESCAPE RECOVERY is left out: the person cancelled that take and chose not to deliver it, so offering
///   it here would paste words they threw away. It is the one entry written undelivered AND with its own
///   expiry (<c>HistoryWriteIntent.EscapeRecovery</c>). Once Kept it loses the expiry and becomes an ordinary
///   held entry - a deliberate choice to keep the words, which is also a choice to be able to reuse them.
/// - A HELD entry whose paste was refused IS offered: the words the person said and did not receive are
///   exactly what this is for. macOS narrows the same way and for the same reason.
/// - Whitespace is never offered - it would paste nothing visible. Checked, never trimmed: what is reused
///   is exactly what was delivered.
/// - An expired entry is not offered, though the store prunes it at the next write.
/// </remarks>
public static class LastDictation
{
    /// <summary>The newest entry that may be reused, from entries in the store's order (newest first).</summary>
    public static DictationHistoryEntry? Pick(IReadOnlyList<DictationHistoryEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DictationHistoryEntry? newest = null;
        foreach (var entry in entries)
        {
            // Newest by the entry's own time, not by list position: the order is the store's promise,
            // and a reuse that pasted an older dictation because a list arrived unsorted is silent.
            if (IsReusable(entry, now) && (newest is null || entry.CreatedAt > newest.CreatedAt))
            {
                newest = entry;
            }
        }

        return newest;
    }

    /// <summary>The same entry read again by id, or null if it may no longer be reused.</summary>
    public static DictationHistoryEntry? Find(IReadOnlyList<DictationHistoryEntry> entries, Guid id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            if (entry.Id == id)
            {
                return IsReusable(entry, now) ? entry : null;
            }
        }

        return null;
    }

    public static bool IsReusable(DictationHistoryEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !IsEscapeRecovery(entry) &&
            (entry.ExpiresAt is null || entry.ExpiresAt > now) &&
            !string.IsNullOrWhiteSpace(entry.Text);
    }

    /// <summary>The first line of the text, cut to <paramref name="maximumCharacters"/>, for a menu row that shows which dictation it means.</summary>
    public static string Preview(string text, int maximumCharacters = 30)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 2);
        var line = text.TrimStart();
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            line = line[..newline];
        }

        line = line.TrimEnd();
        return line.Length <= maximumCharacters ? line : string.Concat(line.AsSpan(0, maximumCharacters - 1).TrimEnd(), "…");
    }

    private static bool IsEscapeRecovery(DictationHistoryEntry entry) =>
        !entry.WasDelivered && entry.ExpiresAt is not null;
}
