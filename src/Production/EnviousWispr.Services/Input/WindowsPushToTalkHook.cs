using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace EnviousWispr.Services.Input;

public sealed class WindowsPushToTalkHook : IGlobalPushToTalk
{
    private const int WhKeyboardLowLevel = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSystemKeyDown = 0x0104;
    private const int WmSystemKeyUp = 0x0105;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const uint ModifierNoRepeat = 0x4000;
    private const uint VirtualKeyShift = 0x10;
    private const uint VirtualKeyControl = 0x11;
    private const uint VirtualKeyAlt = 0x12;
    private const uint VirtualKeyLeftWindows = 0x5B;
    private const uint VirtualKeyRightWindows = 0x5C;

    private static int _probeId = 0x5100;

    private readonly LowLevelKeyboardProcedure _procedure;
    private readonly HotkeyEdgeTracker _edgeTracker;
    private readonly Channel<PushToTalkSignal> _signals = Channel.CreateUnbounded<PushToTalkSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Task _dispatchTask;
    private nint _hook;
    // Fully qualified: this project also sees System.Windows.Forms.Timer, and a Forms timer
    // needs a message pump, which the hook thread does not have.
    private readonly System.Threading.Timer? _gestureTimer;

    /// <summary>How often a pending gesture is given a chance to complete.</summary>
    /// <remarks>
    /// Comfortably finer than the shortest deadline it has to catch, so the worst case adds a few
    /// tens of milliseconds to a gesture that already waits two hundred. Cheap enough to run for the
    /// life of the app, and it only runs at all for a modifier binding.
    /// </remarks>
    private static readonly TimeSpan GestureTickInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>Whether the recording binding needs the gesture clock running.</summary>
    private static bool IsModifierBinding(HotkeyGesture gesture, uint virtualKey) =>
        (virtualKey == 0 && gesture.Modifiers != HotkeyModifiers.None) ||
        HotkeyEdgeTracker.IsModifierKey(virtualKey);

    private void PumpGesture()
    {
        var signal = _edgeTracker.Tick();
        if (signal is not null)
        {
            _signals.Writer.TryWrite(signal.Value);
        }
    }

    private WindowsPushToTalkHook(
        HotkeyGesture gesture,
        HotkeyGesture cancelGesture,
        HotkeyGesture quickAddGesture,
        DictationRecordingMode recordingMode,
        uint virtualKey,
        uint cancelVirtualKey,
        uint quickAddVirtualKey)
    {
        Gesture = gesture;
        CancelGesture = cancelGesture;
        QuickAddGesture = quickAddGesture;
        RecordingMode = recordingMode;
        _edgeTracker = new HotkeyEdgeTracker(
            new HotkeyBinding(virtualKey, gesture.Modifiers),
            new HotkeyBinding(cancelVirtualKey, cancelGesture.Modifiers),
            new HotkeyBinding(quickAddVirtualKey, quickAddGesture.Modifiers),
            recordingMode);
        _procedure = HookCallback;
        _dispatchTask = Task.Run(DispatchAsync);

        // A HOLD COMPLETES ON TIME, NOT ON A KEYSTROKE. Without something looking at the clock, a
        // modifier binding waits for a threshold that never elapses and can never start a recording
        // at all - a whole feature that builds, tests green at the policy level, and does nothing.
        //
        // Armed only when a modifier binding is in use, so an ordinary key runs no timer and the
        // common path keeps exactly the cost it has today.
        if (_edgeTracker.NextDeadline is not null || IsModifierBinding(gesture, virtualKey))
        {
            _gestureTimer = new System.Threading.Timer(
                _ => PumpGesture(),
                state: null,
                dueTime: GestureTickInterval,
                period: GestureTickInterval);
        }
        _hook = SetWindowsHookEx(
            WhKeyboardLowLevel,
            _procedure,
            GetModuleHandle(moduleName: null),
            threadId: 0);

        if (_hook == 0)
        {
            _signals.Writer.TryComplete();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Global keyboard hook unavailable.");
        }
    }

    public event EventHandler<PushToTalkSignalEvent>? Signalled;

    public HotkeyGesture Gesture { get; }

    public HotkeyGesture CancelGesture { get; }

    public HotkeyGesture QuickAddGesture { get; }

    public DictationRecordingMode RecordingMode { get; }

    public bool IsInstalled => _hook != 0;

    public static bool TryCreate(
        string configuredGesture,
        out WindowsPushToTalkHook? hook,
        out AppError? error) => TryCreate(
            configuredGesture,
            DictationRecordingMode.PushToTalk,
            "Escape",
            "Ctrl+Alt+W",
            out hook,
            out error);

