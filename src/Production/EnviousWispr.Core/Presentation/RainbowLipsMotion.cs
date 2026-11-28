namespace EnviousWispr.Core.Presentation;

/// <summary>All 17 expressive moods of the 18-bar EnviousWispr rainbow lips hero.</summary>
public enum RainbowLipsMood
{
    Idle,
    Denied,
    Happy,
    Equalizer,
    Wave,
    Drooping,
    Shimmer,
    Recording,
    Pulse,
    Smile,
    Triumph,
    Heart,
    Ready,
    Welcome,
    Speaking,
    ModelFast,
    ModelVoice,
}

/// <summary>Static geometry and theme-brush specification for one bar in the 256x256 lips canvas.</summary>
public readonly record struct RainbowLipsBarSpec(
    int Index,
    double X,
    double Y,
    double Width,
    double Height,
    double BaseOpacity,
    bool IsUpper,
    string BrushResourceKey)
{
    /// <summary>Y anchor point in unshifted canvas coordinates (bottom edge for upper bars, top edge for lower bars).</summary>
    public double AnchorY => IsUpper ? Y + Height : Y;
}

/// <summary>Per-bar shimmer opacity multiplier and highlight brightness (0..0.4).</summary>
public readonly record struct RainbowLipsShimmer(double Opacity, double Brightness);

/// <summary>Ambient purple glow opacity and blur radius for celebratory/heart moods.</summary>
public readonly record struct RainbowLipsGlow(double Opacity, double Radius);

/// <summary>Global desaturation and brightness reduction used for Denied and Drooping states.</summary>
public readonly record struct RainbowLipsGlobalModifiers(
    double Saturation,
    double Brightness,
    double TransitionSeconds);

/// <summary>Resolved mood and mood-local elapsed time after applying any active entry burst.</summary>
public readonly record struct RainbowLipsEffectiveMood(
    RainbowLipsMood Mood,
    double ElapsedSeconds);

/// <summary>Complete deterministic snapshot of all 18 bars, glow, and global modifiers for one rendered frame.</summary>
public sealed record RainbowLipsFrame(
    RainbowLipsMood EffectiveMood,
    double MoodElapsedSeconds,
    double[] Scales,
    RainbowLipsShimmer[] Shimmers,
    RainbowLipsGlow Glow,
    RainbowLipsGlobalModifiers GlobalModifiers);

/// <summary>
/// Tracks the last drawn bar scales so mood transitions glide exponentially (~60 ms catch-up)
/// instead of snapping, while snapping immediately on backward clock seeks (dt &lt; 0).
/// </summary>
public sealed class RainbowLipsGlideMemory
{
    private double[]? _drawn;
    private double? _lastTime;

    public bool HasState => _drawn is not null && _lastTime.HasValue;

    public void Reset()
    {
        _drawn = null;
        _lastTime = null;
    }

    public double[] Step(double[] targets, double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(targets);
        double[] drawn;
        var dt = _lastTime.HasValue ? timeSeconds - _lastTime.Value : 0.0;

        if (dt < 0.0)
        {
            // Backward seek (such as scrubbing the Closing Film): draw the target frame immediately.
            drawn = [.. targets];
        }
        else if (_drawn is not null && _drawn.Length == targets.Length && dt > 0.0)
        {
            drawn = new double[targets.Length];
            for (var i = 0; i < targets.Length; i++)
            {
                drawn[i] = RainbowLipsMotion.Glide(_drawn[i], targets[i], dt);
            }
        }
        else if (_drawn is not null && _drawn.Length == targets.Length)
        {
            // Same timestamp re-render in one frame: keep last drawn values stable.
            drawn = [.. _drawn];
        }
        else
        {
            drawn = [.. targets];
        }

        _drawn = drawn;
        _lastTime = timeSeconds;
        return drawn;
    }
}

