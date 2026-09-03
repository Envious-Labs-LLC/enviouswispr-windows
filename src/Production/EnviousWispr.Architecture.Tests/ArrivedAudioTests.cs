using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>A recording holds only what has been spoken into it so far.</summary>
public sealed class ArrivedAudioTests
{
    private const int Rate = 16_000;

    [Fact]
    public void NothingHasArrivedBeforeTheRecordingStarts()
    {
        Assert.Equal(0, ArrivedAudio.Count(TimeSpan.Zero, 10 * Rate, Rate));
    }

    /// <summary>A clock going backwards is not a recording that has not started.</summary>
    [Fact]
    public void ANegativeElapsedTimeYieldsNothingRatherThanANegativeIndex()
    {
        Assert.Equal(0, ArrivedAudio.Count(TimeSpan.FromSeconds(-5), 10 * Rate, Rate));
    }

    [Theory]
    [InlineData(1, 16_000)]
    [InlineData(2, 32_000)]
    [InlineData(0.5, 8_000)]
    public void AudioArrivesAtTheSampleRate(double seconds, int expected)
    {
        Assert.Equal(expected, ArrivedAudio.Count(TimeSpan.FromSeconds(seconds), 10 * Rate, Rate));
    }

    /// <summary>Holding longer than the fixture lasts does not read past its end.</summary>
    /// <remarks>
    /// THE ORDINARY CASE, NOT AN EDGE ONE. The harness holds a recording for a fixed time and the
    /// fixtures are shorter than some of those holds, so the audio simply runs out partway through.
    /// </remarks>
    [Fact]
    public void HoldingLongerThanTheFixtureStopsAtItsEnd()
    {
        Assert.Equal(3 * Rate, ArrivedAudio.Count(TimeSpan.FromSeconds(99), 3 * Rate, Rate));
    }

    /// <summary>The one that was actually wrong: half a second in, half a second exists.</summary>
    /// <remarks>
    /// THE DEFECT THIS REPLACES, STATED AS A NUMBER. The streaming head start polls at 500 ms, and the
    /// capture used to hand it the entire file at that moment - so a five second take was fully
    /// committed before the speaker had said a second word, and the measured benefit was the benefit
    /// of having the future available. Ref: #96.
    /// </remarks>
    [Fact]
    public void AtTheFirstPollOnlyTheFirstHalfSecondExists()
    {
        Assert.Equal(8_000, ArrivedAudio.Count(TimeSpan.FromMilliseconds(500), 5 * Rate, Rate));
    }
}
