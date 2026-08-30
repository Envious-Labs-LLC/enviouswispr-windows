using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A quiet first word is kept, and an ordinary recording still gets its head start.
/// </summary>
/// <remarks>
/// EACH CASE TURNS EXACTLY ONE CONDITION, so a threshold that moves fails the test that owns it
/// rather than all of them at once. The numbers are the macOS ones and the point of the tests is
/// that they stay the macOS ones: a recording that behaves differently on the two platforms is the
/// defect, whatever either platform does on its own.
/// </remarks>
public sealed class SoftOnsetPolicyTests
{
    private const int Rate = 16_000;

    [Fact]
    public void AShortTakeLosingAQuarterToAnEarlyOnsetUsesTheWholeRecording()
    {
        // Three seconds, speech detected at one second, so the first second is dropped: exactly the
        // shape of a soft opening word that the detector heard as silence.
        Assert.True(SoftOnsetPolicy.ShouldUseWholeRecording(
            rawSampleCount: Rate * 3,
            sampleRate: Rate,
            firstSpeechSample: Rate,
            droppedSampleCount: Rate));
    }

    [Fact]
    public void ALongTakeKeepsItsHeadStart()
    {
        // Twelve seconds. The same proportion is dropped, and streaming is worth more here than the
        // risk - which is why the rule is about SHORT takes and not about the fraction alone.
        Assert.False(SoftOnsetPolicy.ShouldUseWholeRecording(
            rawSampleCount: Rate * 12,
            sampleRate: Rate,
            firstSpeechSample: Rate,
            droppedSampleCount: Rate * 3));
    }

    [Fact]
    public void SpeechThatStartsLateWasNotAnOnsetProblem()
    {
        // Somebody who waited two and a half seconds before speaking was not clipped; they paused.
        Assert.False(SoftOnsetPolicy.ShouldUseWholeRecording(
            rawSampleCount: Rate * 6,
            sampleRate: Rate,
            firstSpeechSample: (int)(Rate * 2.5),
            droppedSampleCount: (int)(Rate * 2.5)));
    }

    [Fact]
    public void ASmallDropIsJustTheGapBeforeSomebodySpeaks()
    {
        // A tenth of the recording. Every recording begins with some silence and none of it is news.
        Assert.False(SoftOnsetPolicy.ShouldUseWholeRecording(
            rawSampleCount: Rate * 4,
            sampleRate: Rate,
            firstSpeechSample: Rate / 10,
            droppedSampleCount: Rate * 4 / 10));
    }

    [Fact]
    public void TooShortToJudgeIsLeftAlone()
    {
        // Under a second there is not enough recording for any of these proportions to mean anything.
        Assert.False(SoftOnsetPolicy.ShouldUseWholeRecording(
            rawSampleCount: 15_999,
            sampleRate: Rate,
            firstSpeechSample: 8_000,
            droppedSampleCount: 8_000));
    }

    [Fact]
    public void NothingDroppedNeedsNoProtection()
    {
        Assert.False(SoftOnsetPolicy.ShouldUseWholeRecording(
            rawSampleCount: Rate * 3,
            sampleRate: Rate,
            firstSpeechSample: 0,
            droppedSampleCount: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnImpossibleSampleRateIsRefusedRatherThanDividedBy(int sampleRate)
    {
        Assert.False(SoftOnsetPolicy.ShouldUseWholeRecording(
            rawSampleCount: Rate * 3,
            sampleRate: sampleRate,
            firstSpeechSample: Rate,
            droppedSampleCount: Rate));
    }

    [Fact]
    public void TheThresholdsAreTheOnesMacOsUses()
    {
        // WRITTEN DOWN SO A CHANGE IS DELIBERATE. These four numbers are the reason a recording
        // behaves the same way on both platforms; drifting one of them silently is how that stops
        // being true without anybody noticing.
        Assert.Equal(16_000, SoftOnsetPolicy.MinimumSamples);
        Assert.Equal(TimeSpan.FromSeconds(8), SoftOnsetPolicy.LongestProtectedTake);
        Assert.Equal(TimeSpan.FromSeconds(2), SoftOnsetPolicy.LatestProtectedOnset);
        Assert.Equal(0.25, SoftOnsetPolicy.DroppedFraction);
    }
}
