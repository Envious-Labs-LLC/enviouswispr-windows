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
    private readonly Func<uint, HotkeyModifiers, bool>? _typesCharacter;
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

    /// <summary>The modifiers Windows reports as held purely because the record key is down.</summary>
    private readonly HotkeyModifiers _recordSuppliedModifiers;

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

    /// <param name="typesCharacter">
    /// Whether a key with the modifiers held types a character in the layout the person is typing in; a
    /// Ctrl+Alt binding then stands aside (see <see cref="IsTypingChord"/>). Null answers never, which is
    /// what a test that is not about layouts wants; the hook always passes the real question.
    /// </param>
    public HotkeyEdgeTracker(
        HotkeyBinding record,
        HotkeyBinding cancel,
        HotkeyBinding quickAdd,
        DictationRecordingMode recordingMode,
        Func<uint, HotkeyModifiers, bool>? typesCharacter = null)
    {
        _typesCharacter = typesCharacter;
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

        // WHAT WINDOWS WILL REPORT AS HELD WHILE THIS BINDING IS ENGAGED. Binding right Control
        // means every key pressed during a recording arrives with Control active, and an exact
        // modifier match then refuses Escape - so taking the offer on the Keybinds page silently
        // cost the person their cancel key.
        _recordSuppliedModifiers = isModifierSet
            ? record.Modifiers
            : ModifierSuppliedBy(record.VirtualKey);
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

    /// <summary>Which modifier flag Windows raises while one sided modifier key is held.</summary>
    private static HotkeyModifiers ModifierSuppliedBy(uint virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => HotkeyModifiers.Shift,
        0xA2 or 0xA3 => HotkeyModifiers.Control,
        0x5B or 0x5C => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };

    /// <summary>
    /// Whether the keys held right now are the ones a binding asks for, ignoring the record key.
    /// </summary>
    /// <remarks>
    /// THE RECORD KEY'S OWN MODIFIER IS NOT PART OF THE QUESTION WHILE IT IS RECORDING. Escape is
    /// bound with no modifiers, and holding right Control to talk makes Windows report Control on
    /// every key that follows - so an exact match refuses the one key that must always work.
    ///
    /// ONLY WHILE THE KEY IS PHYSICALLY HELD, which is not the same as "while a recording is
    /// running". A hands-free recording runs with the key long since released, so masking for the
    /// whole recording turned a deliberate Ctrl+Escape into a bare Escape and cancelled on a
    /// keystroke nobody bound.
    ///
    /// AND ONLY MODIFIERS THE BINDING DOES NOT ASK FOR. Removing Control from what is held breaks a
    /// cancel binding of Ctrl+Escape, which then can never match while right Control is down -
    /// taking the key away from the one person who deliberately chose it.
    /// </remarks>
    private bool ModifiersMatch(HotkeyModifiers active, HotkeyModifiers required)
    {
        var suppliedNow = _recordGesture?.IsHolding == true
            ? _recordSuppliedModifiers
            : HotkeyModifiers.None;
        var ignored = suppliedNow & ~required;
        return (active & ~ignored) == required;
    }

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
                if (virtualKey == _typingKey)
                {
                    _typingKey = NoKey;
                    return new HotkeyEdgeDecision(Consume: false);
                }

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

            // A HELD KEY REPEATING IS STILL THE SAME PRESS. It went to the application on the way down, so
            // every repeat and its release go there too, whatever the layout or modifiers say by then.
            if (virtualKey == _typingKey)
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            if (virtualKey == _record.VirtualKey && activeModifiers == _record.Modifiers &&
                !StandsAsideForTyping(virtualKey, activeModifiers, _recordHeld))
            {
                return ProcessRecord(isKeyDown: true, activeModifiers: activeModifiers);
            }

            if (virtualKey == _cancel.VirtualKey && ModifiersMatch(activeModifiers, _cancel.Modifiers) &&
                !StandsAsideForTyping(virtualKey, activeModifiers, _cancelHeld))
            {
                return ProcessCancel(isKeyDown: true, activeModifiers: activeModifiers);
            }

            if (virtualKey == _quickAdd.VirtualKey && activeModifiers == _quickAdd.Modifiers &&
                !StandsAsideForTyping(virtualKey, activeModifiers, _quickAddHeld))
            {
                return ProcessQuickAdd(isKeyDown: true, activeModifiers: activeModifiers);
            }

            return new HotkeyEdgeDecision(Consume: false);
        }
    }

    /// <summary>
    /// Whether a key-down that would match a binding is AltGr typing instead, decided ONCE for the press.
    /// </summary>
    /// <remarks>
    /// A press the binding already owns is never re-asked: its repeats stay the binding's. A press that
    /// goes to the application is remembered in <see cref="_typingKey"/>, so its repeats and its release go
    /// there too even if the layout or the modifiers change while it is held - otherwise the application
    /// could receive a key-down without its key-up. Ref: #206.
    /// </remarks>
    private bool StandsAsideForTyping(uint virtualKey, HotkeyModifiers activeModifiers, bool alreadyHeld)
    {
        if (alreadyHeld || !IsTypingChord(virtualKey, activeModifiers))
        {
            return false;
        }

        _typingKey = virtualKey;
        return true;
    }

    /// <summary>A Ctrl+Alt press that the person's keyboard layout turns into a character: AltGr typing.</summary>
    /// <remarks>
    /// ASKED ONLY ON A KEY-DOWN THAT WOULD OTHERWISE MATCH, so the layout is read once per candidate press
    /// and never for ordinary typing. Windows chords are left alone - no layout puts characters on Win.
    ///
    /// AN ANSWER THAT CANNOT BE HAD IS TYPING. This runs inside the low-level hook; an exception escaping
    /// it would take the hook down, and a shortcut that does not fire is visible and retryable where a
    /// swallowed character is neither.
    /// </remarks>
    private bool IsTypingChord(uint virtualKey, HotkeyModifiers activeModifiers)
    {
        if (_typesCharacter is null ||
            (activeModifiers & CtrlAlt) != CtrlAlt ||
            activeModifiers.HasFlag(HotkeyModifiers.Windows))
        {
            return false;
        }

        try
        {
            return _typesCharacter(virtualKey, activeModifiers);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return true;
        }
    }

    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private const uint NoKey = 0;

    /// <summary>The key whose current press went to the application as typing, or <see cref="NoKey"/>.</summary>
    private uint _typingKey = NoKey;

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

            if (!_recordingActive || !ModifiersMatch(activeModifiers, _cancel.Modifiers))
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            _cancelHeld = true;
            _cancelledUntilRecordRelease = _recordHeld;

            // THE GESTURE FORGETS EVERYTHING, INCLUDING A HANDS-FREE RECORDING. Without this,
            // letting go of the record key after a cancel ends a hold that is no longer running and
            // delivers text the person threw away, and a hands-free recording stays believed-in so
            // the next single tap "stops" something that ended a minute ago.
            _recordGesture?.Abandon();
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
