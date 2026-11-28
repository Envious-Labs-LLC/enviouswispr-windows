using System;
using System.Collections.Generic;

namespace EnviousWispr.Services.Input;

internal enum TerminalBoxOpeningKind
{
    Plain,
    Titled,
}

internal enum TerminalBoxClass
{
    Light,
    Block,
}

internal readonly record struct TerminalBoundaryMatch(
    TerminalBoxClass BoxClass,
    TerminalBoxOpeningKind OpeningKind);

internal enum TerminalBoundaryUse
{
    Opening,
    Closing,
}

internal sealed record TerminalLocatedInput(
    SupportedWindowsTerminalCli Cli,
    string InputLine,
    bool LeftWasCut = false,
    TerminalBoxOpeningKind? BoxOpeningKind = null);

internal enum TerminalCodexRefusal
{
    None,
    NoOpeningMarker,
    TooManyPopulatedRowsBelow,
    LivePromptBelow,
    EmptyInput,
    PlaceholderInput,
    AmbiguousTrailingBlankRow,
}

internal enum TerminalBoxedRefusal
{
    None,
    FewerThanTwoBoundaries,
    IncompatibleBoundaries,
    ZeroPopulatedBodyRows,
    LivePromptBelow,
    WrongOpeningMarker,
    EmptyInput,
    AmbiguousTrailingBlankRow,
}

internal sealed record TerminalParseRefusal(
    TerminalCodexRefusal Codex,
    TerminalBoxedRefusal Boxed);

internal sealed record TerminalParseResult(
    TerminalLocatedInput? Located,
    TerminalParseRefusal? Refusal);

/// <summary>
/// Gate 2 screen parser locating supported CLI prompt input lines (Codex, Claude Code, Gemini CLI)
/// while strictly refusing bare shells, scrollback prompts, placeholder text, and ambiguous wraps.
/// </summary>
internal static class WindowsTerminalScreenParser
{
    internal const char CodexMarker = '\u203A';
    internal const char ClaudeMarker = '\u276F';
    internal const char GeminiMarker = '>';
    internal const string CodexPlaceholderText = "Ask Codex to do anything";

    private static readonly HashSet<char> LightBoxGlyphs = ['\u2500', '\u2501', '\u2550'];
    private static readonly HashSet<char> BlockBoxGlyphs = ['\u2584', '\u2580', '\u2588'];
    private static readonly HashSet<char> ShellPromptMarkers = ['\u276F', '\u279C', '$', '%', '#'];

    internal static bool IsBoxDrawingGlyph(char c) =>
        c is >= '\u2500' and <= '\u259F';

    internal static bool IsCodexPlaceholder(string? textAfterMarker)
    {
        if (string.IsNullOrWhiteSpace(textAfterMarker))
        {
            return false;
        }

        var trimmed = textAfterMarker.Trim().TrimEnd('.', '\u2026').TrimEnd();
        return string.Equals(trimmed, CodexPlaceholderText, StringComparison.OrdinalIgnoreCase);
    }

    internal static char MarkerFor(SupportedWindowsTerminalCli cli) => cli switch
    {
        SupportedWindowsTerminalCli.Codex => CodexMarker,
        SupportedWindowsTerminalCli.ClaudeCode => ClaudeMarker,
        SupportedWindowsTerminalCli.GeminiCli => GeminiMarker,
        _ => CodexMarker,
    };

    internal static TerminalLocatedInput? Locate(string? screenTail) =>
        LocateDetailed(screenTail).Located;

    internal static TerminalParseResult LocateDetailed(string? screenTail)
    {
        if (string.IsNullOrEmpty(screenTail))
        {
            return new TerminalParseResult(
                null,
                new TerminalParseRefusal(
                    TerminalCodexRefusal.NoOpeningMarker,
                    TerminalBoxedRefusal.FewerThanTwoBoundaries));
        }

        var normalized = screenTail.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var rows = normalized.Split('\n');

        var (codexLocated, codexRefusal) = LocateCodex(rows);
        if (codexLocated is not null)
        {
            return new TerminalParseResult(codexLocated, null);
        }

        var (boxedLocated, boxedRefusal) = LocateBoxed(rows);
        if (boxedLocated is not null)
        {
            return new TerminalParseResult(boxedLocated, null);
        }

        return new TerminalParseResult(null, new TerminalParseRefusal(codexRefusal, boxedRefusal));
    }

