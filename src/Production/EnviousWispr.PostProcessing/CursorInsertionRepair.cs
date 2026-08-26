using EnviousWispr.Core.Dictation;
using System.Buffers;
using System.Text;

namespace EnviousWispr.PostProcessing;

public sealed record CursorInsertionRepairResult(
    ProcessedText Output,
    ProcessedText LegacyOutput,
    CursorRepairDisposition Disposition,
    bool RemovedDuplicateWord = false,
    bool DroppedDuplicatePeriod = false,
    bool AddedLeadingSpace = false,
    bool AddedTrailingSpace = false,
    bool RefusedInsideWord = false);

public static class CursorInsertionRepair
{
    private static readonly HashSet<string> UnsegmentedLanguageCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "zh", "ja", "ko", "th", "lo", "km", "my",
        };

    public static CursorInsertionRepairResult Apply(
        ProcessedText input,
        CaretContext? context,
        string? languageCode)
    {
        ArgumentNullException.ThrowIfNull(input);
        var legacy = input with { Text = LegacyPayload(input.Text) };
        if (context is null || !context.HasTextContext)
        {
            return Legacy(legacy);
        }

        var useSpaces = UsesWordSpacing(languageCode, input.Text);
        if (useSpaces && IsInsideWord(context.Left, context.Right))
        {
            return Legacy(legacy, refusedInsideWord: true);
        }

        var leftAnchor = FindLeftAnchor(context.Left);
        if (leftAnchor.Rune is null && context.Right.Length > 0)
        {
            return Legacy(legacy);
        }

        if (context.IsScreenDerived && input.Text.IndexOfAny(['\r', '\n']) >= 0)
        {
            return Legacy(legacy);
        }

        var candidate = input.Text;
        var addedLeading = false;
        if (!context.IsScreenDerived &&
            useSpaces &&
            leftAnchor.Rune is { } anchor &&
            !leftAnchor.CrossedWhitespace &&
            !IsOpeningPunctuation(anchor) &&
            candidate.Length > 0 &&
            !Rune.IsWhiteSpace(Decode(candidate, 0, out _)))
        {
            candidate = " " + candidate;
            addedLeading = true;
        }

        var removedDuplicate = false;
        if (useSpaces &&
            leftAnchor.Rune is { } seam &&
            Rune.IsLetterOrDigit(seam) &&
            TryFindCompleteLeftToken(
                context.Left,
                context.LeftReachedDocumentStart,
                out var leftToken) &&
            TryDropDuplicateSeamToken(
                candidate,
                leftToken,
                leftAnchor.CrossedWhitespace,
                out var deduplicated))
        {
            candidate = deduplicated;
            removedDuplicate = true;
        }

        var droppedPeriod = TryDropDuplicatePeriod(
            candidate,
            context.Right,
            out var withoutDuplicatePeriod);
        if (droppedPeriod)
        {
            candidate = withoutDuplicatePeriod;
        }

        var addedTrailing = false;
        if (!context.IsUrlBarField &&
            useSpaces &&
            NeedsTrailingSpace(candidate, context.Right))
        {
            candidate += " ";
            addedTrailing = true;
        }

        return new CursorInsertionRepairResult(
            input with { Text = candidate },
            legacy,
            CursorRepairDisposition.ContextApplied,
            removedDuplicate,
            droppedPeriod,
            addedLeading,
            addedTrailing);
    }

    private static CursorInsertionRepairResult Legacy(
        ProcessedText legacy,
        bool refusedInsideWord = false) => new(
        legacy,
        legacy,
        CursorRepairDisposition.LegacyPayload,
        RefusedInsideWord: refusedInsideWord);

    private static string LegacyPayload(string text) =>
        text.EndsWith(' ') ? text : text + " ";

    private static bool UsesWordSpacing(string? languageCode, string payload)
    {
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            var separator = languageCode.IndexOfAny(['-', '_']);
            var baseCode = separator < 0 ? languageCode : languageCode[..separator];
            return !UnsegmentedLanguageCodes.Contains(baseCode);
        }

        return !LooksLikeUnsegmentedScript(payload);
    }

    private static bool LooksLikeUnsegmentedScript(string value)
    {
        var relevant = 0;
        var unsegmented = 0;
        for (var index = 0; index < value.Length;)
        {
            var rune = Decode(value, index, out var width);
            index += width;
            if (!Rune.IsLetterOrDigit(rune))
            {
                continue;
            }

            relevant++;
            if (IsUnsegmentedRune(rune))
            {
                unsegmented++;
            }
        }

        return relevant > 0 && unsegmented * 2 >= relevant;
    }

    private static bool IsUnsegmentedRune(Rune rune) =>
        rune.Value is >= 0x3040 and <= 0x30FF or
            >= 0x3400 and <= 0x9FFF or
            >= 0xAC00 and <= 0xD7AF or
            >= 0x0E00 and <= 0x0E7F or
            >= 0x0E80 and <= 0x0EFF or
            >= 0x1000 and <= 0x109F or
            >= 0x1780 and <= 0x17FF;

    private static bool IsInsideWord(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        var leftRune = DecodePrevious(left, left.Length, out var leftWidth);
        var rightRune = Decode(right, 0, out var rightWidth);
        var leftOther = left.Length > leftWidth
            ? DecodePrevious(left, left.Length - leftWidth, out _)
            : (Rune?)null;
        var rightOther = right.Length > rightWidth
            ? Decode(right, rightWidth, out _)
            : (Rune?)null;
        return IsWordSide(leftRune, leftOther) && IsWordSide(rightRune, rightOther);
    }

    private static bool IsWordSide(Rune rune, Rune? otherSide)
    {
        if (Rune.IsLetterOrDigit(rune))
        {
            return true;
        }

        return IsWordConnector(rune) &&
            otherSide is { } other &&
            Rune.IsLetterOrDigit(other);
    }

    private static LeftAnchor FindLeftAnchor(string left)
    {
        var index = left.Length;
        var crossedWhitespace = false;
        while (index > 0)
        {
            var rune = DecodePrevious(left, index, out var width);
            if (IsNewline(rune))
            {
                return new LeftAnchor(null, crossedWhitespace);
            }

            if (!Rune.IsWhiteSpace(rune))
            {
                return new LeftAnchor(rune, crossedWhitespace);
            }

            crossedWhitespace = true;
            index -= width;
        }

        return new LeftAnchor(null, crossedWhitespace);
    }

    private static bool TryFindCompleteLeftToken(
        string left,
        bool reachesDocumentStart,
        out string token)
    {
        token = string.Empty;
        var end = left.Length;
        while (end > 0)
        {
            var rune = DecodePrevious(left, end, out var width);
            if (!Rune.IsWhiteSpace(rune))
            {
                break;
            }

            end -= width;
        }

        var start = end;
        while (start > 0)
        {
            var rune = DecodePrevious(left, start, out var width);
            if (!IsTokenRune(rune))
            {
                break;
            }

            start -= width;
        }

        if (start == end || (start == 0 && !reachesDocumentStart))
        {
            return false;
        }

        var raw = left[start..end];
        if (!TryNormalizeToken(raw, out token) || token.Length != raw.Length)
        {
            token = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryDropDuplicateSeamToken(
        string text,
        string leftToken,
        bool documentOwnsSeparator,
        out string repaired)
    {
        repaired = text;
        var index = 0;
        while (index < text.Length)
        {
            var rune = Decode(text, index, out var width);
            if (!IsHorizontalWhitespace(rune))
            {
                break;
            }

            index += width;
        }

        var leadingWhitespaceEnd = index;
        if (index >= text.Length || IsNewline(Decode(text, index, out _)))
        {
            return false;
        }

        var tokenStart = index;
        while (index < text.Length)
        {
            var rune = Decode(text, index, out var width);
            if (!IsTokenRune(rune))
            {
                break;
            }

            index += width;
        }

        var rawToken = text[tokenStart..index];
        if (!TryNormalizeToken(rawToken, out var token) ||
            token.Length != rawToken.Length ||
            !string.Equals(token, leftToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index >= text.Length)
        {
            return false;
        }

        var separator = Decode(text, index, out var separatorWidth);
        if (!IsHorizontalWhitespace(separator))
        {
            return false;
        }

        index += separatorWidth;
        while (index < text.Length)
        {
            var rune = Decode(text, index, out var width);
            if (!IsHorizontalWhitespace(rune))
            {
                break;
            }

            index += width;
        }

        if (index >= text.Length || IsNewline(Decode(text, index, out _)))
        {
            return false;
        }

        var remainder = text[index..];
        if (!ContainsLetterOrDigit(remainder))
        {
            return false;
        }

        var leading = documentOwnsSeparator ? string.Empty : text[..leadingWhitespaceEnd];
        repaired = leading + remainder;
        return true;
    }

    private static bool TryDropDuplicatePeriod(
        string text,
        string right,
        out string repaired)
    {
        repaired = text;
        if (right.Length == 0 || Decode(right, 0, out _).Value != '.')
        {
            return false;
        }

        var end = text.Length;
        while (end > 0)
        {
            var rune = DecodePrevious(text, end, out var width);
            if (!Rune.IsWhiteSpace(rune))
            {
                if (rune.Value != '.')
                {
                    return false;
                }

                repaired = text.Remove(end - width, width);
                return true;
            }

            end -= width;
        }

        return false;
    }

    private static bool NeedsTrailingSpace(string candidate, string right)
    {
        if (candidate.EndsWith(' '))
        {
            return false;
        }

        if (right.Length == 0)
        {
            return true;
        }

        var firstRight = Decode(right, 0, out _);
        return !Rune.IsWhiteSpace(firstRight) &&
            !IsTrailingSuppressor(firstRight);
    }

    private static bool TryNormalizeToken(string raw, out string token)
    {
        var start = 0;
        var end = raw.Length;
        while (start < end)
        {
            var rune = Decode(raw, start, out var width);
            if (!IsWordConnector(rune))
            {
                break;
            }

            start += width;
        }

        while (end > start)
        {
            var rune = DecodePrevious(raw, end, out var width);
            if (!IsWordConnector(rune))
            {
                break;
            }

            end -= width;
        }

        token = raw[start..end];
        return token.Length > 0 && ContainsLetterOrDigit(token);
    }

    private static bool ContainsLetterOrDigit(string value)
    {
        for (var index = 0; index < value.Length;)
        {
            var rune = Decode(value, index, out var width);
            if (Rune.IsLetterOrDigit(rune))
            {
                return true;
            }

            index += width;
        }

        return false;
    }

    private static bool IsTokenRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune) || IsWordConnector(rune);

    private static bool IsWordConnector(Rune rune) =>
        rune.Value is '\'' or 0x2019 or '-' or 0x2010 or 0x2011 or '_';

    private static bool IsHorizontalWhitespace(Rune rune) =>
        Rune.IsWhiteSpace(rune) && !IsNewline(rune);

    private static bool IsNewline(Rune rune) => rune.Value is '\r' or '\n';

    private static bool IsOpeningPunctuation(Rune rune) =>
        rune.Value is '(' or '[' or '{' or 0x2018 or 0x201C;

    private static bool IsTrailingSuppressor(Rune rune) =>
        rune.Value is '.' or '!' or '?' or ',' or ';' or ':' or ')' or ']' or '}' or
            0x2019 or 0x201D;

    private static Rune Decode(string value, int index, out int width)
    {
        var status = Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out width);
        if (status != OperationStatus.Done)
        {
            width = 1;
            return Rune.ReplacementChar;
        }

        return rune;
    }

    private static Rune DecodePrevious(string value, int end, out int width)
    {
        var start = end - 1;
        if (start > 0 && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1]))
        {
            start--;
        }

        return Decode(value, start, out width);
    }

    private readonly record struct LeftAnchor(Rune? Rune, bool CrossedWhitespace);
}
