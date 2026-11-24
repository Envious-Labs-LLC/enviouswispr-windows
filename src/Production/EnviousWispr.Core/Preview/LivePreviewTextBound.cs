namespace EnviousWispr.Core.Preview;

/// <summary>
/// The cap on how much preview text is kept alive, and the one implementation of applying it (macOS #1988, Windows #127).
/// </summary>
/// <remarks>
/// Applied at the producer (<c>RuntimeWorkerLivePreviewEngine.PreviewAsync</c>) and again after any display-only
/// custom-word correction or British spelling conversion (<c>LivePreviewTextFormatter.Format</c>), so retained
/// preview text never exceeds 2,000 characters during long dictations.
/// </remarks>
public static class LivePreviewTextBound
{
    public const int MaxCharacters = 2_000;

    /// <summary>Keeps the tail of at most <see cref="MaxCharacters"/> characters without cutting a word in half.</summary>
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= MaxCharacters)
        {
            return text;
        }

        var start = text.Length - MaxCharacters;
        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        var tail = text[start..];
        var space = tail.IndexOf(' ');
        return space >= 0
            ? tail[(space + 1)..]
            : tail;
    }
}
