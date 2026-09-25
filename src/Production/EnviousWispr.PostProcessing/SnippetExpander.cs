using System.Security.Cryptography;
using System.Text;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.PostProcessing;

/// <summary>
/// One expansion the pipeline owes the person: the sentinel that stands in for it through the text
/// chain, and the exact text that must replace that sentinel before anything is stored, shown or pasted.
/// </summary>
/// <param name="Sentinel">The opaque token substituted into the text. Unique within its take.</param>
/// <param name="Expansion">The text this snippet delivers, fill-ins already resolved. Never sent to a model.</param>
/// <param name="SuppressFollowingSentenceEnding">
/// True when THIS SNIPPET OWNS ITS ENDING, so the finalizer drops a sentence terminator sitting right
/// after the sentinel in whatever polish returns. It records the DECISION, not whether anything was
/// removed from the recogniser's text: a whole-dictation snippet the recogniser left unpunctuated still
/// owns its ending, and a model is the other source of a terminator. The decision has to TRAVEL because
/// the model is the last writer (macOS #2637).
/// </param>
public sealed record SnippetExpansionRecord(string Sentinel, string Expansion, bool SuppressFollowingSentenceEnding = false);

/// <summary>What the expander did to one take.</summary>
/// <param name="Text">The text with each fired snippet replaced by its sentinel.</param>
/// <param name="Records">One record per fired snippet, in the order they appear. Empty means nothing fired.</param>
/// <param name="UsedPlaceholders">
/// The fill-ins used by the snippets that FIRED, read from their SAVED text, so the stage can decide
/// whether THIS take needs the clipboard after matching rather than before it.
/// </param>
public sealed record SnippetExpansionOutcome(
    string Text,
    IReadOnlyList<SnippetExpansionRecord> Records,
    IReadOnlySet<SnippetPlaceholder> UsedPlaceholders)
{
    public bool DidFire => Records.Count > 0;
}

/// <summary>The snippet matcher: literal where custom-word correction is fuzzy, and one pass.</summary>
/// <remarks>
/// PORTED FROM macOS <c>SnippetExpander</c> (Sources/EnviousWisprPostProcessing/SnippetExpander.swift):
/// <list type="bullet">
/// <item>A snippet fires only when the keyword is spoken immediately before its trigger.</item>
/// <item>Trigger tokens compare through <see cref="SnippetText.Normalize"/> - literal, never fuzzy.</item>
/// <item>The LONGEST match wins when several triggers match at one position, because triggers nest:
/// with "my email" and "my email address" both saved, first-match would strand "address".</item>
/// <item>Punctuation clinging to the last trigger word is re-attached after the substitution, unless
/// the snippet owns its ending (see <see cref="TrailingToRestore"/>).</item>
/// <item>The keyword is consumed on a hit and left in place on a miss, which is what makes "the path is
/// backslash users" come through untouched.</item>
/// </list>
/// </remarks>
public sealed class SnippetExpander
{
    /// <summary>
    /// The sentinel shape. A single unbroken token of capitals and digits: no space for anything to
    /// split, no punctuation for inverse text normalisation to convert, not a word filler removal
    /// knows, and long and meaningless enough that custom-word correction has no candidate anywhere
    /// near it. Opaque enough that a polish model reads an identifier, not prose to rewrite. Its
    /// survival through every deterministic stage is PROVED by a test that drives the real stages over
    /// it with every option on, not assumed from this reasoning.
    /// </summary>
    public const string Prefix = "EWSNIP";

    private readonly Func<string> _candidateSource;

    /// <param name="candidateSource">Mints a sentinel candidate. Injectable so a test can FORCE a collision rather than wait 128 bits for one.</param>
    public SnippetExpander(Func<string>? candidateSource = null)
    {
        _candidateSource = candidateSource ?? RandomCandidate;
    }

