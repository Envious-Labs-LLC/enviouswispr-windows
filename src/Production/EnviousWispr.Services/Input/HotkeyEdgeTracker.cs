using EnviousWispr.Core.Input;
using System.Diagnostics;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Input;

/// <param name="MaskMenuKey">
/// The press consumed a key while Alt or Win was held, so Windows must be told something happened between that
/// modifier's press and release: otherwise releasing Alt opens a menu, Win opens Start, and Alt+Shift switches the
/// keyboard layout. The hook answers it by sending one unassigned key (VK 0xE8). Ref: #206.
/// </param>
internal readonly record struct HotkeyEdgeDecision(
    bool Consume,
    PushToTalkSignal? Signal = null,
    bool MaskMenuKey = false,
    bool IsHandsFree = false,
    bool ArmPasteLastTarget = false);

internal readonly record struct HotkeyBinding(uint VirtualKey, HotkeyModifiers Modifiers);

internal sealed class HotkeyEdgeTracker
{
    internal const uint EscapeVirtualKey = 0x1B;

    private readonly object _sync = new();
    private readonly HotkeyBinding _record;
    private readonly HotkeyBinding _cancel;
    private readonly HotkeyBinding _quickAdd;
    private readonly Func<uint, HotkeyModifiers, bool>? _typesCharacter;
    private readonly OneShotBinding? _pasteLast;
    private readonly OneShotBinding? _copyLast;
    private readonly DictationRecordingMode _recordingMode;
    private bool _recordHeld;
    private bool _cancelHeld;
    private bool _quickAddHeld;
    private bool _recordingActive;
    private bool _cancelArmed;
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
    private bool _ownsActiveRecording;
    private bool _altMaskSentForPress;
    private readonly HashSet<uint> _heldOrdinaryKeys = [];

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
        Func<uint, HotkeyModifiers, bool>? typesCharacter = null,
        HotkeyBinding? pasteLast = null,
        HotkeyBinding? copyLast = null)
    {
        _typesCharacter = typesCharacter;
        _pasteLast = pasteLast is { } paste ? new OneShotBinding(paste, PushToTalkSignal.PasteLast) : null;
        _copyLast = copyLast is { } copy ? new OneShotBinding(copy, PushToTalkSignal.CopyLast) : null;
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
                needsHoldThreshold: true,
                toggleMode: recordingMode == DictationRecordingMode.Toggle)
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
    internal static bool IsModifierKey(uint virtualKey) => virtualKey is
        0xA0 or 0xA1 or   // left and right Shift
        0xA2 or 0xA3 or   // left and right Control
        0xA4 or 0xA5 or   // left and right Alt
        0x5B or 0x5C;     // left and right Windows

    /// <summary>Which modifier flag Windows raises while one sided modifier key is held.</summary>
    private static HotkeyModifiers ModifierSuppliedBy(uint virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => HotkeyModifiers.Shift,
        0xA2 or 0xA3 => HotkeyModifiers.Control,
        0xA4 or 0xA5 => HotkeyModifiers.Alt,
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
            var wasActive = _recordingActive;
            _recordingActive = active;
            if (!active)
            {
                _ownsActiveRecording = false;
                if (wasActive)
                {
                    _recordGesture?.EndHandsFree();
                }
            }
        }
    }

    public void SetCancelArmed(bool armed)
    {
        lock (_sync)
        {
            _cancelArmed = armed;
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

    /// <summary>
    /// One decision per physical press for a key a last-dictation shortcut uses: its first key-down decides, and its
    /// repeats and release follow that decision whatever changes in between. Ref: #206 review.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS A PRESS COULD CHANGE HANDS HALF WAY. Release Alt and Shift while Z is still held and the repeats
    /// no longer match the shortcut, so they went to the application while the release was still swallowed; press
    /// the shortcut during a recording and end the recording while it is held, and a repeat fired the paste. Every
    /// other key is untouched by this wrapper.
    /// </remarks>
    /// <param name="gestureModifiers">
    /// The modifiers from the hook's own events (<c>HeldModifierKeys</c>), used ONLY to decide whether a modifier-set
    /// binding such as Ctrl+Win is complete. Every keyed binding keeps <paramref name="activeModifiers"/>, the keyboard
    /// state, which lags only for the key moving now and so is right for them - and which still counts a modifier held
    /// since before the hook was installed. Null means the same reading for both. Ref: #66.
    /// </param>
    public HotkeyEdgeDecision Process(
        uint virtualKey,
        bool isKeyDown,
        HotkeyModifiers activeModifiers,
        HotkeyModifiers? gestureModifiers = null)
    {
        lock (_sync)
        {
            if (!OneShots().Any(oneShot => oneShot.Binding.VirtualKey == virtualKey))
            {
                return ProcessCore(virtualKey, isKeyDown, activeModifiers, gestureModifiers);
            }

            if (isKeyDown && _oneShotKeyPresses.TryGetValue(virtualKey, out var consumedDown))
            {
                // THE REPEAT KEEPS ITS OWNER, AND A PENDING MODIFIER GESTURE STILL SEES IT: an ordinary key repeating
                // while Right Ctrl or Ctrl+Win is held is what disqualifies that gesture, so it is still offered to
                // it. Ref: #206 review round 2.
                var gestureDecision =
                    _recordGesture is not null && (!_capturingKeybind || IsAnythingInFlight())
                        ? ProcessRecordGesture(virtualKey, isKeyDown, gestureModifiers ?? activeModifiers)
                        : null;
                return new HotkeyEdgeDecision(
                    Consume: consumedDown,
                    Signal: gestureDecision?.Signal,
                    MaskMenuKey: gestureDecision?.MaskMenuKey == true,
                    IsHandsFree: gestureDecision?.IsHandsFree == true);
            }

            var decision = ProcessCore(virtualKey, isKeyDown, activeModifiers, gestureModifiers);
            if (isKeyDown)
            {
                _oneShotKeyPresses[virtualKey] = decision.Consume;
                return decision;
            }

            if (_oneShotKeyPresses.Remove(virtualKey, out var consumed))
            {
                decision = decision with { Consume = consumed };
            }

            foreach (var oneShot in OneShots())
            {
                if (oneShot.Binding.VirtualKey == virtualKey)
                {
                    oneShot.Held = false;
                }
            }

            return decision;
        }
    }

    /// <summary>What each key a one-shot uses did on its first key-down: true if the app consumed it.</summary>
    private readonly Dictionary<uint, bool> _oneShotKeyPresses = new();

    private HotkeyEdgeDecision ProcessCore(
        uint virtualKey,
        bool isKeyDown,
        HotkeyModifiers activeModifiers,
        HotkeyModifiers? gestureModifiers)
    {
        lock (_sync)
        {
            UpdateHeldOrdinaryKeys(virtualKey, isKeyDown);

            if (_capturingKeybind && !IsAnythingInFlight())
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            // CANCEL WINS OVER OTHER-KEY SHORTCUT DISMISSAL once cancellation is armed (recording active
            // or escape recovery), while un-armed Escape falls through to the modifier gesture so a
            // tentative instant-start capture is still discarded cleanly.
            if (isKeyDown &&
                virtualKey == _cancel.VirtualKey &&
                (_recordingActive || _cancelArmed || _cancelHeld) &&
                ModifiersMatch(activeModifiers, _cancel.Modifiers) &&
                !StandsAsideForTyping(virtualKey, activeModifiers, _cancelHeld))
            {
                return ProcessCancel(isKeyDown: true, activeModifiers: activeModifiers);
            }

            if (_recordGesture is not null)
            {
                var decision = ProcessRecordGesture(virtualKey, isKeyDown, gestureModifiers ?? activeModifiers);
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

                foreach (var oneShot in OneShots())
                {
                    if (oneShot.Held && virtualKey == oneShot.Binding.VirtualKey)
                    {
                        oneShot.Held = false;
                        return oneShot.Signal == PushToTalkSignal.PasteLast
                            ? new HotkeyEdgeDecision(
                                Consume: true,
                                PushToTalkSignal.PasteLast,
                                MaskMenuKey: (oneShot.Binding.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0)
                            : new HotkeyEdgeDecision(Consume: true);
                    }
                }

                return new HotkeyEdgeDecision(Consume: false);
            }

            // A HELD KEY REPEATING IS STILL THE SAME PRESS. It went to the application on the way down, so
            // every repeat and its release go there too, whatever the layout or modifiers say by then.
            if (virtualKey == _typingKey)
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            // A GESTURE BINDING NEVER TAKES THIS ROUTE. A lone modifier or a modifier set is decided by the gesture
            // policy above, and when that policy is still waiting it answers null - which used to fall through to
            // here. The hook runs before Windows updates the pressed key's own state, so a Right Ctrl press arrives
            // with no modifiers, matched a binding of "RightCtrl" with none, and was consumed as an instant recording:
            // a 50 ms tap started a take and Right Ctrl + C stopped working. Ref: #207.
            if (_recordGesture is null &&
                virtualKey == _record.VirtualKey && activeModifiers == _record.Modifiers &&
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

            foreach (var oneShot in OneShots())
            {
                if (virtualKey == oneShot.Binding.VirtualKey && activeModifiers == oneShot.Binding.Modifiers &&
                    !StandsAsideForTyping(virtualKey, activeModifiers, oneShot.Held))
                {
                    return ProcessOneShot(oneShot);
                }
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
        _recordHeld || _cancelHeld || _quickAddHeld || _recordingActive || _cancelArmed ||
        _recordGesture?.IsHolding == true || _recordGesture?.HasTentativeRecording == true ||
        _pasteLast?.Held == true || _copyLast?.Held == true;

    private void UpdateHeldOrdinaryKeys(uint virtualKey, bool isKeyDown)
    {
        if (virtualKey == MenuKeyMask.VirtualKeyUnassigned || IsModifierKey(virtualKey))
        {
            return;
        }

        if (isKeyDown)
        {
            _heldOrdinaryKeys.Add(virtualKey);
        }
        else
        {
            _heldOrdinaryKeys.Remove(virtualKey);
        }
    }

    /// <summary>
    /// The modifier-binding route: every key is offered, and the modifier is never swallowed.
    /// </summary>
    /// <remarks>
    /// EVERY KEY GOES IN, NOT JUST THE BOUND ONE, because an instant modifier start must abandon
    /// the moment any other chord key arrives, and refuse to start while an ordinary key or
    /// unrelated modifier is already held.
    ///
    /// IT NEVER CONSUMES. A modifier that does not reach Windows breaks copy, paste and every other
    /// shortcut on the machine. Returning null hands other keys back to the ordinary path, which
    /// still owns cancel and Quick Add.
    /// </remarks>
    private bool RequiresAltMenuMask => _recordSuppliedModifiers.HasFlag(HotkeyModifiers.Alt);

    private HotkeyEdgeDecision? ProcessRecordGesture(
        uint virtualKey,
        bool isKeyDown,
        HotkeyModifiers activeModifiers)
    {
        var gesture = _recordGesture!;
        if (_recordingActive && !_ownsActiveRecording)
        {
            return null;
        }

        HotkeyGestureOutcome outcome;
        var nonBoundKeyEvent = false;

        if (_recordModifierSet != HotkeyModifiers.None)
        {
            var complete = (activeModifiers & _recordModifierSet) == _recordModifierSet;

            if (complete != _modifierSetEngaged)
            {
                if (complete &&
                    (!isKeyDown ||
                     !IsModifierKey(virtualKey) ||
                     (ModifierSuppliedBy(virtualKey) & _recordModifierSet) == 0 ||
                     _heldOrdinaryKeys.Count > 0 ||
                     (activeModifiers & ~_recordSuppliedModifiers) != HotkeyModifiers.None) &&
                    !gesture.IsRecordingHandsFree &&
                    !gesture.HasTentativeRecording)
                {
                    return null;
                }

                _modifierSetEngaged = complete;
                outcome = gesture.Process(ModifierSetSentinel, complete, _clock.Elapsed);
            }
            else if (isKeyDown && (!IsModifierKey(virtualKey) || (ModifierSuppliedBy(virtualKey) & _recordModifierSet) == 0))
            {
                nonBoundKeyEvent = true;
                outcome = gesture.Process(virtualKey, isKeyDown: true, _clock.Elapsed);
            }
            else
            {
                return null;
            }
        }
        else
        {
            if (RequiresAltMenuMask && virtualKey == _record.VirtualKey && !isKeyDown)
            {
                _altMaskSentForPress = false;
            }

            if (virtualKey == _record.VirtualKey && isKeyDown &&
                (_heldOrdinaryKeys.Count > 0 || (activeModifiers & ~_recordSuppliedModifiers) != HotkeyModifiers.None) &&
                !gesture.IsRecordingHandsFree &&
                !gesture.HasTentativeRecording)
            {
                return null;
            }

            nonBoundKeyEvent = virtualKey != _record.VirtualKey;
            outcome = gesture.Process(virtualKey, isKeyDown, _clock.Elapsed);
        }

        if (DecisionFor(outcome, nonBoundKeyEvent) is { } decision)
        {
            if (RequiresAltMenuMask && virtualKey == _record.VirtualKey && isKeyDown && decision.MaskMenuKey)
            {
                _altMaskSentForPress = true;
            }

            return decision;
        }

        if (RequiresAltMenuMask && virtualKey == _record.VirtualKey && isKeyDown && !_altMaskSentForPress)
        {
            _altMaskSentForPress = true;
            return new HotkeyEdgeDecision(Consume: false, MaskMenuKey: true);
        }

        return null;
    }

    /// <summary>Lets a pending hold or tap window complete without a key event.</summary>
    /// <remarks>
    /// A HOLD AND A TAP WINDOW BOTH FINISH ON TIME RATHER THAN ON A KEYSTROKE, so something has to
    /// look. Without this call the threshold never elapses and a modifier binding can never start a
    /// recording at all - a whole feature that builds, tests green at the policy level, and does
    /// nothing.
    /// </remarks>
    public PushToTalkSignal? Tick() => TickDecision()?.Signal;

    internal HotkeyEdgeDecision? TickDecision()
    {
        lock (_sync)
        {
            return _recordGesture is null ? null : DecisionFor(_recordGesture.Elapsed(_clock.Elapsed));
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

    private HotkeyEdgeDecision? DecisionFor(HotkeyGestureOutcome outcome, bool fromOtherKey = false)
    {
        switch (outcome)
        {
            case HotkeyGestureOutcome.HoldStarted:
                _cancelledUntilRecordRelease = false;
                _ownsActiveRecording = true;
                return new HotkeyEdgeDecision(
                    Consume: false,
                    PushToTalkSignal.Pressed,
                    MaskMenuKey: RequiresAltMenuMask && !_altMaskSentForPress,
                    IsHandsFree: _recordingMode == DictationRecordingMode.Toggle);

            case HotkeyGestureOutcome.ToggleStarted:
                if (_recordingMode == DictationRecordingMode.Toggle && _ownsActiveRecording)
                {
                    return null;
                }

                _cancelledUntilRecordRelease = false;
                _ownsActiveRecording = true;
                return new HotkeyEdgeDecision(
                    Consume: false,
                    PushToTalkSignal.Pressed,
                    MaskMenuKey: RequiresAltMenuMask && !_altMaskSentForPress,
                    IsHandsFree: true);

            case HotkeyGestureOutcome.HoldEnded:
            case HotkeyGestureOutcome.ToggleStopped:
                _ownsActiveRecording = false;
                return new HotkeyEdgeDecision(
                    Consume: false,
                    PushToTalkSignal.Released,
                    MaskMenuKey: RequiresAltMenuMask);

            case HotkeyGestureOutcome.Cancelled:
                _ownsActiveRecording = false;
                return new HotkeyEdgeDecision(
                    Consume: false,
                    PushToTalkSignal.Cancelled,
                    MaskMenuKey: RequiresAltMenuMask);

            case HotkeyGestureOutcome.Discarded:
                if (!_ownsActiveRecording)
                {
                    return null;
                }

                _ownsActiveRecording = false;
                return new HotkeyEdgeDecision(
                    Consume: false,
                    PushToTalkSignal.Discarded,
                    MaskMenuKey: !fromOtherKey && RequiresAltMenuMask && !_altMaskSentForPress);

            default:
                return null;
        }
    }

    /// <summary>True when any unconsumed key, modifier, or pending gesture is currently marked held.</summary>
    public bool HasHeldState
    {
        get
        {
            lock (_sync)
            {
                return _heldOrdinaryKeys.Count > 0 ||
                    _typingKey != NoKey ||
                    _modifierSetEngaged ||
                    (_recordGesture?.IsHolding ?? false) ||
                    (_recordGesture?.HasTentativeRecording ?? false);
            }
        }
    }

    /// <summary>
    /// Reconciles unconsumed ordinary keys against <paramref name="isKeyDown"/> so keys whose release
    /// was swallowed (e.g. UAC, UIPI, Win+L) cannot poison future modifier gestures.
    /// </summary>
    /// <remarks>
    /// Consumed keys (ordinary-key PTT, Cancel, QuickAdd, PasteLast/CopyLast, one-shots) are swallowed by
    /// <c>WH_KEYBOARD_LL</c> before Windows updates <c>GetAsyncKeyState</c>, so polling
    /// <c>GetAsyncKeyState</c> for them would always read up and drop active holds at 40 ms.
    /// Consumed holds are cleared via <see cref="ResetHeldKeys"/> on suspend/lock/unlock/capture.
    /// </remarks>
    internal void ReconcileHeldKeys(Func<uint, bool> isKeyDown)
    {
        ArgumentNullException.ThrowIfNull(isKeyDown);

        lock (_sync)
        {
            if (_typingKey != NoKey && _typingKey != MenuKeyMask.VirtualKeyUnassigned && !isKeyDown(_typingKey))
            {
                _typingKey = NoKey;
            }

            if (_heldOrdinaryKeys.Count > 0)
            {
                _heldOrdinaryKeys.RemoveWhere(vk => vk != MenuKeyMask.VirtualKeyUnassigned && !isKeyDown(vk));
            }
        }
    }

    /// <summary>Forgets held key, modifier, and gesture presses in progress.</summary>
    public void ResetHeldKeys(bool preserveActiveRecording = false)
    {
        lock (_sync)
        {
            var keepRecording = preserveActiveRecording &&
                (_recordHeld || _ownsActiveRecording || (_recordGesture?.IsRecordingHandsFree ?? false));
            if (!keepRecording)
            {
                _recordHeld = false;
                _cancelledUntilRecordRelease = false;
                _altMaskSentForPress = false;
                _modifierSetEngaged = false;
                _ownsActiveRecording = false;
                _recordGesture?.Reset();
            }

            _cancelHeld = false;
            _quickAddHeld = false;
            _typingKey = NoKey;
            _oneShotKeyPresses.Clear();
            if (_pasteLast is not null)
            {
                _pasteLast.Held = false;
            }

            if (_copyLast is not null)
            {
                _copyLast.Held = false;
            }

            _heldOrdinaryKeys.Clear();
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
            return new HotkeyEdgeDecision(
                Consume: true,
                signal,
                MaskMenuKey: (_record.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0);
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

            if ((!_recordingActive && !_cancelArmed) || !ModifiersMatch(activeModifiers, _cancel.Modifiers))
            {
                return new HotkeyEdgeDecision(Consume: false);
            }

            _cancelHeld = true;
            _cancelledUntilRecordRelease = _recordHeld;
            _ownsActiveRecording = false;

            // THE GESTURE FORGETS EVERYTHING, INCLUDING A HANDS-FREE RECORDING. Without this,
            // letting go of the record key after a cancel ends a hold that is no longer running and
            // delivers text the person threw away, and a hands-free recording stays believed-in so
            // the next single tap "stops" something that ended a minute ago.
            _recordGesture?.Abandon();
            return new HotkeyEdgeDecision(
                Consume: true,
                PushToTalkSignal.Cancelled,
                MaskMenuKey: ((_cancel.Modifiers | activeModifiers) & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0);
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
            return new HotkeyEdgeDecision(
                Consume: true,
                PushToTalkSignal.QuickAdd,
                MaskMenuKey: (_quickAdd.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0);
        }

        if (!_quickAddHeld)
        {
            return new HotkeyEdgeDecision(Consume: false);
        }

        _quickAddHeld = false;
        return new HotkeyEdgeDecision(Consume: true);
    }

    /// <summary>The last-dictation shortcuts that are bound, in a fixed order.</summary>
    private IEnumerable<OneShotBinding> OneShots()
    {
        if (_pasteLast is not null)
        {
            yield return _pasteLast;
        }

        if (_copyLast is not null)
        {
            yield return _copyLast;
        }
    }

    /// <summary>A last-dictation shortcut went down: one signal per press, never while a take records. Ref: #206.</summary>
    /// <remarks>
    /// SHAPED LIKE QUICK ADD'S: the first press signals and is consumed, its repeats are consumed silently, its release
    /// is consumed. While a recording runs the key passes through untouched - the app would refuse the reuse anyway,
    /// and a key swallowed then is a key the person's application never got. A chord holding Alt or Win asks the
    /// hook to mask it, so the modifier's release does not open a menu, Start, or switch the keyboard layout.
    /// </remarks>
    private HotkeyEdgeDecision ProcessOneShot(OneShotBinding oneShot)
    {
        if (oneShot.Held)
        {
            return new HotkeyEdgeDecision(Consume: true);
        }

        if (_recordingActive)
        {
            return new HotkeyEdgeDecision(Consume: false);
        }

        oneShot.Held = true;
        return new HotkeyEdgeDecision(
            Consume: true,
            oneShot.Signal == PushToTalkSignal.PasteLast ? null : oneShot.Signal,
            MaskMenuKey: (oneShot.Binding.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0,
            ArmPasteLastTarget: oneShot.Signal == PushToTalkSignal.PasteLast);
    }

    private sealed class OneShotBinding(HotkeyBinding binding, PushToTalkSignal signal)
    {
        public HotkeyBinding Binding { get; } = binding;

        public PushToTalkSignal Signal { get; } = signal;

        public bool Held { get; set; }
    }
}
