using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Input;

internal readonly record struct HotkeyEdgeDecision(bool Consume, PushToTalkSignal? Signal = null);

internal readonly record struct HotkeyBinding(uint VirtualKey, HotkeyModifiers Modifiers);

internal sealed class HotkeyEdgeTracker
{
    internal const uint EscapeVirtualKey = 0x1B;

    private readonly object _sync = new();
    private readonly HotkeyBinding _record;
    private readonly HotkeyBinding _cancel;
    private readonly HotkeyBinding _quickAdd;
    private readonly DictationRecordingMode _recordingMode;
    private bool _recordHeld;
    private bool _cancelHeld;
    private bool _quickAddHeld;
    private bool _recordingActive;
    private bool _cancelledUntilRecordRelease;

    public HotkeyEdgeTracker(uint triggerVirtualKey, HotkeyModifiers requiredModifiers)
        : this(
            new HotkeyBinding(triggerVirtualKey, requiredModifiers),
            new HotkeyBinding(EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk)
    {
    }

    public HotkeyEdgeTracker(
        HotkeyBinding record,
        HotkeyBinding cancel,
        HotkeyBinding quickAdd,
        DictationRecordingMode recordingMode)
    {
        _record = record;
        _cancel = cancel;
        _quickAdd = quickAdd;
        _recordingMode = recordingMode;
    }

    public void SetRecordingActive(bool active)
    {
        lock (_sync)
        {
            _recordingActive = active;
        }
    }

    public HotkeyEdgeDecision Process(uint virtualKey, bool isKeyDown, HotkeyModifiers activeModifiers)
    {
        lock (_sync)
        {
            if (!isKeyDown)
            {
                if (_recordHeld && virtualKey == _record.VirtualKey)
                {
                    return ProcessRecord(isKeyDown: false, activeModifiers: activeModifiers);
                }

                if (_cancelHeld && virtualKey == _cancel.VirtualKey)
                {
                    return ProcessCancel(isKeyDown: false, activeModifiers: activeModifiers);
                }

                if (_quickAddHeld && virtualKey == _quickAdd.VirtualKey)
                {
                    return ProcessQuickAdd(isKeyDown: false, activeModifiers: activeModifiers);
                }

                return new HotkeyEdgeDecision(Consume: false);
            }

            if (virtualKey == _record.VirtualKey && activeModifiers == _record.Modifiers)
            {
                return ProcessRecord(isKeyDown: true, activeModifiers: activeModifiers);
            }

            if (virtualKey == _cancel.VirtualKey && activeModifiers == _cancel.Modifiers)
            {
                return ProcessCancel(isKeyDown: true, activeModifiers: activeModifiers);
            }

            if (virtualKey == _quickAdd.VirtualKey && activeModifiers == _quickAdd.Modifiers)
            {
                return ProcessQuickAdd(isKeyDown: true, activeModifiers: activeModifiers);
            }

            return new HotkeyEdgeDecision(Consume: false);
        }
    }

    private HotkeyEdgeDecision ProcessRecord(bool isKeyDown, HotkeyModifiers activeModifiers)
    {
        if (isKeyDown)
        {
            if (_recordHeld)
            {
                return new HotkeyEdgeDecision(Consume: true);
            }

            if (activeModifiers != _record.Modifiers)
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            _recordHeld = true;
            _cancelledUntilRecordRelease = false;
            var signal = _recordingMode == DictationRecordingMode.Toggle && _recordingActive
                ? PushToTalkSignal.Released
                : PushToTalkSignal.Pressed;
            return new HotkeyEdgeDecision(Consume: true, signal);
        }

        if (!_recordHeld)
        {
            return new HotkeyEdgeDecision(Consume: false);
        }

        _recordHeld = false;
        if (_recordingMode == DictationRecordingMode.Toggle || _cancelledUntilRecordRelease)
        {
            _cancelledUntilRecordRelease = false;
            return new HotkeyEdgeDecision(Consume: true);
        }

        return new HotkeyEdgeDecision(Consume: true, PushToTalkSignal.Released);
    }

    private HotkeyEdgeDecision ProcessCancel(bool isKeyDown, HotkeyModifiers activeModifiers)
    {
        if (isKeyDown)
        {
            if (_cancelHeld)
            {
                return new HotkeyEdgeDecision(Consume: true);
            }

            if (!_recordingActive || activeModifiers != _cancel.Modifiers)
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            _cancelHeld = true;
            _cancelledUntilRecordRelease = _recordHeld;
            return new HotkeyEdgeDecision(Consume: true, PushToTalkSignal.Cancelled);
        }

        if (!_cancelHeld)
        {
            return new HotkeyEdgeDecision(Consume: false);
        }

        _cancelHeld = false;
        return new HotkeyEdgeDecision(Consume: true);
    }

    private HotkeyEdgeDecision ProcessQuickAdd(bool isKeyDown, HotkeyModifiers activeModifiers)
    {
        if (isKeyDown)
        {
            if (_quickAddHeld)
            {
                return new HotkeyEdgeDecision(Consume: true);
            }

            if (_recordingActive || activeModifiers != _quickAdd.Modifiers)
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            _quickAddHeld = true;
            return new HotkeyEdgeDecision(Consume: true, PushToTalkSignal.QuickAdd);
        }

        if (!_quickAddHeld)
        {
            return new HotkeyEdgeDecision(Consume: false);
        }

        _quickAddHeld = false;
        return new HotkeyEdgeDecision(Consume: true);
    }
}
