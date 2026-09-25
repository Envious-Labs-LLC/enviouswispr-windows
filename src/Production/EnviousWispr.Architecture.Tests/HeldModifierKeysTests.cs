using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The hook's modifier reading, from its own events, against the keyboard state as it really lags. Ref: #66, #207.</summary>
/// <remarks>
/// THE TRACKER SUITE FED A CTRL+WIN BINDING THE READING A PERSON MEANS - Windows held on the Win key's press - which is
/// the one value the hook never supplied. It was green while, on the running app, a hold did nothing and one tap
/// counted as two. These tests replay what the hook really sees: the keyboard state one event behind, and the shell's
/// own Control press and second Win release on letting go of Win, traced on the running machine (2026-09-25):
///
///     vk 0xA2 down / vk 0x5B down / vk 0x5B up / vk 0xA2 down (shell) / vk 0x5B up (shell) / vk 0xA2 up
/// </remarks>
public sealed class HeldModifierKeysTests
{
    private const uint LeftControl = 0xA2;
    private const uint RightControl = 0xA3;
    private const uint LeftWindows = 0x5B;
    private const uint LetterC = 0x43;

    [Fact]
    public void TheKeyMovingNowIsItsOwnEdgeWhateverTheStateSays()
    {
        var held = new HeldModifierKeys();

        Assert.Equal(HotkeyModifiers.Control, held.Observe(LeftControl, isKeyDown: true, _ => false));
        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Windows,
            held.Observe(LeftWindows, isKeyDown: true, key => key == LeftControl));
        Assert.Equal(HotkeyModifiers.Control, held.Observe(LeftWindows, isKeyDown: false, _ => true));
    }

    [Fact]
    public void TheStateCanOnlySayUp()
    {
        // A lock took the Control key-up: the stream still has it down, the state says up, and up wins.
        var held = new HeldModifierKeys();
        held.Observe(LeftControl, isKeyDown: true, _ => false);

        Assert.Equal(HotkeyModifiers.None, held.Observe(LetterC, isKeyDown: true, _ => false));

        // A key the stream never saw go down is not held, whatever the state says.
        Assert.Equal(HotkeyModifiers.None, new HeldModifierKeys().Observe(LetterC, isKeyDown: true, _ => true));
    }

    [Fact]
    public void LettingGoOfOneSideLeavesTheOtherHeld()
    {
        var held = new HeldModifierKeys();
        held.Observe(LeftControl, isKeyDown: true, _ => false);
        held.Observe(RightControl, isKeyDown: true, _ => true);

        Assert.Equal(HotkeyModifiers.Control, held.Observe(LeftControl, isKeyDown: false, _ => true));
    }

    /// <summary>A Ctrl+Win hold as the hook sees it starts a recording, and letting go ends it.</summary>
    [Fact]
    public void ACtrlWinHoldStartsARecording()
    {
        var (tracker, held) = CtrlWinBound();

        Feed(tracker, held, LeftControl, down: true, stateDown: []);
        Feed(tracker, held, LeftWindows, down: true, stateDown: [LeftControl]);
        Assert.Equal(PushToTalkSignal.Pressed, TickPast(tracker, HotkeyGesturePolicy.ModifierHoldThreshold));

        var released = Feed(tracker, held, LeftWindows, down: false, stateDown: [LeftControl, LeftWindows]);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
        Assert.False(released.Consume);
    }

    /// <summary>One Ctrl+Win tap, with the shell's echo, is one tap: nothing starts.</summary>
    [Fact]
    public void OneTapWithTheShellsEchoIsOneTap()
    {
        var (tracker, held) = CtrlWinBound();

        Tap(tracker, held);

        Assert.Null(TickPast(tracker, HotkeyGesturePolicy.MultiTapWindow));
    }

    /// <summary>Two Ctrl+Win taps, each with the shell's echo, start hands-free.</summary>
    [Fact]
    public void TwoTapsWithTheShellsEchoStartHandsFree()
    {
        var (tracker, held) = CtrlWinBound();

        Tap(tracker, held);
        Tap(tracker, held);

        Assert.Equal(PushToTalkSignal.Pressed, TickPast(tracker, HotkeyGesturePolicy.MultiTapWindow));
    }

    /// <summary>The raw keyboard state, as the hook used to pass it, starts nothing on a hold. The defect.</summary>
    [Fact]
    public void TheRawStateAloneMissesTheHold()
    {
        var (tracker, _) = CtrlWinBound();

        tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.None);
        tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control);

        Assert.Null(tracker.NextDeadline);
    }

    /// <summary>A modifier held since before the hook was installed still qualifies a keyed binding. Ref: #66 review.</summary>
    /// <remarks>
    /// THE HOOK'S OWN EVENTS NEVER SAW THAT CONTROL GO DOWN, so feeding them to every binding made Ctrl+F8 read as a
    /// bare F8 and start a recording, and a Ctrl+Escape cancel fail to match. Keyed bindings read the keyboard state.
    /// </remarks>
    [Fact]
    public void PreInstallControlStillQualifiesKeyedBindings()
    {
        var bareRecord = new HotkeyEdgeTracker(0x77, HotkeyModifiers.None);
        Assert.False(Feed(bareRecord, new HeldModifierKeys(), 0x77, down: true, stateDown: [LeftControl]).Consume);

        var controlRecord = new HotkeyEdgeTracker(0x77, HotkeyModifiers.Control);
        var started = Feed(controlRecord, new HeldModifierKeys(), 0x77, down: true, stateDown: [LeftControl]);
        Assert.True(started.Consume);
        Assert.Equal(PushToTalkSignal.Pressed, started.Signal);

        var controlCancel = new HotkeyEdgeTracker(
            new HotkeyBinding(0x77, HotkeyModifiers.None),
            new HotkeyBinding(0x1B, HotkeyModifiers.Control),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk);
        controlCancel.SetRecordingActive(true);

        var cancelled = Feed(controlCancel, new HeldModifierKeys(), 0x1B, down: true, stateDown: [LeftControl]);
        Assert.True(cancelled.Consume);
        Assert.Equal(PushToTalkSignal.Cancelled, cancelled.Signal);
    }

    /// <summary>The sequence traced on the machine: the state is one event behind throughout.</summary>
    private static void Tap(HotkeyEdgeTracker tracker, HeldModifierKeys held)
    {
        Feed(tracker, held, LeftControl, down: true, stateDown: []);
        Feed(tracker, held, LeftWindows, down: true, stateDown: [LeftControl]);
        Feed(tracker, held, LeftWindows, down: false, stateDown: [LeftControl, LeftWindows]);
        Feed(tracker, held, LeftControl, down: true, stateDown: [LeftControl, LeftWindows]); // the shell's
        Feed(tracker, held, LeftWindows, down: false, stateDown: [LeftControl, LeftWindows]); // the shell's
        Feed(tracker, held, LeftControl, down: false, stateDown: [LeftControl]);
        Thread.Sleep(110);
    }

    private static (HotkeyEdgeTracker Tracker, HeldModifierKeys Held) CtrlWinBound() =>
        (new HotkeyEdgeTracker(
            new HotkeyBinding(0, HotkeyModifiers.Control | HotkeyModifiers.Windows),
            new HotkeyBinding(0x1B, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk), new HeldModifierKeys());

    /// <summary>What the hook passes: the keyboard state for keyed bindings, its own events for the gesture.</summary>
    private static HotkeyEdgeDecision Feed(
        HotkeyEdgeTracker tracker, HeldModifierKeys held, uint key, bool down, uint[] stateDown)
    {
        var active = HotkeyModifiers.None;
        foreach (var pressed in stateDown)
        {
            active |= pressed switch
            {
                0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
                0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Control,
                0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
                0x5B or 0x5C => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
        }

        return tracker.Process(
            key,
            down,
            active,
            held.Observe(key, down, candidate => Array.IndexOf(stateDown, candidate) >= 0));
    }

    private static PushToTalkSignal? TickPast(HotkeyEdgeTracker tracker, TimeSpan wait)
    {
        Thread.Sleep(wait + TimeSpan.FromMilliseconds(60));
        return tracker.Tick();
    }
}