/// <summary>
/// Pure deterministic 18-bar (9 upper + 9 lower) motion engine ported from macOS RainbowLipsView.swift.
/// Zero UI dependencies so every mood, glide step, burst override, live mic scaling, and Reduce Motion
/// resting silhouette is exhaustively unit-testable.
/// </summary>
public static class RainbowLipsMotion
{
    public const double CanvasSize = 256.0;
    public const double OriginOffsetX = 8.0;
    public const double OriginOffsetY = 13.0;
    public const double BarWidth = 14.0;
    public const double BarCornerRadius = 5.0;
    public const int HalfBarCount = 9;
    public const int BarCount = 18;

    /// <summary>Duration of the one-shot Happy entry bounce played on screen arrival or tap.</summary>
    public const double BurstDurationSeconds = 0.8;

    /// <summary>Exponential glide catch-up rate between moods (16/s, ~60 ms).</summary>
    public const double GlideRate = 16.0;

    private readonly record struct RecordingBarConfig(
        double MinScale,
        double MaxScale,
        double Duration,
        double Delay);

    private readonly record struct GlowShape(
        double Delay,
        double Period,
        double PeakOpacity,
        double PeakRadius);

    private static readonly double[] IdleDelays = [0.0, 0.1, 0.2, 0.15, 0.05, 0.25, 0.3, 0.1, 0.2];
    private static readonly double[] ShimmerDelays = [0.0, 0.2, 0.4, 0.6, 0.8, 0.6, 0.4, 0.2, 0.0];
    private static readonly double[] PulseDelays = [0.0, 0.06, 0.12, 0.18, 0.22, 0.18, 0.12, 0.06, 0.0];
    private static readonly double[] WaveDelays = [0.0, 0.06, 0.12, 0.18, 0.24, 0.18, 0.12, 0.06, 0.0];
    private static readonly double[] HappyDelays = [0.0, 0.04, 0.08, 0.06, 0.02, 0.06, 0.08, 0.04, 0.0];
    private static readonly double[] TriumphDelays = [0.0, 0.05, 0.1, 0.15, 0.18, 0.15, 0.1, 0.05, 0.0];

    private static readonly double[] SmileUpperScales = [1.3, 1.1, 1.0, 1.0, 0.6, 1.0, 1.0, 1.1, 1.3];
    private static readonly double[] SmileLowerScales = [0.5, 1.0, 1.0, 1.3, 1.3, 1.3, 1.0, 1.0, 0.5];
    private static readonly double[] HeartUpperScales = [0.3, 1.0, 1.4, 1.0, 0.4, 1.0, 1.4, 1.0, 0.3];
    private static readonly double[] HeartLowerScales = [0.15, 0.4, 0.7, 1.1, 1.5, 1.1, 0.7, 0.4, 0.15];

    private static readonly RecordingBarConfig[] RecordingConfigs =
    [
        new(0.4, 1.3, 0.22, 0.0),
        new(0.3, 1.2, 0.27, 0.03),
        new(0.6, 1.4, 0.19, 0.07),
        new(0.4, 1.3, 0.31, 0.02),
        new(0.5, 1.35, 0.24, 0.05),
        new(0.35, 1.25, 0.28, 0.01),
        new(0.65, 1.3, 0.21, 0.09),
        new(0.4, 1.2, 0.25, 0.04),
        new(0.45, 1.25, 0.29, 0.06),
    ];

    public static IReadOnlyList<RainbowLipsBarSpec> UpperBars { get; } =
    [
        new(0, 16.0, 84.2, BarWidth, 20.0, 0.92, true, "BrandSpectrumRedBrush"),
        new(1, 40.0, 65.75, BarWidth, 32.0, 0.92, true, "BrandSpectrumOrangeBrush"),
        new(2, 64.0, 43.36, BarWidth, 48.0, 0.92, true, "BrandSpectrumGoldBrush"),
        new(3, 88.0, 63.04, BarWidth, 36.0, 0.92, true, "BrandSpectrumLimeBrush"),
        new(4, 112.0, 81.43, BarWidth, 24.0, 0.92, true, "BrandSpectrumSpringBrush"),
        new(5, 136.0, 63.04, BarWidth, 36.0, 0.92, true, "BrandSpectrumCyanBrush"),
        new(6, 160.0, 43.36, BarWidth, 48.0, 0.92, true, "BrandSpectrumBlueBrush"),
        new(7, 184.0, 65.75, BarWidth, 32.0, 0.92, true, "BrandSpectrumRoyalBrush"),
        new(8, 208.0, 84.2, BarWidth, 20.0, 0.92, true, "BrandSpectrumVioletBrush"),
    ];

