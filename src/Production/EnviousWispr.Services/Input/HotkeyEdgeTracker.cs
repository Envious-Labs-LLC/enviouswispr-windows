using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Input;

internal readonly record struct HotkeyEdgeDecision(bool Consume, PushToTalkSignal? Signal = null);

internal readonly record struct HotkeyBinding(uint VirtualKey, HotkeyModifiers Modifiers);

internal sealed class HotkeyEdgeTracker
{
    internal const uint EscapeVirtualKey = 0x1B;

    private readonly object _sync = new();
    private readonly HandsFreeLockPolicy _handsFree = new();
    private readonly Func<DateTimeOffset> _now;
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

    public HotkeyEdgeTracker(uint triggerVirtualKey, HotkeyModifiers requiredModifiers)
        : this(
            new HotkeyBinding(triggerVirtualKey, requiredModifiers),
            new HotkeyBinding(EscapeVirtualKey, HotkeyModifiers.None),
            new HotkeyBinding('W', HotkeyModifiers.Control | HotkeyModifiers.Alt),
            DictationRecordingMode.PushToTalk)
    {
    }

    /// <param name="now">
    /// Injected rather than read from the system, because the hands-free gesture is a question
    /// about elapsed time and a gesture with a real clock inside it can only be tested by sleeping.
    /// </param>
    public HotkeyEdgeTracker(
        HotkeyBinding record,
        HotkeyBinding cancel,
        HotkeyBinding quickAdd,
        DictationRecordingMode recordingMode,
        Func<DateTimeOffset>? now = null)
    {
        _record = record;
        _cancel = cancel;
        _quickAdd = quickAdd;
        _recordingMode = recordingMode;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether the recording is running with the key released.</summary>
    public bool IsHandsFreeLocked
    {
        get
        {
            lock (_sync)
            {
                return _handsFree.IsLocked;
            }
        }
    }

    public void SetRecordingActive(bool active)
    {
        lock (_sync)
        {
            _recordingActive = active;
            if (!active)
            {
                // Whatever ended the recording - release, cancel, auto-stop, a crash recovery -
                // ends the gesture with it. A lock surviving into the next recording would make
                // its second press CANCEL instead of lock, and nothing on screen would connect
                // that to what the user did a minute earlier.
                _handsFree.RecordingEnded();
            }
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

            // The hands-free gesture reads the SAME press edge the recording does, so a second
            // quick press is a lock rather than the start of a new dictation. Asked before the
            // ordinary decision below, because that decision would otherwise consume the press.
            var gesture = _handsFree.Press(
                _now(),
                _recordingMode == DictationRecordingMode.Toggle);
            if (gesture == HandsFreePressOutcome.Lock)
            {
                return new HotkeyEdgeDecision(Consume: true);
            }

            if (gesture == HandsFreePressOutcome.Cancel)
            {
                _cancelledUntilRecordRelease = true;
                return new HotkeyEdgeDecision(Consume: true, PushToTalkSignal.Cancelled);
            }

            var signal = _recordingMode == DictationRecordingMode.Toggle && _recordingActive
                ? PushToTalkSignal.Released
                : PushToTalkSignal.Pressed;
            if (signal == PushToTalkSignal.Pressed)
            {
                _handsFree.RecordingStarted(_now());
            }

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

        // A locked recording keeps running with the key released. Asked of the policy rather than
        // read off its flag here, so there is ONE answer to "does this release stop the recording"
        // rather than two places deciding it.
        if (!_handsFree.ReleaseEndsRecording())
        {
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
