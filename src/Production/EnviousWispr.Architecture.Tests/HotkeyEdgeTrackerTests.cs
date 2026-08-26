using EnviousWispr.Core.Input;
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
}
