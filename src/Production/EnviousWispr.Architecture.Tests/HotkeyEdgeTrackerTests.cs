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
    public void ReleaseCompletesEvenWhenModifierWasReleasedFirst()
    {
        var tracker = new HotkeyEdgeTracker(F8, HotkeyModifiers.Control);
        tracker.Process(F8, isKeyDown: true, HotkeyModifiers.Control);

        var released = tracker.Process(F8, isKeyDown: false, HotkeyModifiers.None);

        Assert.Equal(PushToTalkSignal.Released, released.Signal);
        Assert.True(released.Consume);
    }
}