    /// <summary>The prefix and 128 random bits in upper-case hex.</summary>
    public static string RandomCandidate() =>
        Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Expands every fired snippet in <paramref name="text"/> into a sentinel.</summary>
    /// <remarks>
    /// Returns the input unchanged with no records when the vocabulary cannot fire. <paramref name="values"/>
    /// has no default: every caller states which instant, culture, zone and clipboard its snippets are
    /// rendered against.
    /// </remarks>
    public SnippetExpansionOutcome Expand(string text, SnippetVocabulary vocabulary, SnippetDynamicValues values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(values);
        var noneUsed = new HashSet<SnippetPlaceholder>();
        if (!vocabulary.CanFire)
        {
            return new SnippetExpansionOutcome(text, [], noneUsed);
        }

        var keyword = SnippetText.Normalize(vocabulary.Keyword);
        var triggers = vocabulary.Snippets
            .Select(snippet => (Snippet: snippet, Tokens: SnippetText.TriggerTokens(snippet.Name)))
            .Where(candidate => candidate.Tokens.Count > 0)
            .ToArray();
        var pieces = Split(text);
        var wordIndices = Enumerable.Range(0, pieces.Count).Where(index => !pieces[index].IsWhitespace).ToArray();

        // SEEDED WITH ANY WHITESPACE BEFORE THE FIRST WORD. The walk below appends the gap that FOLLOWS
        // each word, so a leading run belongs to no word; on macOS it was silently dropped on every
        // dictation that armed the step, found by review because every fixture started with a letter.
        var output = new StringBuilder(text.Length);
        if (pieces.Count > 0 && pieces[0].IsWhitespace)
        {
            output.Append(pieces[0].Text);
        }

        var records = new List<SnippetExpansionRecord>();
        var issued = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<SnippetPlaceholder>();
        // DECIDED ONCE PER TAKE, AND ONLY WHEN SOMETHING FIRES. The collision domain is the RESOLVED
        // expansions, because the clipboard is the one domain a person can put "EWSNIP..." into.
        SnippetResolvedExpansions? expansions = null;
        bool? domainCanCollide = null;
        var cursor = 0;
        while (cursor < wordIndices.Length)
        {
            var pieceIndex = wordIndices[cursor];
            var token = pieces[pieceIndex].Text;

            // THE KEYWORD IS A CONSUMED TOKEN TOO, and it was the one the first macOS guard could not
            // see: "backslash. My email address is below" has its boundary on the keyword, which
            // normalisation strips. Every consumed token but the last is checked for a sentence end.
            var hit = string.Equals(SnippetText.Normalize(token), keyword, StringComparison.Ordinal) &&
                !SnippetText.EndsSentence(token)
                    ? LongestMatch(triggers, pieces, wordIndices, cursor + 1)
                    : null;
            if (hit is not { } match)
            {
                output.Append(token);
                AppendWhitespaceAfter(pieceIndex, pieces, output);
                cursor++;
                continue;
            }

            var lastWordIndex = wordIndices[cursor + match.Length];
            var lastToken = pieces[lastWordIndex].Text;
            expansions ??= new SnippetResolvedExpansions(vocabulary.Snippets.Select(snippet => snippet.Body), values);
            domainCanCollide ??= DomainCanCollide(text, expansions);
            var sentinel = MintSentinel(text, expansions, issued, domainCanCollide.Value);
            issued.Add(sentinel);

            // THE FILL-INS BECOME TEXT HERE, ONCE, and everything downstream carries the result: the
            // ending decision reads the DELIVERED text, and the record holds it verbatim.
            var resolved = SnippetPlaceholders.Resolve(match.Snippet.Body, values);
            used.UnionWith(SnippetPlaceholders.Used(match.Snippet.Body));
            var (restored, suppressed) = TrailingToRestore(
                lastToken,
                resolved,
                isWholeDictation: cursor == 0 && cursor + match.Length == wordIndices.Length - 1);
            records.Add(new SnippetExpansionRecord(sentinel, resolved, suppressed));

            // THE PUNCTUATION THE PERSON SPOKE BELONGS AROUND THE PASTED TEXT, not swallowed with the
            // trigger. Both opening positions, because the quote can sit on the keyword or on the
            // first trigger word, and restoring only one left an orphan closing quote in the other.
            var firstTriggerToken = pieces[wordIndices[cursor + 1]].Text;
            output
                .Append(SnippetText.LeadingPunctuation(token))
                .Append(SnippetText.LeadingPunctuation(firstTriggerToken))
                .Append(sentinel)
                .Append(restored);
            AppendWhitespaceAfter(lastWordIndex, pieces, output);
            cursor += match.Length + 1;
        }

        return new SnippetExpansionOutcome(output.ToString(), records, used);
    }

    /// <summary>The trailing punctuation to re-attach after the expansion, and whether the snippet owns its ending.</summary>
    /// <remarks>
    /// RE-ATTACHING IS THE DEFAULT: in "Please contact me at backslash my email." the full stop is the
    /// person's sentence. The recogniser's TERMINATOR is suppressed in the two cases where the snippet's
    /// own text is the authority on how it ends (founder, 2026-09-03: "People will add punctuation and
    /// formatting to their snippet. We would honor that."): the snippet is the WHOLE dictation, which a
    /// recogniser punctuates as a complete sentence and so welded a stop onto an email address every
    /// time; or the saved text already ends a sentence, which would otherwise arrive as "..". Only the
    /// terminator goes; a comma, bracket or closing quote is re-attached exactly as before.
    /// </remarks>
    internal static (string Restored, bool Suppressed) TrailingToRestore(string lastToken, string expansion, bool isWholeDictation)
    {
        var run = SnippetText.TrailingPunctuation(lastToken);
        return isWholeDictation || SnippetText.EndsSentence(expansion)
            ? (SnippetText.DroppingSentenceEndings(run), true)
            : (run, false);
    }

