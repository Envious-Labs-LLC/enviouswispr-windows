using System.Text;
using System.Text.RegularExpressions;

namespace EnviousWispr.Core.Dictation;

/// <summary>
/// Removes non-speech Whisper decoder markers (such as <c>[BLANK_AUDIO]</c>, <c>[MUSIC]</c>,
/// <c>(silence)</c>, angle-bracket special tokens, and music-note spans) while preserving normal
/// bracketed user text such as <c>[1]</c>, <c>[TODO]</c>, or <c>(see note)</c>.
/// </summary>
public static partial class WhisperNonSpeechFilter
{
    private const int RegexTimeoutMilliseconds = 250;

    [GeneratedRegex(
        @"(?:\[\s*(?:BLANK[\s_-]*AUDIO|MUSIC(?:\s+PLAYING)?|SILENCE|NO[\s_-]*SPEECH|INAUDIBLE|APPLAUSE|LAUGHTER|LAUGHING|SOUND|(?:BACKGROUND[\s_-]*)?NOISE|COUGH(?:ING|S)?|BREATHING|SIGH(?:ING|S)?|TYPING|CLICKING|STATIC|BEEP(?:ING|S)?|BELL(?:S)?|FOOTSTEPS)\s*\]|\(\s*(?:BLANK[\s_-]*AUDIO|MUSIC(?:\s+PLAYING)?|SILENCE|NO[\s_-]*SPEECH|INAUDIBLE|APPLAUSE|LAUGHTER|LAUGHING|SOUND|(?:BACKGROUND[\s_-]*)?NOISE|COUGH(?:ING|S)?|BREATHING|SIGH(?:ING|S)?|TYPING|CLICKING|STATIC|BEEP(?:ING|S)?|BELL(?:S)?|FOOTSTEPS)\s*\)|<\|[^<>\r\n]{1,64}\|>|[\u266A\u266B\u266C][^\u266A\u266B\u266C\r\n]{0,120}[\u266A\u266B\u266C]|[\u266A\u266B\u266C]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex NonSpeechMarkerPattern();

    [GeneratedRegex(@"[ \t\u00A0]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexTimeoutMilliseconds)]
    private static partial Regex HorizontalWhitespacePattern();

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var matches = NonSpeechMarkerPattern().Matches(text);
        if (matches.Count == 0)
        {
            return text.Trim();
        }

        var removedAtLeadingEdge = IsOnlyWhitespaceBefore(text, matches[0].Index);
        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        foreach (Match match in matches)
        {
            if (match.Index > cursor)
            {
                builder.Append(text, cursor, match.Index - cursor);
            }

            if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
            {
                builder.Append(' ');
            }

            cursor = match.Index + match.Length;

            // Consume isolated separator punctuation directly attached to a removed marker
            // (for example, "[BLANK_AUDIO], Hello" or "[MUSIC] - Hello").
            while (cursor < text.Length && (text[cursor] == ',' || text[cursor] == ';' || text[cursor] == ':'))
            {
                if (removedAtLeadingEdge || IsFollowedByWhitespaceOrEnd(text, cursor + 1))
                {
                    cursor++;
                }
                else
                {
                    break;
                }
            }
        }

        if (cursor < text.Length)
        {
            builder.Append(text, cursor, text.Length - cursor);
        }

        var collapsed = HorizontalWhitespacePattern().Replace(builder.ToString(), " ").Trim();
        if (collapsed.Length == 0)
        {
            return string.Empty;
        }

        if (removedAtLeadingEdge)
        {
            collapsed = TrimLeadingStrayPunctuation(collapsed);
        }

        return ContainsLetterOrDigit(collapsed) ? collapsed : string.Empty;
    }

    public static bool ContainsLetterOrDigit(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOnlyWhitespaceBefore(string text, int index)
    {
        for (var i = 0; i < index; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFollowedByWhitespaceOrEnd(string text, int index) =>
        index >= text.Length || char.IsWhiteSpace(text[index]);

    private static string TrimLeadingStrayPunctuation(string text)
    {
        var start = 0;
        while (start < text.Length &&
               (text[start] is ',' or ';' or ':' or '-' or '.' || char.IsWhiteSpace(text[start])))
        {
            start++;
        }

        return start >= text.Length ? string.Empty : text[start..];
    }
}
