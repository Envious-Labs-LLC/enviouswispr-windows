using EnviousWispr.Core.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// One key, four meanings: hold to talk, double-tap for hands-free, one tap to stop, three to throw
/// it away. Most of this file is about the presses that must do nothing.
/// </summary>
public sealed class HotkeyGesturePolicyTests
{
    private const uint RightControl = 0xA3;
    private const uint F8 = 0x77;
    private const uint LetterC = 0x43;

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static HotkeyGesturePolicy Modifier() => new(RightControl, needsHoldThreshold: true);

    private static HotkeyGesturePolicy Ordinary() => new(F8, needsHoldThreshold: false);

    private static readonly TimeSpan PastHold =
        HotkeyGesturePolicy.ModifierHoldThreshold + Ms(1);

    private static readonly TimeSpan PastTaps =
        HotkeyGesturePolicy.MultiTapWindow + Ms(1);

    // ---- the control, and the reason this feature was reverted once ----

    /// <summary>
    /// THE CONTROL FOR THE WHOLE FILE. An ordinary key still holds to talk, instantly, exactly as it
    /// does today.
    /// </summary>
    /// <remarks>
    /// Without this every refusal test below would pass against a policy that never fires, which is
    /// how a gesture reaches ten green tests while being unreachable.
    /// </remarks>
    [Fact]
    public void AnOrdinaryKeyStillHoldsToTalkWithNoDelay()
    {
        var policy = Ordinary();

        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(F8, true, Ms(0)));
        Assert.Equal(HotkeyGestureOutcome.HoldEnded, policy.Process(F8, false, Ms(900)));
    }

    /// <summary>
    /// THE LINE THAT KILLED THIS FEATURE LAST TIME. An earlier attempt made every dictation wait for
    /// a possible second press before finalising, so the common path paid for a gesture most people
    /// never use. Releasing a hold must finalise immediately and leave nothing pending.
    /// </summary>
    [Fact]
    public void ReleasingAHoldFinalisesImmediatelyAndWaitsForNothing()
    {
        var policy = Modifier();

        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));

        Assert.Equal(HotkeyGestureOutcome.HoldEnded, policy.Process(RightControl, false, Ms(5_000)));

        // Nothing is pending, so nothing is waiting to be resolved before the text is delivered.
        Assert.Null(policy.NextDeadline);
    }

    // ---- instant start on modifier press + shortcut safety ----

    [Fact]
    public void AModifierHeldPastTheThresholdStartsRecording()
    {
        var policy = Modifier();

        // Starts immediately on key-down with zero 200ms hold wait.
        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(PastHold));
        Assert.Equal(HotkeyGestureOutcome.HoldEnded, policy.Process(RightControl, false, Ms(300)));
    }

    /// <summary>
    /// Pressing another chord key (e.g. Ctrl+C) aborts and discards immediately without committing.
    /// </summary>
    [Fact]
    public void AShortcutNeverStartsARecording()
    {
        var policy = Modifier();

        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));
        Assert.Equal(HotkeyGestureOutcome.Discarded, policy.Process(LetterC, true, Ms(40)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(LetterC, false, Ms(90)));

        Assert.Null(policy.NextDeadline);
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(PastHold));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, false, Ms(150)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(Ms(150) + PastTaps));
    }

    [Fact]
    public void PressingOtherKeyAfterModifierHoldThresholdDoesNotDiscard()
    {
        // Deliberate initial hold past ModifierHoldThreshold without calling Elapsed().
        var policy = Modifier();
        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(LetterC, true, Ms(250)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(LetterC, false, Ms(300)));
        Assert.Equal(HotkeyGestureOutcome.HoldEnded, policy.Process(RightControl, false, Ms(600)));

        // Tap-then-hold settled by Elapsed(): tentative flag clears and stray keys do not abort.
        var promoted = Modifier();
        Tap(promoted, Ms(0));
        Assert.Equal(HotkeyGestureOutcome.Nothing, promoted.Process(RightControl, true, Ms(100)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, promoted.Elapsed(Ms(100) + PastHold));
        Assert.False(promoted.HasTentativeRecording);
        Assert.Equal(HotkeyGestureOutcome.Nothing, promoted.Process(LetterC, true, Ms(400)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, promoted.Process(LetterC, false, Ms(450)));
        Assert.Equal(HotkeyGestureOutcome.HoldEnded, promoted.Process(RightControl, false, Ms(700)));
    }

    /// <summary>
    /// A super-brief single tap opens the tap window briefly and discards quietly if no second tap arrives.
    /// </summary>
    [Fact]
    public void AQuickPressIsATapAndOneTapAloneDoesNothing()
    {
        var policy = Modifier();

        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, false, Ms(50)));
        Assert.Equal(HotkeyGestureOutcome.Discarded, Settle(policy));
    }

    [Fact]
    public void AHoldExactlyAtTheThresholdCounts()
    {
        var policy = Modifier();

        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));

        Assert.Equal(
            HotkeyGestureOutcome.HoldEnded,
            policy.Process(RightControl, false, HotkeyGesturePolicy.ModifierHoldThreshold));
        Assert.Null(policy.NextDeadline);
    }

    /// <summary>
    /// Auto-repeat sends the same key down again while it is held and must not re-fire HoldStarted.
    /// </summary>
    [Fact]
    public void AutoRepeatDoesNotRestartTheHoldClock()
    {
        var policy = Modifier();

        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));
        for (var repeat = 20; repeat <= 180; repeat += 20)
        {
            Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, true, Ms(repeat)));
        }

        Assert.Equal(HotkeyGestureOutcome.HoldEnded, policy.Process(RightControl, false, PastHold));
    }

    // ---- hands-free, by double tap ----

    [Fact]
    public void TwoTapsStartRecordingHandsFree()
    {
        var policy = Modifier();

        Tap(policy, Ms(0));
        Tap(policy, Ms(100));

        Assert.Equal(HotkeyGestureOutcome.ToggleStarted, Settle(policy));
        Assert.True(policy.IsRecordingHandsFree);
    }

    [Fact]
    public void OneTapStopsAHandsFreeRecording()
    {
        var policy = HandsFreeRunning();

        Tap(policy, Ms(1_000));

        Assert.Equal(HotkeyGestureOutcome.ToggleStopped, Settle(policy));
        Assert.False(policy.IsRecordingHandsFree);
    }

    [Fact]
    public void ThreeTapsThrowAHandsFreeRecordingAway()
    {
        var policy = HandsFreeRunning();

        Tap(policy, Ms(1_000));
        Tap(policy, Ms(1_080));
        Tap(policy, Ms(1_160));

        Assert.Equal(HotkeyGestureOutcome.Cancelled, Settle(policy));
        Assert.False(policy.IsRecordingHandsFree);
    }

    /// <summary>
    /// Three quick taps from idle cleanly discard the tentative capture rather than leaving a session stranded.
    /// </summary>
    [Fact]
    public void ThreeTapsWithNothingRunningDoesNothing()
    {
        var policy = Modifier();

        Tap(policy, Ms(0));
        Tap(policy, Ms(80));
        Tap(policy, Ms(160));

        Assert.Equal(HotkeyGestureOutcome.Discarded, Settle(policy));
        Assert.False(policy.IsRecordingHandsFree);
    }

    /// <summary>
    /// Double-tapping while already hands-free must not restart, because restarting would discard
    /// everything the user has said so far.
    /// </summary>
    [Fact]
    public void DoubleTappingWhileAlreadyHandsFreeKeepsWhatWasSaid()
    {
        var policy = HandsFreeRunning();

        Tap(policy, Ms(1_000));
        Tap(policy, Ms(1_080));

        Assert.Equal(HotkeyGestureOutcome.Nothing, Settle(policy));
        Assert.True(policy.IsRecordingHandsFree);
    }

    /// <summary>
    /// Taps too far apart are separate brief taps that discard instead of locking hands-free.
    /// </summary>
    [Fact]
    public void TapsOutsideTheWindowDoNotCombine()
    {
        var policy = Modifier();

        Tap(policy, Ms(0));
        Assert.Equal(HotkeyGestureOutcome.Discarded, Settle(policy));

        Tap(policy, Ms(2_000));
        Assert.Equal(HotkeyGestureOutcome.Discarded, Settle(policy));
        Assert.False(policy.IsRecordingHandsFree);
    }

    /// <summary>
    /// Tapping twice and then holding is one person changing their mind, not a double-tap followed
    /// by a hold. Holding the third press past threshold finalises immediately on release.
    /// </summary>
    [Fact]
    public void TapsBeforeAHoldAreForgotten()
    {
        var policy = Modifier();

        Tap(policy, Ms(0));
        Tap(policy, Ms(80));

        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, true, Ms(120)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(Ms(120) + PastHold));

        Assert.Equal(HotkeyGestureOutcome.HoldEnded, policy.Process(RightControl, false, Ms(2_000)));
        Assert.Null(policy.NextDeadline);
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(Ms(9_000)));
        Assert.False(policy.IsRecordingHandsFree);
    }

    // ---- housekeeping ----

    /// <summary>
    /// Focus leaving the machine mid-press means the release never arrives. A hands-free recording
    /// is deliberately left running: it does not depend on a key being held, so losing focus is no
    /// reason to throw away what someone is still saying.
    /// </summary>
    [Fact]
    public void LosingFocusForgetsThePressAndKeepsTheRecording()
    {
        var policy = HandsFreeRunning();

        policy.Process(RightControl, true, Ms(1_000));
        policy.Reset();

        Assert.True(policy.IsRecordingHandsFree);
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, false, Ms(90_000)));
    }

    [Fact]
    public void AReleaseWithNoPressIsNothing()
    {
        Assert.Equal(
            HotkeyGestureOutcome.Nothing,
            Modifier().Process(RightControl, false, Ms(50)));
    }

    /// <summary>
    /// The caller needs a deadline only for multi-tap resolution and tap-then-hold promotion;
    /// an initial modifier press fires immediately with no threshold timer pending.
    /// </summary>
    [Fact]
    public void TheCallerIsToldWhenToLookAgain()
    {
        var policy = Modifier();
        Assert.Null(policy.NextDeadline);

        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Process(RightControl, true, Ms(0)));
        Assert.Null(policy.NextDeadline);

        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, false, Ms(50)));
        Assert.Equal(Ms(50) + HotkeyGesturePolicy.MultiTapWindow, policy.NextDeadline);

        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(RightControl, true, Ms(100)));
        Assert.Equal(Ms(100) + HotkeyGesturePolicy.ModifierHoldThreshold, policy.NextDeadline);
    }

    private static void Tap(HotkeyGesturePolicy policy, TimeSpan at)
    {
        policy.Process(RightControl, true, at);
        policy.Process(RightControl, false, at + Ms(30));
    }

    /// <summary>Runs the clock to whenever the policy says the gesture completes.</summary>
    /// <remarks>
    /// ASK THE POLICY, DO NOT RECOMPUTE ITS ARITHMETIC. The first version of this file worked the
    /// deadline out from the press time while the policy measures from the RELEASE, so every
    /// multi-tap test failed by exactly the thirty milliseconds the helper holds the key. Five red
    /// tests, all of them the test's fault, all of them looking like a broken policy.
    ///
    /// Recomputing a value the subject already exposes is a second implementation of the same rule,
    /// and the two disagreeing is the only thing it can ever prove.
    /// </remarks>
    private static HotkeyGestureOutcome Settle(HotkeyGesturePolicy policy)
    {
        var deadline = policy.NextDeadline;
        Assert.True(deadline is not null, "Nothing is pending, so nothing can complete.");
        return policy.Elapsed(deadline!.Value + Ms(1));
    }

    private static HotkeyGesturePolicy HandsFreeRunning()
    {
        var policy = Modifier();
        Tap(policy, Ms(0));
        Tap(policy, Ms(100));
        Assert.Equal(HotkeyGestureOutcome.ToggleStarted, Settle(policy));
        return policy;
    }
}
