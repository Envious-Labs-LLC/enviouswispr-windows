using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Where the segmenter says speech starts and stops, on audio whose answer is known by construction.
/// </summary>
/// <remarks>
/// Every input here is BUILT, not recorded, and that is the point: the expected boundaries come from
/// the generator's own arithmetic and not from running the segmenter and writing down what it said.
/// A fixture whose expectation is the subject's own output can only prove the subject agrees with
/// itself.
/// </remarks>
public sealed class SpeechSegmenterTests
{
    private const int SampleRate = 16_000;

    private static SpeechSegmenter Segmenter() =>
        new(SampleRate, TimeSpan.FromMilliseconds(400));

    /// <summary>Quiet room tone at a realistic level, so the floor is not digital silence.</summary>
    private static void FillNoise(float[] samples, int start, int length)
    {
        for (var i = 0; i < length; i++)
        {
            // Deterministic, and alternating so its RMS is the amplitude rather than zero.
            samples[start + i] = (i % 2 == 0) ? 0.001f : -0.001f;
        }
    }

    private static void FillSpeech(float[] samples, int start, int length)
    {
        for (var i = 0; i < length; i++)
        {
            samples[start + i] = (i % 2 == 0) ? 0.2f : -0.2f;
        }
    }

    private static float[] Build(params (bool IsSpeech, int Milliseconds)[] parts)
    {
        var total = parts.Sum(part => SampleRate * part.Milliseconds / 1000);
        var samples = new float[total];
        var cursor = 0;
        foreach (var (isSpeech, milliseconds) in parts)
        {
            var length = SampleRate * milliseconds / 1000;
            if (isSpeech)
            {
                FillSpeech(samples, cursor, length);
            }
            else
            {
                FillNoise(samples, cursor, length);
            }

            cursor += length;
        }

        return samples;
    }

    [Fact]
    public void SilenceThenSpeechThenSilenceIsThreeSegments()
    {
        var audio = Build((false, 500), (true, 1000), (false, 1000));

        var segments = Segmenter().Segment(audio);

        Assert.Equal(3, segments.Count);
        Assert.False(segments[0].IsSpeech);
        Assert.True(segments[1].IsSpeech);
        Assert.False(segments[2].IsSpeech);
    }

    /// <summary>
    /// The control for every other row here. Uniform room tone must produce NO speech, or a
    /// segmenter that called everything speech would pass all the positive cases above.
    /// </summary>
    [Fact]
    public void RoomToneAloneContainsNoSpeech()
    {
        var audio = Build((false, 3000));

        var segments = Segmenter().Segment(audio);

        Assert.DoesNotContain(segments, segment => segment.IsSpeech);
    }

    /// <summary>
    /// The second control, in the other direction: continuous speech must not be chopped up.
    /// </summary>
    [Fact]
    public void ContinuousSpeechIsOneSegment()
    {
        var audio = Build((true, 3000));

        var segments = Segmenter().Segment(audio);

        Assert.Single(segments);
        Assert.True(segments[0].IsSpeech);
    }

    /// <summary>
    /// A short gap between words must NOT end a segment. This is the whole reason for hysteresis:
    /// without it, a boundary lands between the two halves of a word.
    /// </summary>
    [Fact]
    public void AGapBetweenWordsDoesNotEndTheSpeech()
    {
        var audio = Build((true, 600), (false, 150), (true, 600));

        var speech = Segmenter().Segment(audio).Count(segment => segment.IsSpeech);

        Assert.Equal(1, speech);
    }

    /// <summary>
    /// A gap between sentences DOES. Paired with the test above, this is what pins the threshold to
    /// a real duration rather than to whatever the implementation happens to do.
    /// </summary>
    [Fact]
    public void AGapBetweenSentencesEndsTheSpeech()
    {
        var audio = Build((true, 600), (false, 900), (true, 600));

        var speech = Segmenter().Segment(audio).Count(segment => segment.IsSpeech);

        Assert.Equal(2, speech);
    }

    /// <summary>
    /// Segments must be contiguous and cover every sample. A caller concatenating the speech
    /// stretches would lose audio in any gap, and the loss would be invisible in the output - it
    /// would read as the speaker having paused.
    /// </summary>
    [Theory]
    [InlineData(500, 1000, 1000)]
    [InlineData(0, 2000, 0)]
    [InlineData(120, 80, 3000)]
    public void SegmentsCoverEverySampleWithNoGapAndNoOverlap(int lead, int speech, int tail)
    {
        var audio = Build((false, lead), (true, speech), (false, tail));

        var segments = Segmenter().Segment(audio);

        Assert.Equal(0, segments[0].StartSample);
        Assert.Equal(audio.Length, segments[^1].EndSample);
        for (var i = 1; i < segments.Count; i++)
        {
            Assert.Equal(segments[i - 1].EndSample, segments[i].StartSample);
        }
    }

    /// <summary>
    /// The tail must reach the LAST SAMPLE, not the last whole frame. A recording whose length is
    /// not a whole number of frames is the normal case, and dropping the remainder would clip the
    /// final word by up to a frame in nearly every dictation.
    /// </summary>
    [Fact]
    public void APartialFrameAtTheEndIsNotDropped()
    {
        var audio = Build((false, 200), (true, 1000));
        var withRemainder = audio.Concat(new float[SampleRate * 7 / 1000]).ToArray();

        var segments = Segmenter().Segment(withRemainder);

        Assert.Equal(withRemainder.Length, segments[^1].EndSample);
    }

    [Fact]
    public void TrailingSilenceIsZeroWhileSomeoneIsStillSpeaking()
    {
        var audio = Build((false, 300), (true, 1500));

        Assert.Equal(TimeSpan.Zero, Segmenter().TrailingSilence(audio));
    }

    /// <summary>
    /// Auto-stop asks this question, so it has to be a DURATION rather than a yes or no. A caller
    /// reading "is the last segment silence" would stop the moment anyone drew breath.
    /// </summary>
    [Fact]
    public void TrailingSilenceGrowsWithThePauseAtTheEnd()
    {
        var shortPause = Segmenter().TrailingSilence(Build((true, 1000), (false, 700)));
        var longPause = Segmenter().TrailingSilence(Build((true, 1000), (false, 2000)));

        Assert.True(shortPause > TimeSpan.Zero);
        Assert.True(
            longPause > shortPause,
            $"A longer pause reported {longPause.TotalMilliseconds}ms, not more than {shortPause.TotalMilliseconds}ms.");
    }

    [Fact]
    public void EmptyAudioIsNoSegmentsRatherThanACrash()
    {
        Assert.Empty(Segmenter().Segment([]));
    }

    [Fact]
    public void AudioShorterThanOneFrameIsReportedAsSilenceRatherThanDropped()
    {
        var segments = Segmenter().Segment(new float[64]);

        Assert.Single(segments);
        Assert.False(segments[0].IsSpeech);
        Assert.Equal(64, segments[0].EndSample);
    }
}
