namespace EnviousWispr.Core.Audio;

/// <summary>How much of a recording exists yet, given how long it has been running.</summary>
/// <remarks>
/// A FIXTURE THAT ARRIVES INSTANTLY MEASURES A RECORDING NOBODY MAKES. The public-fixture capture used
/// by the journey harness reads its whole WAV at construction and hands all of it back on the first
/// snapshot - so anything reading the recording mid-take sees a complete one half a second in, rather
/// than the half second a microphone would have produced.
///
/// IT PRODUCED A WRONG NUMBER BEFORE IT PRODUCED THIS FILE. The streaming head start was measured
/// against that capture and reported cutting the wait after release roughly in half. The direction was
/// right and the magnitude was not: the head start had the entire recording available at its first
/// poll, committed nearly all of it at once, and left the final pass with almost nothing to do. The
/// log said so plainly - one commit 786 ms into a five second take and then silence - and was read
/// past. Ref: #96, corrected there; #85, which asks for exactly this technique.
///
/// THE FINAL CAPTURE IS DELIBERATELY UNAFFECTED. Stopping still returns the whole fixture, so every
/// transcript assertion in the harness keeps its meaning. What changes is only what a reader
/// mid-recording can see, which is the thing that was pretending.
/// </remarks>
public static class ArrivedAudio
{
    /// <summary>Samples a capture of <paramref name="totalSamples"/> would hold after this long.</summary>
    /// <remarks>
    /// CLAMPED AT BOTH ENDS. A negative elapsed time is a clock going backwards rather than a
    /// recording that has not started, and a take held longer than the fixture is the ordinary case
    /// once the audio runs out - neither may produce an index outside the buffer.
    /// </remarks>
    public static int Count(TimeSpan elapsed, int totalSamples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var arrived = elapsed.TotalSeconds * sampleRate;
        return arrived >= totalSamples ? totalSamples : (int)arrived;
    }
}