    public static IReadOnlyList<RainbowLipsBarSpec> LowerBars { get; } =
    [
        new(0, 16.0, 125.8, BarWidth, 20.0, 0.88, false, "BrandSpectrumRoyalBrush"),
        new(1, 40.0, 119.35, BarWidth, 36.0, 0.88, false, "BrandSpectrumBlueBrush"),
        new(2, 64.0, 112.96, BarWidth, 48.0, 0.88, false, "BrandSpectrumCyanBrush"),
        new(3, 88.0, 120.64, BarWidth, 60.0, 0.88, false, "BrandSpectrumSpringBrush"),
        new(4, 112.0, 127.03, BarWidth, 68.0, 0.88, false, "BrandSpectrumLimeBrush"),
        new(5, 136.0, 120.64, BarWidth, 60.0, 0.88, false, "BrandSpectrumGoldBrush"),
        new(6, 160.0, 112.96, BarWidth, 48.0, 0.88, false, "BrandSpectrumOrangeBrush"),
        new(7, 184.0, 119.35, BarWidth, 36.0, 0.88, false, "BrandSpectrumRedBrush"),
        new(8, 208.0, 125.8, BarWidth, 20.0, 0.88, false, "BrandSpectrumVioletBrush"),
    ];

    public static IReadOnlyList<RainbowLipsBarSpec> AllBars { get; } = [.. UpperBars, .. LowerBars];

    /// <summary>
    /// Returns true for the two Speech Pick engine moods that share a single phase clock
    /// so toggling between Parakeet and Whisper does not replay the initial triumph bounce.
    /// </summary>
    public static bool IsModelMood(RainbowLipsMood mood) =>
        mood is RainbowLipsMood.ModelFast or RainbowLipsMood.ModelVoice;

    /// <summary>Resolves the active mood and elapsed time, letting a 0.8 s entry burst briefly override to Happy.</summary>
    public static RainbowLipsEffectiveMood GetEffectiveMood(
        RainbowLipsMood mood,
        double sinceStateChangeSeconds,
        double? sinceBurstSeconds = null)
    {
        if (sinceBurstSeconds is { } burst && burst >= 0.0 && burst < BurstDurationSeconds)
        {
            return new RainbowLipsEffectiveMood(RainbowLipsMood.Happy, burst);
        }

        return new RainbowLipsEffectiveMood(mood, Math.Max(0.0, sinceStateChangeSeconds));
    }

