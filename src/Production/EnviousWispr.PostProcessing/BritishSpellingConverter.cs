using System.Text;
using System.Text.Json;

namespace EnviousWispr.PostProcessing;

/// <summary>American to British spelling for the English (UK) choice.</summary>
/// <remarks>
/// A PORT, NOT A REDESIGN, of the macOS `BritishSpellingConverter` (EnviousWispr #3124), with the same bundled
/// table, `british-spelling.json` - 5,763 forms generated from VarCon 2020.12.07 by the macOS repository's
/// `scripts/generate-british-spelling.py`. The table already excludes every word VarCon marks as
/// sense-dependent ("program", "check", "practice", "license"...), so this type makes no meaning judgements of
/// its own: it decides only WHICH tokens in a text are prose words it may touch.
///
/// A token is left alone, and the reason is the failure it prevents, when it is:
/// <list type="bullet">
/// <item>Title-case in mid-sentence: a name ("Kennedy Center", "Labor Day"). Respelling a name changes a fact. A
/// Title-case word at the start of a sentence or a list item IS converted; nothing in the text tells the two
/// apart.</item>
/// <item>ALL-CAPS or mixed case ("COLOR", "iColor"): an acronym, a shout or an identifier.</item>
/// <item>Touching a digit, a letter outside ASCII, or <c>_ @ / \ # $ = &lt; &gt;</c>, or joined to a word by <c>.</c>
/// or <c>:</c> with no space ("color.js", "self.color", "https://center.io"): code, a path or an address.</item>
/// <item>One of the person's Custom Words, or inside a protected span: their own spelling.</item>
/// </list>
/// A hyphen separates prose words ("colour-coded"), so a hyphenated CSS name ("background-color") converts as
/// well - an accepted limit, since hyphen compounds in dictation are overwhelmingly prose.
///
/// Pure and synchronous. <see cref="Result.Swaps"/> counts replaced tokens; when it is zero the input comes back
/// unchanged, the same string instance.
/// </remarks>
public sealed class BritishSpellingConverter
{
    private const string ResourceName = "EnviousWispr.PostProcessing.Resources.british-spelling.json";

    private static readonly Lazy<BritishSpellingConverter?> SharedInstance = new(TryLoadBundled);

    /// <summary>Lowercased American form (apostrophes as <c>'</c>) to its British form.</summary>
    private readonly IReadOnlyDictionary<string, string> _table;

    public BritishSpellingConverter(IReadOnlyDictionary<string, string> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    public readonly record struct Result(string Text, int Swaps);

    /// <summary>The number of American forms the table maps.</summary>
    public int EntryCount => _table.Count;

    /// <summary>
    /// The bundled table, loaded once per process, or null when it failed to load - the step then stands down and
    /// the take is delivered in American spelling, which is the product without this feature, not a failure.
    /// </summary>
    public static BritishSpellingConverter? Shared => SharedInstance.Value;

    public static BritishSpellingConverter LoadBundled()
    {
        using var stream = typeof(BritishSpellingConverter).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The bundled British spelling table is missing.");
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("The bundled British spelling table is invalid.");
        if (table.Count == 0)
        {
            throw new InvalidOperationException("The bundled British spelling table is empty.");
        }

        return new BritishSpellingConverter(table);
    }

    /// <summary>
    /// The lowercased words a conversion must leave alone: every canonical Custom Word and every word inside a
    /// multi-word one (a Custom Word "Kennedy Center" protects "center"). Only the PERSON's own words: this port
    /// has no app-authored vocabulary in the list it is handed.
    /// </summary>
    public static IReadOnlySet<string> ProtectedWords(IEnumerable<string> canonicalCustomWords)
    {
        ArgumentNullException.ThrowIfNull(canonicalCustomWords);
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var canonical in canonicalCustomWords)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                continue;
            }

