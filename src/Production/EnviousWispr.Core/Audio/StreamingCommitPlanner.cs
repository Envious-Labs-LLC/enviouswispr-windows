namespace EnviousWispr.Core.Audio;

/// <summary>A stretch of the recording that is ready to transcribe before the user has finished.</summary>
public readonly record struct StreamingCommit(int StartSample, int EndSample)
{
    public int LengthSamples => EndSample - StartSample;
}

/// <summary>
/// Decides which of a still-running recording can be transcribed now.
/// </summary>
/// <remarks>
/// THE POINT IS THE WAIT AFTER THE KEY IS RELEASED. Today the whole recording is transcribed then,
/// so a thirty-second dictation buys thirty seconds of audio and then waits for all of it. macOS
/// transcribes while you speak, so the release costs only whatever is left.
///
/// A COMMIT MUST END WHERE NOBODY IS TALKING, which is why this needs the segmenter and could not
/// have been written first. Cutting mid-word produces two half-words at the seam, and no amount of
/// stitching afterwards recovers a syllable the recogniser never heard as part of a word.
///
/// SO A SPEECH SEGMENT IS ONLY COMMITTABLE ONCE SOMETHING FOLLOWS IT. A segment at the very end of
/// the audio so far might be a completed sentence, or might be the first half of a word the user is
/// still saying - and the two are indistinguishable until silence arrives after it. Committing the
/// last segment is the single most tempting mistake here and it is the one that corrupts words.
///
/// NOTHING IS EVER COMMITTED TWICE. The planner is told where the last commit ended and never
/// returns anything before it, so a caller polling every few hundred milliseconds accumulates each
/// stretch exactly once. The alternative - the caller deduplicating text afterwards - means guessing
/// whether a repeated phrase was a double commit or something the user actually said twice.
/// </remarks>
public static class StreamingCommitPlanner
{
    /// <summary>
    /// The shortest stretch worth sending to a recogniser on its own.
    /// </summary>
    /// <remarks>
    /// A recogniser given a fragment of a second has almost no context and produces worse text than
    /// the same audio would have contributed to a longer request. Below this it is better to wait
    /// and send more, because the goal is a faster FINAL result rather than more requests.
    /// </remarks>
    public static readonly TimeSpan MinimumCommit = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The next stretch ready to transcribe, or null when nothing is.
    /// </summary>
    /// <param name="samples">Everything captured so far.</param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="committedThrough">
    /// The sample index the previous commit ended at. Zero at the start of a recording.
    /// </param>
    /// <param name="segmenter">Where speech starts and stops.</param>
    public static StreamingCommit? NextCommit(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int committedThrough,
        SpeechSegmenter segmenter)
    {
        ArgumentNullException.ThrowIfNull(segmenter);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(committedThrough);

        if (committedThrough >= samples.Length)
        {
            return null;
        }

        var segments = segmenter.Segment(samples);
        if (segments.Count == 0)
        {
            return null;
        }

        // The LAST segment is excluded whatever it is. If it is speech it may be a word in progress;
        // if it is silence there is nothing after it to prove the speech before it has ended.
        var lastCommittable = segments.Count - 1;

        var end = 0;
        var speechSamples = 0;
        for (var index = 0; index < lastCommittable; index++)
        {
            if (!segments[index].IsSpeech)
            {
                continue;
            }

            // Commit through the END of the following silence rather than the end of the speech.
            // The audio between two words belongs to neither, and leaving it uncommitted means the
            // next commit begins with a fragment of silence that shifts every later boundary a
            // little further.
            end = segments[index + 1].EndSample;

            if (segments[index].EndSample > committedThrough)
            {
                speechSamples += segments[index].LengthSamples;
            }
        }

        if (end <= committedThrough)
        {
            return null;
        }

        // MEASURED ON THE SPEECH, NOT ON THE AUDIO. The minimum exists because a recogniser given a
        // fragment has almost no context, and context comes from words rather than from seconds -
        // so 1.8 seconds containing 600ms of speech either side of a long pause is still a fragment.
        // The first version measured the whole commit and would have sent exactly that.
        //
        // Found by a test asserting a short utterance waits. It failed on its first run, and the
        // failure was the policy rather than the expectation.
        var minimumSamples = (int)(MinimumCommit.TotalSeconds * sampleRate);
        return speechSamples < minimumSamples
            ? null
            : new StreamingCommit(committedThrough, end);
    }
}
