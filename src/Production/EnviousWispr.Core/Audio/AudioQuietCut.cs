namespace EnviousWispr.Core.Audio;

/// <summary>
/// Cuts continuous audio near a duration limit at its quietest 100 ms valley so words stay whole.
/// </summary>
public static class AudioQuietCut
{
    public const int DefaultSampleRate = 16_000;
    public const int MaximumPieceSeconds = 60;
    public const int SearchWindowSeconds = 10;

    /// <summary>How many samples of <paramref name="audio"/> the next piece takes.</summary>
    public static int NextPieceLength(ReadOnlySpan<float> audio, bool atEnd, int sampleRate = DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        var maximumPieceSamples = checked(sampleRate * MaximumPieceSeconds);
        if (audio.Length <= maximumPieceSamples)
        {
            return atEnd ? audio.Length : 0;
        }

        var (quietestCenter, _) = FindQuietestFrame(audio, sampleRate, maximumPieceSamples);
        return quietestCenter > 0 ? quietestCenter : maximumPieceSamples;
    }

    /// <summary>
    /// Finds a verified quiet 100 ms valley inside the trailing search window before <see cref="MaximumPieceSeconds"/>.
    /// Returns false when continuous loud speech spans the entire window so streaming never cuts mid-word.
    /// </summary>
    public static bool TryFindQuietValley(
        ReadOnlySpan<float> audio,
        int sampleRate,
        out int cutLength,
        double maximumFrameRms = 0.005)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        cutLength = 0;
        var maximumPieceSamples = checked(sampleRate * MaximumPieceSeconds);
        if (audio.Length <= maximumPieceSamples)
        {
            return false;
        }

        var (quietestCenter, quietestRms) = FindQuietestFrame(audio, sampleRate, maximumPieceSamples);
        if (quietestCenter <= 0 || quietestRms > maximumFrameRms)
        {
            return false;
        }

        cutLength = quietestCenter;
        return true;
    }

    private static (int CenterSample, double Rms) FindQuietestFrame(
        ReadOnlySpan<float> audio,
        int sampleRate,
        int maximumPieceSamples)
    {
        var searchSamples = Math.Min(maximumPieceSamples, sampleRate * SearchWindowSeconds);
        var frameSamples = Math.Max(1, sampleRate / 10);
        var searchStart = maximumPieceSamples - searchSamples;
        var quietest = 0;
        var quietestRms = double.MaxValue;
        for (var frameStart = searchStart; frameStart + frameSamples <= maximumPieceSamples; frameStart += frameSamples)
        {
            var energy = 0.0;
            foreach (var sample in audio.Slice(frameStart, frameSamples))
            {
                energy += sample * (double)sample;
            }

            var rms = Math.Sqrt(energy / frameSamples);
            // The LAST of equally quiet frames, so a silence is cut at its far end and the piece keeps its words whole.
            if (rms <= quietestRms)
            {
                quietestRms = rms;
                quietest = frameStart + (frameSamples / 2);
            }
        }

        return (quietest, quietestRms);
    }

    /// <summary>A piece with nothing in it: engines invent words into silence, so it can be skipped.</summary>
    public static bool IsSilent(ReadOnlySpan<float> piece) => IsBelowPeak(piece, 0.005f);

    /// <summary>
    /// Confirmed silence strictly below Parakeet's 0.002 RMS voiced floor so quiet speech around 0.003 is never dropped.
    /// </summary>
    public static bool IsBelowVoicedFloor(ReadOnlySpan<float> piece) => IsBelowPeak(piece, 0.0015f);

    private static bool IsBelowPeak(ReadOnlySpan<float> piece, float threshold)
    {
        var peak = 0f;
        foreach (var sample in piece)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak < threshold;
    }
}
