using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EnviousWispr.Services.Input;

internal static partial class WindowsClipboardPaste
{
    internal static int NativeInputSize => Marshal.SizeOf<NativeInput>();

    internal static int NativeKeyboardInputSize => Marshal.SizeOf<NativeKeyboardInput>();

    internal static int NativeKeyboardFlagsOffset =>
        Marshal.OffsetOf<NativeKeyboardInput>(nameof(NativeKeyboardInput.Flags)).ToInt32();

    internal static readonly TimeSpan SyntheticPasteKeyHoldDuration = TimeSpan.FromMilliseconds(20);

    private static bool SendCtrlC() => SendInputs(BuildCtrlCInputs());

    private static bool SendCtrlV() =>
        SendHoldAndReleaseChord(BuildCtrlVInputs(), SyntheticPasteKeyHoldDuration, SendInputs, PumpingWait);

    private static bool SendShiftInsert() =>
        SendHoldAndReleaseChord(BuildShiftInsertInputs(), SyntheticPasteKeyHoldDuration, SendInputs, PumpingWait);

    /// <summary>
    /// #3978: Dispatch key-down, hold on the STA clipboard thread without sleeping via
    /// <see cref="PumpingWait"/>, then release key-up so Chromium/Electron/TinyMCE editors
    /// observe a distinct key press rather than a zero-duration chord.
    /// </summary>
    internal static bool SendHoldAndReleaseChord(
        NativeInput[] fullChord,
        TimeSpan holdDuration,
        Func<NativeInput[], bool> send,
        Action<TimeSpan> wait)
    {
        if (fullChord.Length < 2 || fullChord.Length % 2 != 0)
        {
            return send(fullChord);
        }

        var mid = fullChord.Length / 2;
        var down = fullChord[..mid];
        var up = fullChord[mid..];
        if (!send(down))
        {
            _ = send(up);
            return false;
        }

        var released = false;
        try
        {
            wait(holdDuration);
            var ok = send(up);
            released = ok;
            return ok;
        }
        finally
        {
            if (!released)
            {
                _ = send(up);
            }
        }
    }

    internal static NativeInput[] BuildCtrlCInputs() =>
    [
        MakeInput(VkControl, keyUp: false),
        MakeInput(VkC, keyUp: false),
        MakeInput(VkC, keyUp: true),
        MakeInput(VkControl, keyUp: true),
    ];

    internal static NativeInput[] BuildCtrlVInputs() =>
    [
        MakeInput(VkControl, keyUp: false),
        MakeInput(VkV, keyUp: false),
        MakeInput(VkV, keyUp: true),
        MakeInput(VkControl, keyUp: true),
    ];

    internal static NativeInput[] BuildShiftInsertInputs() =>
    [
        MakeInput(VkShift, keyUp: false),
        MakeInput(VkInsert, keyUp: false, extended: true),
        MakeInput(VkInsert, keyUp: true, extended: true),
        MakeInput(VkShift, keyUp: true),
    ];

    private static bool SendInputs(NativeInput[] inputs) =>
        NativeInputSize == 40 &&
        SendInput(
            checked((uint)inputs.Length),
            inputs,
            NativeInputSize) == inputs.Length;

    private static NativeInput MakeInput(ushort virtualKey, bool keyUp, bool extended = false) => new()
    {
        Type = InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = (extended ? KeyEventExtendedKey : 0u) | (keyUp ? KeyEventKeyUp : 0u),
                // THE APP'S OWN KEYSTROKE, SO ITS OWN HOOK STEPS ASIDE. With a last-dictation shortcut bound to
                // Ctrl+V or Ctrl+C, the hook would otherwise swallow the very paste or copy this sends. Ref: #206.
                ExtraInfo = MenuKeyMask.Tag,
            },
        },
    };

    private sealed class ClipboardMessageWindow : NativeWindow, IDisposable
    {
        public ClipboardMessageWindow()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        NativeInput[] inputs,
        int inputSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(IntPtr dataObject);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClipboardFormatName(uint format, [Out] char[] formatName, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string formatName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("gdi32.dll")]
    private static extern uint GetEnhMetaFileBits(IntPtr enhMetaFile, uint bufferSize, [Out] byte[]? buffer);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SetEnhMetaFileBits(uint bufferSize, byte[] data);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteEnhMetaFile(IntPtr enhMetaFile);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern uint GetMetaFileBitsEx(IntPtr metaFile, uint bufferSize, [Out] byte[]? buffer);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SetMetaFileBitsEx(uint bufferSize, byte[] data);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteMetaFile(IntPtr metaFile);
}
