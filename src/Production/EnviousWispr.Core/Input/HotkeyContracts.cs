using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        // A MODIFIER-ONLY BINDING HAS NO KEY, and appending an empty one produces "Ctrl+Win+" -
        // a string that renders wrong on screen and no longer parses back to itself.
        if (!string.IsNullOrEmpty(Key))
        {
            parts.Add(Key);
        }

        return string.Join('+', parts);
    }
}

public sealed record HotkeyGestureParseResult(
    bool Succeeded,
    HotkeyGesture? Gesture = null,
    AppError? Error = null);

public static class HotkeyGestureParser
{
    /// <summary>An optional shortcut: blank means none and is a success with no gesture. Ref: #206.</summary>
    /// <remarks>
    /// THE LAST-DICTATION SHORTCUTS MAY BE UNSET, the recording, cancel and Add-a-word keys may not. Blank is the
    /// one extra answer; anything else written in the field is held to exactly the rules of <see cref="Parse"/>.
    /// </remarks>
    public static HotkeyGestureParseResult ParseOptional(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HotkeyGestureParseResult(true)
            : Parse(value);

    /// <summary>An optional shortcut that fires once per press: blank, or a gesture with an ordinary key.</summary>
    /// <remarks>
    /// THE ONE RULE FOR THE LAST-DICTATION SHORTCUTS, read by the validator, the settings presenter and the hook alike,
    /// so no two of them can accept different sets (validation-discipline: compare the values the UI can produce
    /// against what storage accepts and runtime listens for). A modifier-only chord has no key-down of its own and a
    /// lone sided modifier is the recording key's shape, so both are refused here. Ref: #206.
    /// </remarks>
    public static HotkeyGestureParseResult ParseOneShot(string? value)
    {
        var parsed = ParseOptional(value);
        return parsed.Gesture is { } gesture && !IsOrdinaryKey(gesture.Key)
            ? Failure()
            : parsed;
    }

    /// <summary>A required shortcut that needs a key of its own: the cancel and Add-a-word keys. Ref: #66.</summary>
    /// <remarks>
    /// ONLY THE RECORDING KEY MAY BE A MODIFIER SET. The hook maps the cancel and Add-a-word keys to a virtual key and
    /// refuses the whole hook - recording included - when either has none, so a Ctrl+Shift saved there would leave
    /// dictation dead at the next launch. Held to exactly the rules of <see cref="Parse"/> otherwise.
    /// </remarks>
    public static HotkeyGestureParseResult ParseKeyed(string? value)
    {
        var parsed = Parse(value);
        return parsed.Gesture is { } gesture && string.IsNullOrEmpty(gesture.Key)
            ? Failure()
            : parsed;
    }

    /// <summary>Whether a recording key learns the four gestures: a modifier set, or one sided modifier on its own.</summary>
    /// <remarks>
    /// THE SAME SPLIT <see cref="ParseOneShot"/> MAKES, read from the same key list, so the Ready screen's
    /// "double-press to go hands-free" and the hook's gesture route cannot disagree about which keys have it.
    /// </remarks>
    public static bool UnlocksGestures(HotkeyGesture gesture) =>
        string.IsNullOrEmpty(gesture.Key) ||
        (gesture.Modifiers == HotkeyModifiers.None && !IsOrdinaryKey(gesture.Key));

    private static bool IsOrdinaryKey(string key) =>
        !string.IsNullOrEmpty(key) &&
        key is not ("RightCtrl" or "LeftCtrl" or "RightShift" or "LeftShift" or "RightWin" or "LeftWin");

    public static HotkeyGestureParseResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return Failure();
        }

