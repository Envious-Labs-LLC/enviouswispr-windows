using System.Text;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

/// <summary>What resolving a take's sentinels decided.</summary>
/// <param name="Text">The deterministic text with every expansion substituted in.</param>
/// <param name="PolishedText">The polished text with every expansion substituted in, or null when polish was rejected or absent.</param>
/// <param name="RejectedPolish">True when polish came back without every sentinel exactly once, so it cannot be trusted to carry the snippets.</param>
public sealed record SnippetResolution(string Text, string? PolishedText, bool RejectedPolish);

/// <summary>Resolves every snippet sentinel back into the person's saved text.</summary>
/// <remarks>
/// PORTED FROM macOS <c>SnippetFinalizer</c> (Sources/EnviousWisprPipeline/SnippetFinalizer.swift), and
/// deliberately NOT a stage the deterministic executor runs: that executor answers a stage that fails or
/// times out with the stage's input, which is right for a limb and wrong here - a skipped resolution
/// would deliver text with a raw sentinel in it, into the person's document, History and the recovery
/// copy at once. The one rule this type keeps: IT NEVER RETURNS TEXT CONTAINING A SENTINEL IT OWNS.
/// </remarks>
public static class SnippetFinalizer
{
    /// <summary>Substitutes every record into <paramref name="text"/>, and into the polish when every sentinel survived it exactly once.</summary>
    /// <remarks>
    /// POLISH IS KEPT ONLY WHEN EVERY SENTINEL APPEARS IN IT EXACTLY ONCE. Not "at least once": a model
    /// that duplicated a sentinel would paste the saved text twice, which is a wrong document rather than
    /// a missing flourish. A lost or duplicated sentinel costs the WHOLE polish, not a repair at a
    /// guessed position: the promise is that the saved text arrives exactly, and reconstructing a lost
    /// span trades that for a heuristic on the one property whose value is that it is not one.
    /// </remarks>
    public static SnippetResolution Resolve(string text, string? polishedText, IReadOnlyList<SnippetExpansionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return new SnippetResolution(text, polishedText, RejectedPolish: false);
        }

        var deterministic = Substitute(records, text);
        if (polishedText is null)
        {
            return new SnippetResolution(deterministic, null, RejectedPolish: false);
        }

        return records.All(record => Occurrences(record.Sentinel, polishedText) == 1)
            ? new SnippetResolution(deterministic, Substitute(records, polishedText), RejectedPolish: false)
            : new SnippetResolution(deterministic, null, RejectedPolish: true);
    }

    /// <summary>Substitutes every record. Order is irrelevant: sentinels are mutually exclusive by construction.</summary>
    /// <remarks>
    /// The expander rejects a candidate that appears in the raw input, in any resolved expansion, or among
    /// the sentinels already issued, so no substitution can create or destroy another's match.
    /// </remarks>
    private static string Substitute(IReadOnlyList<SnippetExpansionRecord> records, string text)
    {
        var output = text;
        foreach (var record in records)
        {
            if (record.SuppressFollowingSentenceEnding)
            {
                output = RemovingSentenceEndingsAfter(record.Sentinel, output);
            }

            output = output.Replace(record.Sentinel, record.Expansion, StringComparison.Ordinal);
        }

        return output;
    }

    /// <summary>Drops a sentence terminator sitting right after a sentinel whose snippet owns its ending.</summary>
    /// <remarks>
    /// THE MODEL IS THE LAST WRITER, so the expander's decision cannot be enforced upstream of it: polish
    /// receives the sentinel and can put a full stop back. Applied to the deterministic text too, where it
    /// is a no-op by construction, so one path cannot drift from the other. Scoped to sentence endings: a
    /// comma the model added is the model punctuating a sentence it can see. Reads the WHOLE punctuation
    /// run first, so a full stop behind a kept bracket or quote is still dropped and the mark is kept.
    /// </remarks>
    private static string RemovingSentenceEndingsAfter(string sentinel, string text)
    {
        var output = new StringBuilder(text.Length);
        var cursor = 0;
        while (true)
        {
            var found = text.IndexOf(sentinel, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            var afterSentinel = found + sentinel.Length;
            output.Append(text, cursor, afterSentinel - cursor);
            var run = SnippetText.PunctuationRunLength(text, afterSentinel);
            output.Append(SnippetText.DroppingSentenceEndings(text.Substring(afterSentinel, run)));
            cursor = afterSentinel + run;
        }

        return output.Append(text, cursor, text.Length - cursor).ToString();
    }

    private static int Occurrences(string needle, string haystack)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var cursor = 0;
        while ((cursor = haystack.IndexOf(needle, cursor, StringComparison.Ordinal)) >= 0)
        {
            count++;
            cursor += needle.Length;
        }

        return count;
    }
}
