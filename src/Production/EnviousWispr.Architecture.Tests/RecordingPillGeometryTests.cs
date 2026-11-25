using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The recording pill draws what macOS draws: the lips, the meters and the clock.
/// </summary>
/// <remarks>
/// A XAML canvas cannot be asked what it drew, so the shapes are pinned here as arithmetic. Every
/// expected value is read off the macOS source (RainbowLipsIcon.swift, RainbowLevelMeter.swift,
/// FormattingConstants.formatDuration), not off the code under test.
/// </remarks>
public sealed class RecordingPillGeometryTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7, "0:07")]
    [InlineData(127, "2:07")]
    [InlineData(3665, "61:05")]
    public void TheClockReadsMinutesAndTwoDigitSeconds(int seconds, string expected) =>
        Assert.Equal(expected, RecordingPillGeometry.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ANegativeClockReadsZero() =>
        Assert.Equal("0:00", RecordingPillGeometry.FormatElapsed(TimeSpan.FromSeconds(-3)));

    [Fact]
    public void TheSweepRunsRedToVioletOnceAcrossTheWholeStrip()
    {
        Assert.Equal((0, 1, 0d), RecordingPillGeometry.SpectrumPosition(0, 24));
        var last = RecordingPillGeometry.SpectrumPosition(23, 24);
        Assert.Equal(RecordingPillGeometry.SpectrumStops - 1, last.Lower);
        Assert.Equal(0d, last.Fraction, 6);

        // Monotonic: no bar sits earlier on the spectrum than the bar to its left, which is the
        // property the old repeating nine-colour cycle broke twice per strip.
        var positions = Enumerable.Range(0, 24)
            .Select(index => RecordingPillGeometry.SpectrumPosition(index, 24))
            .Select(position => position.Lower + position.Fraction)
            .ToArray();
        Assert.True(positions.Zip(positions.Skip(1)).All(pair => pair.Second > pair.First));
    }

    [Theory]
    [InlineData(24d, 3.36, 24d)]
    [InlineData(16d, 2.24, 16d)]
    public void AMeterBarNeverCollapsesAndFillsItsStripAtFullLevel(double strip, double silent, double loud)
    {
        Assert.Equal(silent, RecordingPillGeometry.MeterBarHeight(0f, strip), 6);
        Assert.Equal(loud, RecordingPillGeometry.MeterBarHeight(1f, strip), 6);
        Assert.Equal(silent, RecordingPillGeometry.MeterBarHeight(float.NaN, strip), 6);
        Assert.Equal(loud, RecordingPillGeometry.MeterBarHeight(4f, strip), 6);
    }

    [Fact]
    public void TheLipsAreClosedAtSilenceAndPartAtFullLevel()
    {
        // Centre upper bar (index 4) and centre lower bar (index 13) at 24pt: the 64-grid scaled by
        // 0.375, heights scaled by 0.55 at silence and by 1.45 at full level.
        var upperQuiet = RecordingPillGeometry.LipsBar(4, 0f, 24);
        var lowerQuiet = RecordingPillGeometry.LipsBar(13, 0f, 24);
        Assert.Equal(6 * 0.375 * 0.55, upperQuiet.Height, 6);
        Assert.Equal(17 * 0.375 * 0.55, lowerQuiet.Height, 6);
        Assert.Equal(1.6875, upperQuiet.Width, 6);
        Assert.Equal(28 * 0.375, upperQuiet.X, 6);

        var upperLoud = RecordingPillGeometry.LipsBar(4, 1f, 24);
        var lowerLoud = RecordingPillGeometry.LipsBar(13, 1f, 24);
        Assert.Equal(6 * 0.375 * 1.45, upperLoud.Height, 6);
        Assert.Equal(17 * 0.375 * 1.45, lowerLoud.Height, 6);

        // The gap between the lips widens with the voice.
        var quietGap = lowerQuiet.Y - (upperQuiet.Y + upperQuiet.Height);
        var loudGap = lowerLoud.Y - (upperLoud.Y + upperLoud.Height);
        Assert.True(loudGap > quietGap, $"The lips did not part: {quietGap} at silence, {loudGap} at full level.");
    }

    [Fact]
    public void TheLowerLipMovesInCounterpointToTheUpper()
    {
        // The lower lip reads its sensitivity mirrored: its first bar answers like the upper lip's last.
        var upperLast = RecordingPillGeometry.LipsBar(8, 1f, 64);
        var lowerFirst = RecordingPillGeometry.LipsBar(9, 1f, 64);
        Assert.Equal(5 * (0.55 + 0.9 * 0.70), upperLast.Height, 6);
        Assert.Equal(5 * (0.55 + 0.9 * 0.70), lowerFirst.Height, 6);
    }

    [Fact]
    public void EveryLipsBarStaysInsideTheMarkAtAnyLevel()
    {
        foreach (var level in new[] { 0f, 0.5f, 1f })
        {
            for (var index = 0; index < RecordingPillGeometry.LipsBarCount; index++)
            {
                var bar = RecordingPillGeometry.LipsBar(index, level, 24);
                Assert.InRange(bar.X, 0, 24);
                Assert.InRange(bar.Y, 0, 24);
                Assert.InRange(bar.Y + bar.Height, 0, 24);
            }
        }
    }
}
