using EnviousWispr.Core.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The double press that locks a recording on, and the third that cancels it.
/// </summary>
public sealed class HandsFreeLockPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset After(double milliseconds) =>
        Start + TimeSpan.FromMilliseconds(milliseconds);

    private static HandsFreeLockPolicy Recording()
    {
        var policy = new HandsFreeLockPolicy();
        policy.RecordingStarted(Start);
        return policy;
    }

    [Fact]
    public void AQuickSecondPressLocksTheRecordingOn()
    {
        var policy = Recording();

        Assert.Equal(HandsFreePressOutcome.Lock, policy.Press(After(200), isToggleMode: false));
        Assert.True(policy.IsLocked);
        Assert.False(policy.ReleaseEndsRecording());
    }

    [Fact]
    public void AQuickThirdPressCancels()
    {
        var policy = Recording();
        policy.Press(After(150), isToggleMode: false);

        Assert.Equal(HandsFreePressOutcome.Cancel, policy.Press(After(300), isToggleMode: false));
    }

    /// <summary>
    /// The control for both tests above. A press outside the window is ordinary, or a policy that
    /// locked on every press would pass them and lock recordings nobody asked to lock.
    /// </summary>
    [Fact]
    public void APressAfterTheWindowIsOrdinary()
    {
        var policy = Recording();

        Assert.Equal(HandsFreePressOutcome.Ordinary, policy.Press(After(900), isToggleMode: false));
        Assert.False(policy.IsLocked);
        Assert.True(policy.ReleaseEndsRecording());
    }

    /// <summary>
    /// In toggle mode a second press already means stop. A gesture that quietly redefined it would
    /// break the one mode the user actually chose.
    /// </summary>
    [Fact]
    public void ToggleModeIsNeverAffected()
    {
        var policy = Recording();

        Assert.Equal(HandsFreePressOutcome.Ordinary, policy.Press(After(100), isToggleMode: true));
        Assert.False(policy.IsLocked);
        Assert.True(policy.ReleaseEndsRecording());
    }

    [Fact]
    public void APressWithNoRecordingRunningIsOrdinary()
    {
        var policy = new HandsFreeLockPolicy();

        Assert.Equal(HandsFreePressOutcome.Ordinary, policy.Press(Start, isToggleMode: false));
    }

    /// <summary>
    /// The window is measured from the START of the recording, not from the previous press.
    /// Press-to-press timing lets a slow triple press drift arbitrarily far from the start, so a
    /// user who pressed twice, thought about it, and pressed again seconds later would silently
    /// cancel a recording they meant to keep.
    /// </summary>
    [Fact]
    public void TheWindowIsAnchoredToTheStartRatherThanToThePreviousPress()
    {
        var policy = Recording();
        Assert.Equal(HandsFreePressOutcome.Lock, policy.Press(After(400), isToggleMode: false));

        // 300ms after the LOCK, but 700ms after the start. Press-to-press timing would cancel.
        Assert.Equal(HandsFreePressOutcome.Ordinary, policy.Press(After(700), isToggleMode: false));
    }

    /// <summary>
    /// A locked flag surviving into the next recording would make its second press CANCEL instead
    /// of lock, and the user would have no way to connect that to what they did a minute earlier.
    /// </summary>
    [Fact]
    public void TheLockDoesNotSurviveIntoTheNextRecording()
    {
        var policy = Recording();
        policy.Press(After(100), isToggleMode: false);
        Assert.True(policy.IsLocked);

        policy.RecordingEnded();
        Assert.False(policy.IsLocked);
        Assert.True(policy.ReleaseEndsRecording());

        var nextStart = After(10_000);
        policy.RecordingStarted(nextStart);
        Assert.Equal(
            HandsFreePressOutcome.Lock,
            policy.Press(nextStart + TimeSpan.FromMilliseconds(100), isToggleMode: false));
    }

    /// <summary>
    /// The boundary from both sides, so the window is a real duration rather than whatever the
    /// implementation happens to do.
    /// </summary>
    [Fact]
    public void TheWindowIsReachableFromBothSides()
    {
        Assert.Equal(
            HandsFreePressOutcome.Lock,
            Recording().Press(Start + HandsFreeLockPolicy.Window, isToggleMode: false));

        Assert.Equal(
            HandsFreePressOutcome.Ordinary,
            Recording().Press(
                Start + HandsFreeLockPolicy.Window + TimeSpan.FromMilliseconds(1),
                isToggleMode: false));
    }

    /// <summary>
    /// Releasing the key ends an unlocked recording. Without this, a policy that always answered
    /// false would pass every lock test above and no recording would ever stop on release.
    /// </summary>
    [Fact]
    public void ReleasingTheKeyStillEndsAnUnlockedRecording()
    {
        Assert.True(Recording().ReleaseEndsRecording());
    }
}
