using System.Runtime.InteropServices;
using System.Text;

namespace EnviousWispr.Input;

/// Inserts text into the focused application the way dictation apps do:
/// clipboard + synthetic Ctrl+V, with best-effort clipboard restore.
/// (SendInput-typing is the alternative; clipboard-paste is what the Wispr
/// family ships and how the Mac's paste contract behaves for rich apps.)
public static class TextInserter
{
    private const uint VkControl = 0x11;
    private const uint VkV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public int Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public uint wVk;
        public uint wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    /// One atomic SendInput batch: Ctrl down, V down, V up, Ctrl up.
    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            MakeInput(VkControl, false),
            MakeInput(VkV, false),
            MakeInput(VkV, true),
            MakeInput(VkControl, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input MakeInput(uint vk, bool up) => new()
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

    public static void Paste(string text)
    {
        var previous = TryGetClipboardText();

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
        if (!set) return; // no text inserted; the caller logs it

        SendCtrlV();

        // Give the target app a beat to consume the paste, then restore.
        System.Threading.Thread.Sleep(120);
        if (previous is not null && set)
        {
            try { System.Windows.Clipboard.SetDataObject(previous, true); }
            catch { /* best effort */ }
        }
    }

    private static string? TryGetClipboardText()
    {
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch { return null; }
    }
}
