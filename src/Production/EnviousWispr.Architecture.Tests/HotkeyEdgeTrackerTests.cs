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

    private static void HoldPastThreshold() =>
        Thread.Sleep(HotkeyGesturePolicy.ModifierHoldThreshold + TimeSpan.FromMilliseconds(40));

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

    private const uint EscapeKey = 0x1B;

    [Fact]
    public void EscapeStillCancelsWhileTheRecordKeyIsRightControl()
    {
        // WINDOWS REPORTS CONTROL ON EVERY KEY THAT FOLLOWS while right Control is held, and Escape
        // is bound with no modifiers, so an exact match refused the one key that must always work.
        // Taking the offer on the Keybinds page silently cost the person their cancel key.
        var tracker = ModifierBound();
        var started = tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Pressed, started.Signal);
        tracker.SetRecordingActive(true);

        var cancelled = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.Control);

        Assert.Equal(PushToTalkSignal.Cancelled, cancelled.Signal);
    }

    [Fact]
    public void LettingGoAfterACancelDoesNotDeliverWhatWasThrownAway()
    {
        var tracker = ModifierBound();
        Assert.Equal(PushToTalkSignal.Pressed, tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control).Signal);
        tracker.SetRecordingActive(true);
        tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.Control);
        tracker.Process(EscapeKey, isKeyDown: false, HotkeyModifiers.Control);

        var released = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);

        Assert.Null(released.Signal);
    }

    [Fact]
    public void EscapeCancelsAHandsFreeRecordingAndItStaysCancelled()
    {
        // A hands-free recording survives losing focus on purpose, so the ordinary reset leaves it
        // running. A cancel is the opposite, and leaving it believed-in means the next single tap
        // "stops" something that ended a minute ago.
        var tracker = ModifierBound();
        DoubleTap(tracker);
        tracker.SetRecordingActive(true);

        var cancelled = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Cancelled, cancelled.Signal);
        tracker.Process(EscapeKey, isKeyDown: false, HotkeyModifiers.None);
        tracker.SetRecordingActive(false);
        var tap = SingleTap(tracker);

        Assert.Equal(PushToTalkSignal.Cancelled, cancelled.Signal);
        // After Escape cleared hands-free, a single brief tap discards cleanly instead of stopping an old take.
        Assert.Equal(PushToTalkSignal.Discarded, tap);
    }

    [Fact]
    public void ControlAndEscapeIsNotACancelWhenNothingIsRecording()
    {
        // The mask is only lifted while a recording is running. Otherwise binding right Control
        // would quietly turn Ctrl+Escape into a cancel for the rest of the session.
        var tracker = ModifierBound();

        var pressed = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.Control);

        Assert.Null(pressed.Signal);
    }

    [Fact]
    public void ControlAndEscapeDoesNotCancelAHandsFreeRecording()
    {
        // THE KEY IS LONG SINCE RELEASED in hands-free, so nothing is supplying Control and Escape
        // is not what was pressed. Masking for the whole recording turned a deliberate Ctrl+Escape
        // into a bare Escape and threw away what somebody was still saying.
        var tracker = ModifierBound();
        DoubleTap(tracker);
        tracker.SetRecordingActive(true);

        var pressed = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.Control);

        Assert.Null(pressed.Signal);
    }

    [Fact]
    public void ACancelBindingOfControlAndEscapeStillWorksWhileRightControlIsHeld()
    {
        // Removing Control from what is held breaks the binding of the one person who deliberately
        // chose Ctrl+Escape, which could then never match while the record key was down.
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(RightControl, HotkeyModifiers.None),
            new HotkeyBinding(EscapeKey, HotkeyModifiers.Control),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk);
        Assert.Equal(PushToTalkSignal.Pressed, tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control).Signal);
        tracker.SetRecordingActive(true);

        var cancelled = tracker.Process(EscapeKey, isKeyDown: true, HotkeyModifiers.Control);

        Assert.Equal(PushToTalkSignal.Cancelled, cancelled.Signal);
    }

    /// <summary>Taps the bound modifier twice, which locks hands-free after immediate initial start.</summary>
    private static void DoubleTap(HotkeyEdgeTracker tracker)
    {
        var first = tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Pressed, first.Signal);
        Assert.False(first.IsHandsFree);
        tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);
        tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);

        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        var locked = tracker.TickDecision();
        Assert.NotNull(locked);
        Assert.Equal(PushToTalkSignal.Pressed, locked.Value.Signal);
        Assert.True(locked.Value.IsHandsFree);
    }

    /// <summary>Taps the bound modifier once and lets the tap window close.</summary>
    private static PushToTalkSignal? SingleTap(HotkeyEdgeTracker tracker)
    {
        tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);
        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        return tracker.Tick();
    }

    /// <summary>
    /// THE CONTROL FOR THE WHOLE MODIFIER BINDING. Pressing it starts recording immediately with no 200ms wait.
    /// </summary>
    [Fact]
    public void HoldingABoundModifierStartsARecording()
    {
        var tracker = ModifierBound();

        var started = tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Pressed, started.Signal);
        Assert.False(started.IsHandsFree);
        Assert.Null(tracker.NextDeadline);
    }

    /// <summary>
    /// Two modifiers together are a binding (Ctrl+Win), starting immediately as soon as the full set engages.
    /// </summary>
    [Fact]
    public void HoldingABoundModifierSetStartsARecording()
    {
        var tracker = ModifierSetBound();

        // Control alone is not the set, so nothing starts.
        var partial = tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Null(partial.Signal);
        Assert.Null(tracker.NextDeadline);

        // Windows joins it and the set starts immediately with zero hold threshold delay.
        var started = tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Windows);
        Assert.Equal(PushToTalkSignal.Pressed, started.Signal);
        Assert.Null(tracker.NextDeadline);

        HoldPastThreshold();

        // Letting either one go ends it.
        var released = tracker.Process(LeftWindows, isKeyDown: false, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
    }

    /// <summary>
    /// Ctrl+Win+D makes a new desktop. Pressing an ordinary key while the set is held cleanly discards
    /// without committing and leaves the chord unconsumed.
    /// </summary>
    [Fact]
    public void AShortcutBuiltOnTheBoundSetStartsNothing()
    {
        var tracker = ModifierSetBound();

        Assert.Null(tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.Control).Signal);
        Assert.Equal(
            PushToTalkSignal.Pressed,
            tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Windows).Signal);

        var chord = tracker.Process(
            LetterC,
            isKeyDown: true,
            HotkeyModifiers.Control | HotkeyModifiers.Windows);
        Assert.Equal(PushToTalkSignal.Discarded, chord.Signal);
        Assert.False(chord.Consume);
        Assert.Null(tracker.NextDeadline);

        // Releasing the shortcut afterwards emits no Released or stray hands-free lock.
        Assert.Null(tracker.Process(LetterC, isKeyDown: false, HotkeyModifiers.Control | HotkeyModifiers.Windows).Signal);
        Assert.Null(tracker.Process(LeftWindows, isKeyDown: false, HotkeyModifiers.Control).Signal);
        Assert.Null(tracker.Process(LeftControl, isKeyDown: false, HotkeyModifiers.None).Signal);

        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        Assert.Null(tracker.Tick());
    }

    /// <summary>
    /// A modifier that does not reach Windows breaks copy, paste, and every shortcut on the machine.
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
    /// Holding the bound modifier and pressing C/V/Tab cancels & discards quietly so shortcuts pass cleanly.
    /// </summary>
    [Fact]
    public void AShortcutOnTheBoundModifierStartsNothing()
    {
        var tracker = ModifierBound();

        var press = tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Pressed, press.Signal);
        Assert.False(press.Consume);

        var letter = tracker.Process(LetterC, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Discarded, letter.Signal);
        Assert.False(letter.Consume);

        Assert.Null(tracker.Process(LetterC, isKeyDown: false, HotkeyModifiers.Control).Signal);
        var release = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);

        Assert.Null(release.Signal);
        Assert.False(release.Consume);
    }

    /// <summary>Releasing a held modifier past threshold finishes the recording immediately with nothing pending.</summary>
    [Fact]
    public void ReleasingTheHoldFinishesTheRecordingImmediately()
    {
        var tracker = ModifierBound();

        Assert.Equal(PushToTalkSignal.Pressed, tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control).Signal);
        HoldPastThreshold();

        var released = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Released, released.Signal);
        Assert.Null(tracker.NextDeadline);
    }

    [Theory]
    [InlineData(0x43u)] // Ctrl+C
    [InlineData(0x56u)] // Ctrl+V
    public void CommonControlShortcutsDiscardCaptureQuietlyAndPassThrough(uint letterKey)
    {
        var tracker = ModifierBound();

        Assert.Equal(PushToTalkSignal.Pressed, tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control).Signal);
        var chord = tracker.Process(letterKey, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Discarded, chord.Signal);
        Assert.False(chord.Consume);
        Assert.Null(tracker.Process(letterKey, isKeyDown: false, HotkeyModifiers.Control).Signal);
        Assert.Null(tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None).Signal);
    }

    [Fact]
    public void AltTabWhileLeftAltIsBoundDiscardsCaptureAndPassesTabThrough()
    {
        const uint leftAlt = 0xA4;
        const uint tab = 0x09;
        var tracker = new HotkeyEdgeTracker(leftAlt, HotkeyModifiers.None);

        var altDown = tracker.Process(leftAlt, isKeyDown: true, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Pressed, altDown.Signal);
        Assert.False(altDown.Consume);

        var tabDown = tracker.Process(tab, isKeyDown: true, HotkeyModifiers.Alt);
        Assert.Equal(PushToTalkSignal.Discarded, tabDown.Signal);
        Assert.False(tabDown.Consume);

        Assert.Null(tracker.Process(tab, isKeyDown: false, HotkeyModifiers.Alt).Signal);
        Assert.Null(tracker.Process(leftAlt, isKeyDown: false, HotkeyModifiers.None).Signal);
    }

    [Fact]
    public void OrdinaryKeyOrExtraModifierHeldFirstRefusesToStartModifierRecording()
    {
        var tracker = ModifierBound();

        // Typing 'A' when RightCtrl arrives -> refused.
        tracker.Process('A', isKeyDown: true, HotkeyModifiers.None);
        Assert.Null(tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Control).Signal);
        Assert.Null(tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None).Signal);
        tracker.Process('A', isKeyDown: false, HotkeyModifiers.None);

        // Shift held before RightCtrl -> Shift+Ctrl shortcut protection, refused.
        tracker.Process(0xA0, isKeyDown: true, HotkeyModifiers.Shift);
        Assert.Null(tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.Shift | HotkeyModifiers.Control).Signal);
        Assert.Null(tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.Shift).Signal);
        tracker.Process(0xA0, isKeyDown: false, HotkeyModifiers.None);
    }

    [Fact]
    public void ExtraModifierPressedWhileModifierSetHeldDiscardsQuietly()
    {
        const uint leftShift = 0xA0;
        var tracker = ModifierSetBound();

        tracker.Process(LeftControl, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(
            PushToTalkSignal.Pressed,
            tracker.Process(LeftWindows, isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Windows).Signal);

        var shift = tracker.Process(leftShift, isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Windows | HotkeyModifiers.Shift);
        Assert.Equal(PushToTalkSignal.Discarded, shift.Signal);
        Assert.False(shift.Consume);
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
    /// Sided Alt keys are accepted as modifier bindings alongside Shift, Ctrl, and Win.
    /// </summary>
    [Theory]
    [InlineData(0xA4u)]
    [InlineData(0xA5u)]
    public void SidedAltIsAcceptedAsAModifierBinding(uint alt)
    {
        Assert.True(HotkeyEdgeTracker.IsModifierKey(alt));
    }

    /// <summary>
    /// The control for the check above. The keys that ARE accepted must be recognised, or a
    /// method that always returned false would pass it while disabling the feature entirely.
    /// </summary>
    [Theory]
    [InlineData(0xA0u)]
    [InlineData(0xA2u)]
    [InlineData(0xA3u)]
    [InlineData(0xA4u)]
    [InlineData(0xA5u)]
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

/// <summary>A single-modifier binding at the modifier state the hook really sees on the press. Ref: #207.</summary>
/// <remarks>
/// A LOW-LEVEL HOOK RUNS BEFORE WINDOWS UPDATES THE PRESSED KEY'S OWN STATE, so Right Ctrl's first key-down
/// arrives with no Control reported. The suite above feeds it with Control, the one value the machine never
/// supplies on that edge, which is how a tap that started a recording at once went unseen.
/// </remarks>
public sealed class SingleModifierFirstPressTests
{
    private const uint RightControl = 0xA3;

    [Theory]
    [InlineData(HotkeyModifiers.None)]
    [InlineData(HotkeyModifiers.Control)]
    public void ASingleTapStartsNothingAndIsNeverSwallowed(HotkeyModifiers onThePress)
    {
        var tracker = new HotkeyEdgeTracker(RightControl, HotkeyModifiers.None);

        var down = tracker.Process(RightControl, isKeyDown: true, onThePress);
        var up = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Pressed, down.Signal);
        Assert.False(down.Consume);
        Assert.Null(up.Signal);
        Assert.False(up.Consume);

        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        Assert.Equal(PushToTalkSignal.Discarded, tracker.Tick());
    }

    /// <summary>Right Ctrl + C discards any instant capture and reaches the application as Right Ctrl + C.</summary>
    [Fact]
    public void AShortcutUnderTheBoundModifierStillReachesTheApplication()
    {
        var tracker = new HotkeyEdgeTracker(RightControl, HotkeyModifiers.None);

        var control = tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.None);
        var letter = tracker.Process('C', isKeyDown: true, HotkeyModifiers.Control);

        Assert.Equal(PushToTalkSignal.Pressed, control.Signal);
        Assert.False(control.Consume);
        Assert.Equal(PushToTalkSignal.Discarded, letter.Signal);
        Assert.False(letter.Consume);
    }

    [Fact]
    public void EscapeBeforeRecordingIsActiveDiscardsTentativeModifierCapture()
    {
        var tracker = new HotkeyEdgeTracker(RightControl, HotkeyModifiers.None);
        var down = tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Pressed, down.Signal);

        // Escape before microphone startup marks the session active must discard the tentative
        // capture rather than falling through ProcessCancel with Consume=false.
        var escape = tracker.Process(HotkeyEdgeTracker.EscapeVirtualKey, isKeyDown: true, HotkeyModifiers.Control);
        Assert.Equal(PushToTalkSignal.Discarded, escape.Signal);

        Thread.Sleep(HotkeyGesturePolicy.ModifierHoldThreshold + TimeSpan.FromMilliseconds(30));
        Assert.Null(tracker.Tick());
        var release = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);
        Assert.Null(release.Signal);
    }

    [Fact]
    public void ReleasingOrdinaryKeyWhileModifierSetIsHeldDoesNotStartRecording()
    {
        const uint letterC = 0x43;
        const uint leftControl = 0xA2;
        const uint leftWindows = 0x5B;
        var tracker = new HotkeyEdgeTracker(
            new HotkeyBinding(0, HotkeyModifiers.Control | HotkeyModifiers.Windows),
            new HotkeyBinding(0x1B, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk);

        Assert.Null(tracker.Process(letterC, isKeyDown: true, HotkeyModifiers.None).Signal);
        Assert.Null(tracker.Process(leftControl, isKeyDown: true, HotkeyModifiers.Control).Signal);
        Assert.Null(tracker.Process(leftWindows, isKeyDown: true, HotkeyModifiers.Control | HotkeyModifiers.Windows).Signal);

        // Releasing 'C' leaves Control+Windows held, but no recording key went down to complete it.
        var cUp = tracker.Process(letterC, isKeyDown: false, HotkeyModifiers.Control | HotkeyModifiers.Windows);
        Assert.Null(cUp.Signal);
        Assert.Null(tracker.NextDeadline);
    }

    [Fact]
    public void BriefModifierTapDuringExternalRecordingDoesNotEmitDiscarded()
    {
        var tracker = new HotkeyEdgeTracker(RightControl, HotkeyModifiers.None);
        tracker.SetRecordingActive(true);

        var down = tracker.Process(RightControl, isKeyDown: true, HotkeyModifiers.None);
        var up = tracker.Process(RightControl, isKeyDown: false, HotkeyModifiers.None);
        Assert.Null(down.Signal);
        Assert.Null(up.Signal);

        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        Assert.Null(tracker.Tick());
    }
}

