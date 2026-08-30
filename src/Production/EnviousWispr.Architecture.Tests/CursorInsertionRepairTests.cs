using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

public sealed class CursorInsertionRepairTests
{
    private static readonly DictationSessionId SessionId = new(Guid.Parse(
        "2bf4f0ee-400f-451d-92c0-b3e939d158eb"));

    [Fact]
    public void MissingContextUsesThePinnedLegacyTrailingSpace()
    {
        var input = new ProcessedText(SessionId, "Hello");

        var result = CursorInsertionRepair.Apply(input, context: null, "en");

        Assert.Equal(CursorRepairDisposition.LegacyPayload, result.Disposition);
        Assert.Equal("Hello ", result.Output.Text);
        Assert.Equal("Hello ", result.LegacyOutput.Text);
    }

    [Fact]
    public void AddsSpacesAtLexicalSeams()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "world"),
            Context(left: "Hello", right: string.Empty),
            "en-US");

        Assert.Equal(" world ", result.Output.Text);
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

        Assert.Equal(expected, result.Output.Text);
    }

    [Fact]
    public void UnsegmentedLanguagesDoNotGainSpaces()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "世界"),
            Context(left: "你好", right: "今天"),
            "zh-CN");

        Assert.Equal("世界", result.Output.Text);
    }

    [Fact]
    public void RemovesACompleteCrossSeamDuplicate()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "world is ready"),
            Context(left: "hello world", right: string.Empty),
            "en");

        Assert.Equal(" is ready ", result.Output.Text);
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

        Assert.Equal(" world is ready ", result.Output.Text);
        Assert.False(result.RemovedDuplicateWord);
    }

    [Fact]
    public void UnicodeScalarBoundariesRemainIntact()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "🌍"),
            Context(left: "hello ", right: "again"),
            "en");

        Assert.Equal("🌍 ", result.Output.Text);
    }

    [Fact]
    public void RefusesContextualRepairInsideAWord()
    {
        var result = CursorInsertionRepair.Apply(
            new ProcessedText(SessionId, "store"),
            Context(left: "the sto", right: "re"),
            "en");

        Assert.Equal(CursorRepairDisposition.LegacyPayload, result.Disposition);
        Assert.True(result.RefusedInsideWord);
        Assert.Equal("store ", result.Output.Text);
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

        Assert.Equal("Done", result.Output.Text);
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

        Assert.Equal("next ", terminal.Output.Text);
        Assert.Equal(" issues", urlBar.Output.Text);
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
