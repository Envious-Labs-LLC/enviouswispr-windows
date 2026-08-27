namespace EnviousWispr.Core.Audio;

/// <summary>A stretch of the recording, in samples, and whether anyone was speaking during it.</summary>
public readonly record struct SpeechSegment(int StartSample, int EndSample, bool IsSpeech)
{
    public int LengthSamples => EndSample - StartSample;
}

/// <summary>
/// Finds where speech starts and stops inside captured audio, by loudness.
/// </summary>
/// <remarks>
/// WHAT THIS IS AND IS NOT. This is an ENERGY segmenter with hysteresis and an adaptive floor. It
/// is not a neural voice-activity detector and must never be described as one: it hears a slammed
/// door as speech and a whisper under a fan as silence. macOS ships a neural detector, and naming
/// this one after that one would be the kind of claim nobody can check by looking.
///
/// WHY IT EXISTS. Two things need it. A recording in toggle mode should be able to end when the
/// speaker stops, and a transcription that wants to work DURING the recording needs somewhere safe
/// to cut. Both questions are "where is nobody talking", and answering it twice in two places is
/// how the two answers start disagreeing.
///
/// HYSTERESIS IS THE WHOLE DESIGN. A single threshold flickers: any level near it produces speech,
/// silence, speech, silence, several times a second, and a chunk boundary placed on that flicker
/// lands mid-word. Speech must cross a HIGHER bar to begin than it has to cross to continue, and a
/// pause must last a while before it counts, so the boundaries land where a person would put them.
///
/// THE FLOOR ADAPTS DOWNWARD ONLY. Room noise sets it, and it tracks the quietest thing heard so
/// far rather than a running average, because a running average is dragged upward by the speech it
/// is supposed to be distinguishing from silence - which raises the bar exactly when someone is
/// talking, and then everything after a loud passage reads as silence.
/// </remarks>
public sealed class SpeechSegmenter
{
    /// <summary>How much of the recording one loudness reading covers.</summary>
    public const int FrameMilliseconds = 20;

    /// <summary>How far above the noise floor a sound must be to START a stretch of speech.</summary>
    private const double SpeechEntryFactor = 3.0;

    /// <summary>How far above the floor it must stay to CONTINUE one. Lower on purpose.</summary>
    private const double SpeechExitFactor = 1.8;

    /// <summary>
    /// The quietest floor considered real, so a digitally silent lead-in cannot make every later
    /// sample read as speech by dividing against nothing.
    /// </summary>
    private const double MinimumNoiseFloor = 1e-4;

    /// <summary>
    /// The loudest thing that may be treated as ROOM NOISE.
    /// </summary>
    /// <remarks>
    /// Without this the segmenter cannot hear speech in a recording that contains no silence,
    /// because the floor is the quietest frame and in that recording the quietest frame is speech.
    /// The entry bar then sits above everything present and the whole clip reads as silence.
    ///
    /// That is not a hypothetical: it was the FIRST test to fail, and it failed for exactly this
    /// reason. The control that caught it - continuous speech must be one speech segment - was
    /// written to prove the segmenter could not simply call everything silence, and it did its job
    /// on the first run.
    ///
    /// No room is this loud. Capping here means an all-speech clip is measured against a plausible
    /// room rather than against itself.
    /// </remarks>
    private const double MaximumNoiseFloor = 0.02;

    private readonly int _sampleRate;
    private readonly int _hangoverFrames;

    /// <param name="sampleRate">Samples per second of the audio being segmented.</param>
    /// <param name="trailingSilence">
    /// How long a pause must last before it ends a stretch of speech. Shorter than the gap between
    /// sentences and longer than the gap between words, or the segmenter cuts people off mid-thought.
    /// </param>
    public SpeechSegmenter(int sampleRate, TimeSpan trailingSilence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(trailingSilence, TimeSpan.Zero);

        _sampleRate = sampleRate;
        _hangoverFrames = Math.Max(
            1,
            (int)(trailingSilence.TotalMilliseconds / FrameMilliseconds));
    }

