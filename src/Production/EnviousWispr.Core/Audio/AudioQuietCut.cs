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

        var searchSamples = Math.Min(maximumPieceSamples, sampleRate * SearchWindowSeconds);
        var frameSamples = Math.Max(1, sampleRate / 10);
        var searchStart = maximumPieceSamples - searchSamples;
        var quietest = maximumPieceSamples;
        var quietestEnergy = double.MaxValue;
        for (var frameStart = searchStart; frameStart + frameSamples <= maximumPieceSamples; frameStart += frameSamples)
        {
            var energy = 0.0;
            foreach (var sample in audio.Slice(frameStart, frameSamples))
            {
                energy += sample * (double)sample;
            }

            // The LAST of equally quiet frames, so a silence is cut at its far end and the piece keeps its words whole.
            if (energy <= quietestEnergy)
            {
                quietestEnergy = energy;
                quietest = frameStart + (frameSamples / 2);
            }
        }

        return quietest;
    }

    /// <summary>A piece with nothing in it: engines invent words into silence, so it can be skipped.</summary>
    public static bool IsSilent(ReadOnlySpan<float> piece)
    {
        var peak = 0f;
        foreach (var sample in piece)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak < 0.005f;
    }
}
