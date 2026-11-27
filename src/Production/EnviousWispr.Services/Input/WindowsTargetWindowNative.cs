using System.Runtime.InteropServices;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Services.Input;

internal static class WindowsTargetWindowNative
{
    private const uint MonitorDefaultToNearest = 2;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkV = 0x56;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    internal static TargetContextStatus ActivateAndValidateWindow(
        TargetWindowId target,
        UiaPreparationBudget budget,
        out bool wasReactivated)
    {
        wasReactivated = false;
        if (!target.IsValid || !IsWindow(target.Value))
        {
            return TargetContextStatus.TargetUnavailable;
        }

        _ = GetWindowThreadProcessId(target.Value, out var currentProcessId);
        if (target.ProcessId == 0 || currentProcessId != target.ProcessId)
        {
            return TargetContextStatus.TargetChanged;
        }

        var integrity = WindowsWindowIdentity.TryIsHigherIntegrityProcess(target.ProcessId);
        if (integrity is null or true)
        {
            return TargetContextStatus.Elevated;
        }

        if (GetForegroundWindow() != target.Value)
        {
            wasReactivated = true;
            _ = WindowsWindowIdentity.TryRestoreAndFocus(target.Value);
            while (GetForegroundWindow() != target.Value && !budget.IsExhausted)
            {
                Thread.Sleep(25);
            }
        }

        return GetForegroundWindow() == target.Value
            ? TargetContextStatus.Available
            : TargetContextStatus.TargetChanged;
    }

    internal static bool IsSameForegroundWindowAndProcess(TargetWindowId target)
    {
        if (GetForegroundWindow() != target.Value)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(target.Value, out var processId);
        return processId == target.ProcessId;
    }

    internal static nint CurrentForegroundWindow() => GetForegroundWindow();

    internal static bool AnyInputKeyHeld() =>
        IsKeyHeld(VkControl) ||
        IsKeyHeld(VkMenu) ||
        IsKeyHeld(VkShift) ||
        IsKeyHeld(VkLeftWindows) ||
        IsKeyHeld(VkRightWindows) ||
        IsKeyHeld(VkV);

    private static bool IsKeyHeld(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    internal static bool IsFullscreen(nint window)
    {
        if (!GetWindowRect(window, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 &&
            GetMonitorInfo(monitor, ref info) &&
            windowRect.Left <= info.Monitor.Left &&
            windowRect.Top <= info.Monitor.Top &&
            windowRect.Right >= info.Monitor.Right &&
            windowRect.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
