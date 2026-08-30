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
    /// <summary>Five levels a person could actually produce, quietest first.</summary>
    private static readonly float[] LouderAndLouder = [0.002f, 0.01f, 0.05f, 0.2f, 0.6f];

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
    [InlineData(1f, 1f)]
    [InlineData(9f, 1f)]
    public void TheNormalizerStaysInsideNoughtToOne(float level, float expected)
    {
        Assert.Equal(expected, RecordingLevelHistory.Normalize(level), 3);
    }

    [Fact]
    public void OrdinarySpeechIsVISIBLYOffTheFloor()
    {
        // THE NUMBER THAT CAUGHT THIS. Ordinary speech measured on the real hardware sits at a
        // root-mean-square of about 0.004, which through the old sqrt curve became 0.125 - on a
        // twenty-four bar rail that is a fraction of a pixel above the floor. Technically moving,
        // visually flat, and reported by a person with a screen as a dead meter.
        var speech = RecordingLevelHistory.Normalize(0.004f);

        Assert.True(speech > 0.15f, $"Ordinary speech normalised to {speech}, which draws as flat.");
        Assert.True(speech < 0.6f, $"Ordinary speech normalised to {speech}, which leaves no headroom.");
    }

    [Fact]
    public void ASilentRoomDoesNotLightTheMeter()
    {
        // A meter that glows in a quiet room destroys the one reading that matters, which is that
        // nothing is arriving.
        Assert.Equal(0f, RecordingLevelHistory.Normalize(0.0005f), 2);
    }

    [Fact]
    public void LouderReadsHigherAllTheWayUp()
    {
        // A curve that saturates early is a meter with one useful half. Every step has to move it.
        var steps = LouderAndLouder.Select(RecordingLevelHistory.Normalize).ToArray();

        for (var index = 1; index < steps.Length; index++)
        {
            Assert.True(
                steps[index] > steps[index - 1] + 0.05f,
                $"Level {index} normalised to {steps[index]}, barely above {steps[index - 1]}.");
        }
    }

    /// <summary>A poller running at half the interval keeps the history accepting samples.</summary>
    /// <remarks>
    /// THE PILL'S RAIL WAS DEAD FOR EXACTLY THIS REASON AND NOTHING CAUGHT IT. Its DispatcherTimer
    /// was set to the sample interval, putting two clocks in series at the same period, and this
    /// gate rejects anything arriving early. Windows quantises timer callbacks to about 15.625
    /// milliseconds, so a fifty millisecond timer fires at 46.9 - reliably early, every single tick
    /// - and the rail drew its first sample and then never again. The timer now polls at half the
    /// interval so this gate is the single pacer, and that pairing is what this holds.
    /// </remarks>
    [Fact]
    public void HalfIntervalPollingCannotPhaseLockTheHistoryGate()
    {
        var history = new RecordingLevelHistory();
        var poll = RecordingLevelHistory.SampleInterval / 2;
        var accepted = 0;

        // Twenty polls, and deliberately a shade early on every one, which is what a quantised
        // timer actually does. An equal-period timer accepts one of these and then nothing.
        for (var tick = 1; tick <= 20; tick++)
        {
            var now = (poll * tick) - TimeSpan.FromMilliseconds(3);
            if (history.Sample(0.5f, now))
            {
                accepted++;
            }
        }

        Assert.True(accepted >= 9, $"expected about ten accepted samples, got {accepted}");
    }
}
