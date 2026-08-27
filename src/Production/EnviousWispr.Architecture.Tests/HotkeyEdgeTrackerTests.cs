using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Input;

namespace EnviousWispr.Architecture.Tests;

public sealed class HotkeyEdgeTrackerTests
{
    private const uint F8 = 0x77;

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
