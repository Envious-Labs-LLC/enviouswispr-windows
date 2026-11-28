using EnviousWispr.Core.Presentation;

namespace EnviousWispr.Architecture.Tests;

public sealed class RainbowLipsMotionTests
{
    [Fact]
    public void GeometryMatches18BarsInside256CanvasWithBrandSpectrumTokens()
    {
        Assert.Equal(9, RainbowLipsMotion.UpperBars.Count);
        Assert.Equal(9, RainbowLipsMotion.LowerBars.Count);
        Assert.Equal(18, RainbowLipsMotion.AllBars.Count);

        for (var i = 0; i < 9; i++)
        {
            var upper = RainbowLipsMotion.UpperBars[i];
            var lower = RainbowLipsMotion.LowerBars[i];

            Assert.True(upper.IsUpper);
            Assert.False(lower.IsUpper);
            Assert.Equal(i, upper.Index);
            Assert.Equal(i, lower.Index);
            Assert.Equal(16.0 + i * 24.0, upper.X, precision: 4);
            Assert.Equal(16.0 + i * 24.0, lower.X, precision: 4);
            Assert.Equal(14.0, upper.Width, precision: 4);
            Assert.Equal(14.0, lower.Width, precision: 4);
            Assert.Equal(upper.Y + upper.Height, upper.AnchorY, precision: 4);
            Assert.Equal(lower.Y, lower.AnchorY, precision: 4);
            Assert.StartsWith("BrandSpectrum", upper.BrushResourceKey, StringComparison.Ordinal);
            Assert.StartsWith("BrandSpectrum", lower.BrushResourceKey, StringComparison.Ordinal);
            Assert.EndsWith("Brush", upper.BrushResourceKey, StringComparison.Ordinal);
            Assert.EndsWith("Brush", lower.BrushResourceKey, StringComparison.Ordinal);
            Assert.InRange(upper.X + upper.Width + RainbowLipsMotion.OriginOffsetX, 0.0, RainbowLipsMotion.CanvasSize);
            Assert.InRange(lower.Y + lower.Height + RainbowLipsMotion.OriginOffsetY, 0.0, RainbowLipsMotion.CanvasSize);
        }
    }

    [Fact]
    public void AllSeventeenMoodsProduceFinitePositiveScalesAcrossAll18Bars()
    {
        var moods = Enum.GetValues<RainbowLipsMood>();
        Assert.Equal(17, moods.Length);

        foreach (var mood in moods)
        {
            foreach (var elapsed in new[] { 0.0, 0.25, 0.95, 2.2 })
            {
                var frame = RainbowLipsMotion.EvaluateFrame(
                    mood: mood,
                    wallSeconds: elapsed + 1.5,
                    sinceStateChangeSeconds: elapsed,
                    voiceLevel: mood == RainbowLipsMood.Equalizer ? 0.6 : null,
                    speakingBeats: 3,
                    reduceMotion: false);

                Assert.Equal(18, frame.Scales.Length);
                Assert.Equal(18, frame.Shimmers.Length);
                foreach (var scale in frame.Scales)
                {
                    Assert.True(double.IsFinite(scale) && scale > 0.0 && scale < 3.0, $"Mood {mood} produced scale {scale}.");
                }
            }
        }
    }

    [Fact]
    public void GlideMemorySmoothsForwardStepsHoldsSameTimestampAndSnapsOnBackwardSeek()
    {
        var memory = new RainbowLipsGlideMemory();
        var initial = Enumerable.Repeat(1.0, 18).ToArray();
        var high = Enumerable.Repeat(2.0, 18).ToArray();
        var low = Enumerable.Repeat(0.5, 18).ToArray();

        // First call initializes immediately.
        var s0 = memory.Step(initial, 1.0);
        Assert.All(s0, v => Assert.Equal(1.0, v, precision: 6));

        // Forward dt = 1/32 s -> factor = (1/32) * 16 = 0.5 -> glides halfway to 1.5.
        var s1 = memory.Step(high, 1.0 + (1.0 / 32.0));
        Assert.All(s1, v => Assert.Equal(1.5, v, precision: 6));

        // Same timestamp holds previous drawn frame without jumping.
        var sSame = memory.Step(high, 1.0 + (1.0 / 32.0));
        Assert.All(sSame, v => Assert.Equal(1.5, v, precision: 6));

        // Backward seek (dt < 0) snaps immediately to target (film scrub behavior).
        var sSeek = memory.Step(low, 0.2);
        Assert.All(sSeek, v => Assert.Equal(0.5, v, precision: 6));
    }

    [Fact]
    public void EntryBurstOverridesMoodToHappyForPointEightSeconds()
    {
        var duringBurst = RainbowLipsMotion.GetEffectiveMood(
            RainbowLipsMood.Idle,
            sinceStateChangeSeconds: 4.0,
            sinceBurstSeconds: 0.25);
        Assert.Equal(RainbowLipsMood.Happy, duringBurst.Mood);
        Assert.Equal(0.25, duringBurst.ElapsedSeconds, precision: 6);

        var afterBurst = RainbowLipsMotion.GetEffectiveMood(
            RainbowLipsMood.Idle,
            sinceStateChangeSeconds: 4.0,
            sinceBurstSeconds: 0.85);
        Assert.Equal(RainbowLipsMood.Idle, afterBurst.Mood);
        Assert.Equal(4.0, afterBurst.ElapsedSeconds, precision: 6);
    }

