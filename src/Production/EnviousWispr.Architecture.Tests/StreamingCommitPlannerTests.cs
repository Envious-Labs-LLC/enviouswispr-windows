using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Committing the wrong stretch corrupts the user's words, so most of these assert it waits.
/// </summary>
public sealed class StreamingCommitPlannerTests
{
    private const int SampleRate = 16_000;

    private static SpeechSegmenter Segmenter() => new(SampleRate, TimeSpan.FromMilliseconds(400));

    private static float[] Build(params (bool IsSpeech, int Milliseconds)[] parts)
    {
        var samples = new float[parts.Sum(part => SampleRate * part.Milliseconds / 1000)];
        var cursor = 0;
        foreach (var (isSpeech, milliseconds) in parts)
        {
            var length = SampleRate * milliseconds / 1000;
            for (var i = 0; i < length; i++)
            {
                var amplitude = isSpeech ? 0.2f : 0.001f;
                samples[cursor + i] = (i % 2 == 0) ? amplitude : -amplitude;
            }

            cursor += length;
        }

        return samples;
    }

    private static StreamingCommit? Plan(float[] audio, int committedThrough = 0) =>
        StreamingCommitPlanner.NextCommit(audio, SampleRate, committedThrough, Segmenter());

    /// <summary>
    /// The control for the whole file. A finished sentence with silence after it must actually
    /// commit, or a planner that never committed would pass every other test here and the feature
    /// would do nothing.
    /// </summary>
    [Fact]
    public void AFinishedSentenceFollowedBySilenceIsCommitted()
    {
        var commit = Plan(Build((false, 200), (true, 3000), (false, 1200), (true, 500)));

        Assert.NotNull(commit);
        Assert.Equal(0, commit!.Value.StartSample);
        Assert.True(commit.Value.LengthSamples > 0);
    }

    /// <summary>
    /// The most tempting mistake in this file. Speech at the very end might be a completed sentence
    /// or the first half of a word the user is still saying, and the two are indistinguishable
    /// until silence arrives after it.
    /// </summary>
    [Fact]
    public void SpeechStillInProgressIsNeverCommitted()
    {
        Assert.Null(Plan(Build((false, 200), (true, 4000))));
    }

    /// <summary>
    /// Nor is speech followed by a pause too short to be its end - that pause is between two words,
    /// and the segmenter treats it as one stretch for exactly that reason.
    /// </summary>
    [Fact]
    public void SpeechFollowedByAGapBetweenWordsIsNotCommitted()
    {
        Assert.Null(Plan(Build((false, 200), (true, 2000), (false, 150))));
    }

    [Fact]
    public void SilenceAloneCommitsNothing()
    {
        Assert.Null(Plan(Build((false, 5000))));
    }

    [Fact]
    public void AStretchShorterThanTheMinimumWaitsForMore()
    {
        // 600ms of speech, properly ended, but not worth a request on its own.
        Assert.Null(Plan(Build((false, 200), (true, 600), (false, 1000), (true, 500))));
    }

    /// <summary>
    /// Nothing is ever committed twice. A caller polling every few hundred milliseconds must
    /// accumulate each stretch exactly once, or it has to guess afterwards whether a repeated
    /// phrase was a double commit or something the user actually said twice.
    /// </summary>
    [Fact]
    public void AlreadyCommittedAudioIsNeverReturnedAgain()
    {
        var audio = Build((false, 200), (true, 3000), (false, 1200), (true, 500));

        var first = Plan(audio);
        Assert.NotNull(first);

        var second = Plan(audio, first!.Value.EndSample);
        Assert.Null(second);
    }

    /// <summary>
    /// The commit begins exactly where the last one ended, with no gap and no overlap. A gap loses
    /// audio silently - it reads as the speaker having paused - and an overlap repeats words.
    /// </summary>
    [Fact]
    public void ASecondCommitBeginsWhereTheFirstEnded()
    {
        var first = Plan(Build((false, 200), (true, 3000), (false, 1200), (true, 500)));
        Assert.NotNull(first);

        var longer = Build(
            (false, 200), (true, 3000), (false, 1200), (true, 3000), (false, 1200), (true, 500));

        var second = Plan(longer, first!.Value.EndSample);

        Assert.NotNull(second);
        Assert.Equal(first.Value.EndSample, second!.Value.StartSample);
    }

    /// <summary>
    /// A commit ends after the silence rather than at the last word. The audio between two words
    /// belongs to neither, and leaving it uncommitted makes the next commit start with a fragment
    /// of silence that shifts every later boundary a little further.
    /// </summary>
    [Fact]
    public void ACommitEndsAfterTheSilenceRatherThanAtTheLastWord()
    {
        var audio = Build((false, 200), (true, 3000), (false, 1200), (true, 500));

        var commit = Plan(audio);

        Assert.NotNull(commit);
        // Speech ends around 3.2s; the silence after it runs to about 4.4s. Ending at the word
        // would land near 51,200 samples and ending after the silence near 70,400.
        Assert.True(
            commit!.Value.EndSample > (int)(3.5 * SampleRate),
            $"Committed through {commit.Value.EndSample}, which is at the word rather than past the silence.");
    }

    [Fact]
    public void EmptyAudioCommitsNothingRatherThanCrashing()
    {
        Assert.Null(StreamingCommitPlanner.NextCommit([], SampleRate, 0, Segmenter()));
    }

    [Fact]
    public void ACommittedPointBeyondTheAudioCommitsNothing()
    {
        var audio = Build((false, 200), (true, 3000), (false, 1200));

        Assert.Null(Plan(audio, audio.Length));
        Assert.Null(Plan(audio, audio.Length + 1_000));
    }
}