    internal static TerminalBoundaryMatch? MatchBoundary(string row, TerminalBoundaryUse use)
    {
        var trimmed = TrimBoxWhitespace(row);
        if (trimmed.Length < 4)
        {
            return null;
        }

        var glyph = trimmed[0];
        TerminalBoxClass boxClass;
        if (LightBoxGlyphs.Contains(glyph))
        {
            boxClass = TerminalBoxClass.Light;
        }
        else if (BlockBoxGlyphs.Contains(glyph))
        {
            boxClass = TerminalBoxClass.Block;
        }
        else
        {
            return null;
        }

        var allSame = true;
        for (var i = 1; i < trimmed.Length; i++)
        {
            if (trimmed[i] != glyph)
            {
                allSame = false;
                break;
            }
        }

        if (allSame)
        {
            return new TerminalBoundaryMatch(boxClass, TerminalBoxOpeningKind.Plain);
        }

        if (use != TerminalBoundaryUse.Opening ||
            boxClass != TerminalBoxClass.Light ||
            trimmed[^1] != glyph)
        {
            return null;
        }

        var leading = 0;
        while (leading < trimmed.Length && trimmed[leading] == glyph)
        {
            leading++;
        }

        var trailing = 0;
        while (trailing < trimmed.Length && trimmed[trimmed.Length - 1 - trailing] == glyph)
        {
            trailing++;
        }

        if (leading < 4 || trailing is < 1 or > 2 || leading + trailing >= trimmed.Length)
        {
            return null;
        }

        var middle = trimmed.AsSpan(leading, trimmed.Length - leading - trailing);
        if (middle.Length < 3)
        {
            return null;
        }

        var opensPadding = middle[0];
        var closesPadding = middle[^1];
        if ((opensPadding != ' ' && opensPadding != '\u00A0') ||
            (closesPadding != ' ' && closesPadding != '\u00A0'))
        {
            return null;
        }

        var hasTitleContent = false;
        for (var i = 0; i < middle.Length; i++)
        {
            var ch = middle[i];
            if (IsBoxDrawingGlyph(ch))
            {
                return null;
            }

            if (!char.IsWhiteSpace(ch) && ch != '\u00A0')
            {
                hasTitleContent = true;
            }
        }

        return hasTitleContent
            ? new TerminalBoundaryMatch(boxClass, TerminalBoxOpeningKind.Titled)
            : null;
    }

    internal static bool IsBlank(string row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            var ch = row[i];
            if (!char.IsWhiteSpace(ch) && ch != '\u00A0')
            {
                return false;
            }
        }

