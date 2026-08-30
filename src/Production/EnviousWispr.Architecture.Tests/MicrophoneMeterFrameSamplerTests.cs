using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Architecture.Tests;

public sealed class MicrophoneMeterFrameSamplerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void BurstProducesAtMostOneFramePerSampleInterval()
    {
        var sampler = new MicrophoneMeterFrameSampler(Interval);
        var frames = 0;

        // Two hundred levels a second for a fifth of a second, which is the real capture rate.
        for (var packet = 0; packet < 40; packet++)
        {
            if (sampler.TryTakeFrame(0.01f, TimeSpan.FromMilliseconds(packet * 5), out _))
            {
                frames++;
            }
        }

        // The first level opens a frame, then one per interval across the remaining 195ms.
        Assert.Equal(4, frames);
    }

    [Fact]
    public void PeakBetweenFramesIsPreserved()
    {
        var sampler = new MicrophoneMeterFrameSampler(Interval);
        Assert.True(sampler.TryTakeFrame(0f, TimeSpan.Zero, out _));

        // A single loud packet mid-frame, surrounded by quiet ones. Taking the first level after
        // the boundary would report 0.01 and lose the consonant entirely.
        Assert.False(sampler.TryTakeFrame(0.01f, TimeSpan.FromMilliseconds(10), out _));
        Assert.False(sampler.TryTakeFrame(0.90f, TimeSpan.FromMilliseconds(20), out _));
        Assert.False(sampler.TryTakeFrame(0.01f, TimeSpan.FromMilliseconds(30), out _));

        Assert.True(sampler.TryTakeFrame(0.01f, TimeSpan.FromMilliseconds(50), out var frame));
        Assert.Equal(0.90f, frame);
    }

    [Fact]
    public void ALoudFrameDoesNotLeakIntoTheNextOne()
    {
        var sampler = new MicrophoneMeterFrameSampler(Interval);
        Assert.True(sampler.TryTakeFrame(0.90f, TimeSpan.Zero, out var first));
        Assert.True(sampler.TryTakeFrame(0.01f, TimeSpan.FromMilliseconds(50), out var second));

        Assert.Equal(0.90f, first);
        Assert.Equal(0.01f, second);
    }

    [Fact]
    public void ALevelNobodyCouldMeasureIsSilenceRatherThanAPeak()
    {
        var sampler = new MicrophoneMeterFrameSampler(Interval);
        Assert.True(sampler.TryTakeFrame(0f, TimeSpan.Zero, out _));
        Assert.False(sampler.TryTakeFrame(float.NaN, TimeSpan.FromMilliseconds(10), out _));
        Assert.False(sampler.TryTakeFrame(float.PositiveInfinity, TimeSpan.FromMilliseconds(20), out _));
        Assert.False(sampler.TryTakeFrame(0.02f, TimeSpan.FromMilliseconds(30), out _));

        Assert.True(sampler.TryTakeFrame(0f, TimeSpan.FromMilliseconds(50), out var frame));
        Assert.Equal(0.02f, frame);
    }
}
