using EnviousWispr.Core.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Every number a speed claim rests on. A percentile is the easiest statistic to compute in a way
/// that is defensible, plausible, and about a run that never happened.
/// </summary>
public sealed class LatencySummaryTests
{
    private static LatencySummary Of(params double[] values) => LatencySummary.From(values);

    [Fact]
    public void AnOddCountTakesTheMiddleObservation()
    {
        Assert.Equal(30, Of(10, 20, 30, 40, 50).MedianMilliseconds);
    }

    /// <summary>
    /// An even count has no middle sample, and this is the one place a value between two
    /// observations is the honest answer: it is what "half were faster" means when no single run
    /// sits on the line.
    /// </summary>
    [Fact]
    public void AnEvenCountTakesTheMidpointOfTheTwoMiddleObservations()
    {
        Assert.Equal(25, Of(10, 20, 30, 40).MedianMilliseconds);
    }

    [Fact]
    public void InputOrderDoesNotChangeTheAnswer()
    {
        var forwards = Of(10, 20, 30, 40, 50);
        var backwards = Of(50, 40, 30, 20, 10);
        var shuffled = Of(30, 10, 50, 20, 40);

        Assert.Equal(forwards, backwards);
        Assert.Equal(forwards, shuffled);
    }

    /// <summary>
    /// NEAREST-RANK, so every number published is one that actually happened. Interpolating invents
    /// a duration nobody measured, which is the wrong thing to put in a speed claim.
    /// </summary>
    [Fact]
    public void ThePercentileIsAlwaysARunThatHappened()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();

        var summary = LatencySummary.From(values);

        Assert.Equal(95, summary.Percentile95Milliseconds);
        Assert.Contains(summary.Percentile95Milliseconds, values);
    }

    /// <summary>
    /// The stated consequence, asserted so it stays a KNOWN limit rather than becoming a surprise.
    /// Below twenty samples there is no twentieth value for the 95th percentile to be, so it IS the
    /// maximum - and a percentile silently equal to the maximum reads as a measurement while being
    /// an artefact of the sample size.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(19)]
    public void BelowTwentySamplesThePercentileIsTheMaximumAndSaysSo(int count)
    {
        var summary = LatencySummary.From(Enumerable.Range(1, count).Select(i => (double)i).ToArray());

        Assert.True(summary.Percentile95IsJustTheMaximum);
        Assert.Equal(summary.MaxMilliseconds, summary.Percentile95Milliseconds);
    }

    /// <summary>
    /// The control for the test above: at twenty samples the flag clears AND the percentile stops
    /// being the maximum. Without the second half, a flag that was simply always true would pass.
    /// </summary>
    [Fact]
    public void AtTwentySamplesThePercentileStopsBeingTheMaximum()
    {
        var summary = LatencySummary.From(Enumerable.Range(1, 20).Select(i => (double)i).ToArray());

        Assert.False(summary.Percentile95IsJustTheMaximum);
        Assert.Equal(19, summary.Percentile95Milliseconds);
        Assert.Equal(20, summary.MaxMilliseconds);
    }

    /// <summary>
    /// One slow run in twenty is what a user remembers, and it must move the tail without moving
    /// the median. This is the whole reason both numbers are reported.
    /// </summary>
    [Fact]
    public void OneSlowRunMovesTheTailAndLeavesTheMedianAlone()
    {
        var steady = Enumerable.Repeat(100.0, 39).ToList();

        var without = LatencySummary.From([.. steady, 100.0]);
        var with = LatencySummary.From([.. steady, 5_000.0]);

        Assert.Equal(without.MedianMilliseconds, with.MedianMilliseconds);
        Assert.True(
            with.MaxMilliseconds > without.MaxMilliseconds,
            "A five-second run did not move the maximum, so the summary cannot see a slow run at all.");
    }

    [Fact]
    public void NoMeasurementsIsAnEmptySummaryRatherThanACrash()
    {
        var summary = LatencySummary.From([]);

        Assert.Equal(0, summary.Count);
        Assert.Equal(0, summary.MedianMilliseconds);
    }

    [Fact]
    public void ASingleMeasurementIsEveryStatistic()
    {
        var summary = Of(42);

        Assert.Equal(1, summary.Count);
        Assert.Equal(42, summary.MinMilliseconds);
        Assert.Equal(42, summary.MedianMilliseconds);
        Assert.Equal(42, summary.Percentile95Milliseconds);
        Assert.Equal(42, summary.MaxMilliseconds);
    }

    [Fact]
    public void TheMinimumAndMaximumAreTheExtremesRatherThanTheEnds()
    {
        var summary = Of(30, 10, 50, 20, 40);

        Assert.Equal(10, summary.MinMilliseconds);
        Assert.Equal(50, summary.MaxMilliseconds);
    }
}
