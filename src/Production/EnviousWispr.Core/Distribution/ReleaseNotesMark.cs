namespace EnviousWispr.Core.Distribution;

/// <summary>Whether this build's release notes still have something new to say.</summary>
/// <remarks>
/// ONE COMPARISON, WRITTEN DOWN BECAUSE IT IS THE WHOLE FEATURE. The mark is on until the notes for
/// THIS build have been opened, so the rule is: what was last read against what is here now.
///
/// A FIRST RUN HAS READ NOTHING, AND THAT IS NOT A SPECIAL CASE. Nothing stored means the notes are
/// new to this person, which is true - they have never seen them.
///
/// A BUILD THAT DOES NOT NAME ITSELF IS NOT NEWS. If the current identity is missing there is
/// nothing to be unread about, and marking it would leave a dot nobody can ever clear.
/// </remarks>
public static class ReleaseNotesMark
{
    /// <summary>True when the mark should be shown.</summary>
    /// <param name="lastSeen">The build whose notes were last opened, or null.</param>
    /// <param name="current">The build running now.</param>
    public static bool IsUnread(string? lastSeen, string? current) =>
        !string.IsNullOrWhiteSpace(current) &&
        !string.Equals(lastSeen, current, StringComparison.Ordinal);
}
