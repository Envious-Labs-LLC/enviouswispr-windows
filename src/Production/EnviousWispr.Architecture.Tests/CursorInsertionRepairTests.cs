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

        Assert.Equal("Done", result.Insertion.Text);
        Assert.True(result.DroppedDuplicatePeriod);
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
