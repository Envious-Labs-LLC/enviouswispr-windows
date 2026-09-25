using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EnviousWispr.Core.Settings;

/// <summary>The fill-ins a saved snippet may carry, and the only three there are.</summary>
/// <remarks>
/// A CLOSED SET ON PURPOSE (macOS <c>SnippetPlaceholder</c>, #3018). The page's buttons, the matcher
/// and the resolver all ask this one type what a <c>{{...}}</c> span means, so a new member changes
/// everywhere at once. Anything else inside braces is not a fill-in: it is carried through byte for byte.
/// </remarks>
public enum SnippetPlaceholder
{
    Date,
    Time,
    Clipboard,
}

/// <summary>Everything a fill-in needs to become text, frozen at one instant for the whole take.</summary>
/// <remarks>
/// A VALUE RATHER THAN A SET OF CALLBACKS, because the expander reads the same saved text twice in one
/// take - once into the sentinel-collision domain and once at the fire site - and the two must agree.
/// The culture and the zone are captured once, never re-read, so two fill-ins in one dictation cannot
/// disagree about what day it is.
/// </remarks>
/// <param name="Clipboard">Null when the clipboard was not read or holds no text; both fill in as nothing.</param>
public sealed record SnippetDynamicValues(
    DateTimeOffset Now,
    CultureInfo Culture,
    TimeZoneInfo TimeZone,
    string? Clipboard);

/// <summary>One piece of a resolved expansion: a literal run of the saved text, or one substituted value.</summary>
public readonly record struct SnippetResolvedSegment(SnippetPlaceholder? Placeholder, string Text);

/// <summary>The one scanner for <c>{{...}}</c> spans, and the one place a fill-in becomes text.</summary>
public static partial class SnippetPlaceholders
{
    /// <summary>Every fill-in, in the order the page offers them.</summary>
    public static IReadOnlyList<SnippetPlaceholder> All { get; } =
        [SnippetPlaceholder.Date, SnippetPlaceholder.Time, SnippetPlaceholder.Clipboard];

    /// <summary>The canonical spelling the page inserts. Matching is case-insensitive; this is only the spelling written.</summary>
    public static string Token(SnippetPlaceholder placeholder) => placeholder switch
    {
        SnippetPlaceholder.Date => "{{date}}",
        SnippetPlaceholder.Time => "{{time}}",
        SnippetPlaceholder.Clipboard => "{{clipboard}}",
        _ => throw new ArgumentOutOfRangeException(nameof(placeholder), placeholder, "Not a fill-in."),
    };

    /// <summary>Which supported fill-ins <paramref name="text"/> uses, read from the SAVED text, the only place the token still exists.</summary>
    public static IReadOnlySet<SnippetPlaceholder> Used(string text) =>
        Spans(text).Where(span => span.Placeholder is not null).Select(span => span.Placeholder!.Value).ToHashSet();

    /// <summary>True when <paramref name="text"/> carries a <c>{{...}}</c> span this app cannot fill in.</summary>
    public static bool CarriesUnsupportedPlaceholder(string text) =>
        Spans(text).Any(span => span.Placeholder is null);

    /// <summary>Replaces every supported fill-in with its value, copying everything else byte for byte.</summary>
    /// <remarks>
    /// SINGLE PASS. A SUBSTITUTED VALUE IS NEVER RE-SCANNED. The clipboard is arbitrary content, so a
    /// second pass would let whatever was copied drive further substitution: a clipboard holding
    /// <c>{{date}}</c> pastes those eight characters, which is what was copied.
    /// </remarks>
    public static string Resolve(string text, SnippetDynamicValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var found = Spans(text);
        if (found.Count == 0)
        {
            return text;
        }

        var output = new StringBuilder(text.Length);
        foreach (var segment in Segments(text, values, found))
        {
            output.Append(segment.Text);
        }

        return output.ToString();
    }

    /// <summary>The pieces a resolved expansion is made of, in order, from the SAME walk as <see cref="Resolve"/>.</summary>
    public static IReadOnlyList<SnippetResolvedSegment> Segments(string text, SnippetDynamicValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Segments(text, values, Spans(text));
    }

