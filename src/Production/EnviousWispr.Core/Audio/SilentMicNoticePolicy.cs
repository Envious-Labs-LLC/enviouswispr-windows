namespace EnviousWispr.Core.Audio;

/// <summary>Evaluates live exact-zero runs for silent-microphone notices and trims trailing exact-zero audio (#4841).</summary>
public static class SilentMicNoticePolicy
{
    public const int CalmMidTakeSamples = 80_000;
    public const int CalmSinceStartSamples = 96_000;
    public const int StrongSamples = 240_000;
    public const int TrailingZeroTrimMinimumSamples = 16_000;

    public static MicSilenceLevel Evaluate(ExactZeroRun? run, bool warningsEnabled)
    {
        if (!warningsEnabled || run is not { SampleCount: > 0 } current)
        {
            return MicSilenceLevel.None;
        }

        if (current.SampleCount >= StrongSamples)
        {
            return MicSilenceLevel.Strong;
        }

        var calmThreshold = current.SinceTakeStart
            ? CalmSinceStartSamples
            : CalmMidTakeSamples;
        return current.SampleCount >= calmThreshold
            ? MicSilenceLevel.Calm
            : MicSilenceLevel.None;
    }

    public static ReadOnlyMemory<float> TrimTrailingExactZeros(
        ReadOnlyMemory<float> samples,
        out bool isWholeZero)
    {
        if (samples.IsEmpty)
        {
            isWholeZero = false;
            return samples;
        }

        var span = samples.Span;
        var trailingZeros = 0;
        for (var index = span.Length - 1; index >= 0; index--)
        {
            if (span[index] == 0f)
            {
                trailingZeros++;
            }
            else
            {
                break;
            }
        }

        if (trailingZeros == span.Length)
        {
            isWholeZero = true;
            return ReadOnlyMemory<float>.Empty;
        }

        isWholeZero = false;
        if (trailingZeros >= TrailingZeroTrimMinimumSamples)
        {
            return samples[..(span.Length - trailingZeros)];
        }

        return samples;
    }
}