    /// <summary>
    /// Splits the audio into consecutive speech and silence stretches, covering every sample.
    /// </summary>
    /// <remarks>
    /// The returned segments are contiguous and exhaustive: the first starts at sample 0, each
    /// begins where the previous ended, and the last ends at the final sample. A caller that
    /// concatenates the speech stretches must not silently lose audio between them, and a gap
    /// would be invisible in the output - it would read as the speaker having paused.
    /// </remarks>
    public IReadOnlyList<SpeechSegment> Segment(ReadOnlySpan<float> samples)
    {
        var frameLength = _sampleRate * FrameMilliseconds / 1000;
        if (frameLength <= 0 || samples.Length < frameLength)
        {
            return samples.Length == 0
                ? []
                : [new SpeechSegment(0, samples.Length, IsSpeech: false)];
        }

        var frameCount = samples.Length / frameLength;
        var loudness = new double[frameCount];
        var floor = double.MaxValue;
        for (var frame = 0; frame < frameCount; frame++)
        {
            loudness[frame] = RootMeanSquare(samples.Slice(frame * frameLength, frameLength));
            floor = Math.Min(floor, loudness[frame]);
        }

        floor = Math.Clamp(floor, MinimumNoiseFloor, MaximumNoiseFloor);
        var entry = floor * SpeechEntryFactor;
        var exit = floor * SpeechExitFactor;

        var segments = new List<SpeechSegment>();
        var speaking = false;
        var quietFrames = 0;
        var segmentStartFrame = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            if (!speaking)
            {
                if (loudness[frame] < entry)
                {
                    continue;
                }

                if (frame > segmentStartFrame)
                {
                    segments.Add(Frames(segmentStartFrame, frame, frameLength, isSpeech: false));
                }

                segmentStartFrame = frame;
                speaking = true;
                quietFrames = 0;
                continue;
            }

            if (loudness[frame] >= exit)
            {
                quietFrames = 0;
                continue;
            }

            // The pause has to LAST. Ending a segment on the first quiet frame cuts between the
            // two halves of a word, which is exactly what hysteresis exists to prevent.
            if (++quietFrames < _hangoverFrames)
            {
                continue;
            }

            var speechEnd = frame - quietFrames + 1;
            segments.Add(Frames(segmentStartFrame, speechEnd, frameLength, isSpeech: true));
            segmentStartFrame = speechEnd;
            speaking = false;
            quietFrames = 0;
        }

        // The tail, and it must reach the LAST SAMPLE rather than the last whole frame. A partial
        // frame at the end is real audio, and dropping it would clip the final word by up to 20ms
        // in every recording that does not divide evenly - which is nearly all of them.
        segments.Add(new SpeechSegment(
            segmentStartFrame * frameLength,
            samples.Length,
            speaking));

        return segments;
    }

    /// <summary>
    /// How long the recording has been quiet at its end. Zero while someone is still speaking.
    /// </summary>
    /// <remarks>
    /// This is the question auto-stop asks, and it is deliberately NOT "is the last segment
    /// silence" - a caller reading that would stop the moment anyone drew breath. It reports a
    /// duration so the decision to stop lives with the caller and its own threshold.
    /// </remarks>
    public TimeSpan TrailingSilence(ReadOnlySpan<float> samples)
    {
        var segments = Segment(samples);
        if (segments.Count == 0 || segments[^1].IsSpeech)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds((double)segments[^1].LengthSamples / _sampleRate);
    }

    private static SpeechSegment Frames(int startFrame, int endFrame, int frameLength, bool isSpeech) =>
        new(startFrame * frameLength, endFrame * frameLength, isSpeech);

    private static double RootMeanSquare(ReadOnlySpan<float> frame)
    {
        double sumSquares = 0;
        foreach (var sample in frame)
        {
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / frame.Length);
    }
}
