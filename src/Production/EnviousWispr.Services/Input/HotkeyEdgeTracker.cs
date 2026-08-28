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

    /// <summary>Set only when the recording binding is a modifier, or a set of them.</summary>
    /// <remarks>
    /// A MODIFIER CANNOT BE HELD TO TALK WITHOUT A THRESHOLD, because holding it is how every
    /// shortcut begins. So a modifier binding takes a different route: it waits, it abandons the
    /// moment any other key arrives, and the key is NEVER consumed.
    ///
    /// NULL FOR EVERY ORDINARY BINDING, AND THAT IS DELIBERATE BLAST-RADIUS CONTROL. This is the
    /// most dangerous code in the app to get wrong - the failure mode is somebody's whole keyboard -
    /// and it is being changed on a day when the only verification available is unit tests. An
    /// ordinary key therefore runs the SAME code it ran before, byte for byte, and only a user who
    /// chooses a modifier binding is on the new path.
    /// </remarks>
    private readonly HotkeyGesturePolicy? _recordGesture;

    /// <summary>The modifier set a modifier-only binding waits for, or None.</summary>
    private readonly HotkeyModifiers _recordModifierSet;

    /// <summary>True while the bound modifier set is fully held.</summary>
    private bool _modifierSetEngaged;

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
        // A binding with no virtual key is a modifier SET - Ctrl+Win and friends. One with a
        // modifier AS its key is a single sided modifier. Everything else is an ordinary key.
        var isModifierSet = record.VirtualKey == 0 && record.Modifiers != HotkeyModifiers.None;
        var isSingleModifier = IsModifierKey(record.VirtualKey) &&
            record.Modifiers == HotkeyModifiers.None;

        _recordModifierSet = isModifierSet ? record.Modifiers : HotkeyModifiers.None;
        _recordGesture = isModifierSet || isSingleModifier
            ? new HotkeyGesturePolicy(
                isModifierSet ? ModifierSetSentinel : record.VirtualKey,
                needsHoldThreshold: true)
            : null;
    }

    /// <summary>
    /// Stands in for "the whole bound modifier set" so one policy handles both shapes.
    /// </summary>
    /// <remarks>
    /// Zero is never a real virtual key, and a modifier SET has no single key to name. Translating
    /// "the set became complete" into a press of this sentinel means the gesture policy does not
    /// need to know the difference, and there is one implementation of hold-versus-tap rather than
    /// two that can drift apart.
    /// </remarks>
    private const uint ModifierSetSentinel = 0;

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

            if (_recordGesture is not null)
            {
                var decision = ProcessRecordGesture(virtualKey, isKeyDown, activeModifiers);
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
    /// EVERY KEY GOES IN, NOT JUST THE BOUND ONE, because the policy decides a hold by what did NOT
    /// happen during it. Feeding it only the bound key would leave it unable to tell a deliberate
    /// hold from the start of a shortcut.
    ///
    /// IT NEVER CONSUMES. A modifier that does not reach Windows breaks copy, paste and every other
    /// shortcut on the machine - total, immediate, and landing on somebody who has not opened this
    /// app today. Returning null hands other keys back to the ordinary path, which still owns cancel
    /// and Quick Add.
    ///
    /// A MODIFIER SET IS TRANSLATED INTO ONE SYNTHETIC KEY. "Ctrl+Win became complete" is a press
    /// and "it stopped being complete" is a release, so the gesture policy sees the same two events
    /// it sees for a single key and there is one implementation of the timing rather than two.
    /// </remarks>
    private HotkeyEdgeDecision? ProcessRecordGesture(
        uint virtualKey,
        bool isKeyDown,
        HotkeyModifiers activeModifiers)
    {
        var gesture = _recordGesture!;
        HotkeyGestureOutcome outcome;

        if (_recordModifierSet != HotkeyModifiers.None)
        {
            var complete = (activeModifiers & _recordModifierSet) == _recordModifierSet;

            if (complete != _modifierSetEngaged)
            {
                _modifierSetEngaged = complete;
                outcome = gesture.Process(ModifierSetSentinel, complete, _clock.Elapsed);
            }
            else if (isKeyDown && !IsModifierKey(virtualKey))
            {
                // An ordinary key pressed while the set is held is a shortcut - Ctrl+Win+D makes a
                // new desktop - so the gesture is abandoned rather than becoming a recording.
                outcome = gesture.Process(virtualKey, isKeyDown: true, _clock.Elapsed);
            }
            else
            {
                return null;
            }
        }
        else
        {
            outcome = gesture.Process(virtualKey, isKeyDown, _clock.Elapsed);
        }

        return SignalFor(outcome) is { } signal
            ? new HotkeyEdgeDecision(Consume: false, signal)
            : null;
    }

    /// <summary>Lets a pending hold or tap window complete without a key event.</summary>
    /// <remarks>
    /// A HOLD AND A TAP WINDOW BOTH FINISH ON TIME RATHER THAN ON A KEYSTROKE, so something has to
    /// look. Without this call the threshold never elapses and a modifier binding can never start a
    /// recording at all - a whole feature that builds, tests green at the policy level, and does
    /// nothing.
    /// </remarks>
    public PushToTalkSignal? Tick()
    {
        lock (_sync)
        {
            return _recordGesture is null ? null : SignalFor(_recordGesture.Elapsed(_clock.Elapsed));
        }
    }

    /// <summary>When the caller should next call <see cref="Tick"/>, or null if never.</summary>
    public TimeSpan? NextDeadline
    {
        get
        {
            lock (_sync)
            {
                return _recordGesture?.NextDeadline;
            }
        }
    }

    private PushToTalkSignal? SignalFor(HotkeyGestureOutcome outcome)
    {
        switch (outcome)
        {
            case HotkeyGestureOutcome.HoldStarted:
            case HotkeyGestureOutcome.ToggleStarted:
                _cancelledUntilRecordRelease = false;
                return PushToTalkSignal.Pressed;

            case HotkeyGestureOutcome.HoldEnded:
            case HotkeyGestureOutcome.ToggleStopped:
                return PushToTalkSignal.Released;

            case HotkeyGestureOutcome.Cancelled:
                return PushToTalkSignal.Cancelled;

            default:
                return null;
        }
    }

    /// <summary>Forgets a modifier press in progress.</summary>
    public void ResetHeldKeys() => _recordGesture?.Reset();

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