    public static bool TryCreate(
        string configuredGesture,
        DictationRecordingMode recordingMode,
        string configuredCancelGesture,
        string configuredQuickAddGesture,
        out WindowsPushToTalkHook? hook,
        out AppError? error)
    {
        hook = null;
        var parsed = HotkeyGestureParser.Parse(configuredGesture);
        var parsedCancel = HotkeyGestureParser.Parse(configuredCancelGesture);
        var parsedQuickAdd = HotkeyGestureParser.Parse(configuredQuickAddGesture);
        if (!parsed.Succeeded || parsed.Gesture is null ||
            !parsedCancel.Succeeded || parsedCancel.Gesture is null ||
            !parsedQuickAdd.Succeeded || parsedQuickAdd.Gesture is null ||
            !Enum.IsDefined(recordingMode) ||
            parsed.Gesture.Value == parsedCancel.Gesture.Value ||
            parsed.Gesture.Value == parsedQuickAdd.Gesture.Value ||
            parsedCancel.Gesture.Value == parsedQuickAdd.Gesture.Value)
        {
            error = parsed.Error;
            return false;
        }

        var gesture = parsed.Gesture.Value;
        var cancelGesture = parsedCancel.Gesture.Value;
        var quickAddGesture = parsedQuickAdd.Gesture.Value;
        if (!WindowsVirtualKeyMap.TryMap(gesture.Key, out var virtualKey) ||
            !WindowsVirtualKeyMap.TryMap(cancelGesture.Key, out var cancelVirtualKey) ||
            !WindowsVirtualKeyMap.TryMap(quickAddGesture.Key, out var quickAddVirtualKey))
        {
            error = new AppError(
                AppErrorCode.HotkeyInvalid,
                AppErrorStage.HotkeyConfiguration,
                CanRetry: false);
            return false;
        }

        var conflict = ProbeConflict(gesture, virtualKey) ??
            ProbeConflict(cancelGesture, cancelVirtualKey) ??
            ProbeConflict(quickAddGesture, quickAddVirtualKey);
        if (conflict is not null)
        {
            error = conflict;
            return false;
        }

        try
        {
            hook = new WindowsPushToTalkHook(
                gesture,
                cancelGesture,
                quickAddGesture,
                recordingMode,
                virtualKey,
                cancelVirtualKey,
                quickAddVirtualKey);
            error = null;
            return true;
        }
        catch (Win32Exception)
        {
            error = new AppError(
                AppErrorCode.HotkeyUnavailable,
                AppErrorStage.HotkeyHook,
                CanRetry: true);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var hook = Interlocked.Exchange(ref _hook, 0);
        if (hook == 0)
        {
            return;
        }

        UnhookWindowsHookEx(hook);

        // Stop the clock BEFORE closing the channel. A tick that lands after the writer completes
        // would try to write to a closed channel, which is a fault raised on a timer thread nobody
        // is watching - the kind that shows up as a mysterious process exit rather than an error.
        if (_gestureTimer is not null)
        {
            await _gestureTimer.DisposeAsync().ConfigureAwait(false);
        }

        _signals.Writer.TryComplete();
        await _dispatchTask.ConfigureAwait(false);
    }

    public void SetRecordingActive(bool active) => _edgeTracker.SetRecordingActive(active);

    public void SetCapturingKeybind(bool capturing) => _edgeTracker.SetCapturingKeybind(capturing);

    private nint HookCallback(int code, nint message, nint data)
    {
        if (code >= 0 && IsKeyboardEdge(message))
        {
            var keyboard = Marshal.PtrToStructure<LowLevelKeyboardData>(data);
            var decision = _edgeTracker.Process(
                keyboard.VirtualKey,
                IsKeyDown(message),
                ReadActiveModifiers());
            if (decision.Signal is not null)
            {
                _signals.Writer.TryWrite(decision.Signal.Value);
            }

            if (decision.Consume)
            {
                return 1;
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private async Task DispatchAsync()
    {
        await foreach (var signal in _signals.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Signalled?.Invoke(this, new PushToTalkSignalEvent(signal));
            }
            catch
            {
                // Subscribers cannot be allowed to tear down the native hook dispatch loop.
            }
        }
    }

    private static AppError? ProbeConflict(HotkeyGesture gesture, uint virtualKey)
    {
        var id = Interlocked.Increment(ref _probeId);
        if (RegisterHotKey(0, id, ToWindowsModifiers(gesture.Modifiers) | ModifierNoRepeat, virtualKey))
        {
            UnregisterHotKey(0, id);
            return null;
        }

        return new AppError(
            Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered
                ? AppErrorCode.HotkeyConflict
                : AppErrorCode.HotkeyUnavailable,
            AppErrorStage.HotkeyConfiguration,
            CanRetry: true);
    }

    private static HotkeyModifiers ReadActiveModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsPressed(VirtualKeyControl))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsPressed(VirtualKeyAlt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsPressed(VirtualKeyShift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsPressed(VirtualKeyLeftWindows) || IsPressed(VirtualKeyRightWindows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsPressed(uint virtualKey) => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    private static bool IsKeyboardEdge(nint message) =>
        message is WmKeyDown or WmKeyUp or WmSystemKeyDown or WmSystemKeyUp;

    private static bool IsKeyDown(nint message) => message is WmKeyDown or WmSystemKeyDown;

    private static uint ToWindowsModifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= 0x0001;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= 0x0002;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= 0x0004;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            result |= 0x0008;
        }

        return result;
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}

internal static class WindowsVirtualKeyMap
{
    public static bool TryMap(string key, out uint virtualKey)
    {
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            virtualKey = key[0];
            return true;
        }

        if (key.Length is 2 or 3 &&
            key[0] == 'F' &&
            int.TryParse(key.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = checked((uint)(0x70 + functionKey - 1));
            return true;
        }

        virtualKey = key switch
        {
            "Space" => 0x20,
            "Pause" => 0x13,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "End" => 0x23,
            "Home" => 0x24,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            "ScrollLock" => 0x91,
            "Escape" => 0x1B,

            // The sided modifiers, each naming one physical key. HotkeyEdgeTracker recognises these
            // codes and routes them through the tap gesture rather than hold-to-talk.
            "RightCtrl" => 0xA3,
            "LeftCtrl" => 0xA2,
            "RightShift" => 0xA1,
            "LeftShift" => 0xA0,
            "RightWin" => 0x5C,
            "LeftWin" => 0x5B,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