    /// <summary>Evaluates all 18 bars, shimmer, glow, and global modifiers for one frame.</summary>
    public static RainbowLipsFrame EvaluateFrame(
        RainbowLipsMood mood,
        double wallSeconds,
        double sinceStateChangeSeconds,
        double? sinceBurstSeconds = null,
        double? voiceLevel = null,
        int speakingBeats = 2,
        bool reduceMotion = false,
        RainbowLipsGlideMemory? glideMemory = null)
    {
        var globalModifiers = GetGlobalModifiers(mood);
        var scales = new double[BarCount];
        var shimmers = new RainbowLipsShimmer[BarCount];

        if (reduceMotion)
        {
            glideMemory?.Reset();
            for (var n = 0; n < BarCount; n++)
            {
                var bar = AllBars[n];
                scales[n] = ComputeRestingScale(mood, bar.Index, bar.IsUpper);
                shimmers[n] = new RainbowLipsShimmer(1.0, 0.0);
            }

            return new RainbowLipsFrame(
                EffectiveMood: mood,
                MoodElapsedSeconds: 0.0,
                Scales: scales,
                Shimmers: shimmers,
                Glow: new RainbowLipsGlow(0.0, 0.0),
                GlobalModifiers: globalModifiers);
        }

        var effective = GetEffectiveMood(mood, sinceStateChangeSeconds, sinceBurstSeconds);
        var targets = new double[BarCount];
        for (var n = 0; n < BarCount; n++)
        {
            var bar = AllBars[n];
            targets[n] = ComputeTargetScale(
                effective.Mood,
                bar.Index,
                bar.IsUpper,
                wallSeconds,
                effective.ElapsedSeconds,
                voiceLevel,
                speakingBeats);
            shimmers[n] = ComputeShimmer(effective.Mood, bar.Index, wallSeconds);
        }

        var drawn = glideMemory is not null ? glideMemory.Step(targets, wallSeconds) : targets;
        var glow = ComputeGlow(effective.Mood, effective.ElapsedSeconds);

        return new RainbowLipsFrame(
            EffectiveMood: effective.Mood,
            MoodElapsedSeconds: effective.ElapsedSeconds,
            Scales: drawn,
            Shimmers: shimmers,
            Glow: glow,
            GlobalModifiers: globalModifiers);
    }