        return true;
    }

    internal static char? OpeningCharacter(string row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            var ch = row[i];
            if (!char.IsWhiteSpace(ch) && ch != '\u00A0')
            {
                return ch;
            }
        }

        return null;
    }

    internal static string StripLeadingMarker(string row, char marker)
    {
        var i = 0;
        while (i < row.Length && (char.IsWhiteSpace(row[i]) || row[i] == '\u00A0'))
        {
            i++;
        }

        if (i < row.Length && row[i] == marker)
        {
            i++;
            if (i < row.Length && (row[i] == ' ' || row[i] == '\u00A0'))
            {
                i++;
            }
        }

        return i == 0
            ? row.Replace('\u00A0', ' ')
            : row[i..].Replace('\u00A0', ' ');
    }

    internal static string StripContinuationGutter(string row)
    {
        var i = 0;
        while (i < row.Length && (char.IsWhiteSpace(row[i]) || row[i] == '\u00A0'))
        {
            i++;
        }

        return (i == 0 ? row : row[i..]).Replace('\u00A0', ' ');
    }

    private static int? IndentWidth(string row)
    {
        var count = 0;
        while (count < row.Length && (char.IsWhiteSpace(row[count]) || row[count] == '\u00A0'))
        {
            count++;
        }

        return count == row.Length ? null : count;
    }

    private static bool ContinuesInput(string row, int? gutter)
    {
        if (IsBlank(row))
        {
            return true;
        }

        if (!gutter.HasValue)
        {
            return false;
        }

        var indent = IndentWidth(row);
        return indent.HasValue && indent.Value >= gutter.Value;
    }

    private static int? MarkerTextColumn(string row, char marker)
    {
        var indent = IndentWidth(row);
        if (!indent.HasValue)
        {
            return null;
        }

        var index = indent.Value;
        if (index >= row.Length || row[index] != marker)
        {
            return null;
        }

        var column = index + 1;
        if (column < row.Length && (row[column] == ' ' || row[column] == '\u00A0'))
        {
            column++;
        }

        return column;
    }

    private static bool ALivePromptSitsBelow(int index, string[] rows)
    {
        if (index + 1 >= rows.Length)
        {
            return false;
        }

        for (var i = index + 1; i < rows.Length; i++)
        {
            var row = rows[i];
            if (IsBlank(row))
            {
                continue;
            }

            if (LooksLikeBareShellPrompt(row))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool LooksLikeBareShellPrompt(string row)
    {
        var opener = OpeningCharacter(row);
        if (opener.HasValue && ShellPromptMarkers.Contains(opener.Value))
        {
            return true;
        }

        var trimmed = TrimBoxWhitespace(row);
        if (trimmed.Length == 0)
        {
            return false;
        }

        // PowerShell ("PS C:\...> ") or CMD ("C:\...>") bare prompts below a closed CLI session.
        if (trimmed.StartsWith("PS ", StringComparison.Ordinal) &&
            trimmed.Contains('>', StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.Length >= 4 &&
            char.IsAsciiLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed[2] == '\\' || trimmed[2] == '/') &&
            trimmed.EndsWith('>'))
        {
            return true;
        }

        return false;
    }

    private static (TerminalLocatedInput? Located, TerminalCodexRefusal Refusal) LocateCodex(string[] rows)
    {
        var index = -1;
        for (var i = rows.Length - 1; i >= 0; i--)
        {
            if (OpeningCharacter(rows[i]) == CodexMarker)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return (null, TerminalCodexRefusal.NoOpeningMarker);
        }

        var gutter = MarkerTextColumn(rows[index], CodexMarker);
        int? separator = null;
        var statusRows = 0;

        var lastPopulated = -1;
        for (var i = rows.Length - 1; i >= 0; i--)
        {
            if (!IsBlank(rows[i]))
            {
                lastPopulated = i;
                break;
            }
        }

        if (lastPopulated > index)
        {
            var probe = lastPopulated;
            while (probe > index)
            {
                if (IsBlank(rows[probe]))
                {
                    separator = probe;
                    break;
                }

                statusRows++;
                probe--;
            }
        }

        var blockEnd = index;
        if (separator.HasValue && statusRows <= 2)
        {
            var allContinue = true;
            for (var i = index + 1; i < separator.Value; i++)
            {
                if (!ContinuesInput(rows[i], gutter))
                {
                    allContinue = false;
                    break;
                }
            }

            if (allContinue)
            {
                if (separator.Value - 1 > index && IsBlank(rows[separator.Value - 1]))
                {
                    return (null, TerminalCodexRefusal.AmbiguousTrailingBlankRow);
                }

                blockEnd = separator.Value - 1 >= index + 1 ? separator.Value - 1 : index;
            }
        }

        var populatedBelow = 0;
        for (var i = blockEnd + 1; i < rows.Length; i++)
        {
            if (!IsBlank(rows[i]))
            {
                populatedBelow++;
            }
        }

        if (populatedBelow > 2)
        {
            return (null, TerminalCodexRefusal.TooManyPopulatedRowsBelow);
        }

        if (ALivePromptSitsBelow(blockEnd, rows))
        {
            return (null, TerminalCodexRefusal.LivePromptBelow);
        }

        var line = blockEnd == index
            ? StripLeadingMarker(rows[index], CodexMarker)
            : StripContinuationGutter(rows[blockEnd]);
        if (string.IsNullOrWhiteSpace(line))
        {
            return (null, TerminalCodexRefusal.EmptyInput);
        }

        if (blockEnd == index && IsCodexPlaceholder(line))
        {
            return (null, TerminalCodexRefusal.PlaceholderInput);
        }

        return (
            new TerminalLocatedInput(
                SupportedWindowsTerminalCli.Codex,
                line,
                LeftWasCut: blockEnd != index),
            TerminalCodexRefusal.None);
    }

    private static (TerminalLocatedInput? Located, TerminalBoxedRefusal Refusal) LocateBoxed(string[] rows)
    {
        var closingIndex = -1;
        TerminalBoundaryMatch closingMatch = default;
        for (var i = rows.Length - 1; i >= 0; i--)
        {
            var match = MatchBoundary(rows[i], TerminalBoundaryUse.Closing);
            if (match.HasValue)
            {
                closingIndex = i;
                closingMatch = match.Value;
                break;
            }
        }

        if (closingIndex < 0)
        {
            return (null, TerminalBoxedRefusal.FewerThanTwoBoundaries);
        }

        var openingIndex = -1;
        TerminalBoundaryMatch openingMatch = default;
        for (var i = closingIndex - 1; i >= 0; i--)
        {
            var match = MatchBoundary(rows[i], TerminalBoundaryUse.Opening);
            if (match.HasValue)
            {
                openingIndex = i;
                openingMatch = match.Value;
                break;
            }
        }

        if (openingIndex < 0)
        {
            return (null, TerminalBoxedRefusal.FewerThanTwoBoundaries);
        }

        if (openingMatch.BoxClass != closingMatch.BoxClass || closingIndex <= openingIndex + 1)
        {
            return (null, TerminalBoxedRefusal.IncompatibleBoundaries);
        }

        var bodyCount = closingIndex - openingIndex - 1;
        var firstPopulated = -1;
        var lastPopulated = -1;
        var populatedCount = 0;

        for (var offset = 0; offset < bodyCount; offset++)
        {
            if (!IsBlank(rows[openingIndex + 1 + offset]))
            {
                if (firstPopulated < 0)
                {
                    firstPopulated = offset;
                }

                lastPopulated = offset;
                populatedCount++;
            }
        }

        if (firstPopulated < 0)
        {
            return (null, TerminalBoxedRefusal.ZeroPopulatedBodyRows);
        }

        if (ALivePromptSitsBelow(closingIndex, rows))
        {
            return (null, TerminalBoxedRefusal.LivePromptBelow);
        }

        var cli = closingMatch.BoxClass == TerminalBoxClass.Block
            ? SupportedWindowsTerminalCli.GeminiCli
            : SupportedWindowsTerminalCli.ClaudeCode;
        var marker = MarkerFor(cli);

        if (OpeningCharacter(rows[openingIndex + 1 + firstPopulated]) != marker)
        {
            return (null, TerminalBoxedRefusal.WrongOpeningMarker);
        }

        if (populatedCount == 1 &&
            string.IsNullOrWhiteSpace(StripLeadingMarker(rows[openingIndex + 1 + firstPopulated], marker)))
        {
            return (null, TerminalBoxedRefusal.EmptyInput);
        }

        if (lastPopulated != bodyCount - 1)
        {
            return (null, TerminalBoxedRefusal.AmbiguousTrailingBlankRow);
        }

        var lastRow = rows[openingIndex + 1 + lastPopulated];
        var line = lastPopulated == firstPopulated
            ? StripLeadingMarker(lastRow, marker)
            : StripContinuationGutter(lastRow);
        if (string.IsNullOrWhiteSpace(line))
        {
            return (null, TerminalBoxedRefusal.EmptyInput);
        }

        return (
            new TerminalLocatedInput(
                cli,
                line,
                LeftWasCut: lastPopulated != firstPopulated,
                BoxOpeningKind: openingMatch.OpeningKind),
            TerminalBoxedRefusal.None);
    }

    private static string TrimBoxWhitespace(string row)
    {
        var start = 0;
        while (start < row.Length && (char.IsWhiteSpace(row[start]) || row[start] == '\u00A0'))
        {
            start++;
        }

        var end = row.Length - 1;
        while (end >= start && (char.IsWhiteSpace(row[end]) || row[end] == '\u00A0'))
        {
            end--;
        }

        return start > end ? string.Empty : row.Substring(start, end - start + 1);
    }
}