    [Fact]
    public void EqualizerScalesWithLiveVoiceLevel()
    {
        var quiet = RainbowLipsMotion.EvaluateFrame(
            RainbowLipsMood.Equalizer,
            wallSeconds: 1.0,
            sinceStateChangeSeconds: 1.0,
            voiceLevel: 0.0);
        var loud = RainbowLipsMotion.EvaluateFrame(
            RainbowLipsMood.Equalizer,
            wallSeconds: 1.0,
            sinceStateChangeSeconds: 1.0,
            voiceLevel: 1.0);

        Assert.True(loud.Scales.Max() > quiet.Scales.Max() + 0.5);
        Assert.True(quiet.Scales.Min() >= 0.3 - 1e-6);
    }

    [Fact]
    public void SpeakingOpensCenterWeightedPerSyllableThenSmiles()
    {
        // Mid-first syllable (elapsed = 0.15 s -> sin(pi * 0.5) = 1.0): center bar (index 4) opens wider than edge bar (index 0).
        var centerSpeaking = RainbowLipsMotion.ComputeTargetScale(
            RainbowLipsMood.Speaking, index: 4, isUpper: true, wallSeconds: 0.15, elapsedSeconds: 0.15, speakingBeats: 2);
        var edgeSpeaking = RainbowLipsMotion.ComputeTargetScale(
            RainbowLipsMood.Speaking, index: 0, isUpper: true, wallSeconds: 0.15, elapsedSeconds: 0.15, speakingBeats: 2);
        Assert.True(centerSpeaking > edgeSpeaking + 0.35);

        // After 2 beats (0.6 s), at elapsed = 1.2 s (0.6 s after beats), full smile weight (w = 1.0) holds Smile silhouette.
        var upperCornerSmile = RainbowLipsMotion.ComputeTargetScale(
            RainbowLipsMood.Speaking, index: 0, isUpper: true, wallSeconds: 1.2, elapsedSeconds: 1.2, speakingBeats: 2);
        Assert.Equal(1.3, upperCornerSmile, precision: 4);
    }

    [Fact]
    public void ReduceMotionHoldsRestingSilhouettesAndDisablesGlowAndShimmer()
    {
        var glide = new RainbowLipsGlideMemory();
        glide.Step(Enumerable.Repeat(1.4, 18).ToArray(), 1.0);
        Assert.True(glide.HasState);

        var heart = RainbowLipsMotion.EvaluateFrame(
            RainbowLipsMood.Heart,
            wallSeconds: 2.0,
            sinceStateChangeSeconds: 2.0,
            sinceBurstSeconds: 0.1,
            reduceMotion: true,
            glideMemory: glide);
        Assert.False(glide.HasState);
        Assert.Equal(0.0, heart.Glow.Opacity);
        Assert.Equal(0.0, heart.Glow.Radius);
        Assert.Equal(0.3, heart.Scales[0], precision: 6);
        Assert.Equal(1.4, heart.Scales[2], precision: 6);
        Assert.Equal(1.5, heart.Scales[9 + 4], precision: 6);

        var smile = RainbowLipsMotion.EvaluateFrame(RainbowLipsMood.Smile, 2.0, 2.0, reduceMotion: true);
        Assert.Equal(1.3, smile.Scales[0], precision: 6);
        Assert.Equal(0.6, smile.Scales[4], precision: 6);

        var denied = RainbowLipsMotion.EvaluateFrame(RainbowLipsMood.Denied, 2.0, 2.0, reduceMotion: true);
        Assert.Equal(0.42, denied.Scales[0], precision: 6);
        Assert.Equal(0.32, denied.Scales[9], precision: 6);

        var drooping = RainbowLipsMotion.EvaluateFrame(RainbowLipsMood.Drooping, 2.0, 2.0, reduceMotion: true);
        Assert.All(drooping.Scales, s => Assert.Equal(0.28, s, precision: 6));

        var shimmer = RainbowLipsMotion.EvaluateFrame(RainbowLipsMood.Shimmer, 2.0, 2.0, reduceMotion: true);
        Assert.All(shimmer.Scales, s => Assert.Equal(1.0, s, precision: 6));
        Assert.All(shimmer.Shimmers, sh =>
        {
            Assert.Equal(1.0, sh.Opacity, precision: 6);
            Assert.Equal(0.0, sh.Brightness, precision: 6);
        });
    }

    [Fact]
    public void GlowAppliesOnlyToHeartTriumphAndModelMoodsAfterDelay()
    {
        Assert.Equal(0.0, RainbowLipsMotion.ComputeGlow(RainbowLipsMood.Heart, elapsedSeconds: 0.2).Opacity);
        Assert.True(RainbowLipsMotion.ComputeGlow(RainbowLipsMood.Heart, elapsedSeconds: 1.5).Opacity > 0.4);

        Assert.Equal(0.0, RainbowLipsMotion.ComputeGlow(RainbowLipsMood.Triumph, elapsedSeconds: 0.5).Opacity);
        Assert.True(RainbowLipsMotion.ComputeGlow(RainbowLipsMood.Triumph, elapsedSeconds: 2.5).Opacity > 0.45);
        Assert.True(RainbowLipsMotion.ComputeGlow(RainbowLipsMood.ModelFast, elapsedSeconds: 2.5).Opacity > 0.45);
        Assert.True(RainbowLipsMotion.ComputeGlow(RainbowLipsMood.ModelVoice, elapsedSeconds: 2.5).Opacity > 0.45);

        Assert.Equal(0.0, RainbowLipsMotion.GetGlowPeak(RainbowLipsMood.Idle));
        Assert.Equal(0.0, RainbowLipsMotion.GetGlowPeak(RainbowLipsMood.Equalizer));
        Assert.True(RainbowLipsMotion.IsModelMood(RainbowLipsMood.ModelFast));
        Assert.True(RainbowLipsMotion.IsModelMood(RainbowLipsMood.ModelVoice));
        Assert.False(RainbowLipsMotion.IsModelMood(RainbowLipsMood.Idle));
    }
}
