using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Live Preview keeps up, and the arithmetic that stopped it is asserted directly.</summary>
public sealed class LivePreviewCadenceTests
{
    [Fact]
    public void TheIntervalIsTheFloorRatherThanAnAddition()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), LivePreviewCadence.DelayAfter(TimeSpan.FromMilliseconds(1_000)));
        Assert.Equal(TimeSpan.FromMilliseconds(2_500), LivePreviewCadence.DelayAfter(TimeSpan.Zero));
    }

    /// <summary>A pass slower than the interval waits no time at all, and never a negative span.</summary>
    /// <remarks>
    /// THE NORMAL CASE ON A PROCESSOR, NOT AN EDGE ONE. The measured pass cost 2374 ms against a
    /// 2500 ms interval on the machine this shipped from; anything slower exceeds it outright. A
    /// negative span thrown into the delay would stop the preview loop rather than slow it.
    /// </remarks>
    [Theory]
    [InlineData(2_500)]
    [InlineData(2_501)]
    [InlineData(11_000)]
    public void APassSlowerThanTheIntervalWaitsNothingAndNeverGoesNegative(int passMilliseconds)
    {
        var delay = LivePreviewCadence.DelayAfter(TimeSpan.FromMilliseconds(passMilliseconds));

        Assert.Equal(TimeSpan.Zero, delay);
        Assert.True(delay >= TimeSpan.Zero);
    }

    /// <summary>The measured take, walked under both schemes, and the longer takes behind it.</summary>
    /// <remarks>
    /// THE DEFECT, REPLAYED RATHER THAN DESCRIBED. Numbers from the recording on #99: preview started
    /// 545 ms after the recording did, one pass cost 2374 ms, and the capture finished at 7873 ms.
    /// Under the old scheme - wait the whole interval, then work - the first update landed at 5421 ms
    /// and the second was due at 10295 ms, after the recording had already ended.
    ///
    /// WRITING THE SIMULATION IS WHAT PRODUCED THE NUMBER. The first version of this test asserted
    /// three updates on the 7.9-second take because that was the improvement it felt like; walking it
    /// gives two. The gain is real and it grows with the take - a fifteen-second one goes from two
    /// updates to five - but the eight-second case doubles rather than triples, and a claim about a
    /// feature that keeps up is worth having right.
    /// </remarks>
    [Theory]
    [InlineData(7_873, 1, 2)]
    [InlineData(10_000, 1, 3)]
    [InlineData(15_000, 2, 5)]
    public void TheSameTakeProducesMoreUpdatesWhenTheIntervalIsAFloor(
        int captureMilliseconds,
        int before,
        int after)
    {
        var startup = TimeSpan.FromMilliseconds(545);
        var pass = TimeSpan.FromMilliseconds(2_374);
        var captureEnded = TimeSpan.FromMilliseconds(captureMilliseconds);

        Assert.Equal(before, UpdatesBefore(captureEnded, startup, pass, waitBeforeWorking: true));
        Assert.Equal(after, UpdatesBefore(captureEnded, startup, pass, waitBeforeWorking: false));
    }

    /// <summary>Walks one take and counts the updates that reach the screen before it ends.</summary>
    private static int UpdatesBefore(
        TimeSpan captureEnded,
        TimeSpan startup,
        TimeSpan pass,
        bool waitBeforeWorking)
    {
        var now = startup;
        var updates = 0;
        while (true)
        {
            if (waitBeforeWorking)
            {
                now += LivePreviewCadence.Interval;
            }

            now += pass;
            if (now > captureEnded)
            {
                return updates;
            }

            updates++;
            if (!waitBeforeWorking)
            {
                now += LivePreviewCadence.DelayAfter(pass);
            }
        }
    }
}
