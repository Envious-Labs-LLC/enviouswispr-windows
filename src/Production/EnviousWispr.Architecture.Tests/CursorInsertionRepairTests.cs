using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

public sealed class CursorInsertionRepairTests
{
    private static readonly DictationSessionId SessionId = new(Guid.Parse(
        "2bf4f0ee-400f-451d-92c0-b3e939d158eb"));

    [Theory]
    [InlineData("Hello", "Hello ")]
    [InlineData("Hello ", "Hello ")]
    [InlineData("Hello  ", "Hello  ")]
    [InlineData("", " ")]
    public void FallbackPayloadRetainsTrailingSpaceRule(string said, string fallback)
    {
        // THE FALLBACK IS NOT RAW TEXT (plan-2 step 14): it is the words as said with exactly one
        // trailing space added where there was none, so a paste with no caret context can continue
        // a sentence; a trailing space already there is kept, not doubled. Without a context the
        // insertion IS the fallback, and the disposition says so.
        var input = new ProcessedText(SessionId, said);

        var result = CursorInsertionRepair.Apply(input, context: null, "en");

        Assert.Equal(CursorRepairDisposition.FallbackPayload, result.Disposition);
        Assert.Equal(fallback, result.Fallback.Text);
        Assert.Equal(fallback, result.Insertion.Text);
    }

