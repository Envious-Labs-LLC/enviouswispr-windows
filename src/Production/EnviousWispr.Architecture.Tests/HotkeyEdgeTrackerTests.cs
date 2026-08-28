using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

public sealed class HotkeyEdgeTrackerTests
{
    private const uint F8 = 0x77;
    private const uint RightControl = 0xA3;
    private const uint LetterC = 0x43;

    private const uint LeftControl = 0xA2;
    private const uint LeftWindows = 0x5B;

    /// <summary>Runs the clock past whatever the tracker is waiting for.</summary>
    /// <remarks>
    /// A hold completes on TIME, not on a keystroke, so a test that only sends keys can never see
    /// one start. Sleeping past the real deadline is the honest way to drive it from here, because
    /// the tracker owns its own clock.
    /// </remarks>
    private static PushToTalkSignal? TickPastDeadline(HotkeyEdgeTracker tracker)
    {
        var deadline = tracker.NextDeadline;
        Assert.True(deadline is not null, "Nothing is pending, so nothing can complete.");
        Thread.Sleep(HotkeyGesturePolicy.ModifierHoldThreshold + TimeSpan.FromMilliseconds(60));
        return tracker.Tick();
    }

    private static HotkeyEdgeTracker ModifierSetBound() =>
        new(
            new HotkeyBinding(0, HotkeyModifiers.Control | HotkeyModifiers.Windows),
            new HotkeyBinding(0x1B, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk);

    private static HotkeyEdgeTracker ModifierBound() =>
        new(
            new HotkeyBinding(RightControl, HotkeyModifiers.None),
            new HotkeyBinding(0x1B, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk);

    /// <summary>
    /// THE CONTROL FOR THE WHOLE MODIFIER BINDING. Holding it must actually start a recording.
    /// </summary>
    /// <remarks>
    /// Without this, every refusal test below would pass against a binding that can never fire -
    /// which is exactly how the hands-free lock reached ten green tests while being unreachable.
    /// The gesture policy has its own tests; this one proves the WIRING carries them, including the
    /// tick that lets a hold complete without a keystroke.
    /// </remarks>
    [Fact]
    public void HoldingABoundModifierStartsARecording()
    {
        var tracker = ModifierBound();

        tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Pressed, TickPastDeadline(tracker));
    }

    /// <summary>
    /// Two modifiers together are a binding, and Ctrl+Win is the shape the default will use. The
    /// tracker sees only that the SET became complete, which is why it works with no key of its own.
    /// </summary>
    [Fact]
    public void HoldingABoundModifierSetStartsARecording()
    {
        var tracker = ModifierSetBound();

        // Control alone is not the set, so nothing arms.
        tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Null(tracker.NextDeadline);

        // Windows joins it and the set is complete.
        tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Windows);
        Assert.Equal(PushToTalkSignal.Pressed, TickPastDeadline(tracker));

        // Letting either one go ends it.
        var released = tracker.Process(LeftWindows, isKeyDown: false, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
    }

    /// <summary>
    /// Ctrl+Win+D makes a new desktop. Pressing an ordinary key while the set is held is a shortcut
    /// and must not become a recording.
    /// </summary>
    [Fact]
    public void AShortcutBuiltOnTheBoundSetStartsNothing()
    {
        var tracker = ModifierSetBound();

        tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.Control);
        tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Windows);
        tracker.Process(
            LetterC,
            isKeyDown: true,
            HotkeyModifiers.Control | HotkeyModifiers.Windows);

