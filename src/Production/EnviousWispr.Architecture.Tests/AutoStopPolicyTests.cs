using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Auto-stop can cut someone off mid-sentence, so most of these tests are about when it must NOT fire.
/// </summary>
public sealed class AutoStopPolicyTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(2);

    private static AutoStopDecision Decide(
        bool enabled = true,
        bool isToggleMode = true,
        bool hasHeardSpeech = true,
        double trailingSeconds = 3,
        double? requiredSeconds = null) =>
        AutoStopPolicy.Decide(
            enabled,
            isToggleMode,
            hasHeardSpeech,
            TimeSpan.FromSeconds(trailingSeconds),
            requiredSeconds is null ? Threshold : TimeSpan.FromSeconds(requiredSeconds.Value));

    /// <summary>
    /// The positive case, and the control for every negative one below. Without it, a policy that
    /// never stopped would pass the whole rest of this file.
    /// </summary>
    [Fact]
    public void ASpeakerWhoHasFinishedStopsTheRecording()
    {
        Assert.Equal(AutoStopDecision.Stop, Decide());
    }

    [Fact]
    public void TheSettingBeingOffMeansItNeverStops()
    {
        Assert.Equal(AutoStopDecision.KeepRecording, Decide(enabled: false));
    }

    /// <summary>
    /// In push-to-talk the user is holding the key, and the release is their own live decision.
    /// Ending the recording underneath a held key takes that decision away from them.
    /// </summary>
    [Fact]
    public void PushToTalkIsNeverStoppedAutomatically()
    {
        Assert.Equal(AutoStopDecision.KeepRecording, Decide(isToggleMode: false));
    }

    /// <summary>
    /// A user who starts a recording and then thinks has not FINISHED, they have not BEGUN.
    /// Without this clause the symptom is "it cancels itself before I say anything".
    /// </summary>
    [Fact]
    public void ARecordingWhereNobodyHasSpokenYetIsNeverStopped()
    {
        Assert.Equal(
            AutoStopDecision.KeepRecording,
            Decide(hasHeardSpeech: false, trailingSeconds: 60));
    }

    [Fact]
    public void APauseShorterThanTheThresholdKeepsRecording()
    {
        Assert.Equal(AutoStopDecision.KeepRecording, Decide(trailingSeconds: 1.9));
    }

    /// <summary>
    /// A setting below the floor is clamped UP rather than rejected. A setting out of range must
    /// never be able to stop dictation working, and it must never be able to make auto-stop more
    /// aggressive than the floor allows.
    /// </summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(0)]
    [InlineData(-5)]
    public void ARequestBelowTheFloorIsClampedUpRatherThanHonoured(double requestedSeconds)
    {
        // 1.4s is under the 1.5s floor, so a policy that honoured the request would stop here.
        Assert.Equal(
            AutoStopDecision.KeepRecording,
            Decide(trailingSeconds: 1.4, requiredSeconds: requestedSeconds));

        // And the floor is genuinely reachable, so the clamp is not simply disabling the feature.
        Assert.Equal(
            AutoStopDecision.Stop,
            Decide(trailingSeconds: 1.6, requiredSeconds: requestedSeconds));
    }

    [Fact]
    public void ARequestAboveTheFloorIsHonouredRatherThanClampedDown()
    {
        Assert.Equal(
            AutoStopDecision.KeepRecording,
            Decide(trailingSeconds: 4, requiredSeconds: 8));
        Assert.Equal(
            AutoStopDecision.Stop,
            Decide(trailingSeconds: 9, requiredSeconds: 8));
    }

    /// <summary>
    /// Every guard is independent. A test that only ever flips one at a time would pass a policy
    /// that ORed them together instead of ANDing them, and that policy stops recordings nobody
    /// has spoken into.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void AnyGuardAloneIsEnoughToKeepRecording(bool enabled, bool toggle, bool heardSpeech)
    {
        Assert.Equal(
            AutoStopDecision.KeepRecording,
            Decide(
                enabled: enabled,
                isToggleMode: toggle,
                hasHeardSpeech: heardSpeech,
                trailingSeconds: 30));
    }
}
