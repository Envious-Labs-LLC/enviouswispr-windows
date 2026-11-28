using System.Numerics;

namespace EnviousWispr.Core.Input;

public enum KeybindCaptureStatus
{
    Listening,
    Committed,
    Cancelled,
    InvalidAttempt,
}

public readonly record struct KeybindCaptureStepResult(
    KeybindCaptureStatus Status,
    string? GestureText = null,
    string? HintMessage = null);

/// <summary>
/// Pure, UI-free state machine that turns low-level key down/up events into a validated keybind gesture.
/// </summary>
public sealed class KeybindCaptureStateMachine
{
    private const uint VirtualKeyBackspace = 0x08;
    private const uint VirtualKeyShift = 0x10;
    private const uint VirtualKeyControl = 0x11;
    private const uint VirtualKeyAlt = 0x12;
    private const uint VirtualKeyEscape = 0x1B;
    private const uint VirtualKeyLeftShift = 0xA0;
    private const uint VirtualKeyRightShift = 0xA1;
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyRightControl = 0xA3;
    private const uint VirtualKeyLeftAlt = 0xA4;
    private const uint VirtualKeyRightAlt = 0xA5;
    private const uint VirtualKeyLeftWindows = 0x5B;
    private const uint VirtualKeyRightWindows = 0x5C;

    private readonly bool _allowModifierOnly;
    private readonly bool _allowEscapeAsBinding;
    private readonly bool _allowClear;
    private readonly Func<string, HotkeyGestureParseResult> _validator;

    private readonly HashSet<uint> _heldModifiers = [];
    private readonly HashSet<uint> _chordModifiers = [];
    private bool _ordinaryKeyPressedInChord;

    public KeybindCaptureStateMachine(
        bool allowModifierOnly,
        bool allowEscapeAsBinding,
        bool allowClear = false,
        Func<string, HotkeyGestureParseResult>? validator = null)
    {
        _allowModifierOnly = allowModifierOnly;
        _allowEscapeAsBinding = allowEscapeAsBinding;
        _allowClear = allowClear;
        _validator = validator ?? (allowModifierOnly
            ? HotkeyGestureParser.Parse
            : allowClear
                ? HotkeyGestureParser.ParseOneShot
                : HotkeyGestureParser.ParseKeyed);
    }

    public void Reset()
    {
        _heldModifiers.Clear();
        _chordModifiers.Clear();
        _ordinaryKeyPressedInChord = false;
    }

    public KeybindCaptureStepResult Process(uint virtualKey, bool isKeyDown)
    {
        var canonical = CanonicalModifierKey(virtualKey);
        if (IsModifierKey(canonical))
        {
            return ProcessModifier(canonical, isKeyDown);
        }

        if (!isKeyDown)
        {
            return new KeybindCaptureStepResult(KeybindCaptureStatus.Listening);
        }

        if (virtualKey == VirtualKeyEscape && !_allowEscapeAsBinding)
        {
            Reset();
            return new KeybindCaptureStepResult(KeybindCaptureStatus.Cancelled);
        }

        if (virtualKey == VirtualKeyBackspace && _allowClear && _heldModifiers.Count == 0)
        {
            Reset();
            return new KeybindCaptureStepResult(KeybindCaptureStatus.Committed, string.Empty);
        }

        if (_heldModifiers.Count > 0)
        {
            _ordinaryKeyPressedInChord = true;
        }

        if (!TryDescribeOrdinaryKey(virtualKey, out var keyName))
        {
            return new KeybindCaptureStepResult(
                KeybindCaptureStatus.InvalidAttempt,
                HintMessage: "That key is not supported as a shortcut. Press a letter, number, function key, or navigation key.");
        }

        var candidate = new HotkeyGesture(CurrentModifiers(), keyName).ToString();
        var parsed = _validator(candidate);
        if (parsed.Succeeded && parsed.Gesture is { } gesture)
        {
            Reset();
            return new KeybindCaptureStepResult(KeybindCaptureStatus.Committed, gesture.ToString());
        }

        return new KeybindCaptureStepResult(
            KeybindCaptureStatus.InvalidAttempt,
            HintMessage: "Use supported keys such as F8, Escape, or Ctrl+Alt+W.");
    }

