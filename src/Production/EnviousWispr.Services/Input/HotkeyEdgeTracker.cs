using EnviousWispr.Core.Input;
using System.Diagnostics;
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
    private bool _capturingKeybind;

    /// <summary>Set only when the recording key is itself a modifier.</summary>
    /// <remarks>
    /// A MODIFIER CANNOT BE HELD TO TALK, because holding it is how every shortcut begins, so a
    /// modifier binding takes a completely different route through this class: a tap toggles, the
    /// key is NEVER consumed, and the ordinary press/release path is not used at all. Null for every
    /// normal binding, which is what keeps that path unchanged.
    /// </remarks>
    private readonly ModifierTapPolicy? _recordTap;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

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
        _recordTap = IsModifierKey(record.VirtualKey) && record.Modifiers == HotkeyModifiers.None
            ? new ModifierTapPolicy(record.VirtualKey)
            : null;
    }

    /// <summary>The virtual keys that are modifiers, either side.</summary>
    /// <remarks>
    /// ALT IS DELIBERATELY ABSENT. A lone Alt tap already opens a window's menu bar in Windows, so
    /// binding dictation to it would put this app in a fight with the shell over the same gesture -
    /// and the shell wins in ways nobody can debug. Refusing it costs one binding; taking it costs
    /// the user their menu key.
    /// </remarks>
    internal static bool IsModifierKey(uint virtualKey) => virtualKey is
        0xA0 or 0xA1 or   // left and right Shift
        0xA2 or 0xA3 or   // left and right Control
        0x5B or 0x5C;     // left and right Windows

    public void SetRecordingActive(bool active)
    {
        lock (_sync)
        {
            _recordingActive = active;
        }
    }

    /// <summary>
    /// While true, a keybind field on the Keybinds page is waiting for a keystroke.
    /// </summary>
    /// <remarks>
    /// The recording key is a system-wide hook, so pressing it to rebind it STARTS A RECORDING:
    /// measured on the running app, pressing the recording key inside its own capture field ran
    /// a live recording for 64 seconds. The field marks the keystroke handled, but that is a
    /// different path - the hook sees the key before any window does, so being handled in the
    /// window changes nothing.
    ///
    /// The exception below is the important half. A recording ALREADY IN FLIGHT must always be
    /// able to stop, so capture never suppresses a key while something is held or while a
    /// toggle-mode recording is running. Suppressing there would swallow the release edge and
    /// leave a recording nothing could end, which is a worse defect than the one being fixed.
    /// The consequence, deliberately: press the recording key mid-recording and it stops the
    /// recording rather than being captured.
    /// </remarks>
    public void SetCapturingKeybind(bool capturing)
    {
        lock (_sync)
        {
            _capturingKeybind = capturing;
        }
    }

    public HotkeyEdgeDecision Process(uint virtualKey, bool isKeyDown, HotkeyModifiers activeModifiers)
    {
        lock (_sync)
        {
            if (_capturingKeybind && !IsAnythingInFlight())
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            if (_recordTap is not null)
            {
                var decision = ProcessRecordTap(virtualKey, isKeyDown);
                if (decision is not null)
                {
                    return decision.Value;
                }
            }

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

    /// <summary>
    /// True while a gesture is part-way through, or a toggle-mode recording is running - the
    /// states in which a key must still reach the tracker so the recording can be ended.
    /// </summary>
    private bool IsAnythingInFlight() =>
        _recordHeld || _cancelHeld || _quickAddHeld || _recordingActive;

    /// <summary>
    /// The modifier-binding route: every key is offered, and the modifier is never swallowed.
    /// </summary>
    /// <remarks>
    /// EVERY KEY GOES IN, NOT JUST THE BOUND ONE, because the policy decides a tap by what did NOT
    /// happen during the press. Feeding it only the bound key would leave it unable to tell a tap
    /// from the start of a shortcut.
    ///
    /// IT NEVER CONSUMES. A modifier that does not reach Windows breaks copy, paste and every other
    /// shortcut on the machine - the failure is total, immediate, and lands on someone who has not
    /// opened this app today. Returning null lets the ordinary path have its say about cancel and
    /// Quick Add, which are still normal keys.
    ///
    /// A TAP TOGGLES, whatever the recording mode says. Push-to-talk has no meaning here: there is
    /// no hold to talk through, because holding is the gesture that had to be given up.
    /// </remarks>
    private HotkeyEdgeDecision? ProcessRecordTap(uint virtualKey, bool isKeyDown)
    {
        var outcome = _recordTap!.Process(virtualKey, isKeyDown, _clock.Elapsed);
        if (outcome != ModifierTapOutcome.Tap)
        {
            return virtualKey == _record.VirtualKey
                ? new HotkeyEdgeDecision(Consume: false)
                : null;
        }

        _cancelledUntilRecordRelease = false;
        return new HotkeyEdgeDecision(
            Consume: false,
            _recordingActive ? PushToTalkSignal.Released : PushToTalkSignal.Pressed);
    }

    /// <summary>Forgets a modifier press in progress.</summary>
    public void ResetHeldKeys() => _recordTap?.Reset();

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