    [Fact]
    public void ContextAdjustedAndFallbackPayloadsRemainDistinct()
    {
        // TWO PAYLOADS, TWO JOBS. With a caret context the insertion is adjusted to the seam - a
        // leading space after "Hello", a trailing one before nothing - while the fallback stays the
        // words as said with the one trailing space: what the clipboard gets if the target refuses
        // at the last moment, where the seam's spacing would be wrong.
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "world"),
            Context(left: "Hello", right: string.Empty),
            "en-US");

        Assert.Equal(CursorRepairDisposition.ContextApplied, result.Disposition);
        Assert.Equal(" world ", result.Insertion.Text);
        Assert.Equal("world ", result.Fallback.Text);
        Assert.NotEqual(result.Insertion.Text, result.Fallback.Text);
    }

    [Fact]
    public void TheDispositionsKeepTheirNumericValues()
    {
        // RENAMED, NOT RENUMBERED: the fallback was "legacy" and is still 0; context-applied is still 1.
        Assert.Equal(0, (int)CursorRepairDisposition.FallbackPayload);
        Assert.Equal(1, (int)CursorRepairDisposition.ContextApplied);
        Assert.Equal(2, Enum.GetValues<CursorRepairDisposition>().Length);
    }

    [Fact]
    public void AddsSpacesAtLexicalSeams()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "world"),
            Context(left: "Hello", right: string.Empty),
            "en-US");

        Assert.Equal(" world ", result.Insertion.Text);
        Assert.True(result.AddedLeadingSpace);
        Assert.True(result.AddedTrailingSpace);
    }

    [Theory]
    [InlineData("Hello ", "world", "", "world ")]
    [InlineData("(", "world", ")", "world")]
    [InlineData("Hello", ", thanks", "", " , thanks ")]
    [InlineData("", "Hello", " world", "Hello ")]
    public void SuppressesSpacesWhereTheExistingSeamOwnsThem(
        string left,
        string input,
        string right,
        string expected)
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, input),
            Context(left, right),
            "en");

        Assert.Equal(expected, result.Insertion.Text);
    }

    [Fact]
    public void UnsegmentedLanguagesDoNotGainSpaces()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "世界"),
            Context(left: "你好", right: "今天"),
            "zh-CN");

        Assert.Equal("世界", result.Insertion.Text);
    }

    [Fact]
    public void RemovesACompleteCrossSeamDuplicate()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "world is ready"),
            Context(left: "hello world", right: string.Empty),
            "en");

        Assert.Equal(" is ready ", result.Insertion.Text);
        Assert.True(result.RemovedDuplicateWord);
    }

    [Fact]
    public void DoesNotDeduplicateAnUnboundedLeftWord()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "world is ready"),
            Context(
                left: "world",
                right: string.Empty,
                leftReachedStart: false),
            "en");

        Assert.Equal(" world is ready ", result.Insertion.Text);
        Assert.False(result.RemovedDuplicateWord);
    }

    [Fact]
    public void UnicodeScalarBoundariesRemainIntact()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "🌍"),
            Context(left: "hello ", right: "again"),
            "en");

        Assert.Equal("🌍 ", result.Insertion.Text);
    }

    [Fact]
    public void RefusesContextualRepairInsideAWord()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "store"),
            Context(left: "the sto", right: "re"),
            "en");

        Assert.Equal(CursorRepairDisposition.FallbackPayload, result.Disposition);
        Assert.True(result.RefusedInsideWord);
        Assert.Equal("store ", result.Insertion.Text);
    }

    [Fact]
    public void DoesNotDeleteAWholeOrPunctuationBoundDuplicate()
    {
        var whole = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "World"),
            Context(left: "hello world", right: ""),
            "en");
        var punctuation = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "World, again"),
            Context(left: "hello world", right: ""),
            "en");

        Assert.False(whole.RemovedDuplicateWord);
        Assert.False(punctuation.RemovedDuplicateWord);
    }

    [Fact]
    public void DropsOnlyAFullStopDuplicatedAtTheRightSeam()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "Done."),
            Context(left: "We are ", right: ". Next"),
            "en");

        Assert.Equal("done", result.Insertion.Text);
        Assert.True(result.DroppedDuplicatePeriod);
        Assert.True(result.LowercasedFirst);

        var afterTerminator = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "Done."),
            Context(left: "Finished. ", right: ". Next"),
            "en");

        Assert.Equal("Done", afterTerminator.Insertion.Text);
        Assert.True(afterTerminator.DroppedDuplicatePeriod);
        Assert.False(afterTerminator.LowercasedFirst);
    }

    [Fact]
    public void TerminalAndUrlBarPoliciesStayNarrow()
    {
        var terminal = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "next"),
            Context(left: "prompt", right: "") with { IsScreenDerived = true },
            "en");
        var urlBar = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "issues"),
            Context(left: "github.com/", right: "") with { IsUrlBarField = true },
            "en");

        Assert.Equal("next ", terminal.Insertion.Text);
        Assert.Equal(" issues", urlBar.Insertion.Text);
    }

    [Theory]
    [InlineData("世界", "zh-CN")]
    [InlineData("こんにちは", "ja")]
    [InlineData("世界", null)]
    public void UnsegmentedLanguagesDoNotGainTrailingSpaceInFallbackPayload(string said, string? languageCode)
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, said),
            context: null,
            languageCode);

        Assert.Equal(CursorRepairDisposition.FallbackPayload, result.Disposition);
        Assert.Equal(said, result.Fallback.Text);
        Assert.Equal(said, result.Insertion.Text);
    }

    [Fact]
    public void SkipsFormatCharactersAndTreatsObjectReplacementAsNonWordBoundary()
    {
        // G0-1: zero-width format characters around the caret are transparent while raw CaretContext stays intact,
        // and U+FFFC never causes word-glue or inside-word refusal.
        var zwspResult = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "Ask them."),
            Context(left: "Can you \u200B", right: "\u200B"),
            "en");
        Assert.Equal(CursorRepairDisposition.ContextApplied, zwspResult.Disposition);
        Assert.Equal("ask them.", zwspResult.Insertion.Text.TrimEnd());
        Assert.True(zwspResult.LowercasedFirst);
        Assert.False(zwspResult.AddedLeadingSpace);

        var zwspTightLeft = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "Ask"),
            Context(left: "Can you\u200B", right: "\u200B."),
            "en");
        Assert.Equal(CursorRepairDisposition.ContextApplied, zwspTightLeft.Disposition);
        Assert.Equal(" ask", zwspTightLeft.Insertion.Text);
        Assert.True(zwspTightLeft.LowercasedFirst);

        var zwspInsideWord = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "mid"),
            Context(left: "hel\u200B", right: "\u200Blo"),
            "en");
        Assert.True(zwspInsideWord.RefusedInsideWord);

        var fffcLeft = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "World"),
            Context(left: "See\uFFFC", right: "\uFFFC"),
            "en");
        Assert.Equal(CursorRepairDisposition.ContextApplied, fffcLeft.Disposition);
        Assert.False(fffcLeft.RefusedInsideWord);
        Assert.True(fffcLeft.AddedLeadingSpace);
        Assert.True(fffcLeft.AddedTrailingSpace);
        Assert.Equal(" World ", fffcLeft.Insertion.Text);
    }

    [Fact]
    public void NeedsTrailingSpaceSuppressesHorizontalSpaceOnlyNeverNewlines()
    {
        // G0-3: \r and \n are line breaks, not horizontal separators, so trailing space is preserved before them.
        var beforeNewline = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "hello"),
            Context(left: "Say ", right: "\nnext line"),
            "en");
        Assert.True(beforeNewline.AddedTrailingSpace);
        Assert.Equal("hello ", beforeNewline.Insertion.Text);

        var beforeCrLf = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "hello"),
            Context(left: "Say ", right: "\r\nnext line"),
            "en");
        Assert.True(beforeCrLf.AddedTrailingSpace);
        Assert.Equal("hello ", beforeCrLf.Insertion.Text);

        var beforeTab = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "hello"),
            Context(left: "Say ", right: "\tnext column"),
            "en");
        Assert.False(beforeTab.AddedTrailingSpace);
        Assert.Equal("hello", beforeTab.Insertion.Text);
    }

    [Fact]
    public void KoreanIsTreatedAsSpaceSeparated()
    {
        // G0-5: Hangul and ko are word-spaced rather than unsegmented.
        var koResult = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "세계"),
            Context(left: "안녕", right: " 오늘"),
            "ko");
        Assert.Equal(CursorRepairDisposition.ContextApplied, koResult.Disposition);
        Assert.True(koResult.AddedLeadingSpace);
        Assert.False(koResult.AddedTrailingSpace);
        Assert.Equal(" 세계", koResult.Insertion.Text);

        var koFallback = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "안녕하세요"),
            context: null,
            "ko");
        Assert.Equal("안녕하세요 ", koFallback.Fallback.Text);
    }

    [Fact]
    public void StraightQuotesHonorOpeningAndClosingDirectionality()
    {
        // G0-6: straight opening quote gets no leading space and keeps capitalization; closing quote adds space.
        var afterOpening = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "Hello"),
            Context(left: "He said \"", right: "\""),
            "en");
        Assert.False(afterOpening.AddedLeadingSpace);
        Assert.False(afterOpening.AddedTrailingSpace);
        Assert.False(afterOpening.LowercasedFirst);
        Assert.Equal(CaseRepairDecision.KeptAfterOpener, afterOpening.CaseDecision);
        Assert.Equal("Hello", afterOpening.Insertion.Text);

        var afterClosing = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "And then"),
            Context(left: "\"Hello\"", right: string.Empty),
            "en");
        Assert.True(afterClosing.AddedLeadingSpace);
        Assert.Equal(" And then ", afterClosing.Insertion.Text);

        var beforeOpening = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "to"),
            Context(left: "spoken ", right: "\"hello\""),
            "en");
        Assert.True(beforeOpening.AddedTrailingSpace);
        Assert.Equal("to ", beforeOpening.Insertion.Text);
    }

    [Fact]
    public void LeadingCaseRepairRunsBeforeSeamDeduplication()
    {
        // G0-8: "The" lowers to "the" and drops duplicate "the ", preserving "Review" capitalized cleanly.
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "The Review is ready."),
            Context(left: "Go to the ", right: string.Empty),
            "en");

        Assert.Equal("Review is ready. ", result.Insertion.Text);
        Assert.True(result.LowercasedFirst);
        Assert.Equal(CaseRepairDecision.LowercasedFirst, result.CaseDecision);
        Assert.True(result.RemovedDuplicateWord);
        Assert.True(result.CandidateDeletesDictatedText);
    }

    [Fact]
    public void SmartInsertionDisabledReturnsFallbackPayloadWithoutModifyingText()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "Ask them."),
            Context(left: "Can you ", right: "please"),
            "en",
            protectedWords: null,
            smartInsertionEnabled: false);

        Assert.Equal(CursorRepairDisposition.FallbackPayload, result.Disposition);
        Assert.Equal(CaseRepairDecision.SkippedSmartInsertionDisabled, result.CaseDecision);
        Assert.False(result.LowercasedFirst);
        Assert.False(result.AddedLeadingSpace);
        Assert.Equal("Ask them. ", result.Insertion.Text);
    }

    private static CaretContext Context(
        string left,
        string right,
        bool leftReachedStart = true) => new(
        new TargetWindowId(42, 7, "1.2.3"),
        "1.2.3",
        TextTargetKind.StandardEdit,
        left,
        Selection: string.Empty,
        right,
        leftReachedStart,
        RightReachedDocumentEnd: right.Length == 0,
        HasTextContext: true,
        SupportsDirectValueWrite: true,
        DirectValueWriteAtEnd: right.Length == 0);
}
