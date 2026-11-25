using System.ComponentModel;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

/// <summary>
/// Installs a temporary low-level keyboard hook while capturing a shortcut so Windows menus, Start,
/// and WinUI focus traversal never intercept modifier presses like LeftAlt, RightAlt, or Win.
/// </summary>
public sealed class WindowsKeybindCaptureSession : IDisposable
{
    private const int WhKeyboardLowLevel = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSystemKeyDown = 0x0104;
    private const int WmSystemKeyUp = 0x0105;
    private const uint LlkHfInjected = 0x10;

    private readonly object _sync = new();
    private readonly KeybindCaptureStateMachine _stateMachine;
    private readonly Action<string> _onCommitted;
    private readonly Action _onCancelled;
    private readonly Action<string>? _onInvalidAttempt;
    private readonly Action<Action> _dispatchToUi;
    private readonly LowLevelKeyboardProcedure _procedure;
    private nint _hook;
    private bool _finished;
    private bool _disposed;

    public WindowsKeybindCaptureSession(
        bool allowModifierOnly,
        bool allowEscapeAsBinding,
        bool allowClear,
        Action<string> onCommitted,
        Action onCancelled,
        Action<string>? onInvalidAttempt = null,
        Action<Action>? dispatchToUi = null)
    {
        ArgumentNullException.ThrowIfNull(onCommitted);
        ArgumentNullException.ThrowIfNull(onCancelled);

        _stateMachine = new KeybindCaptureStateMachine(
            allowModifierOnly,
            allowEscapeAsBinding,
            allowClear);
        _onCommitted = onCommitted;
        _onCancelled = onCancelled;
        _onInvalidAttempt = onInvalidAttempt;
        _dispatchToUi = dispatchToUi ?? CreateDefaultDispatcher();
        _procedure = HookCallback;

        _hook = SetWindowsHookEx(
            WhKeyboardLowLevel,
            _procedure,
            GetModuleHandle(moduleName: null),
            threadId: 0);

        if (_hook == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Keybind capture keyboard hook unavailable.");
        }
    }

    public bool IsInstalled => Volatile.Read(ref _hook) != 0;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _finished = true;
        }

        var hook = Interlocked.Exchange(ref _hook, 0);
        if (hook != 0)
        {
            UnhookWindowsHookEx(hook);
        }
    }

    internal static bool ShouldProcessHookKey(uint virtualKey, uint flags, bool isAppForeground)
    {
        const uint maskMenuKey = 0xE8;
        if (virtualKey == maskMenuKey)
        {
            return false;
        }

        var isInjected = (flags & LlkHfInjected) != 0;
        return !isInjected || isAppForeground;
    }

    private static bool IsCurrentProcessForeground()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private nint HookCallback(int code, nint message, nint data)
    {
        if (code < 0 || !IsKeyboardEdge(message))
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var keyboard = Marshal.PtrToStructure<LowLevelKeyboardData>(data);
        if (!ShouldProcessHookKey(keyboard.VirtualKey, keyboard.Flags, IsCurrentProcessForeground()))
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var isKeyDown = message is WmKeyDown or WmSystemKeyDown;
        KeybindCaptureStepResult result;
        lock (_sync)
        {
            if (_finished || _disposed)
            {
                return 1;
            }

            result = _stateMachine.Process(keyboard.VirtualKey, isKeyDown);
            if (result.Status is KeybindCaptureStatus.Committed or KeybindCaptureStatus.Cancelled)
            {
                _finished = true;
            }
        }

        switch (result.Status)
        {
            case KeybindCaptureStatus.Committed when result.GestureText is not null:
                var committed = result.GestureText;
                _dispatchToUi(() => _onCommitted(committed));
                break;

            case KeybindCaptureStatus.Cancelled:
                _dispatchToUi(_onCancelled);
                break;

            case KeybindCaptureStatus.InvalidAttempt when result.HintMessage is not null:
                var hint = result.HintMessage;
                _dispatchToUi(() => _onInvalidAttempt?.Invoke(hint));
                break;
        }

        return 1;
    }

    private static Action<Action> CreateDefaultDispatcher()
    {
        var context = SynchronizationContext.Current;
        return context is not null
            ? action => context.Post(static state => ((Action)state!)(), action)
            : static action => action();
    }

    private static bool IsKeyboardEdge(nint message) =>
        message is WmKeyDown or WmKeyUp or WmSystemKeyDown or WmSystemKeyUp;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardData
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nint ExtraInfo;
    }

    private delegate nint LowLevelKeyboardProcedure(int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProcedure procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
