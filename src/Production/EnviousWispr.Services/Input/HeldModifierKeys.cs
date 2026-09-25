using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

/// <summary>Which modifiers a person is holding, as the hook's own event stream says. Ref: #66, #207.</summary>
/// <remarks>
/// THE KEYBOARD STATE LAGS THE HOOK, IN BOTH DIRECTIONS. A low-level hook runs before Windows records the key that
/// just moved, so the Win key going down reads as not held and going up reads as still held. Worse, releasing Win
/// makes the shell inject a Control press and a second Win release of its own, synchronously, while the real
/// release has still not reached the state - so that injected Control read as "Ctrl+Win complete" a second time.
/// A Ctrl+Win binding saw nothing at all on a hold, and one tap counted as two, which is a double tap, which starts
/// a hands-free recording. #207 was the first half of the same fact, met by a single modifier.
///
/// SO THE EVENT STREAM DECIDES, AND THE STATE MAY ONLY SAY UP. A key is held when this stream saw it go down and not
/// up, and the keyboard state does not say it is up. The stream alone would keep a key held forever after a release
/// it never saw - a lock or a secure desktop takes the key-up - and a stuck Control would turn every later Ctrl tap
/// into half a recording gesture. The state alone is the lag above. Together: the key moving now is its own edge,
/// and every other key needs both. A key already down when the hook was installed reads as not held until this
/// stream observes another down event for it. Use this reading for modifier-set gesture completion only;
/// keyed shortcuts need the keyboard-state reading so pre-install modifiers still qualify their bindings.
///
/// Owned by the hook, called only on the hook's own thread, so it takes no lock.
/// </remarks>
public sealed class HeldModifierKeys
{
    private static readonly (uint Key, HotkeyModifiers Flag)[] Sided =
    [
        (0xA0, HotkeyModifiers.Shift),
        (0xA1, HotkeyModifiers.Shift),
        (0xA2, HotkeyModifiers.Control),
        (0xA3, HotkeyModifiers.Control),
        (0xA4, HotkeyModifiers.Alt),
        (0xA5, HotkeyModifiers.Alt),
        (0x5B, HotkeyModifiers.Windows),
        (0x5C, HotkeyModifiers.Windows),
    ];

    private readonly HashSet<uint> _down = [];

    /// <summary>Takes one key event in and answers which modifiers are held once it has happened.</summary>
    /// <param name="virtualKey">The key this event is about.</param>
    /// <param name="isKeyDown">Whether it went down.</param>
    /// <param name="stateSaysDown">The keyboard state for a key; trusted only when it says up.</param>
    public HotkeyModifiers Observe(uint virtualKey, bool isKeyDown, Func<uint, bool> stateSaysDown)
    {
        ArgumentNullException.ThrowIfNull(stateSaysDown);
        var key = Canonical(virtualKey);
        if (IsModifier(key))
        {
            if (isKeyDown)
            {
                _down.Add(key);
            }
            else
            {
                _down.Remove(key);
            }
        }

        var held = HotkeyModifiers.None;
        foreach (var (sided, flag) in Sided)
        {
            var down = sided == key ? isKeyDown : _down.Contains(sided) && stateSaysDown(sided);
            if (down)
            {
                held |= flag;
            }
        }

        return held;
    }

    private static bool IsModifier(uint key) => Array.Exists(Sided, entry => entry.Key == key);

    /// <summary>A generic modifier code named as its left-hand key; the hook normally reports the sided one.</summary>
    private static uint Canonical(uint virtualKey) => virtualKey switch
    {
        0x10 => 0xA0,
        0x11 => 0xA2,
        0x12 => 0xA4,
        _ => virtualKey,
    };
}
