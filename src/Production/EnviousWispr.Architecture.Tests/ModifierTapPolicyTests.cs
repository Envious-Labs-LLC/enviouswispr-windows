using EnviousWispr.Core.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A modifier can be the dictation key only if the gesture is one Windows does not already own.
/// Most of this file is about the presses that must be left alone.
/// </summary>
public sealed class ModifierTapPolicyTests
{
    private const uint RightControl = 0xA3;
    private const uint LeftShift = 0xA0;
    private const uint LetterC = 0x43;

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static ModifierTapPolicy Policy() => new(RightControl);

    /// <summary>
    /// The control for the whole file. A press and release on its own, quickly, must actually fire -
    /// otherwise a policy that never returned Tap would pass every refusal test below and the key
    /// would simply be dead.
    /// </summary>
    [Fact]
    public void APressAndReleaseOnItsOwnIsATap()
    {
        var policy = Policy();

        Assert.Equal(ModifierTapOutcome.Nothing, policy.Process(RightControl, true, Ms(0)));
        Assert.Equal(ModifierTapOutcome.Tap, policy.Process(RightControl, false, Ms(120)));
    }

    /// <summary>
    /// The reason this policy exists. Holding the modifier and pressing a letter is a shortcut, and
    /// a shortcut must behave exactly as it would if this app were not installed.
    /// </summary>
    [Fact]
    public void AShortcutIsNotATap()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));
        policy.Process(LetterC, true, Ms(40));
        policy.Process(LetterC, false, Ms(90));

        Assert.Equal(ModifierTapOutcome.Abandoned, policy.Process(RightControl, false, Ms(140)));
    }

    /// <summary>
    /// A second modifier is a shortcut too. Without this, Shift plus the bound key would start a
    /// recording, and chorded shortcuts are exactly where a bare-modifier binding is most dangerous.
    /// </summary>
    [Fact]
    public void ASecondModifierIsNotATapEither()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));
        policy.Process(LeftShift, true, Ms(30));

        Assert.Equal(ModifierTapOutcome.Abandoned, policy.Process(RightControl, false, Ms(200)));
    }

    /// <summary>
    /// Holding a modifier is what people do while reaching for a menu, dragging, or resting a hand
    /// mid-thought. None of those is a request to dictate.
    /// </summary>
    [Fact]
    public void HoldingItTooLongIsNotATap()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));

        Assert.Equal(
            ModifierTapOutcome.Abandoned,
            policy.Process(RightControl, false, ModifierTapPolicy.TapMaximum + Ms(1)));
    }

    /// <summary>
    /// The boundary from the other side, so the limit is a real threshold rather than any hold at
    /// all being refused.
    /// </summary>
    [Fact]
    public void AHoldExactlyAtTheLimitStillCounts()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));

        Assert.Equal(
            ModifierTapOutcome.Tap,
            policy.Process(RightControl, false, ModifierTapPolicy.TapMaximum));
    }

    /// <summary>
    /// Auto-repeat sends the same key down again while it is held. Taking the LAST press as the
    /// start would make a long hold look like a fresh tap every few milliseconds, which is the
    /// version of this bug that fires constantly while a user does nothing.
    /// </summary>
    [Fact]
    public void AutoRepeatDoesNotRestartTheClock()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));
        for (var repeat = 100; repeat <= 900; repeat += 100)
        {
            Assert.Equal(ModifierTapOutcome.Nothing, policy.Process(RightControl, true, Ms(repeat)));
        }

        Assert.Equal(ModifierTapOutcome.Abandoned, policy.Process(RightControl, false, Ms(950)));
    }

    /// <summary>
    /// Releasing a key that went down BEFORE the modifier says nothing about what the modifier is
    /// for. Treating it as a disqualifier would make the gesture succeed or fail depending on the
    /// order a user happens to lift their fingers.
    /// </summary>
    [Fact]
    public void ReleasingSomeOtherKeyDuringThePressDoesNotSpoilIt()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));
        policy.Process(LetterC, false, Ms(20));

        Assert.Equal(ModifierTapOutcome.Tap, policy.Process(RightControl, false, Ms(100)));
    }

    /// <summary>
    /// A release with no press. Happens when the app starts with the key already held, or when the
    /// press went to another window first.
    /// </summary>
    [Fact]
    public void AReleaseWithNoPressIsNothing()
    {
        Assert.Equal(ModifierTapOutcome.Nothing, Policy().Process(RightControl, false, Ms(50)));
    }

    /// <summary>
    /// Other keys must pass through untouched when no press is in progress, or every keystroke on
    /// the machine would be reported on.
    /// </summary>
    [Fact]
    public void OtherKeysAloneAreNothing()
    {
        var policy = Policy();

        Assert.Equal(ModifierTapOutcome.Nothing, policy.Process(LetterC, true, Ms(0)));
        Assert.Equal(ModifierTapOutcome.Nothing, policy.Process(LetterC, false, Ms(50)));
    }

    /// <summary>
    /// A disqualified press must not poison the next one. Without this, one shortcut would disable
    /// the dictation key until the app restarted.
    /// </summary>
    [Fact]
    public void AShortcutDoesNotBreakTheNextTap()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));
        policy.Process(LetterC, true, Ms(40));
        policy.Process(RightControl, false, Ms(140));

        policy.Process(RightControl, true, Ms(500));

        Assert.Equal(ModifierTapOutcome.Tap, policy.Process(RightControl, false, Ms(600)));
    }

    [Fact]
    public void TwoTapsInARowBothFire()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));
        Assert.Equal(ModifierTapOutcome.Tap, policy.Process(RightControl, false, Ms(100)));

        policy.Process(RightControl, true, Ms(300));
        Assert.Equal(ModifierTapOutcome.Tap, policy.Process(RightControl, false, Ms(380)));
    }

    /// <summary>
    /// Focus leaving the machine mid-press means the release never arrives. Without a reset the
    /// press stays open and the next release reads as a tap that lasted as long as the user was
    /// away - which the length check would then refuse, so the key would appear dead.
    /// </summary>
    [Fact]
    public void LosingFocusMidPressDoesNotStrandTheGesture()
    {
        var policy = Policy();

        policy.Process(RightControl, true, Ms(0));
        Assert.True(policy.IsCandidate);

        policy.Reset();
        Assert.False(policy.IsCandidate);

        Assert.Equal(ModifierTapOutcome.Nothing, policy.Process(RightControl, false, Ms(90_000)));

        policy.Process(RightControl, true, Ms(90_100));
        Assert.Equal(ModifierTapOutcome.Tap, policy.Process(RightControl, false, Ms(90_200)));
    }
}
