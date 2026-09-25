using System.Runtime.InteropServices;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

/// <summary>Whether a key, with the modifiers held, TYPES something in the layout the person is typing in.</summary>
/// <remarks>
/// CTRL+ALT IS ALTGR ON HALF THE WORLD'S KEYBOARDS. Windows reports Right Alt as Left Control plus Right Alt,
/// and the hook reads modifiers without sides, so a binding of Ctrl+Alt+W also answers AltGr+W - which is
/// how Czech and Hungarian type "|". The hook consumes the key it matches, so the character never arrived.
/// Asking the layout is the rule because it is the only one that knows: physical Ctrl+Left Alt types the
/// same characters as AltGr on those layouts, so telling the two Alt keys apart would not be enough.
///
/// THE FOREGROUND WINDOW'S LAYOUT, NOT OURS. The hook runs on the app's own thread, whose layout is not
/// the one the person is typing into; `GetKeyboardLayout(0)` would answer about the wrong keyboard.
///
/// A DEAD KEY COUNTS AS TYPING, and a layout that cannot be read counts as typing too: when in doubt the
/// key goes to the application, because a shortcut that does not fire is visible and retryable, and a
/// character that silently never arrives is neither.
/// </remarks>
internal static class KeyboardLayoutTyping
{
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyCapital = 0x14;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightAlt = 0xA5;
    private const uint MapVirtualKeyToScanCode = 0;

    /// <summary>Do not change the keyboard state - a dead key pending in the target stays pending. Windows 10 1607+.</summary>
    private const uint ToUnicodeNoStateChange = 0x4;

    /// <summary>The question the edge tracker asks, answered for the foreground window's layout.</summary>
    public static bool TypesInForegroundLayout(uint virtualKey, HotkeyModifiers modifiers)
    {
        var layout = ForegroundLayout();
        // EITHER CAPS LOCK STATE. The layout is the foreground thread's but GetKeyState answers for OURS,
        // so Caps Lock cannot be read for the application reliably; a press that types in either state
        // is typing.
        return layout == 0 ||
            Types(virtualKey, modifiers, layout, capsLock: false) ||
            Types(virtualKey, modifiers, layout, capsLock: true);
    }

    /// <summary>Whether <paramref name="virtualKey"/> with <paramref name="modifiers"/> produces a character in <paramref name="layout"/>.</summary>
    internal static bool Types(uint virtualKey, HotkeyModifiers modifiers, nint layout, bool capsLock)
    {
        var state = new byte[256];
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            state[VirtualKeyControl] = 0x80;
            state[VirtualKeyLeftControl] = 0x80;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            // AltGr is what a layout keys its third level on; set the right-hand Alt as well as the generic one.
            state[VirtualKeyAlt] = 0x80;
            state[VirtualKeyRightAlt] = 0x80;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            state[VirtualKeyShift] = 0x80;
        }

        if (capsLock)
        {
            state[VirtualKeyCapital] = 0x01;
        }

        var scanCode = MapVirtualKeyEx(virtualKey, MapVirtualKeyToScanCode, layout);
        var buffer = new char[8];
        var count = ToUnicodeEx(virtualKey, scanCode, state, buffer, buffer.Length, ToUnicodeNoStateChange, layout);
        if (count < 0)
        {
            return true;
        }

        for (var index = 0; index < count && index < buffer.Length; index++)
        {
            // Control characters are what Ctrl turns a letter into on a layout with nothing on that level.
            if (!char.IsControl(buffer[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static nint ForegroundLayout()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return 0;
        }

        var thread = GetWindowThreadProcessId(window, out _);
        return thread == 0 ? 0 : GetKeyboardLayout(thread);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    // UNICODE, OR IT OVERRUNS: the default ANSI marshalling hands Windows a buffer half the size it writes
    // UTF-16 into, and the process dies - measured, the test host crashed on the first call.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint virtualKey,
        uint scanCode,
        byte[] keyState,
        [Out] char[] buffer,
        int bufferLength,
        uint flags,
        nint layout);
}
