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

        parts.Add(Key);
        return string.Join('+', parts);
    }
}

public sealed record HotkeyGestureParseResult(
    bool Succeeded,
    HotkeyGesture? Gesture = null,
    AppError? Error = null);

public static class HotkeyGestureParser
{
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

        return key is null
            ? Failure()
            : new HotkeyGestureParseResult(true, new HotkeyGesture(modifiers, key));
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
}

public sealed record PushToTalkSignalEvent(PushToTalkSignal Signal);

public interface IGlobalPushToTalk : IAsyncDisposable
{
    event EventHandler<PushToTalkSignalEvent>? Signalled;

    HotkeyGesture Gesture { get; }

    bool IsInstalled { get; }
}
