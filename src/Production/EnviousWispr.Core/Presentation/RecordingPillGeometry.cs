namespace EnviousWispr.Core.Presentation;

/// <summary>One bar of a drawn mark, in layout units, relative to the mark's own top-left corner.</summary>
public readonly record struct PillBarRect(double X, double Y, double Width, double Height);

/// <summary>
/// The arithmetic the recording pill draws with, kept out of the window so it can be asserted.
/// </summary>
/// <remarks>
/// EVERY NUMBER HERE IS READ OFF THE macOS SOURCE, NOT DESIGNED HERE. The lips mark is
/// <c>RainbowLipsIcon.swift</c>, the meter is <c>RainbowLevelMeter.swift</c>, the clock is
/// <c>FormattingConstants.formatDuration</c>. A pill drawn by a XAML canvas cannot be asked what it
/// drew, so the shapes are computed here and the window only places them.
/// </remarks>
public static class RecordingPillGeometry
{
    /// <summary>How many colours the brand spectrum has, red through violet.</summary>
    public const int SpectrumStops = 9;

    /// <summary>A meter bar's share of its strip at silence. Non-zero so a pause never reads as "stopped hearing me".</summary>
    public const double MeterSilenceFraction = 0.14;

    /// <summary>The additional share a meter bar gains at full level.</summary>
    public const double MeterPeakFraction = 0.86;

    /// <summary>The hairline's dim end while it breathes.</summary>
    public const double HairlineDimOpacity = 0.3;

    /// <summary>The hairline's bright end while it breathes.</summary>
    public const double HairlineBrightOpacity = 0.65;

    /// <summary>The hairline when it holds still: the Reading Well, or animations switched off.</summary>
    /// <remarks>Mid-way between the two ends, so a still line is no dimmer on average than a moving one.</remarks>
    public const double HairlineSteadyOpacity = 0.5;

    /// <summary>How many bars the lips mark draws: nine upper and nine lower.</summary>
    public const int LipsBarCount = 18;

    /// <summary>The lips mark is authored on a 64 by 64 grid and scaled to the size it is drawn at.</summary>
    private const double LipsGrid = 64;

    private static readonly (double X, double Y, double H)[] UpperLips =
    [
        (4, 22.25, 5), (10, 17.6375, 8), (16, 12.04, 12), (22, 16.96, 9), (28, 21.5575, 6),
        (34, 16.96, 9), (40, 12.04, 12), (46, 17.6375, 8), (52, 22.25, 5),
    ];

    private static readonly (double X, double Y, double H)[] LowerLips =
    [
        (4, 30.25, 5), (10, 28.6375, 9), (16, 27.04, 12), (22, 28.96, 15), (28, 30.5575, 17),
        (34, 28.96, 15), (40, 27.04, 12), (46, 28.6375, 9), (52, 30.25, 5),
    ];

    /// <summary>Centre bars react most and the edges least, so the mouth opens rather than lifts.</summary>
    private static readonly double[] LipsSensitivity = [0.70, 0.80, 0.90, 0.95, 1.00, 0.95, 0.90, 0.80, 0.70];

    // Full curved lips even at silence, opening smoothly when speaking without collapsing into specks.
    private const double LipsSilenceScale = 0.85;
    private const double LipsPeakRange = 0.60;
    private const double LipsMaxSeparation = 3.5;
    private const double LipsBarWidth = 4.5;

    /// <summary>The corner radius of a lips bar at <paramref name="size"/>.</summary>
    public static double LipsCornerRadius(double size) => 1.5 * size / LipsGrid;

    /// <summary>Where one lips bar sits at this audio level.</summary>
    /// <param name="index">0 to 8 are the upper lip left to right, 9 to 17 the lower lip left to right.</param>
    /// <param name="level">Normalised audio level; clamped, and silence when it is not a number.</param>
    /// <param name="size">The square the mark is drawn in.</param>
    /// <remarks>
    /// THE LOWER LIP READS ITS SENSITIVITY MIRRORED, so the two halves move in counterpoint rather
    /// than in lockstep. At silence the bars sit compressed (lips closed); at full level the centre
    /// bars stretch and the two lips part.
    /// </remarks>
    public static PillBarRect LipsBar(int index, float level, double size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, LipsBarCount);
        var scale = size / LipsGrid;
        var clamped = float.IsNaN(level) ? 0d : Math.Clamp(level, 0f, 1f);
        var width = LipsBarWidth * scale;
        if (index < UpperLips.Length)
        {
            var bar = UpperLips[index];
            var sensitivity = LipsSensitivity[index];
            var height = bar.H * scale * (LipsSilenceScale + LipsPeakRange * clamped * sensitivity);
            var bottom = (bar.Y + bar.H) * scale - LipsMaxSeparation * scale * clamped * sensitivity;
            return new PillBarRect(bar.X * scale, bottom - height, width, height);
        }

        var lowerIndex = index - UpperLips.Length;
        var lower = LowerLips[lowerIndex];
        var mirrored = LipsSensitivity[UpperLips.Length - 1 - lowerIndex];
        var lowerHeight = lower.H * scale * (LipsSilenceScale + LipsPeakRange * clamped * mirrored);
        var top = lower.Y * scale + LipsMaxSeparation * scale * clamped * mirrored;
        return new PillBarRect(lower.X * scale, top, width, lowerHeight);
    }

    /// <summary>The height of one meter bar in a strip this tall.</summary>
    public static double MeterBarHeight(float level, double stripHeight)
    {
        var clamped = float.IsNaN(level) ? 0d : Math.Clamp(level, 0f, 1f);
        return stripHeight * (MeterSilenceFraction + MeterPeakFraction * clamped);
    }

    /// <summary>Where bar <paramref name="index"/> of <paramref name="count"/> falls on the spectrum.</summary>
    /// <returns>The two adjacent spectrum stops and how far towards the upper one to blend.</returns>
    /// <remarks>
    /// POSITIONAL, NOT PER SAMPLE. The colours stay still while the heights scroll through them, so
    /// the strip keeps reading as the brand spectrum rather than as a progress bar. One sweep red to
    /// violet across the whole strip, never the nine colours repeated.
    /// </remarks>
    public static (int Lower, int Upper, double Fraction) SpectrumPosition(int index, int count)
    {
        if (count <= 1)
        {
            return (0, 0, 0);
        }

        var t = (double)Math.Clamp(index, 0, count - 1) / (count - 1);
        var scaled = t * (SpectrumStops - 1);
        var lower = (int)scaled;
        var upper = Math.Min(lower + 1, SpectrumStops - 1);
        return (lower, upper, scaled - lower);
    }

    /// <summary>The recording clock, as macOS writes it: minutes, a colon, two-digit seconds.</summary>
    /// <remarks>"0:00", "2:07", "61:05". Minutes are not padded and are not capped at an hour.</remarks>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = elapsed <= TimeSpan.Zero ? 0L : (long)elapsed.TotalSeconds;
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }
}