        Assert.Null(TickPastDeadline(tracker));
    }

    /// <summary>
    /// A modifier that does not reach Windows breaks copy, paste, and every shortcut on the machine.
    /// The failure is total, immediate, and lands on someone who has not opened this app today.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABoundModifierIsNeverSwallowed(bool isKeyDown)
    {
        var tracker = ModifierBound();

        Assert.False(tracker.Process(RightControl, isKeyDown, HotkeyModifiers.Control).Consume);
    }

    /// <summary>
    /// The whole reason a modifier binding needs a different gesture. Holding it and pressing a
    /// letter is a shortcut and must do nothing here.
    /// </summary>
    [Fact]
    public void AShortcutOnTheBoundModifierStartsNothing()
    {
        var tracker = ModifierBound();

        tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        tracker.Process(LetterC, isKeyDown: true, HotkeyModifiers.Control);
        tracker.Process(LetterC, isKeyDown: false, HotkeyModifiers.Control);
        var release = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);

        Assert.Null(release.Signal);
        Assert.False(release.Consume);
    }

    /// <summary>Releasing the hold finishes the recording, with nothing left pending.</summary>
    /// <remarks>
    /// The property that makes this feature shippable at all: a hold was never a tap, so its release
    /// finalises immediately rather than waiting to see whether a second press is coming.
    /// </remarks>
    [Fact]
    public void ReleasingTheHoldFinishesTheRecordingImmediately()
    {
        var tracker = ModifierBound();

        tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Pressed, TickPastDeadline(tracker));

        var released = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Released, released.Signal);
        Assert.Null(tracker.NextDeadline);
    }

    /// <summary>
    /// Escape must still cancel while a modifier binding is in use. The modifier route returns
    /// early for its own key and must hand every other key back to the ordinary path.
    /// </summary>
    [Fact]
    public void EscapeStillCancelsUnderAModifierBinding()
    {
        var tracker = ModifierBound();
        tracker.SetRecordingActive(active: true);

        var escape = tracker.Process(0x1B, isKeyDown: true, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Cancelled, escape.Signal);
    }

    /// <summary>
    /// Alt is refused as a binding: a lone Alt tap already opens a window's menu bar, so taking it
    /// would put this app in a fight with the shell over one gesture.
    /// </summary>
    [Theory]
    [InlineData(0xA4u)]
    [InlineData(0xA5u)]
    public void AltIsNotAcceptedAsAModifierBinding(uint alt)
    {
        Assert.False(HotkeyEdgeTracker.IsModifierKey(alt));
    }

    /// <summary>
    /// The control for the refusal above. The keys that ARE accepted must be recognised, or a
    /// method that always returned false would pass it while disabling the feature entirely.
    /// </summary>
    [Theory]
    [InlineData(0xA0u)]
    [InlineData(0xA2u)]
    [InlineData(0xA3u)]
    [InlineData(0x5Bu)]
    public void TheModifiersWeDoAcceptAreRecognised(uint key)
    {
        Assert.True(HotkeyEdgeTracker.IsModifierKey(key));
    }

    /// <summary>An ordinary key binding must be untouched by any of this.</summary>
    [Fact]
    public void AnOrdinaryKeyStillHoldsToTalk()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);

        Assert.Equal(
            PushToTalkSignal.Pressed,
            tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None).Signal);
        Assert.Equal(
            PushToTalkSignal.Released,
            tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None).Signal);
    }

    [Fact]
    public void PressRepeatAndReleaseProduceOneEdgeEach()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);

        var pressed = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        var repeated = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        var released = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Pressed, pressed.Signal);
        Assert.True(pressed.Consume);
        Assert.Null(repeated.Signal);
        Assert.True(repeated.Consume);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
        Assert.True(released.Consume);
    }

    [Fact]
    public void TriggerWithoutConfiguredModifiersPassesThrough()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.Control);

        var down = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        var up = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.False(down.Consume);
        Assert.False(up.Consume);
    }

    [Fact]
    public void UnrelatedTypingAlwaysPassesThrough()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);

        var down = tracker.Process(virtualKey: 'A', isKeyDown: true, HotkeyModifiers.None);
        var up = tracker.Process(virtualKey: 'A', isKeyDown: false, HotkeyModifiers.None);

        Assert.False(down.Consume);
        Assert.False(up.Consume);
        Assert.Null(down.Signal);
        Assert.Null(up.Signal);
    }

    [Fact]
    public void EscapeCancelsOnceAndTriggerReleaseDoesNotFinalize()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);
        tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        tracker.SetRecordingActive(active: true);

        var cancelled = tracker.Process(
            HotkeyEdgeTracker.EscapeVirtualKey,
            isKeyDown: true,
            HotkeyModifiers.None);
        var repeatedEscape = tracker.Process(
            HotkeyEdgeTracker.EscapeVirtualKey,
            isKeyDown: true,
            HotkeyModifiers.None);
        var escapeUp = tracker.Process(
            HotkeyEdgeTracker.EscapeVirtualKey,
            isKeyDown: false,
            HotkeyModifiers.None);
        var triggerUp = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Cancelled, cancelled.Signal);
        Assert.True(cancelled.Consume);
        Assert.Null(repeatedEscape.Signal);
        Assert.True(escapeUp.Consume);
        Assert.Null(triggerUp.Signal);
        Assert.True(triggerUp.Consume);
    }

    [Fact]
    public void ToggleModeUsesActualRecordingStateAndIgnoresKeyUp()
    {
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(F8, HotkeyModifiers.None),
            new HotkeyBinding(HotkeyEdgeTracker.EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            EnviousWispr.Core.Settings.DictationRecordingMode.Toggle);

        var start = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        var firstUp = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);
        tracker.SetRecordingActive(active: true);
        var stop = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        var secondUp = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Pressed, start.Signal);
        Assert.Null(firstUp.Signal);
        Assert.Equal(PushToTalkSignal.Released, stop.Signal);
        Assert.Null(secondUp.Signal);
    }

    [Fact]
    public void QuickAddIsGlobalWhenIdleAndPassesThroughWhileRecording()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);

        var add = tracker.Process(
            'W',
            isKeyDown: true,
            HotkeyModifiers.Control | HotkeyModifiers.Alt);
        tracker.Process('W', isKeyDown: false, HotkeyModifiers.None);
        tracker.SetRecordingActive(active: true);
        var blocked = tracker.Process(
            'W',
            isKeyDown: true,
            HotkeyModifiers.Control | HotkeyModifiers.Alt);

        Assert.Equal(PushToTalkSignal.QuickAdd, add.Signal);
        Assert.True(add.Consume);
        Assert.Null(blocked.Signal);
        Assert.False(blocked.Consume);
    }

    [Fact]
    public void ReleaseCompletesEvenWhenModifierWasReleasedFirst()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.Control);
        tracker.Process(F8, isKeyDown: true, HotkeyModifiers.Control);

        var released = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Released, released.Signal);
        Assert.True(released.Consume);
    }

    [Fact]
    public void SameVirtualKeyWithDifferentModifiersRoutesToExactBinding()
    {
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding('W', HotkeyModifiers.Control),
            new HotkeyBinding(HotkeyEdgeTracker.EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            EnviousWispr.Core.Settings.DictationRecordingMode.PushToTalk);

        var quickAddDown = tracker.Process(
            'W',
            isKeyDown: true,
            HotkeyModifiers.Control | HotkeyModifiers.Alt);
        var quickAddUp = tracker.Process('W', isKeyDown: false, HotkeyModifiers.None);
        var recordDown = tracker.Process('W', isKeyDown: true, HotkeyModifiers.Control);
        var recordUp = tracker.Process('W', isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.QuickAdd, quickAddDown.Signal);
        Assert.True(quickAddUp.Consume);
        Assert.Equal(PushToTalkSignal.Pressed, recordDown.Signal);
        Assert.Equal(PushToTalkSignal.Released, recordUp.Signal);
    }

    /// <summary>
    /// The defect: pressing the recording key inside its own capture field started a real
    /// recording. The field marking the keystroke handled cannot reach this - the hook is a
    /// different path - so the tracker itself has to stand down.
    /// </summary>
    [Fact]
    public void WhileCapturingAKeybindTheRecordingKeyProducesNoSignal()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);
        tracker.SetCapturingKeybind(capturing: true);

        var pressed = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        var released = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Null(pressed.Signal);
        Assert.Null(released.Signal);
        // Not consumed either: the keystroke still has to reach the field that is capturing it.
        Assert.False(pressed.Consume);
        Assert.False(released.Consume);
    }

    /// <summary>
    /// The control for the test above. Same tracker, same keystrokes, capture off. Without this
    /// a tracker that had stopped signalling for every reason would pass the test above.
    /// </summary>
    [Fact]
    public void WithoutCaptureTheSameKeystrokesStillRecord()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);

        var pressed = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        var released = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Pressed, pressed.Signal);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
    }

    /// <summary>
    /// Capture must never be able to strand a recording. A key held when capture begins keeps
    /// its release, or the recording it started would have nothing left that could end it.
    /// </summary>
    [Fact]
    public void AKeyAlreadyHeldWhenCaptureBeginsStillDeliversItsRelease()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);

        var pressed = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        tracker.SetCapturingKeybind(capturing: true);
        var released = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Pressed, pressed.Signal);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
    }

    /// <summary>
    /// The same invariant for toggle mode, where a recording runs with nothing held at all. Here
    /// the press IS the stop, so suppressing it would leave a recording nobody could end.
    /// </summary>
    [Fact]
    public void ATogglingRecordingCanStillBeStoppedWhileCapturing()
    {
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(F8, HotkeyModifiers.None),
            new HotkeyBinding(HotkeyEdgeTracker.EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.Toggle);
        tracker.SetRecordingActive(active: true);
        tracker.SetCapturingKeybind(capturing: true);

        var pressed = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Released, pressed.Signal);
    }

    /// <summary>
    /// Capture ends and the key works again. A stand-down that could not be lifted would stop
    /// dictation with nothing on screen to explain it.
    /// </summary>
    [Fact]
    public void ClearingCaptureRestoresTheRecordingKey()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.None);
        tracker.SetCapturingKeybind(capturing: true);
        var whileCapturing = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);
        tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        tracker.SetCapturingKeybind(capturing: false);
        var afterCapturing = tracker.Process(F8, isKeyDown: true, HotkeyModifiers.None);

        Assert.Null(whileCapturing.Signal);
        Assert.Equal(PushToTalkSignal.Pressed, afterCapturing.Signal);
    }
}