public sealed class SidedAltModifierBindingTests
{
    private const uint LeftAlt = 0xA4;
    private const uint RightAlt = 0xA5;

    [Theory]
    [InlineData(LeftAlt)]
    [InlineData(RightAlt)]
    public void HoldingSidedAltStartsAndEndsRecordingWithMenuKeyMask(uint altKey)
    {
        var tracker = new HotkeyEdgeTracker(altKey, HotkeyModifiers.None);

        var down = tracker.Process(altKey, isKeyDown: true, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Pressed, down.Signal);
        Assert.False(down.IsHandsFree);
        Assert.True(down.MaskMenuKey);
        Assert.False(down.Consume);

        Thread.Sleep(HotkeyGesturePolicy.ModifierHoldThreshold + TimeSpan.FromMilliseconds(60));
        tracker.SetRecordingActive(true);
        var released = tracker.Process(altKey, isKeyDown: false, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Released, released.Signal);
        Assert.True(released.MaskMenuKey);
        Assert.False(released.Consume);
    }

    [Theory]
    [InlineData(LeftAlt)]
    [InlineData(RightAlt)]
    public void DoubleTapAndSingleTapStopOnSidedAltMaskMenuBarAndEmitHandsFree(uint altKey)
    {
        var tracker = new HotkeyEdgeTracker(altKey, HotkeyModifiers.None);

        for (var i = 0; i < 2; i++)
        {
            var tapDown = tracker.Process(altKey, isKeyDown: true, HotkeyModifiers.None);
            Assert.True(tapDown.MaskMenuKey);
            tracker.Process(altKey, isKeyDown: false, HotkeyModifiers.None);
        }

        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        var started = tracker.TickDecision();
        Assert.NotNull(started);
        Assert.Equal(PushToTalkSignal.Pressed, started.Value.Signal);
        Assert.True(started.Value.IsHandsFree);
        Assert.True(started.Value.MaskMenuKey);

        tracker.SetRecordingActive(true);
        tracker.Process(altKey, isKeyDown: true, HotkeyModifiers.None);
        tracker.Process(altKey, isKeyDown: false, HotkeyModifiers.None);
        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        var stopped = tracker.TickDecision();
        Assert.NotNull(stopped);
        Assert.Equal(PushToTalkSignal.Released, stopped.Value.Signal);
        Assert.True(stopped.Value.MaskMenuKey);
    }

