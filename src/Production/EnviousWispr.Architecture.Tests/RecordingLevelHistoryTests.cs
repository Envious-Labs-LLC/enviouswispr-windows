using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The meter showed one number twenty-four ways, which looks alive and says nothing.
/// </summary>
/// <remarks>
/// THESE ARE ABOUT THE SEQUENCE, WHICH IS THE PART THAT MATTERS. The first attempt at covering this
/// asked whether the overlay's SOURCE FILE mentioned a field and did not mention a sine function -
/// both of which a version that drew the newest sample into every bar would satisfy. What has to be
/// true is what a second sample does to the first, what a sample arriving too soon does to neither,
/// and what a reset leaves behind.
/// </remarks>
public sealed class RecordingLevelHistoryTests
{
    [Fact]
    public void AFreshMeterIsEmptyAndAsWideAsTheBarsItDraws()
    {
        var history = new RecordingLevelHistory();

        Assert.Equal(RecordingLevelHistory.Capacity, history.Levels.Count);
        Assert.All(history.Levels, level => Assert.Equal(0f, level));
    }

    [Fact]
    public void TheNewestSampleArrivesOnTheRight()
    {
        var history = new RecordingLevelHistory();

        Assert.True(history.Sample(0.5f, TimeSpan.Zero));

        Assert.Equal(0.5f, history.Levels[^1]);
        Assert.Equal(0f, history.Levels[^2]);
    }

    [Fact]
    public void EachSampleMovesTheOneBeforeItAlong()
    {
        // A MIRROR PASSES EVERY OTHER CHECK AND FAILS THIS ONE. Drawing the newest level into every
        // bar keeps a field called history and never moves anything.
        var history = new RecordingLevelHistory();
        var at = TimeSpan.Zero;

        foreach (var level in new[] { 0.1f, 0.2f, 0.3f })
        {
            history.Sample(level, at);
            at += RecordingLevelHistory.SampleInterval;
        }

        Assert.Equal(0.3f, history.Levels[^1]);
        Assert.Equal(0.2f, history.Levels[^2]);
        Assert.Equal(0.1f, history.Levels[^3]);
        Assert.Equal(0f, history.Levels[^4]);
    }

    [Fact]
    public void ASampleArrivingTooSoonIsRefusedAndChangesNothing()
    {
        var history = new RecordingLevelHistory();
        history.Sample(0.5f, TimeSpan.Zero);

        var kept = history.Sample(
            0.9f,
            RecordingLevelHistory.SampleInterval - TimeSpan.FromMilliseconds(1));

        Assert.False(kept);
        Assert.Equal(0.5f, history.Levels[^1]);
    }

    [Fact]
    public void ASampleOnTheIntervalIsKept()
    {
        var history = new RecordingLevelHistory();
        history.Sample(0.5f, TimeSpan.Zero);

        Assert.True(history.Sample(0.9f, RecordingLevelHistory.SampleInterval));
        Assert.Equal(0.9f, history.Levels[^1]);
    }

    [Fact]
    public void TheOldestLevelFallsOffTheLeftWhenTheMeterIsFull()
    {
        var history = new RecordingLevelHistory();
        var at = TimeSpan.Zero;
        for (var index = 0; index <= RecordingLevelHistory.Capacity; index++)
        {
            history.Sample(index / 100f, at);
            at += RecordingLevelHistory.SampleInterval;
        }

        // The first level offered is gone, and the second is now the oldest kept.
        Assert.Equal(0.01f, history.Levels[0]);
        Assert.Equal(RecordingLevelHistory.Capacity / 100f, history.Levels[^1]);
    }

    [Fact]
    public void ANewRecordingDoesNotShowTheLastOne()
    {
        var history = new RecordingLevelHistory();
        history.Sample(0.9f, TimeSpan.Zero);

        history.Reset();

        Assert.All(history.Levels, level => Assert.Equal(0f, level));
    }

    [Fact]
    public void AResetAlsoForgetsWhenTheLastSampleWasTaken()
    {
        // Otherwise the first level of a new recording is refused for arriving too soon after the
        // last level of the previous one, and the meter opens a beat late.
        var history = new RecordingLevelHistory();
        history.Sample(0.9f, TimeSpan.Zero);

        history.Reset();

        Assert.True(history.Sample(0.4f, TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(1.5f, 1f)]
    [InlineData(float.NaN, 0f)]
    public void ALevelOutsideTheRangeIsBroughtIntoIt(float offered, float expected)
    {
        // A bar height is computed from this, so a value outside nought to one draws off the pill.
        var history = new RecordingLevelHistory();

        history.Sample(offered, TimeSpan.Zero);

        Assert.Equal(expected, history.Levels[^1]);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ALevelNobodyCouldMeasureReadsAsSilence(float rootMeanSquare)
    {
        // NOT-A-NUMBER PASSES EVERY RANGE CHECK. Math.Max and Math.Clamp both hand it straight back,
        // so it would have arrived as a bar height and an opacity that no layout can draw - and the
        // Classic pill reads this value directly, before the history ever sees it.
        Assert.Equal(0f, RecordingLevelHistory.Normalize(rootMeanSquare));
    }

    [Theory]
    [InlineData(-5f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.25f, 1f)]
    [InlineData(9f, 1f)]
    public void TheNormalizerStaysInsideNoughtToOne(float rootMeanSquare, float expected)
    {
        Assert.Equal(expected, RecordingLevelHistory.Normalize(rootMeanSquare), 3);
    }

    [Fact]
    public void QuietSpeechIsVisibleRatherThanFlat()
    {
        // A SQUARE ROOT BECAUSE HEARING IS NOT LINEAR. Ordinary speech sits low in a raw
        // root-mean-square, so drawing it directly gives a meter that barely moves until somebody
        // shouts at it.
        var quiet = RecordingLevelHistory.Normalize(0.01f);

        Assert.True(quiet > 0.15f, $"Quiet speech normalised to {quiet}, which draws as nothing.");
        Assert.True(quiet < 0.5f, $"Quiet speech normalised to {quiet}, which draws as loud.");
    }
}