    /// <summary>The text one fill-in becomes.</summary>
    /// <remarks>
    /// THE DATE IS THE CULTURE'S OWN LONG DATE WITH THE WEEKDAY REMOVED AND THE MONTH ABBREVIATED -
    /// "Sep 16, 2026" in the United States, "16 Sep 2026" in Britain - because a numeric date is the
    /// same characters read two ways and a snippet's output is read by other people (macOS uses
    /// <c>.abbreviated</c> for that reason), and the full long date is too wide for a chat line. The
    /// time is the culture's short time. Both in the frozen zone.
    /// </remarks>
    public static string Value(SnippetPlaceholder placeholder, SnippetDynamicValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var local = TimeZoneInfo.ConvertTime(values.Now, values.TimeZone);
        return placeholder switch
        {
            SnippetPlaceholder.Date => local.ToString(AbbreviatedDatePattern(values.Culture), values.Culture),
            SnippetPlaceholder.Time => local.ToString(values.Culture.DateTimeFormat.ShortTimePattern, values.Culture),
            SnippetPlaceholder.Clipboard => values.Clipboard ?? string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(placeholder), placeholder, "Not a fill-in."),
        };
    }

    /// <summary>The culture's long date pattern without its weekday and with the month abbreviated.</summary>
    internal static string AbbreviatedDatePattern(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var pattern = WeekdayField().Replace(culture.DateTimeFormat.LongDatePattern, string.Empty);
        pattern = FullMonthField().Replace(pattern, "MMM");
        pattern = pattern.Trim().Trim(',').Trim();
        return pattern.Length == 0 ? culture.DateTimeFormat.ShortDatePattern : pattern;
    }

    /// <summary>One <c>{{...}}</c> run in the source text; a null placeholder is a span this app does not fill in.</summary>
    private readonly record struct Span(int Start, int End, SnippetPlaceholder? Placeholder);

    /// <summary>The ONE scanner. Every question about fill-ins is answered from this walk.</summary>
    /// <remarks>
    /// Left to right: on <c>{{</c>, find the next <c>}}</c> after the opener; none means the rest is
    /// literal. The inner text is trimmed (newlines included - a saved snippet is routinely multi-line),
    /// lowercased and matched against the closed set. AN UNTERMINATED <c>{{</c> IS LITERAL, NOT AN ERROR,
    /// and overlap resolves left to right: <c>{{{{date}}}}</c> is one unsupported span whose inner text
    /// is <c>{{date</c>, then a literal <c>}}</c>. Ambiguous input is carried through, not guessed at.
    /// </remarks>
    private static List<Span> Spans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<Span>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var open = text.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var name = text[(open + 2)..close].Trim().ToLowerInvariant();
            SnippetPlaceholder? placeholder = name switch
            {
                "date" => SnippetPlaceholder.Date,
                "time" => SnippetPlaceholder.Time,
                "clipboard" => SnippetPlaceholder.Clipboard,
                _ => null,
            };
            spans.Add(new Span(open, close + 2, placeholder));
            cursor = close + 2;
        }

        return spans;
    }

    private static List<SnippetResolvedSegment> Segments(string text, SnippetDynamicValues values, List<Span> found)
    {
        var segments = new List<SnippetResolvedSegment>(found.Count * 2 + 1);
        if (found.Count == 0)
        {
            segments.Add(new SnippetResolvedSegment(null, text));
            return segments;
        }

        var cursor = 0;
        foreach (var span in found)
        {
            if (cursor < span.Start)
            {
                segments.Add(new SnippetResolvedSegment(null, text[cursor..span.Start]));
            }

            segments.Add(span.Placeholder is { } placeholder
                ? new SnippetResolvedSegment(placeholder, Value(placeholder, values))
                : new SnippetResolvedSegment(null, text[span.Start..span.End]));
            cursor = span.End;
        }

        if (cursor < text.Length)
        {
            segments.Add(new SnippetResolvedSegment(null, text[cursor..]));
        }

        return segments;
    }

    [GeneratedRegex(@"\s*[,،]?\s*d{4,}\s*[,،]?\s*", RegexOptions.CultureInvariant)]
    private static partial Regex WeekdayField();

    [GeneratedRegex("M{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex FullMonthField();
}