    [Fact]
    public void EndingRecordingExternallyClearsHandsFreeStateSoNextTapDoesNotStop()
    {
        var tracker = new HotkeyEdgeTracker(RightAlt, HotkeyModifiers.None);

        for (var i = 0; i < 2; i++)
        {
            tracker.Process(RightAlt, isKeyDown: true, HotkeyModifiers.None);
            tracker.Process(RightAlt, isKeyDown: false, HotkeyModifiers.None);
        }

        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        Assert.Equal(PushToTalkSignal.Pressed, tracker.Tick());
        tracker.SetRecordingActive(true);

        // Auto-stop or timeout ends the recording without a key tap.
        tracker.SetRecordingActive(false);

        var nextDown = tracker.Process(RightAlt, isKeyDown: true, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Pressed, nextDown.Signal);
        tracker.Process(RightAlt, isKeyDown: false, HotkeyModifiers.None);
        Thread.Sleep(HotkeyGesturePolicy.MultiTapWindow + TimeSpan.FromMilliseconds(60));
        // Hands-free state was cleared, so a lone brief tap discards rather than emitting Released.
        Assert.Equal(PushToTalkSignal.Discarded, tracker.Tick());
    }

    [Fact]
    public void EscapeStillCancelsWhileLeftAltIsHeldAndMasksMenuKey()
    {
        var tracker = new HotkeyEdgeTracker(LeftAlt, HotkeyModifiers.None);
        var down = tracker.Process(LeftAlt, isKeyDown: true, HotkeyModifiers.None);
        Assert.Equal(PushToTalkSignal.Pressed, down.Signal);
        tracker.SetRecordingActive(true);

        var cancelled = tracker.Process(HotkeyEdgeTracker.EscapeVirtualKey, isKeyDown: true, HotkeyModifiers.Alt);
        Assert.Equal(PushToTalkSignal.Cancelled, cancelled.Signal);
        Assert.True(cancelled.MaskMenuKey);
    }
}
