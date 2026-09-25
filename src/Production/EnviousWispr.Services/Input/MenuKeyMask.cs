using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Input;

/// <summary>Tells Windows a key happened while Alt or Win was held, when the hook swallowed the one that did.</summary>
/// <remarks>
/// A CONSUMED KEY LEAVES THE MODIFIER LOOKING ALONE. Windows opens a window's menu when Alt is released with nothing
/// pressed in between, opens Start for Win, and switches the keyboard layout for Alt+Shift. When the hook swallows
/// the Z of Alt+Shift+Z, Windows saw Alt and Shift go down and up with nothing between - measured on this project
/// (#206): the layout switched in one run of eight without this, in none of eight with it.
///
/// THE ANSWER IS ONE UNASSIGNED KEY, VK 0xE8, down and up, the standard mask. It is tagged, so the hook that sent it
/// recognises it coming back through and hands it straight on without offering it to any binding - a modifier
/// gesture in progress must not read it as a shortcut being pressed.
///
/// Sent off the hook's thread: a low-level hook that injects input from inside its own callback delays every key on
/// the machine behind it.
/// </remarks>
internal static class MenuKeyMask
{
    /// <summary>
    /// Carried in the extra info of every keystroke the app itself sends - this mask, and the clipboard route's Ctrl+V
    /// and Ctrl+C - so the app's own hook hands them straight on instead of offering them to a binding.
    /// </summary>
    public static readonly nint Tag = 0x45574D4B;

    private const ushort VirtualKeyUnassigned = 0xE8;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    public static void Send() =>
        ThreadPool.UnsafeQueueUserWorkItem(static _ => SendNow(), state: null);

    internal static uint SendNow()
    {
        var inputs = new[] { Key(keyUp: false), Key(keyUp: true) };

        // ASSERTED BEFORE ANYTHING LEAVES THE PROCESS (uat-testing.md RULE: assert-the-payload-before-the-syscall):
        // Windows rejects a wrongly sized INPUT outright, and a zero key is a well-formed request for nothing.
        if (Marshal.SizeOf<NativeInput>() != 40 || inputs.Any(input => input.Keyboard.VirtualKey == 0))
        {
            return 0;
        }

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private static NativeInput Key(bool keyUp) => new()
    {
        Type = InputKeyboard,
        Keyboard = new NativeKeyboardInput
        {
            VirtualKey = VirtualKeyUnassigned,
            Flags = keyUp ? KeyEventKeyUp : 0,
            ExtraInfo = Tag,
        },
    };

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
}
