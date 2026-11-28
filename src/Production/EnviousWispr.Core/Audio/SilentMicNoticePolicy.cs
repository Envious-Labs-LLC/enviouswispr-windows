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
        out bool isWholeZero) =>
        TrimTrailingExactZeros(samples, drainedPreRollSampleCount: 0, out isWholeZero);

    public static ReadOnlyMemory<float> TrimTrailingExactZeros(
        ReadOnlyMemory<float> samples,
        int drainedPreRollSampleCount,
        out bool isWholeZero)
    {
        if (samples.IsEmpty)
        {
            isWholeZero = false;
            return samples;
        }

        var preRoll = Math.Clamp(drainedPreRollSampleCount, 0, samples.Length);
        var postPreRoll = samples.Span[preRoll..];
        if (postPreRoll.IsEmpty)
        {
            isWholeZero = preRoll > 0;
            return isWholeZero ? ReadOnlyMemory<float>.Empty : samples;
        }

        var trailingZeros = 0;
        for (var index = postPreRoll.Length - 1; index >= 0; index--)
        {
            if (postPreRoll[index] == 0f)
            {
                trailingZeros++;
            }
            else
            {
                break;
            }
        }

        if (trailingZeros == postPreRoll.Length)
        {
            isWholeZero = true;
            return ReadOnlyMemory<float>.Empty;
        }

        isWholeZero = false;
        if (trailingZeros >= TrailingZeroTrimMinimumSamples)
        {
            return samples[..(samples.Length - trailingZeros)];
        }

        return samples;
    }
}