        var tokens = value.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
        {
            return Failure();
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var token in tokens)
        {
            if (TryParseModifier(token, out var modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    return Failure();
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null || !TryNormalizeKey(token, out key))
            {
                return Failure();
            }
        }

        if (key is not null)
        {
            return new HotkeyGestureParseResult(true, new HotkeyGesture(modifiers, key));
        }

        // MODIFIERS ALONE ARE A BINDING NOW, AND TWO IS THE FLOOR. Ctrl+Win is the default because
        // holding two modifiers together is not how any common shortcut begins, while holding ONE -
        // Ctrl on its own - is how most of them begin. The hold threshold makes even that safe, but
        // a single unsided modifier still cannot name a physical key, so the sided names (RCtrl,
        // LShift) remain the way to bind one of those.
        //
        // Alt is excluded from a pair as well as alone: a lone Alt tap opens a window's menu bar,
        // and Alt+Shift cycles the keyboard layout. Both are shell gestures this app would lose.
        var modifierCount = System.Numerics.BitOperations.PopCount((uint)modifiers);
        return modifierCount >= 2 && !modifiers.HasFlag(HotkeyModifiers.Alt)
            ? new HotkeyGestureParseResult(true, new HotkeyGesture(modifiers, string.Empty))
            : Failure();
    }

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        modifier = token.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => HotkeyModifiers.Control,
            "ALT" => HotkeyModifiers.Alt,
            "SHIFT" => HotkeyModifiers.Shift,
            "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
            _ => HotkeyModifiers.None,
        };
        return modifier != HotkeyModifiers.None;
    }

    private static bool TryNormalizeKey(string token, out string key)
    {
        var candidate = token.ToUpperInvariant();
        if (candidate.Length == 1 &&
            (candidate[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            key = candidate;
            return true;
        }

        if (candidate.Length is 2 or 3 &&
            candidate[0] == 'F' &&
            int.TryParse(candidate.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = $"F{functionKey}";
            return true;
        }

        key = candidate switch
        {
            "SPACE" => "Space",
            "INSERT" => "Insert",
            "DELETE" => "Delete",
            "HOME" => "Home",
            "END" => "End",
            "PAGEUP" => "PageUp",
            "PAGEDOWN" => "PageDown",
            "PAUSE" => "Pause",
            "SCROLLLOCK" => "ScrollLock",
            "ESC" or "ESCAPE" => "Escape",

            // SIDED MODIFIERS ARE KEYS HERE, NOT MODIFIERS, AND THE DISTINCTION IS THE WHOLE POINT.
            // "Ctrl" is a modifier: it qualifies another key and cannot stand alone. "RCtrl" names
            // ONE PHYSICAL KEY, which can. Without this the parser refuses every gesture with no
            // ordinary key in it, so a modifier binding could not be expressed at all - the engine
            // would accept one and nothing could ever produce it.
            //
            // SIDED ON PURPOSE, not "Ctrl on its own". A binding has to name one physical key, and
            // it also lets a user keep the left Control they use for shortcuts while giving up the
            // right one they never press.
            //
            // ALT IS ABSENT, matching the engine: a lone Alt tap already opens a window's menu bar,
            // so binding to it would put this app in a fight with the shell over one gesture.
            "RCTRL" or "RIGHTCTRL" => "RightCtrl",
            "LCTRL" or "LEFTCTRL" => "LeftCtrl",
            "RSHIFT" or "RIGHTSHIFT" => "RightShift",
            "LSHIFT" or "LEFTSHIFT" => "LeftShift",
            "RWIN" or "RIGHTWIN" => "RightWin",
            "LWIN" or "LEFTWIN" => "LeftWin",
            _ => string.Empty,
        };
        return key.Length > 0;
    }

    private static HotkeyGestureParseResult Failure() => new(
        Succeeded: false,
        Error: new AppError(
            AppErrorCode.HotkeyInvalid,
            AppErrorStage.HotkeyConfiguration,
            CanRetry: false));
}

public readonly record struct TargetWindowId(
    nint Value,
    uint ProcessId = 0,
    string? FocusedElementId = null)
{
    public bool IsValid => Value != 0;
}

public interface IForegroundTargetProvider
{
    TargetWindowId? CaptureForegroundTarget();
}

public enum PushToTalkSignal
{
    Pressed,
    Released,
    Cancelled,
    QuickAdd,

    /// <summary>The Paste Last Dictation shortcut went down. Not a session command. Ref: #206.</summary>
    PasteLast,

    /// <summary>The Copy Last Dictation shortcut went down. Not a session command.</summary>
    CopyLast,
}

/// <summary>What became of an optional last-dictation shortcut when the hook was built. Ref: #206.</summary>
public enum LastDictationShortcutState
{
    /// <summary>No shortcut is set.</summary>
    Unset,

    /// <summary>Listening.</summary>
    Bound,

    /// <summary>Not a gesture this shortcut can use: it does not parse, names no key, or names a lone modifier.</summary>
    Invalid,

    /// <summary>Another of the app's own shortcuts already uses it; that one keeps it.</summary>
    Clashes,

    /// <summary>Windows would not let it be registered - another application holds it.</summary>
    Unavailable,
}

public sealed record PushToTalkSignalEvent(PushToTalkSignal Signal);

public interface IGlobalPushToTalk : IAsyncDisposable
{
    event EventHandler<PushToTalkSignalEvent>? Signalled;

    HotkeyGesture Gesture { get; }

    bool IsInstalled { get; }
}
