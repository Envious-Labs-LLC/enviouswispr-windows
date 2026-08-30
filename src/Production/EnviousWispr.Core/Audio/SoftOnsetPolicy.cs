namespace EnviousWispr.Core.Audio;

/// <summary>
/// Decides when speech detection has probably eaten a quiet first word.
/// </summary>
/// <remarks>
/// A SOFT FIRST WORD IS NOT SILENCE, BUT IT MEASURES LIKE ONE. "Okay", "so", a name beginning with a
/// vowel - spoken at the volume people start a sentence with, before they have quite committed to
/// it. Speech detection judges by energy, so it puts that word in the silence before the speech, and
/// everything that acts on those boundaries then drops it.
///
/// ON WINDOWS THE LOSS HAPPENS IN THE STREAMING HEAD START. Recognition begins before the key is
/// released, and each committed stretch starts at the first detected speech. Audio before that point
/// is never transcribed: not in the head start, which begins later, and not in the tail, which
/// begins where the head start ended. So a word the detector missed is gone from both halves and
/// nothing downstream can notice, because the text simply never contained it.
///
/// THE ANSWER IS TO DISTRUST THE HEAD START, NOT TO RE-DETECT. Whether a quiet sound was a word is
/// not a question this code can answer; whether the SHAPE of this take is the shape the mistake
/// takes, it can. When it is, the whole recording is transcribed instead, which costs the head
/// start's saved seconds and keeps the word.
///
/// THE FOUR CONDITIONS ARE THE macOS ONES, DELIBERATELY UNCHANGED. They are narrow on purpose: a
/// long dictation has too much to gain from streaming to give it up over a fraction of a second, and
/// a take with no early speech at all was never at risk. Copying the numbers rather than choosing
/// new ones is what makes the two platforms behave the same way on the same recording, which is the
/// whole point of matching a behaviour rather than reinventing it.
/// </remarks>
public static class SoftOnsetPolicy
{
    /// <summary>Below this there is not enough recording to judge anything.</summary>
    public const int MinimumSamples = 16_000;

    /// <summary>Above this the take is long enough that streaming is worth more than the risk.</summary>
    public static readonly TimeSpan LongestProtectedTake = TimeSpan.FromSeconds(8);

    /// <summary>Speech starting later than this was not an onset problem.</summary>
    public static readonly TimeSpan LatestProtectedOnset = TimeSpan.FromSeconds(2);

    /// <summary>How much of the recording must be dropped before it looks like a lost word.</summary>
    public const double DroppedFraction = 0.25;

    /// <summary>Whether the whole recording should be transcribed instead of the head start.</summary>
    /// <param name="rawSampleCount">Every sample captured.</param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="firstSpeechSample">Where detection says the speech begins.</param>
    /// <param name="droppedSampleCount">How many samples the head start never covered.</param>
    /// <remarks>
    /// EVERY CONDITION HAS TO HOLD, and each one rules out a different innocent recording: too short
    /// to judge, long enough that streaming matters more, speech that started late for an ordinary
    /// reason, and a drop small enough to be the pause before someone speaks.
    /// </remarks>
    public static bool ShouldUseWholeRecording(
        int rawSampleCount,
        int sampleRate,
        int firstSpeechSample,
        int droppedSampleCount)
    {
        if (sampleRate <= 0 || rawSampleCount < MinimumSamples ||
            firstSpeechSample < 0 || droppedSampleCount <= 0)
        {
            return false;
        }

        var duration = TimeSpan.FromSeconds((double)rawSampleCount / sampleRate);
        if (duration > LongestProtectedTake)
        {
            return false;
        }

        var onset = TimeSpan.FromSeconds((double)firstSpeechSample / sampleRate);
        if (onset >= LatestProtectedOnset)
        {
            return false;
        }

        return droppedSampleCount >= rawSampleCount * DroppedFraction;
    }
}
