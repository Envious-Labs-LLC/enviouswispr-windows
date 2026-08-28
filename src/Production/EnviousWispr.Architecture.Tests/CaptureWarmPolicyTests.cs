using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Holding the microphone open between dictations buys a fast press. Holding the WRONG one costs the
/// user their words, so most of this file is about letting go.
/// </summary>
public sealed class CaptureWarmPolicyTests
{
    private const string Headset = "headset";
    private const string Laptop = "laptop-built-in";

    private static readonly TimeSpan JustUsed = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongIdle = CaptureWarmPolicy.IdleBeforeRelease;

    private static CaptureWarmDecision Decide(
        bool warmingAllowed = true,
        bool isRecording = false,
        string? warmDeviceId = null,
        string? selectedDeviceId = Headset,
        TimeSpan? idle = null,
        int consecutiveFailures = 0) =>
        CaptureWarmPolicy.Decide(
            warmingAllowed,
            isRecording,
            warmDeviceId,
            selectedDeviceId,
            idle ?? JustUsed,
            consecutiveFailures);

    /// <summary>
    /// The control for the whole file. Nothing held, a device chosen, permission given, so it must
    /// actually open one - otherwise a policy that never warms would pass every refusal test below
    /// and the feature would be dead while looking careful. This is the test the hands-free lock
    /// did not have.
    /// </summary>
    [Fact]
    public void AnIdleAppWithADeviceChosenOpensIt()
    {
        Assert.Equal(CaptureWarmDecision.Warm, Decide());
    }

    /// <summary>
    /// The second half of the control. The switch has to be able to say no as well as yes, or the
    /// parameter is decoration.
    /// </summary>
    [Fact]
    public void PermissionWithheldOpensNothing()
    {
        Assert.Equal(CaptureWarmDecision.Leave, Decide(warmingAllowed: false));
    }

    /// <summary>
    /// Switching the feature off has to give back what is already held, not merely stop opening
    /// more. Otherwise a user who turns it off keeps a microphone held open by a feature they
    /// switched off, which is the worst of both.
    /// </summary>
    [Fact]
    public void PermissionWithdrawnGivesBackWhatIsAlreadyHeld()
    {
        Assert.Equal(
            CaptureWarmDecision.Release,
            Decide(warmingAllowed: false, warmDeviceId: Headset));
    }

    /// <summary>
    /// The failure that costs words rather than time. A device held from before the user changed
    /// their input would record the microphone they are no longer talking into, and they would only
    /// find out by reading the transcript.
    /// </summary>
    [Fact]
    public void AMicrophoneThatIsNoLongerTheChosenOneIsGivenBack()
    {
        Assert.Equal(
            CaptureWarmDecision.Release,
            Decide(warmDeviceId: Laptop, selectedDeviceId: Headset));
    }

    /// <summary>
    /// The stale check must beat the idle check. A device held ten seconds ago is fresh by every
    /// measure except the one that matters, so ordering these the other way round would hold the
    /// wrong microphone for five minutes.
    /// </summary>
    [Fact]
    public void AStaleMicrophoneIsGivenBackEvenWhenItWasJustUsed()
    {
        Assert.Equal(
            CaptureWarmDecision.Release,
            Decide(warmDeviceId: Laptop, selectedDeviceId: Headset, idle: JustUsed));
    }

    /// <summary>
    /// A device chosen and then unchosen leaves nothing to warm against, and the held one is now
    /// stale by definition.
    /// </summary>
    [Fact]
    public void LosingTheChosenDeviceGivesBackTheHeldOne()
    {
        Assert.Equal(
            CaptureWarmDecision.Release,
            Decide(warmDeviceId: Headset, selectedDeviceId: null));
    }

    [Fact]
    public void NoDeviceChosenMeansThereIsNothingToOpen()
    {
        Assert.Equal(CaptureWarmDecision.Leave, Decide(selectedDeviceId: null));
    }