    private KeybindCaptureStepResult ProcessModifier(uint virtualKey, bool isKeyDown)
    {
        if (isKeyDown)
        {
            if (_heldModifiers.Add(virtualKey))
            {
                _chordModifiers.Add(virtualKey);
            }

            return new KeybindCaptureStepResult(KeybindCaptureStatus.Listening);
        }

        _heldModifiers.Remove(virtualKey);
        if (_heldModifiers.Count > 0 || _chordModifiers.Count == 0)
        {
            return new KeybindCaptureStepResult(KeybindCaptureStatus.Listening);
        }

        var chord = _chordModifiers.ToArray();
        var hadOrdinaryKey = _ordinaryKeyPressedInChord;
        _chordModifiers.Clear();
        _ordinaryKeyPressedInChord = false;

        if (hadOrdinaryKey)
        {
            return new KeybindCaptureStepResult(KeybindCaptureStatus.Listening);
        }

        if (!_allowModifierOnly)
        {
            return new KeybindCaptureStepResult(
                KeybindCaptureStatus.InvalidAttempt,
                HintMessage: "Modifier-only shortcuts can only be used for Start / stop recording. Press a key or combination such as F8 or Ctrl+Alt+W.");
        }

        if (chord.Length == 1 && TryDescribeSidedModifier(chord[0], out var singleModifierName))
        {
            var singleParsed = _validator(singleModifierName);
            if (singleParsed.Succeeded && singleParsed.Gesture is { } singleGesture)
            {
                return new KeybindCaptureStepResult(KeybindCaptureStatus.Committed, singleGesture.ToString());
            }

            return new KeybindCaptureStepResult(
                KeybindCaptureStatus.InvalidAttempt,
                HintMessage: "That modifier cannot be used as a shortcut.");
        }

        var chordModifiers = CombineModifiers(chord);
        if (BitOperations.PopCount((uint)chordModifiers) < 2)
        {
            return new KeybindCaptureStepResult(
                KeybindCaptureStatus.InvalidAttempt,
                HintMessage: "Press two different modifiers such as Ctrl+Win, or a single modifier key.");
        }

        var chordText = new HotkeyGesture(chordModifiers, string.Empty).ToString();
        var chordParsed = _validator(chordText);
        if (chordParsed.Succeeded && chordParsed.Gesture is { } chordGesture)
        {
            return new KeybindCaptureStepResult(KeybindCaptureStatus.Committed, chordGesture.ToString());
        }

        return new KeybindCaptureStepResult(
            KeybindCaptureStatus.InvalidAttempt,
            HintMessage: "Ctrl+Alt and Alt+Shift conflict with Windows typing or layout shortcuts. Try Alt+Win, Ctrl+Win, LeftAlt or RightAlt alone, or add a key such as Ctrl+Alt+W.");
    }

    private HotkeyModifiers CurrentModifiers() => CombineModifiers(_heldModifiers);

    private static HotkeyModifiers CombineModifiers(IEnumerable<uint> keys)
    {
        var modifiers = HotkeyModifiers.None;
        foreach (var key in keys)
        {
            modifiers |= ModifierFlagOf(key);
        }

        return modifiers;
    }

    private static uint CanonicalModifierKey(uint virtualKey) => virtualKey switch
    {
        VirtualKeyShift => VirtualKeyLeftShift,
        VirtualKeyControl => VirtualKeyLeftControl,
        VirtualKeyAlt => VirtualKeyLeftAlt,
        _ => virtualKey,
    };

    private static bool IsModifierKey(uint virtualKey) => virtualKey is
        VirtualKeyLeftShift or VirtualKeyRightShift or
        VirtualKeyLeftControl or VirtualKeyRightControl or
        VirtualKeyLeftAlt or VirtualKeyRightAlt or
        VirtualKeyLeftWindows or VirtualKeyRightWindows;

    private static HotkeyModifiers ModifierFlagOf(uint virtualKey) => virtualKey switch
    {
        VirtualKeyLeftShift or VirtualKeyRightShift => HotkeyModifiers.Shift,
        VirtualKeyLeftControl or VirtualKeyRightControl => HotkeyModifiers.Control,
        VirtualKeyLeftAlt or VirtualKeyRightAlt => HotkeyModifiers.Alt,
        VirtualKeyLeftWindows or VirtualKeyRightWindows => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };

    private static bool TryDescribeSidedModifier(uint virtualKey, out string name)
    {
        name = virtualKey switch
        {
            VirtualKeyLeftControl => "LeftCtrl",
            VirtualKeyRightControl => "RightCtrl",
            VirtualKeyLeftAlt => "LeftAlt",
            VirtualKeyRightAlt => "RightAlt",
            VirtualKeyLeftShift => "LeftShift",
            VirtualKeyRightShift => "RightShift",
            VirtualKeyLeftWindows => "LeftWin",
            VirtualKeyRightWindows => "RightWin",
            _ => string.Empty,
        };
        return name.Length > 0;
    }

    private static bool TryDescribeOrdinaryKey(uint virtualKey, out string key)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            key = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            key = $"F{virtualKey - 0x70 + 1}";
            return true;
        }

        key = virtualKey switch
        {
            0x20 => "Space",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x13 => "Pause",
            0x91 => "ScrollLock",
            0x1B => "Escape",
            _ => string.Empty,
        };
        return key.Length > 0;
    }
}
