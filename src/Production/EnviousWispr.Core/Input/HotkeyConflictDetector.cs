namespace EnviousWispr.Core.Input;

/// <summary>How severely a configured binding collides with a Windows reserved system shortcut.</summary>
public enum SystemShortcutConflictSeverity
{
    /// <summary>Intercepted or used by a common Windows shell feature; warn the user.</summary>
    Warning,

    /// <summary>Handled unconditionally by Windows or closes/locks the active session; block binding.</summary>
    Blocked,
}

/// <summary>A configured role whose shortcut collides with a reserved Windows system shortcut.</summary>
public readonly record struct SystemConflict(
    string Role,
    string Gesture,
    SystemShortcutConflictSeverity Severity,
    string Description);

/// <summary>
/// Which two keybind fields are asking for the same key, and which shortcuts collide with Windows system chords.
/// </summary>
/// <remarks>
/// ONE DEFINITION OF "THESE CLASH", USED BY BOTH THE FIELD AND THE SAVE BUTTON. Saving already
/// refused three matching shortcuts, so nothing broken could ever be written. What was missing was
/// the part a person experiences: two fields reading the same thing, looking settled, with the
/// objection arriving only after they press Save. Both now ask this.
/// </remarks>
public static class HotkeyConflictDetector
{
    private readonly record struct ReservedSystemChord(
        HotkeyModifiers Modifiers,
        string Key,
        string CanonicalLabel,
        SystemShortcutConflictSeverity Severity,
        string Description);

    private static readonly ReservedSystemChord[] ReservedSystemChords =
    [
        new(HotkeyModifiers.Windows, "L", "Win+L", SystemShortcutConflictSeverity.Blocked, "locks Windows"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Delete", "Ctrl+Alt+Del", SystemShortcutConflictSeverity.Blocked, "opens Windows Security"),
        new(HotkeyModifiers.Alt, "Tab", "Alt+Tab", SystemShortcutConflictSeverity.Blocked, "switches apps in Windows"),
        new(HotkeyModifiers.Alt, "F4", "Alt+F4", SystemShortcutConflictSeverity.Blocked, "closes the active window"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Escape", "Ctrl+Shift+Esc", SystemShortcutConflictSeverity.Warning, "opens Task Manager"),
        new(HotkeyModifiers.Windows | HotkeyModifiers.Shift, "S", "Win+Shift+S", SystemShortcutConflictSeverity.Warning, "opens Snipping Tool"),
        new(HotkeyModifiers.Windows, "G", "Win+G", SystemShortcutConflictSeverity.Warning, "opens Xbox Game Bar"),
        new(HotkeyModifiers.Windows, "Space", "Win+Space", SystemShortcutConflictSeverity.Warning, "switches Windows input language"),
        new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, string.Empty, "Alt+Shift", SystemShortcutConflictSeverity.Warning, "switches Windows keyboard layout"),
    ];

    /// <summary>Two roles that resolved to the same key.</summary>
    public readonly record struct Clash(string FirstRole, string SecondRole, string Gesture);

    /// <summary>Every pair of roles that resolved to the same key.</summary>
    /// <remarks>
    /// TEXT THAT DOES NOT PARSE IS NOT A CLASH, and two fields of unparseable text are not a clash
    /// with each other. Comparing the parse results directly would make two empty fields collide,
    /// which is the state every fresh field starts in.
    /// </remarks>
    public static IReadOnlyList<Clash> Find(IReadOnlyList<(string Role, string Text)> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var parsed = bindings
            .Select(binding => (binding.Role, HotkeyGestureParser.Parse(binding.Text).Gesture))
            .ToArray();

        var clashes = new List<Clash>();
        for (var first = 0; first < parsed.Length; first++)
        {
            for (var second = first + 1; second < parsed.Length; second++)
            {
                if (parsed[first].Gesture is { } gesture && gesture == parsed[second].Gesture)
                {
                    clashes.Add(new Clash(parsed[first].Role, parsed[second].Role, gesture.ToString()));
                }
            }
        }

        return clashes;
    }

    /// <summary>Checks each configured role against Windows reserved system shortcuts.</summary>
    public static IReadOnlyList<SystemConflict> FindSystemConflicts(IReadOnlyList<(string Role, string Text)> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var conflicts = new List<SystemConflict>();
        foreach (var (role, text) in bindings)
        {
            if (FindSystemConflict(role, text) is { } conflict)
            {
                conflicts.Add(conflict);
            }
        }

        return conflicts;
    }

    /// <summary>Checks one configured role against Windows reserved system shortcuts.</summary>
    public static SystemConflict? FindSystemConflict(string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryNormalizeForSystemMatch(text, out var modifiers, out var key))
        {
            return null;
        }

        foreach (var reserved in ReservedSystemChords)
        {
            if (reserved.Modifiers == modifiers &&
                string.Equals(reserved.Key, key, StringComparison.Ordinal))
            {
                return new SystemConflict(role, reserved.CanonicalLabel, reserved.Severity, reserved.Description);
            }
        }

        return null;
    }

    /// <summary>The sentence shown under the fields, naming what collided with what.</summary>
    public static string Describe(IReadOnlyList<Clash> clashes)
    {
        ArgumentNullException.ThrowIfNull(clashes);

        if (clashes.Count == 0)
        {
            return string.Empty;
        }

        var sentences = clashes
            .Select(clash => $"{clash.FirstRole} and {clash.SecondRole} are both set to {clash.Gesture}.");

        return string.Join(" ", sentences) + " Give each one its own shortcut.";
    }

    /// <summary>Describes Windows reserved system shortcut conflicts across configured fields.</summary>
    public static string DescribeSystemConflicts(IReadOnlyList<SystemConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);

        if (conflicts.Count == 0)
        {
            return string.Empty;
        }

        var sentences = conflicts.Select(conflict =>
            conflict.Severity == SystemShortcutConflictSeverity.Blocked
                ? $"{conflict.Role} cannot use {conflict.Gesture} because it {conflict.Description} in Windows."
                : $"{conflict.Role} uses {conflict.Gesture}, which {conflict.Description} and may be intercepted by Windows.");

        return string.Join(" ", sentences);
    }

    private static bool TryNormalizeForSystemMatch(
        string text,
        out HotkeyModifiers modifiers,
        out string key)
    {
        var parsed = HotkeyGestureParser.Parse(text);
        if (parsed.Succeeded && parsed.Gesture is { } gesture)
        {
            modifiers = gesture.Modifiers;
            key = gesture.Key;
            return true;
        }

        modifiers = HotkeyModifiers.None;
        key = string.Empty;

        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        string? matchedKey = null;
        foreach (var rawToken in tokens)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return false;
            }

            var upper = rawToken.ToUpperInvariant();
            switch (upper)
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (matchedKey is not null || !TryNormalizeReservedKey(upper, out matchedKey))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (modifiers == HotkeyModifiers.None)
        {
            return false;
        }

        key = matchedKey ?? string.Empty;
        return true;
    }

    private static bool TryNormalizeReservedKey(string upper, out string key)
    {
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            key = upper;
            return true;
        }

        if (upper.Length is 2 or 3 &&
            upper[0] == 'F' &&
            int.TryParse(upper.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = $"F{functionKey}";
            return true;
        }

        key = upper switch
        {
            "DEL" or "DELETE" => "Delete",
            "ESC" or "ESCAPE" => "Escape",
            "SPACE" => "Space",
            "TAB" => "Tab",
            _ => string.Empty,
        };
        return key.Length > 0;
    }
}
