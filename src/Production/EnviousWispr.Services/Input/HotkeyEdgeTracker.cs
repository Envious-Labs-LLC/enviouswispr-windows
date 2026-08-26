using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

internal readonly record struct HotkeyEdgeDecision(
    bool Consume,
    PushToTalkSignal? Signal = null);

internal sealed class HotkeyEdgeTracker(uint triggerVirtualKey, HotkeyModifiers requiredModifiers)
{
    internal const uint EscapeVirtualKey = 0x1B;

    private bool _triggerHeld;
    private bool _cancelHeld;
    private bool _cancelledUntilTriggerRelease;

    public HotkeyEdgeDecision Process(
        uint virtualKey,
        bool isKeyDown,
        HotkeyModifiers activeModifiers)
    {
        if (virtualKey == triggerVirtualKey)
        {
            if (isKeyDown)
            {
                if (_triggerHeld)
                {
                    return new HotkeyEdgeDecision(Consume: true);
                }

                if (activeModifiers != requiredModifiers)
                {
                    return new HotkeyEdgeDecision(Consume: false);
                }

                _triggerHeld = true;
                _cancelledUntilTriggerRelease = false;
                return new HotkeyEdgeDecision(Consume: true, PushToTalkSignal.Pressed);
            }

            if (!_triggerHeld)
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            _triggerHeld = false;
            if (_cancelledUntilTriggerRelease)
            {
                _cancelledUntilTriggerRelease = false;
                return new HotkeyEdgeDecision(Consume: true);
            }

            return new HotkeyEdgeDecision(Consume: true, PushToTalkSignal.Released);
        }

        if (virtualKey != EscapeVirtualKey || !_triggerHeld)
        {
            return new HotkeyEdgeDecision(Consume: false);
        }

        if (isKeyDown)
        {
            if (_cancelHeld)
            {
                return new HotkeyEdgeDecision(Consume: true);
            }

            _cancelHeld = true;
            _cancelledUntilTriggerRelease = true;
            return new HotkeyEdgeDecision(Consume: true, PushToTalkSignal.Cancelled);
        }

        if (!_cancelHeld)
        {
            return new HotkeyEdgeDecision(Consume: false);
        }

        _cancelHeld = false;
        return new HotkeyEdgeDecision(Consume: true);
    }
}
