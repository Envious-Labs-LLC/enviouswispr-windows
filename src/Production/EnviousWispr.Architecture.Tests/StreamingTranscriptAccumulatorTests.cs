using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Each piece is recognised without knowing the others exist, so the seam is where the damage is.
/// </summary>
public sealed class StreamingTranscriptAccumulatorTests
{
    private static string Join(params string?[] pieces)
    {
        var accumulator = new StreamingTranscriptAccumulator();
        foreach (var piece in pieces)
        {
            accumulator.Append(piece);
        }

        return accumulator.ToString();
    }

    /// <summary>
    /// The defect this exists for. A recogniser handed the second half of a sentence capitalises it
    /// like the start of one, and a naive join reads as two sentences the user never said.
    /// </summary>
    [Fact]
    public void AMidSentenceContinuationIsNotLeftCapitalised()
    {
        Assert.Equal(
            "I think we should ship this week",
            Join("I think we should", "Ship this week"));
    }

    /// <summary>
    /// The control for the test above. After a real sentence end, the capital is correct and must
    /// survive - without this, a joiner that lowered every piece would pass the whole file.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("!")]
    [InlineData("?")]
    public void ACapitalAfterARealSentenceEndSurvives(string ending)
    {
        Assert.Equal(
            $"That is done{ending} Ship it this week",
            Join($"That is done{ending}", "Ship it this week"));
    }

    /// <summary>
    /// An acronym is capitalised for its own reason. Lowering it would turn NASA into nASA, which
    /// is worse than the defect being fixed.
    /// </summary>
    [Theory]
    [InlineData("NASA called about it")]
    [InlineData("API keys are stored locally")]
    [InlineData("VS Code opened the file")]
    public void AnAcronymAtASeamIsLeftAlone(string continuation)
    {
        Assert.Equal($"we talked and {continuation}", Join("we talked and", continuation));
    }

    /// <summary>
    /// A single capital letter tells us nothing either way, and "I" is the common case.
    /// </summary>
    [Fact]
    public void AStandaloneIIsLeftAlone()
    {
        Assert.Equal("she said that I should go", Join("she said that", "I should go"));
    }

    [Fact]
    public void ALowerCaseContinuationIsUntouched()
    {
        Assert.Equal("we should ship this week", Join("we should", "ship this week"));
    }

    /// <summary>
    /// A commit the recogniser made nothing of must not add a separator with no words beside it -
    /// that produces a double space, or a leading space on the whole transcript.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPieceAddsNothingAtAll(string? empty)
    {
        Assert.Equal("we should ship", Join("we should", empty, "ship"));
        Assert.Equal("we should", Join(empty, "we should"));
    }

    [Fact]
    public void NothingAtAllIsAnEmptyTranscriptRatherThanASpace()
    {
        Assert.Equal(string.Empty, Join());
        Assert.Equal(string.Empty, Join(null, "", "  "));
        Assert.True(new StreamingTranscriptAccumulator().IsEmpty);
    }

    [Fact]
    public void SurroundingWhitespaceOnAPieceIsNotCarriedIntoTheSeam()
    {
        Assert.Equal("we should ship this week", Join("  we should  ", "  ship this week  "));
    }

    /// <summary>
    /// Three pieces, which is the realistic case - a long dictation commits several times. Each
    /// seam is decided on its own, so a sentence ending in the middle behaves correctly there and
    /// nowhere else.
    /// </summary>
    [Fact]
    public void EachSeamIsDecidedOnItsOwn()
    {
        Assert.Equal(
            "we should ship this week. Then we review it and see what people say",
            Join("we should ship this week.", "Then we review it", "And see what people say"));
    }

    [Fact]
    public void TheFirstPieceKeepsItsCapitalWhateverItIs()
    {
        Assert.Equal("Ship it this week", Join("Ship it this week"));
    }
}