    /// <summary>
    /// Releasing has to be followed by opening the right one, or the user pays full price on the
    /// next press for no reason. This walks the two steps the caller actually takes.
    /// </summary>
    [Fact]
    public void AfterGivingBackTheWrongOneItOpensTheRightOne()
    {
        Assert.Equal(
            CaptureWarmDecision.Release,
            Decide(warmDeviceId: Laptop, selectedDeviceId: Headset));

        Assert.Equal(
            CaptureWarmDecision.Warm,
            Decide(warmDeviceId: null, selectedDeviceId: Headset));
    }

    [Fact]
    public void TheRightMicrophoneJustUsedIsLeftAlone()
    {
        Assert.Equal(
            CaptureWarmDecision.Leave,
            Decide(warmDeviceId: Headset, selectedDeviceId: Headset, idle: JustUsed));
    }

    [Fact]
    public void AMicrophoneNobodyHasUsedForAWhileIsGivenBack()
    {
        Assert.Equal(
            CaptureWarmDecision.Release,
            Decide(warmDeviceId: Headset, selectedDeviceId: Headset, idle: LongIdle));
    }

    /// <summary>
    /// The boundary, from below. A gap one tick short of the threshold must not release, or the
    /// comparison is off by one in the direction that costs a press.
    /// </summary>
    [Fact]
    public void AGapJustUnderTheThresholdKeepsIt()
    {
        Assert.Equal(
            CaptureWarmDecision.Leave,
            Decide(
                warmDeviceId: Headset,
                idle: CaptureWarmPolicy.IdleBeforeRelease - TimeSpan.FromTicks(1)));
    }

    /// <summary>
    /// A dictation in flight owns the device. Releasing here would end a recording the user is
    /// still speaking into, which is the founder's stated order inverted.
    /// </summary>
    [Theory]
    [InlineData(Headset, Headset)]
    [InlineData(Laptop, Headset)]
    [InlineData(null, Headset)]
    public void NothingTouchesTheMicrophoneWhileSomeoneIsTalking(string? warm, string? selected)
    {
        Assert.Equal(
            CaptureWarmDecision.Leave,
            Decide(
                isRecording: true,
                warmDeviceId: warm,
                selectedDeviceId: selected,
                idle: LongIdle));
    }

    /// <summary>
    /// A recording beats even a withdrawn permission. Turning the feature off mid-dictation must
    /// not be the thing that ends the dictation.
    /// </summary>
    [Fact]
    public void EvenWithdrawingPermissionWaitsForTheDictationToEnd()
    {
        Assert.Equal(
            CaptureWarmDecision.Leave,
            Decide(warmingAllowed: false, isRecording: true, warmDeviceId: Headset));
    }

    /// <summary>
    /// A device that will not open is usually held by something else or unplugged. Retrying on a
    /// timer forever would spend the machine's attention on a device that is not coming back.
    /// </summary>
    [Fact]
    public void ADeviceThatKeepsRefusingIsLeftAlone()
    {
        Assert.Equal(
            CaptureWarmDecision.Leave,
            Decide(consecutiveFailures: CaptureWarmPolicy.FailuresBeforeGivingUp));
    }

    /// <summary>
    /// The boundary from the other side, so the count is a real threshold rather than any nonzero
    /// number of failures giving up.
    /// </summary>
    [Fact]
    public void OneFailureShortOfGivingUpItStillTries()
    {
        Assert.Equal(
            CaptureWarmDecision.Warm,
            Decide(consecutiveFailures: CaptureWarmPolicy.FailuresBeforeGivingUp - 1));
    }

    /// <summary>
    /// An empty device id is no device. Without this an empty string reads as "something is held"
    /// and the policy would compare it against the real one and release forever.
    /// </summary>
    [Fact]
    public void AnEmptyDeviceIdIsTreatedAsNothingHeld()
    {
        Assert.Equal(CaptureWarmDecision.Warm, Decide(warmDeviceId: string.Empty));
    }

    /// <summary>
    /// The shipped default is off, and it is off because nobody has yet checked on a real Windows
    /// machine whether opening a microphone lights the in-use indicator. This test exists to FAIL
    /// when someone flips it, so that the flip is a deliberate act with the answer in hand rather
    /// than a tidy-up.
    /// </summary>
    [Fact]
    public void WarmingShipsOffUntilTheIndicatorQuestionIsAnswered()
    {
        Assert.False(CaptureWarmPolicy.WarmingAllowedByDefault);
    }
}