    /// <summary>Whether ANY candidate could collide with the input or a resolved expansion.</summary>
    /// <remarks>
    /// Every candidate this type mints carries <see cref="Prefix"/>, so a domain without the prefix
    /// cannot contain one: one scan answers for every mint of the take. A candidate WITHOUT the prefix
    /// (an injected source) is still scanned, so the guarantee does not rest on the source.
    /// </remarks>
    internal static bool DomainCanCollide(string rawInput, SnippetResolvedExpansions expansions) =>
        rawInput.Contains(Prefix, StringComparison.Ordinal) || expansions.Contains(Prefix);

    /// <summary>A sentinel that appears in NONE of: the raw input, any resolved expansion, or the sentinels already issued this take.</summary>
    /// <remarks>
    /// ALL THREE DOMAINS MATTER, and the second is the one easy to miss: restoration substitutes
    /// expansions back into the text, so an expansion containing a live sentinel would put one back
    /// after the finalizer had checked. A collision on 128 random bits does not recur, but the loop is
    /// written to terminate rather than to trust that: a degenerate source falls back to a counted
    /// spelling that is checked against the same three domains.
    ///
    /// AN ISSUED SENTINEL MAY NEITHER CONTAIN NOR BE CONTAINED BY ANOTHER, not merely differ from it.
    /// Restoration replaces by substring, so a counted fallback spelled "EWSNIPFALLBACK1" would also
    /// match inside "EWSNIPFALLBACK10" and splice one snippet's text into the other's placeholder.
    ///
    /// THE FALLBACK IS THEREFORE CLOSED WITH A TERMINATOR, "EWSNIPFALLBACK1X". Without it the overlap
    /// rule and the count would fight: once "EWSNIPFALLBACK0" to "9" were issued, every larger number
    /// starts with one of them, and the loop ran on until the counter wrapped - measured at over two
    /// minutes for twelve snippets. With the terminator no count is a prefix of another, so only a real
    /// collision with the dictated text or a saved text can skip a number.
    /// </remarks>
    internal string MintSentinel(string rawInput, SnippetResolvedExpansions expansions, IReadOnlySet<string> alreadyIssued, bool domainCanCollide)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = _candidateSource();
            if (string.IsNullOrEmpty(candidate) || OverlapsIssued(candidate, alreadyIssued))
            {
                continue;
            }

            if ((domainCanCollide || !candidate.StartsWith(Prefix, StringComparison.Ordinal)) &&
                (rawInput.Contains(candidate, StringComparison.Ordinal) || expansions.Contains(candidate)))
            {
                continue;
            }

