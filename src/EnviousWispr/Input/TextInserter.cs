using System.Runtime.InteropServices;

namespace EnviousWispr.Input;

public enum PasteResult { NotAttempted, Pasted, ClipboardOnly, Failed }

/// Inserts text into the focused application the way dictation apps do:
/// clipboard + synthetic Ctrl+V, with best-effort clipboard restore.
/// (SendInput-typing is the alternative; clipboard-paste is what the Wispr
/// family ships and how the Mac's paste contract behaves for rich apps.)
public static class TextInserter
{
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion U;
    }

    // INPUT's native union is 32 bytes on 64-bit Windows because MOUSEINPUT
    // is larger than KEYBDINPUT. Without the explicit size, INPUT marshals as
    // 32 bytes instead of 40 and SendInput rejects every Ctrl+V batch.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public int Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        // Win32 WORD fields. Using uint here keeps the total size plausible
        // but moves dwFlags from native offset 4 to offset 8, turning every
        // intended key-up into another key-down (and pasting twice).
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    internal static int NativeInputSize => Marshal.SizeOf<Input>();
    internal static int NativeKeyboardInputSize => Marshal.SizeOf<KEYBDINPUT>();
    internal static int NativeKeyboardFlagsOffset =>
        Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.dwFlags)).ToInt32();

    /// One atomic SendInput batch: Ctrl down, V down, V up, Ctrl up.
    private static bool SendCtrlV()
    {
        var inputs = new[]
        {
            MakeInput(VkControl, false),
            MakeInput(VkV, false),
            MakeInput(VkV, true),
            MakeInput(VkControl, true),
        };
        return SendInput((uint)inputs.Length, inputs, NativeInputSize) == inputs.Length;
    }

    private static Input MakeInput(ushort vk, bool up) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = up ? KeyEventFKeyUp : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    public static PasteResult Paste(string text)
    {
        var previous = TrySnapshotClipboard();

        var set = false;
        for (var attempt = 0; attempt < 3 && !set; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                set = true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Clipboard locked by another process — retry briefly.
                System.Threading.Thread.Sleep(50);
            }
        }
        if (!set) return PasteResult.Failed;

        var ourSequence = GetClipboardSequenceNumber();
        if (!SendCtrlV())
            return PasteResult.ClipboardOnly; // payload stays available for a manual Ctrl+V

        // Give the target app a beat to consume the paste, then restore.
        System.Threading.Thread.Sleep(200);
        if (previous.Captured && GetClipboardSequenceNumber() == ourSequence)
        {
            try
            {
                if (previous.Data is null) System.Windows.Clipboard.Clear();
                else System.Windows.Clipboard.SetDataObject(previous.Data, true);
            }
            catch { /* best effort */ }
        }
        return PasteResult.Pasted;
    }

    private sealed record ClipboardSnapshot(bool Captured, System.Windows.DataObject? Data);

    private static ClipboardSnapshot TrySnapshotClipboard()
    {
        try
        {
            var source = System.Windows.Clipboard.GetDataObject();
            if (source is null) return new ClipboardSnapshot(true, null);

            var copy = new System.Windows.DataObject();
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                var value = source.GetData(format, autoConvert: false);
                if (value is not null) copy.SetData(format, value);
            }
            return new ClipboardSnapshot(true, copy);
        }
        catch
        {
            return new ClipboardSnapshot(false, null);
        }
    }
}