            var lowered = canonical.ToLowerInvariant();
            words.Add(lowered);
            var word = new StringBuilder();
            foreach (var character in lowered)
            {
                if (char.IsLetter(character) || character is '\'' or '\u2019')
                {
                    word.Append(character == '\u2019' ? '\'' : character);
                }
                else if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
            }
        }

        return words;
    }

    /// <summary>Converts every eligible American spelling in <paramref name="text"/> to British.</summary>
    public Result Convert(
        string text,
        IReadOnlySet<string>? protectedWords = null,
        IReadOnlyList<string>? protectedSpans = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return new Result(text, 0);
        }

        var blocked = BlockedMask(text, protectedSpans);
        var output = new StringBuilder(text.Length);
        var swaps = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (!IsAsciiLetter(text[index]))
            {
                output.Append(text[index]);
                index++;
                continue;
            }

            var end = index + 1;
            while (end < text.Length)
            {
                if (IsAsciiLetter(text[end]))
                {
                    end++;
                }
                else if (IsApostrophe(text[end]) && end + 1 < text.Length && IsAsciiLetter(text[end + 1]))
                {
                    end++;
                }
                else
                {
                    break;
                }
            }

            var british = Replacement(text, index, end, blocked, protectedWords);
            if (british is not null)
            {
                output.Append(british);
                swaps++;
            }
            else
            {
                output.Append(text, index, end - index);
            }

            index = end;
        }

        return new Result(swaps == 0 ? text : output.ToString(), swaps);
    }

    private string? Replacement(
        string text,
        int start,
        int end,
        bool[]? blocked,
        IReadOnlySet<string>? protectedWords)
    {
        if (blocked is not null)
        {
            for (var position = start; position < end; position++)
            {
                if (blocked[position])
                {
                    return null;
                }
            }
        }

        if (IsCodeContext(text, start, end))
        {
            return null;
        }

        var key = new StringBuilder(end - start);
        char? apostrophe = null;
        var isLower = true;
        var restLower = true;
        for (var position = start; position < end; position++)
        {
            var character = text[position];
            if (IsApostrophe(character))
            {
                apostrophe = character;
                key.Append('\'');
                continue;
            }

            if (char.IsUpper(character))
            {
                isLower = false;
                if (position > start)
                {
                    restLower = false;
                }
            }

            key.Append(char.ToLowerInvariant(character));
        }

        var lookup = key.ToString();
        if (protectedWords?.Contains(lookup) == true || !_table.TryGetValue(lookup, out var british))
        {
            return null;
        }

        var isTitle = char.IsUpper(text[start]) && restLower;
        if (!isLower && !(isTitle && IsSentenceStart(text, start)))
        {
            return null;
        }

        var result = british;
        if (apostrophe is { } mark && mark != '\'')
        {
            result = result.Replace('\'', mark);
        }

        if (isTitle)
        {
            result = string.Concat(char.ToUpperInvariant(result[0]).ToString(), result.AsSpan(1));
        }

        return result;
    }

    /// <summary>True when the token is part of an identifier, a path, an address or a number.</summary>
    private static bool IsCodeContext(string text, int start, int end)
    {
        if (start > 0)
        {
            var previous = text[start - 1];
            if (IsCodeNeighbour(previous))
            {
                return true;
            }

            // "self.color", "https://center.io": joined to the word before by "." or ":".
            if (previous is '.' or ':' && start > 1 && !char.IsWhiteSpace(text[start - 2]))
            {
                return true;
            }
        }

        if (end < text.Length)
        {
            var next = text[end];
            if (IsCodeNeighbour(next))
            {
                return true;
            }

            // "color.js", "color:red": joined to what follows. A sentence end ("the color.") or a closing quote
            // after it is prose.
            if (next is '.' or ':' && end + 1 < text.Length)
            {
                var after = text[end + 1];
                if (!char.IsWhiteSpace(after) && !IsClosingPunctuation(after))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Start of text, or only spaces and opening quotes or brackets back to ".", "!", "?" or a newline, or back to
    /// a list marker that opens its line.
    /// </summary>
    private static bool IsSentenceStart(string text, int start)
    {
        var cursor = start - 1;
        while (cursor >= 0 && (text[cursor] is ' ' or '\t' || IsOpening(text[cursor])))
        {
            cursor--;
        }

        if (cursor < 0)
        {
            return true;
        }

        var character = text[cursor];
        return character is '.' or '!' or '?' || IsNewline(character) || IsLineOpeningListMarker(text, cursor);
    }

    /// <summary>
    /// True when <c>text[end]</c> is a list or heading marker that is the first thing on its line: a single
    /// <c>-</c>, <c>*</c>, <c>+</c> or bullet, a run of <c>#</c>, or digits followed by <c>)</c>.
    /// </summary>
    private static bool IsLineOpeningListMarker(string text, int end)
    {
        var markerStart = end;
        switch (text[end])
        {
            case '-' or '*' or '+' or '\u2022':
                break;
            case '#':
                while (markerStart > 0 && text[markerStart - 1] == '#')
                {
                    markerStart--;
                }

                break;
            case ')':
                var digit = end - 1;
                while (digit >= 0 && text[digit] is >= '0' and <= '9')
                {
                    digit--;
                }

                if (digit >= end - 1)
                {
                    return false;
                }

                markerStart = digit + 1;
                break;
            default:
                return false;
        }

        var cursor = markerStart - 1;
        while (cursor >= 0 && text[cursor] is ' ' or '\t')
        {
            cursor--;
        }

        return cursor < 0 || IsNewline(text[cursor]);
    }

    private static bool[]? BlockedMask(string text, IReadOnlyList<string>? spans)
    {
        if (spans is null || spans.Count == 0)
        {
            return null;
        }

        var blocked = new bool[text.Length];
        foreach (var span in spans)
        {
            if (string.IsNullOrEmpty(span))
            {
                continue;
            }

            for (var found = text.IndexOf(span, StringComparison.Ordinal);
                 found >= 0;
                 found = text.IndexOf(span, found + 1, StringComparison.Ordinal))
            {
                Array.Fill(blocked, true, found, span.Length);
            }
        }

        return blocked;
    }

    private static bool IsAsciiLetter(char character) => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static bool IsApostrophe(char character) => character is '\'' or '\u2019';

    private static bool IsOpening(char character) => character is '"' or '\'' or '(' or '[' or '\u201C' or '\u2018';

    private static bool IsClosingPunctuation(char character) =>
        character is '"' or '\'' or ')' or ']' or '\u201D' or '\u2019';

    private static bool IsCodeNeighbour(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '@' or '/' or '\\' or '#' or '$' or '=' or '<' or '>';

    private static bool IsNewline(char character) => character is '\n' or '\r' or '\u2028' or '\u2029' or '\u0085';

    private static BritishSpellingConverter? TryLoadBundled()
    {
        try
        {
            return LoadBundled();
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return null;
        }
    }
}