            return candidate;
        }

        for (var suffix = 0; ; suffix++)
        {
            var candidate = $"{Prefix}FALLBACK{suffix}X";
            var collides = OverlapsIssued(candidate, alreadyIssued) ||
                (domainCanCollide &&
                    (rawInput.Contains(candidate, StringComparison.Ordinal) || expansions.Contains(candidate)));
            if (!collides)
            {
                return candidate;
            }
        }
    }

    /// <summary>Whether a candidate equals, contains, or is contained by a sentinel already issued this take.</summary>
    private static bool OverlapsIssued(string candidate, IReadOnlySet<string> alreadyIssued) =>
        alreadyIssued.Any(issued =>
            issued.Contains(candidate, StringComparison.Ordinal) ||
            candidate.Contains(issued, StringComparison.Ordinal));

    private readonly record struct Match(SnippetEntry Snippet, int Length);

    /// <summary>The longest trigger matching the word tokens starting at <paramref name="startingAt"/>, or null.</summary>
    /// <remarks>
    /// Ties cannot arise: a duplicate trigger is refused when it is saved (<see cref="SnippetRules"/>).
    /// Only an INTERIOR sentence end blocks; punctuation on the LAST token belongs to the trigger's
    /// surrounding sentence and is decided by <see cref="TrailingToRestore"/>.
    /// </remarks>
    private static Match? LongestMatch(
        (SnippetEntry Snippet, IReadOnlyList<string> Tokens)[] triggers,
        List<Piece> pieces,
        int[] wordIndices,
        int startingAt)
    {
        Match? best = null;
        foreach (var (snippet, tokens) in triggers)
        {
            if (startingAt + tokens.Count > wordIndices.Length)
            {
                continue;
            }

            var matched = true;
            for (var offset = 0; offset < tokens.Count; offset++)
            {
                var piece = pieces[wordIndices[startingAt + offset]].Text;
                if (!string.Equals(SnippetText.Normalize(piece), tokens[offset], StringComparison.Ordinal) ||
                    (offset < tokens.Count - 1 && SnippetText.EndsSentence(piece)))
                {
                    matched = false;
                    break;
                }
            }

            if (matched && (best is null || tokens.Count > best.Value.Length))
            {
                best = new Match(snippet, tokens.Count);
            }
        }

        return best;
    }

    private readonly record struct Piece(string Text, bool IsWhitespace);

    /// <summary>Splits into alternating runs of whitespace and non-whitespace, keeping both.</summary>
    /// <remarks>
    /// THE WHITESPACE IS KEPT RATHER THAN RE-SYNTHESISED because the expansion must land in text that is
    /// otherwise untouched: rebuilding with single spaces would reformat line breaks and double spaces
    /// on every dictation that fires a snippet.
    /// </remarks>
    private static List<Piece> Split(string text)
    {
        var pieces = new List<Piece>();
        var start = 0;
        for (var index = 1; index <= text.Length; index++)
        {
            if (index == text.Length || char.IsWhiteSpace(text[index]) != char.IsWhiteSpace(text[start]))
            {
                pieces.Add(new Piece(text[start..index], char.IsWhiteSpace(text[start])));
                start = index;
            }
        }

        return pieces;
    }

    private static void AppendWhitespaceAfter(int index, List<Piece> pieces, StringBuilder output)
    {
        if (index + 1 < pieces.Count && pieces[index + 1].IsWhitespace)
        {
            output.Append(pieces[index + 1].Text);
        }
    }
}

/// <summary>The sentinel-collision domain, without building a resolved copy of the clipboard per snippet.</summary>
/// <remarks>
/// WHY THIS EXISTS, MEASURED ON macOS (#3018): answering "does a candidate occur in any expansion?" by
/// resolving every saved expansion copied the clipboard once PER clipboard snippet - sixteen snippets
/// and a 10 MiB clipboard took 2161 ms against a one-second budget, and the snippet silently did not
/// fire. Segments are held instead, so a value is scanned at most once per query.
///
/// THE CONTRACT IS ONE-WAY: <see cref="Contains"/> never misses an occurrence and may report one the
/// resolved string does not have. It is used to REJECT a candidate, so an extra report costs one more
/// random mint while a miss would put a live sentinel into checked text. A match spanning a splice is
/// caught by a window of the last <c>2w</c> characters before a segment joined to the segment's first
/// <c>2w</c>, carried forward so a run of short segments is covered. Comparison is ordinal, the same
/// comparison the finalizer substitutes with, so a byte hit here is a hit there.
/// </remarks>
public sealed class SnippetResolvedExpansions
{
    private readonly IReadOnlyList<IReadOnlyList<SnippetResolvedSegment>> _expansions;

    public SnippetResolvedExpansions(IEnumerable<string> savedTexts, SnippetDynamicValues values)
    {
        ArgumentNullException.ThrowIfNull(savedTexts);
        ArgumentNullException.ThrowIfNull(values);
        _expansions = savedTexts.Select(saved => SnippetPlaceholders.Segments(saved, values)).ToArray();
    }

    /// <summary>Whether <paramref name="needle"/> occurs in ANY resolved expansion, over-reporting rather than missing.</summary>
    public bool Contains(string needle)
    {
        ArgumentNullException.ThrowIfNull(needle);
        if (needle.Length == 0)
        {
            return false;
        }

        var edge = 2 * needle.Length;
        // THE SAME VALUE WHEREVER IT APPEARS, so a large clipboard is scanned once per query.
        var valueHoldsNeedle = new Dictionary<SnippetPlaceholder, bool>();
        foreach (var segments in _expansions)
        {
            var carry = string.Empty;
            foreach (var segment in segments)
            {
                var text = segment.Text;
                if (carry.Length > 0 &&
                    (carry + text[..Math.Min(edge, text.Length)]).Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }

                if (segment.Placeholder is { } placeholder)
                {
                    if (!valueHoldsNeedle.TryGetValue(placeholder, out var holds))
                    {
                        holds = text.Contains(needle, StringComparison.Ordinal);
                        valueHoldsNeedle[placeholder] = holds;
                    }

                    if (holds)
                    {
                        return true;
                    }
                }
                else if (text.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }

                carry = text.Length >= edge
                    ? text[^edge..]
                    : (carry + text)[Math.Max(0, carry.Length + text.Length - edge)..];
            }
        }

        return false;
    }
}
