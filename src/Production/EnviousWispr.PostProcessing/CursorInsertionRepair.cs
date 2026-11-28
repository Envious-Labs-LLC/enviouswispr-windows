using System.Buffers;
using System.Globalization;
using System.Text;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.PostProcessing;

/// <summary>The two payloads a repair produces, and what was done to reach them.</summary>
/// <param name="Insertion">The insertion adjusted to the caret's context - or the fallback, when no context was applied.</param>
/// <param name="Fallback">The fallback payload: the words as said, with a trailing space where they had none. Never context-adjusted.</param>
public sealed record CursorInsertionRepairResult(
    ProcessedText Insertion,
    ProcessedText Fallback,
    CursorRepairDisposition Disposition,
    bool RemovedDuplicateWord = false,
    bool DroppedDuplicatePeriod = false,
    bool AddedLeadingSpace = false,
    bool AddedTrailingSpace = false,
    bool RefusedInsideWord = false,
    bool LowercasedFirst = false,
    CaseRepairDecision CaseDecision = CaseRepairDecision.None,
    bool CandidateDeletesDictatedText = false);

public static class CursorInsertionRepair
{
    private const int ObjectReplacementChar = 0xFFFC;

    private static readonly HashSet<string> UnsegmentedLanguageCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "zh", "ja", "th", "lo", "km", "my",
        };

    public static CursorInsertionRepairResult Apply(
        ProcessedText input,
        CaretContext? context,
        string? languageCode) =>
        Apply(
            input,
            context,
            languageCode,
            protectedWords: null,
            smartInsertionEnabled: true,
            oracle: null);

    public static CursorInsertionRepairResult Apply(
        ProcessedText input,
        CaretContext? context,
        string? languageCode,
        IReadOnlyList<string>? protectedWords = null,
        bool smartInsertionEnabled = true,
        ISeamCasingOracle? oracle = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var useSpaces = UsesWordSpacing(languageCode, input.Text);
        var fallback = input with { Text = FallbackPayload(input.Text, useSpaces) };

        if (!smartInsertionEnabled)
        {
            return Fallback(fallback, caseDecision: CaseRepairDecision.SkippedSmartInsertionDisabled);
        }

        if (context is null || !context.HasTextContext)
        {
            return Fallback(fallback);
        }

        if (useSpaces && IsInsideWord(context.Left, context.Right))
        {
            return Fallback(fallback, refusedInsideWord: true);
        }

        var leftAnchor = FindLeftAnchor(context.Left);
        if (leftAnchor.Rune is null && context.Right.Length > 0)
        {
            var boundaryDecision = leftAnchor.AtLineStart && HasNonFormatCharacters(context.Left)
                ? CaseRepairDecision.KeptLineStart
                : CaseRepairDecision.KeptNothingLeft;
            return Fallback(fallback, caseDecision: boundaryDecision);
        }

        if (context.IsScreenDerived && input.Text.IndexOfAny(['\r', '\n']) >= 0)
        {
            return Fallback(fallback);
        }

        var candidate = input.Text;
        var addedLeading = false;
        if (!context.IsScreenDerived &&
            useSpaces &&
            leftAnchor.Rune is { } anchor &&
            !leftAnchor.CrossedWhitespace &&
            !leftAnchor.IsOpener &&
            TryFindFirstVisibleRune(candidate, out var firstVisibleRune, out _) &&
            !Rune.IsWhiteSpace(firstVisibleRune))
        {
            candidate = " " + candidate;
            addedLeading = true;
        }

        var effectiveOracle = oracle ?? SeamCasingOracleRuntime.Shared;
        var protectedSet = protectedWords is { Count: > 0 }
            ? new HashSet<string>(protectedWords.Where(static w => !string.IsNullOrWhiteSpace(w)), StringComparer.OrdinalIgnoreCase)
            : null;
        var (casedCandidate, lowercasedFirst, caseDecision) = ApplyLeadingCase(
            candidate,
            context.Left,
            leftAnchor,
            context.IsScreenDerived,
            protectedSet,
            effectiveOracle,
            languageCode);
        candidate = casedCandidate;

        // Seam de-duplication intentionally runs AFTER leading continuation casing so "the " + "The Review is ready."
        // lowers "The" to "the" and removes the duplicate without wrongly retargeting casing onto "Review".
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

        var candidateDeletesDictatedText = removedDuplicate || droppedPeriod;
        return new CursorInsertionRepairResult(
            input with { Text = candidate },
            fallback,
            CursorRepairDisposition.ContextApplied,
            removedDuplicate,
            droppedPeriod,
            addedLeading,
            addedTrailing,
            RefusedInsideWord: false,
            LowercasedFirst: lowercasedFirst,
            CaseDecision: caseDecision,
            CandidateDeletesDictatedText: candidateDeletesDictatedText);
    }

    private static (string Text, bool LowercasedFirst, CaseRepairDecision Decision) ApplyLeadingCase(
        string text,
        string leftWindow,
        LeftAnchor leftAnchor,
        bool isScreenDerived,
        IReadOnlySet<string>? protectedWords,
        ISeamCasingOracle oracle,
        string? languageCode)
    {
        var continuing = leftAnchor.Rune is { } anchor &&
            !leftAnchor.AtLineStart &&
            !leftAnchor.IsOpener &&
            (Rune.IsLetterOrDigit(anchor) || IsContinuer(anchor));

        if (!continuing)
        {
            if (leftAnchor.Rune is null)
            {
                var atLineStart = leftAnchor.AtLineStart && HasNonFormatCharacters(leftWindow);
                return (text, false, atLineStart ? CaseRepairDecision.KeptLineStart : CaseRepairDecision.KeptNothingLeft);
            }

            if (leftAnchor.IsOpener)
            {
                return (text, false, CaseRepairDecision.KeptAfterOpener);
            }

            if (IsSentenceTerminator(leftAnchor.Rune.Value))
            {
                return (text, false, CaseRepairDecision.KeptAfterTerminator);
            }

            return (text, false, CaseRepairDecision.KeptNonContinuation);
        }

        var baseCode = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseCode is null || oracle.PolicyFor(baseCode) is null)
        {
            return (text, false, CaseRepairDecision.SkippedLanguageNotSupported);
        }

        var leadingBoundary = FindLeadingVisibleOffset(text);
        var leadingSegment = text[..leadingBoundary];
        var stripped = text[leadingBoundary..];
        if (string.IsNullOrEmpty(stripped))
        {
            return (text, false, CaseRepairDecision.AlreadyLower);
        }

        var cleanStripped = ArmBContextVeto.StripFormatCharacters(stripped);
        var bare = SeamCasingPolicies.ExtractBareFirstWord(cleanStripped);
        var guardFailure = SeamCasingPolicies.CheckDeterministicGuard(cleanStripped, bare, baseCode, protectedWords);
        if (guardFailure is { } skipped)
        {
            return (text, false, skipped);
        }

        var cleanLeft = ArmBContextVeto.StripFormatCharacters(leftWindow);
        var cleanLeading = ArmBContextVeto.StripFormatCharacters(leadingSegment);
        var taggerLeft = cleanLeft + cleanLeading;

        var verdict = oracle.Evaluate(bare, taggerLeft, cleanStripped, baseCode);
        if (verdict.Kind != SeamCasingVerdictKind.MayLower)
        {
            return (text, false, verdict.Decision);
        }

        if (isScreenDerived && taggerLeft.Length > 0 && !char.IsWhiteSpace(taggerLeft[^1]))
        {
            var separatedVerdict = oracle.Evaluate(bare, taggerLeft + " ", cleanStripped, baseCode);
            if (separatedVerdict.Kind != SeamCasingVerdictKind.MayLower)
            {
                return (text, false, separatedVerdict.Decision);
            }
        }

        var lowered = SeamCasingPolicies.LoweringLeadingUnit(text, baseCode);
        var changed = !string.Equals(lowered, text, StringComparison.Ordinal);
        return (lowered, changed, changed ? CaseRepairDecision.LowercasedFirst : CaseRepairDecision.AlreadyLower);
    }

    /// <summary>The result when no context is applied: the fallback payload is both the insertion and the fallback.</summary>
    private static CursorInsertionRepairResult Fallback(
        ProcessedText fallback,
        bool refusedInsideWord = false,
        CaseRepairDecision caseDecision = CaseRepairDecision.None) => new(
        fallback,
        fallback,
        CursorRepairDisposition.FallbackPayload,
        RefusedInsideWord: refusedInsideWord,
        CaseDecision: caseDecision);

    /// <summary>The fallback payload: the words as said, with one trailing space in space-separated languages so a paste can continue a sentence. Not raw text, and never context-adjusted.</summary>
    private static string FallbackPayload(string text, bool usesWordSpacing) =>
        !usesWordSpacing || text.EndsWith(' ') ? text : text + " ";

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
            >= 0x0E00 and <= 0x0E7F or
            >= 0x0E80 and <= 0x0EFF or
            >= 0x1000 and <= 0x109F or
            >= 0x1780 and <= 0x17FF;

    private static bool IsInsideWord(string left, string right)
    {
        if (!TryDecodeLastVisible(left, left.Length, out var leftRune, out var leftStart) ||
            !TryDecodeFirstVisible(right, 0, out var rightRune, out var rightNext))
        {
            return false;
        }

        var leftOther = TryDecodeLastVisible(left, leftStart, out var prev, out _)
            ? prev
            : (Rune?)null;
        var rightOther = TryDecodeFirstVisible(right, rightNext, out var next, out _)
            ? next
            : (Rune?)null;
        return IsWordSide(leftRune, leftOther) && IsWordSide(rightRune, rightOther);
    }

    private static bool IsWordSide(Rune rune, Rune? otherSide)
    {
        if (rune.Value == ObjectReplacementChar)
        {
            return false;
        }

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
            var startIndex = index - width;
            if (IsInvisibleFormat(rune))
            {
                index = startIndex;
                continue;
            }

            if (IsNewline(rune))
            {
                return new LeftAnchor(null, crossedWhitespace, AtLineStart: true, IsOpener: false);
            }

            if (!Rune.IsWhiteSpace(rune))
            {
                var isOpener = IsOpeningPunctuation(rune) ||
                    (IsAmbiguousQuote(rune) && IsOpeningQuote(left, startIndex));
                return new LeftAnchor(rune, crossedWhitespace, AtLineStart: false, IsOpener: isOpener);
            }

            crossedWhitespace = true;
            index = startIndex;
        }

        return new LeftAnchor(null, crossedWhitespace, AtLineStart: true, IsOpener: false);
    }

    internal static bool IsOpeningQuote(string left, int quoteIndex)
    {
        if (!TryDecodeLastVisible(left, quoteIndex, out var preceding, out _))
        {
            return true;
        }

        return Rune.IsWhiteSpace(preceding) ||
            IsOpeningPunctuation(preceding) ||
            IsQuoteIntroducer(preceding);
    }

    internal static bool IsClosingQuoteAhead(string right, int quoteIndex, Rune quoteRune)
    {
        if (!IsAmbiguousQuote(quoteRune))
        {
            return false;
        }

        var nextIndex = quoteIndex + quoteRune.Utf16SequenceLength;
        if (!TryDecodeFirstVisible(right, nextIndex, out var following, out _))
        {
            return true;
        }

        return !Rune.IsLetterOrDigit(following);
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
            if (!Rune.IsWhiteSpace(rune) && !IsInvisibleFormat(rune))
            {
                break;
            }

            end -= width;
        }

        var start = end;
        while (start > 0)
        {
            var rune = DecodePrevious(left, start, out var width);
            if (!IsTokenRune(rune) && !IsInvisibleFormat(rune))
            {
                break;
            }

            start -= width;
        }

        var trimmedStart = start;
        var reachedStartWithoutBoundary = trimmedStart == 0;
        var visibleBeforeStart = TryDecodeLastVisible(left, start, out _, out _);
        if (start == end || ((reachedStartWithoutBoundary || !visibleBeforeStart) && !reachesDocumentStart))
        {
            return false;
        }

        var raw = ArmBContextVeto.StripFormatCharacters(left[start..end]);
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
            if (!IsHorizontalWhitespace(rune) && !IsInvisibleFormat(rune))
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
            if (!IsTokenRune(rune) && !IsInvisibleFormat(rune))
            {
                break;
            }

            index += width;
        }

        var rawToken = ArmBContextVeto.StripFormatCharacters(text[tokenStart..index]);
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
            if (!IsHorizontalWhitespace(rune) && !IsInvisibleFormat(rune))
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
        if (!TryDecodeFirstVisible(right, 0, out var firstRight, out _) || firstRight.Value != '.')
        {
            return false;
        }

        var end = text.Length;
        while (end > 0)
        {
            var rune = DecodePrevious(text, end, out var width);
            if (!Rune.IsWhiteSpace(rune) && !IsInvisibleFormat(rune))
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

        if (!TryFindFirstVisibleRuneIndex(right, 0, out var rightIndex, out var firstRight))
        {
            return true;
        }

        return !IsHorizontalWhitespace(firstRight) &&
            !IsTrailingSuppressor(firstRight) &&
            !IsClosingQuoteAhead(right, rightIndex, firstRight);
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

    private static int FindLeadingVisibleOffset(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var rune = Decode(text, index, out var width);
            if (!Rune.IsWhiteSpace(rune) && !IsInvisibleFormat(rune))
            {
                break;
            }

            index += width;
        }

        return index;
    }

    private static bool TryFindFirstVisibleRune(string value, out Rune rune, out int nextIndex) =>
        TryDecodeFirstVisible(value, 0, out rune, out nextIndex);

    private static bool TryFindFirstVisibleRuneIndex(string value, int startIndex, out int runeIndex, out Rune rune)
    {
        var index = startIndex;
        while (index < value.Length)
        {
            var decoded = Decode(value, index, out var width);
            if (!IsInvisibleFormat(decoded))
            {
                runeIndex = index;
                rune = decoded;
                return true;
            }

            index += width;
        }

        runeIndex = value.Length;
        rune = default;
        return false;
    }

    private static bool TryDecodeFirstVisible(string value, int startIndex, out Rune rune, out int nextIndex)
    {
        var index = startIndex;
        while (index < value.Length)
        {
            var decoded = Decode(value, index, out var width);
            index += width;
            if (!IsInvisibleFormat(decoded))
            {
                rune = decoded;
                nextIndex = index;
                return true;
            }
        }

        rune = default;
        nextIndex = value.Length;
        return false;
    }

    private static bool TryDecodeLastVisible(string value, int endIndex, out Rune rune, out int startIndex)
    {
        var index = endIndex;
        while (index > 0)
        {
            var decoded = DecodePrevious(value, index, out var width);
            index -= width;
            if (!IsInvisibleFormat(decoded))
            {
                rune = decoded;
                startIndex = index;
                return true;
            }
        }

        rune = default;
        startIndex = 0;
        return false;
    }

    private static bool HasNonFormatCharacters(string value)
    {
        for (var index = 0; index < value.Length;)
        {
            var rune = Decode(value, index, out var width);
            if (!IsInvisibleFormat(rune))
            {
                return true;
            }

            index += width;
        }

        return false;
    }

    private static bool IsInvisibleFormat(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;

    private static bool IsTokenRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune) || IsWordConnector(rune);

    private static bool IsWordConnector(Rune rune) =>
        rune.Value is '\'' or 0x2019 or '-' or 0x2010 or 0x2011 or '_';

    private static bool IsHorizontalWhitespace(Rune rune) =>
        Rune.IsWhiteSpace(rune) && !IsNewline(rune);

    private static bool IsNewline(Rune rune) => rune.Value is '\r' or '\n';

    private static bool IsSentenceTerminator(Rune rune) =>
        rune.Value is '.' or '!' or '?';

    private static bool IsContinuer(Rune rune) =>
        rune.Value is ',' or ';' or ':' or '-' or 0x2014;

    private static bool IsOpeningPunctuation(Rune rune) =>
        rune.Value is '(' or '[' or '{' or 0x2018 or 0x201C;

    private static bool IsAmbiguousQuote(Rune rune) =>
        rune.Value is '"' or '\'';

    private static bool IsQuoteIntroducer(Rune rune) =>
        rune.Value is ':' or ';' or 0x2014 or 0x2013 or '-';

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

    private static Rune DecodePrevious(string value, int endIndex, out int width)
    {
        var slice = value.AsSpan(0, endIndex);
        var status = Rune.DecodeLastFromUtf16(slice, out var rune, out width);
        if (status != OperationStatus.Done)
        {
            width = 1;
            return Rune.ReplacementChar;
        }

        return rune;
    }

    private readonly record struct LeftAnchor(Rune? Rune, bool CrossedWhitespace, bool AtLineStart, bool IsOpener);
}
