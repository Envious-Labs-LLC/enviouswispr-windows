using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Input;

/// <summary>Shared Win32 window class, process-name, focus identity, and restore/focus helpers for input services.</summary>
internal static class WindowsWindowIdentity
{
    private const int SwRestore = 9;
    internal const string Win32FocusPrefix = "win32:";

    internal static string? TryGetClassName(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        var buffer = new char[128];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    internal static string? TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            return null;
        }
    }

    internal static void RestoreAndFocus(nint window)
    {
        if (window == 0)
        {
            return;
        }

        if (IsIconic(window))
        {
            _ = ShowWindow(window, SwRestore);
        }

        _ = BringWindowToTop(window);
        _ = SetForegroundWindow(window);
    }

    /// <summary>
    /// Captures a distinct Win32 child control HWND focus token when UIA has no RuntimeId.
    /// Returns null whenever focus equals the top-level window HWND so windowless intra-window
    /// shifts (e.g. Electron panes sharing one HWND) cannot bypass change detection.
    /// </summary>
    internal static string? TryCaptureDistinctWin32ChildFocusId(nint window, uint expectedProcessId)
    {
        if (window == 0)
        {
            return null;
        }

        var threadId = GetWindowThreadProcessId(window, out var ownerProcessId);
        if (threadId == 0 || (expectedProcessId != 0 && ownerProcessId != expectedProcessId))
        {
            return null;
        }

        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref gui) ||
            gui.HwndFocus == 0 ||
            gui.HwndFocus == window)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(gui.HwndFocus, out var focusProcessId);
        var requiredProcessId = expectedProcessId != 0 ? expectedProcessId : ownerProcessId;
        return focusProcessId == requiredProcessId
            ? $"{Win32FocusPrefix}{gui.HwndFocus:X}"
            : null;
    }

    internal static string CombineFocusIdentity(string? win32FocusId, string uiaFocusId) =>
        string.IsNullOrWhiteSpace(win32FocusId)
            ? uiaFocusId
            : $"{uiaFocusId}|{win32FocusId}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint HwndActive;
        public nint HwndFocus;
        public nint HwndCapture;
        public nint HwndMenuOwner;
        public nint HwndMoveSize;
        public nint HwndCaret;
        public Rect RcCaret;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