    /// <summary>Computes the unsmoothed target Y-scale for a single bar at one moment.</summary>
    public static double ComputeTargetScale(
        RainbowLipsMood mood,
        int index,
        bool isUpper,
        double wallSeconds,
        double elapsedSeconds,
        double? voiceLevel = null,
        int speakingBeats = 2)
    {
        var i = Math.Clamp(index, 0, HalfBarCount - 1);
        var t = wallSeconds;
        var elapsed = Math.Max(0.0, elapsedSeconds);

        switch (mood)
        {
            case RainbowLipsMood.Idle:
            {
                var ripple = 0.1 * Math.Sin(t * 2.1 + i * 0.62 + (isUpper ? 0.0 : 1.1));
                var breath = 0.16 * Math.Abs(Math.Sin(PositiveMod(t - IdleDelays[i], 2.8) / 2.8 * Math.PI));
                return 1.0 + ripple - breath;
            }

            case RainbowLipsMood.Denied:
            {
                var phase = PositiveMod(t, 3.0) / 3.0;
                var osc = Math.Abs(Math.Sin(phase * Math.PI));
                return isUpper ? (0.45 - 0.07 * osc) : (0.35 - 0.07 * osc);
            }

            case RainbowLipsMood.Happy:
            {
                var localT = Math.Max(0.0, elapsed - HappyDelays[i]);
                if (elapsed > 0.7)
                {
                    var fade = Math.Min(1.0, (elapsed - 0.7) / 0.3);
                    return 1.0 + 0.1 * fade * Math.Sin(t * 4.2 + i * 0.7 + (isUpper ? 0.0 : 0.9));
                }

                return OneShotBounce(localT, duration: 0.6, peakScale: isUpper ? 1.25 : 1.2);
            }

            case RainbowLipsMood.Equalizer:
            {
                const double spatialFreq = 3.0 * Math.PI / 9.0;
                const double temporalFreq = 5.0;
                var phaseOffset = isUpper ? 0.0 : Math.PI;
                var sinVal = Math.Sin(i * spatialFreq - t * temporalFreq + phaseOffset);
                const double minScale = 0.15;
                const double maxScale = 1.45;
                var scale = minScale + (maxScale - minScale) * (0.5 + 0.5 * sinVal);
                if (voiceLevel is not { } level)
                {
                    return scale;
                }

                var clampedLevel = Math.Clamp(level, 0.0, 1.0);
                return 0.3 + (scale - 0.15) * (0.12 + 0.88 * clampedLevel);
            }

            case RainbowLipsMood.Wave:
                return OneShotWave(Math.Max(0.0, elapsed - WaveDelays[i]));

            case RainbowLipsMood.Drooping:
            {
                var phase = PositiveMod(t, 2.5) / 2.5;
                return 0.3 - 0.05 * Math.Abs(Math.Sin(phase * Math.PI));
            }

            case RainbowLipsMood.Shimmer:
                return 1.0 + 0.12 * Math.Sin(t * 3.4 - i * 0.55 + (isUpper ? 0.0 : Math.PI / 2.0));

            case RainbowLipsMood.Recording:
            {
                var cfg = RecordingConfigs[i];
                var phase = PositiveMod(t - cfg.Delay, cfg.Duration) / cfg.Duration;
                return cfg.MinScale + (cfg.MaxScale - cfg.MinScale) * (0.5 + 0.5 * Math.Sin(phase * Math.PI * 2.0));
            }

            case RainbowLipsMood.Pulse:
            {
                var phase = PositiveMod(t - PulseDelays[i], 1.1) / 1.1;
                return 0.7 + 0.4 * (0.5 + 0.5 * Math.Sin(phase * Math.PI * 2.0));
            }

            case RainbowLipsMood.Smile:
            {
                var phase = PositiveMod(t, 2.2) / 2.2;
                var animatedScale = 0.9 + 0.15 * (0.5 + 0.5 * Math.Sin(phase * Math.PI * 2.0));
                return (isUpper ? SmileUpperScales[i] : SmileLowerScales[i]) * animatedScale;
            }

            case RainbowLipsMood.Heart:
                return (isUpper ? HeartUpperScales[i] : HeartLowerScales[i]) * HeartBeat(t);

            case RainbowLipsMood.Triumph:
            {
                var localT = Math.Max(0.0, elapsed - TriumphDelays[i]);
                if (localT >= 0.9)
                {
                    var settledPhase = PositiveMod(localT - 0.9, 2.0) / 2.0;
                    return 1.1 + 0.10 * Math.Sin(settledPhase * Math.PI * 2.0);
                }

                return OneShotTriumph(localT);
            }

            case RainbowLipsMood.Ready:
            {
                var rc = PositiveMod(elapsed, 2.6);
                var rd = Math.Abs(i - 4.0) * 0.02;
                static double Tap(double rcVal, double rdVal, double t0)
                {
                    var x = (rcVal - rdVal - t0) / 0.2;
                    return x > 0.0 && x < 1.0 ? Math.Sin(Math.PI * x) : 0.0;
                }

                return 1.04
                    + 0.05 * Math.Sin(t * 2.4 + i * 0.35)
                    + 0.32 * Tap(rc, rd, 0.1)
                    + 0.32 * Tap(rc, rd, 0.42);
            }

            case RainbowLipsMood.Welcome:
                return 1.0 + 0.24 * Math.Sin(t * 1000.0 / 190.0 + (isUpper ? i : 9 + i) * 0.7);

            case RainbowLipsMood.Speaking:
            {
                var beats = Math.Max(1, speakingBeats);
                var baseScale = 1.0 - 0.12 * Math.Abs(Math.Sin(PositiveMod(t - IdleDelays[i], 2.8) / 2.8 * Math.PI));
                const double beatDuration = 0.3;
                var k = elapsed / beatDuration;
                if (k < beats)
                {
                    var open = Math.Pow(Math.Sin(Math.PI * (k - Math.Floor(k))), 0.8);
                    return 0.72 + open * (0.25 + 0.8 * (1.0 - Math.Abs(i - 4.0) / 5.5));
                }

                var after = elapsed - beats * beatDuration;
                if (after < 1.6)
                {
                    var smile = isUpper ? SmileUpperScales[i] : SmileLowerScales[i];
                    var w = after < 0.3
                        ? after / 0.3
                        : after > 1.2
                            ? (1.6 - after) / 0.4
                            : 1.0;
                    return baseScale + (smile - baseScale) * w;
                }

                return baseScale;
            }

            case RainbowLipsMood.ModelFast:
            case RainbowLipsMood.ModelVoice:
            {
                var lt = Math.Max(0.0, elapsed - TriumphDelays[i]);
                if (lt < 0.9)
                {
                    return OneShotTriumph(lt);
                }

                var tt = elapsed - 0.9;
                if (mood == RainbowLipsMood.ModelFast)
                {
                    var c = PositiveMod(tt, 2.0);
                    var d = i - (c / 0.26 * 10.0 - 1.0);
                    var zip = c < 0.5 ? 0.7 * Math.Exp(-d * d / 1.4) : 0.0;
                    var settle = c >= 0.3 && c < 0.7 ? 0.08 * Math.Sin((c - 0.3) / 0.4 * Math.PI) : 0.0;
                    return 1.0 + zip + settle;
                }

                var syll = Math.Pow(Math.Abs(Math.Sin(tt * 3.1)), 0.7) * (0.7 + 0.3 * Math.Sin(tt * 1.3 + 0.8));
                var open = 0.15 + 0.85 * syll * (0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(tt * 0.9 - 1.2)));
                var ripple = 0.5 + 0.5 * Math.Sin(i * 0.9 - tt * 3.4 + (isUpper ? 0.0 : 0.7));
                return 0.62 + open * (0.3 + 0.75 * (1.0 - Math.Abs(i - 4.0) / 5.5)) * (0.65 + 0.35 * ripple);
            }

            default:
                return 1.0;
        }
    }

    /// <summary>
    /// Static resting silhouette shown under Reduce Motion.
    /// Silhouette moods (Smile, Heart, Denied, Drooping) hold their recognizable form;
    /// dynamic moods rest cleanly at 1.0.
    /// </summary>
    public static double ComputeRestingScale(RainbowLipsMood mood, int index, bool isUpper)
    {
        var i = Math.Clamp(index, 0, HalfBarCount - 1);
        return mood switch
        {
            RainbowLipsMood.Smile => isUpper ? SmileUpperScales[i] : SmileLowerScales[i],
            RainbowLipsMood.Heart => isUpper ? HeartUpperScales[i] : HeartLowerScales[i],
            RainbowLipsMood.Denied => isUpper ? 0.42 : 0.32,
            RainbowLipsMood.Drooping => 0.28,
            _ => 1.0,
        };
    }

    /// <summary>Advances one bar scale exponentially toward target at 16/s.</summary>
    public static double Glide(double drawn, double target, double dt) =>
        drawn + (target - drawn) * Math.Clamp(dt * GlideRate, 0.0, 1.0);

    /// <summary>Computes per-bar shimmer opacity and brightness highlight for the Shimmer mood.</summary>
    public static RainbowLipsShimmer ComputeShimmer(RainbowLipsMood mood, int index, double wallSeconds)
    {
        if (mood != RainbowLipsMood.Shimmer)
        {
            return new RainbowLipsShimmer(1.0, 0.0);
        }

        var i = Math.Clamp(index, 0, HalfBarCount - 1);
        var phase = PositiveMod(wallSeconds - ShimmerDelays[i], 1.8) / 1.8;
        var sinVal = Math.Sin(phase * Math.PI * 2.0);
        var brightness = sinVal > 0.0 ? sinVal * 0.4 : 0.0;
        var opacity = Math.Max(0.82, 0.92 + sinVal * 0.08);
        return new RainbowLipsShimmer(opacity, brightness);
    }

    /// <summary>Peak glow opacity for a mood (0 when the mood has no glow).</summary>
    public static double GetGlowPeak(RainbowLipsMood mood) =>
        GetGlowShape(mood)?.PeakOpacity ?? 0.0;

    /// <summary>Computes glow opacity and radius at mood-local elapsed seconds.</summary>
    public static RainbowLipsGlow ComputeGlow(RainbowLipsMood mood, double elapsedSeconds)
    {
        var shape = GetGlowShape(mood);
        if (shape is null || elapsedSeconds <= shape.Value.Delay)
        {
            return new RainbowLipsGlow(0.0, 0.0);
        }

        var cycle = PositiveMod(elapsedSeconds - shape.Value.Delay, shape.Value.Period * 2.0) / shape.Value.Period;
        var v = EaseInOut(cycle <= 1.0 ? cycle : 2.0 - cycle);
        return new RainbowLipsGlow(shape.Value.PeakOpacity * v, shape.Value.PeakRadius * v);
    }

    /// <summary>Global saturation/brightness dim applied for Denied and Drooping states.</summary>
    public static RainbowLipsGlobalModifiers GetGlobalModifiers(RainbowLipsMood mood) => mood switch
    {
        RainbowLipsMood.Denied => new(0.25, -0.15, 0.5),
        RainbowLipsMood.Drooping => new(0.20, -0.25, 0.7),
        _ => new(1.0, 0.0, 0.4),
    };

    private static GlowShape? GetGlowShape(RainbowLipsMood mood) => mood switch
    {
        RainbowLipsMood.Heart => new GlowShape(0.3, 1.2, 0.45, 10.0),
        RainbowLipsMood.Triumph or RainbowLipsMood.ModelFast or RainbowLipsMood.ModelVoice =>
            new GlowShape(0.9, 1.6, 0.5, 8.0),
        _ => null,
    };

    public static double PositiveMod(double a, double m)
    {
        var r = a % m;
        return r < 0.0 ? r + m : r;
    }

    private static double HeartBeat(double t)
    {
        var phase = PositiveMod(t, 1.2);
        if (phase < 0.08)
        {
            return 1.0 + 0.20 * EaseOut(phase / 0.08);
        }

        if (phase < 0.18)
        {
            return 1.20 - 0.25 * EaseInOut((phase - 0.08) / 0.10);
        }

        if (phase < 0.30)
        {
            return 0.95 + 0.05 * EaseInOut((phase - 0.18) / 0.12);
        }

        if (phase < 0.38)
        {
            return 1.0 + 0.10 * EaseOut((phase - 0.30) / 0.08);
        }

        if (phase < 0.48)
        {
            return 1.10 - 0.10 * EaseInOut((phase - 0.38) / 0.10);
        }

        return 1.0;
    }

    private static double OneShotBounce(double localT, double duration, double peakScale)
    {
        var p = Math.Min(localT / duration, 1.0);
        if (p < 0.30)
        {
            return 1.0 + (peakScale - 1.0) * EaseOut(p / 0.30);
        }

        if (p < 0.55)
        {
            return peakScale - (peakScale - 0.95) * EaseInOut((p - 0.30) / 0.25);
        }

        if (p < 0.75)
        {
            return 0.95 + 0.15 * EaseInOut((p - 0.55) / 0.20);
        }

        return 1.10 - 0.10 * EaseInOut((p - 0.75) / 0.25);
    }

    private static double OneShotWave(double localT)
    {
        var p = Math.Min(localT / 0.8, 1.0);
        if (p < 0.40)
        {
            return 0.4 + 0.8 * EaseOut(p / 0.40);
        }

        if (p < 0.70)
        {
            return 1.2 - 0.3 * EaseInOut((p - 0.40) / 0.30);
        }

        return 0.9 + 0.1 * EaseInOut((p - 0.70) / 0.30);
    }

    private static double OneShotTriumph(double localT)
    {
        var p = Math.Min(localT / 0.9, 1.0);
        if (p < 0.40)
        {
            return 0.5 + 0.85 * EaseOut(p / 0.40);
        }

        if (p < 0.65)
        {
            return 1.35 - 0.30 * EaseInOut((p - 0.40) / 0.25);
        }

        if (p < 0.80)
        {
            return 1.05 + 0.15 * EaseInOut((p - 0.65) / 0.15);
        }

        return 1.20 - 0.10 * EaseInOut((p - 0.80) / 0.20);
    }

    private static double EaseOut(double t) => 1.0 - Math.Pow(1.0 - t, 3.0);

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4.0 * t * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;
}
