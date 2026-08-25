using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EnviousWispr.Input;

/// Global push-to-talk via a WH_KEYBOARD_LL hook: deterministic key-down /
/// key-up edges for one virtual key, working while any app has focus.
/// (RegisterHotKey + MOD_NOREPEAT does not reliably deliver the key-up edge;
/// the low-level hook is the pattern dictation apps use.)
public sealed class GlobalHotkey : IDisposable
{
    private const int WhKeyboardLL = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLLStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly IntPtr _hook;
    private readonly uint _targetVk;
    private readonly LowLevelKeyboardProc _proc; // keep alive (GC)
    private bool _held;

    public event Action? KeyDown;
    public event Action? KeyUp;

    public GlobalHotkey(uint targetVk)
    {
        _targetVk = targetVk;
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt64() is WmKeydown or WmSyskeydown or WmKeyup or WmSyskeyup)
        {
            var kb = Marshal.PtrToStructure<KbdLLStruct>(lParam);
            if (kb.vkCode == _targetVk)
            {
                var isDown = wParam.ToInt32() is WmKeydown or WmSyskeydown;
                var isRepeat = (kb.flags & 0x80) != 0; // KBDLLHOOKF_INJECTED... no: bit7 of flags is the extended-key flag; repeats arrive as extra WM_KEYDOWNs — track via _held.
                if (isDown && !_held)
                {
                    _held = true;
                    KeyDown?.Invoke();
                }
                else if (!isDown && _held)
                {
                    _held = false;
                    KeyUp?.Invoke();
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        UnhookWindowsHookEx(_hook);
    }
}
