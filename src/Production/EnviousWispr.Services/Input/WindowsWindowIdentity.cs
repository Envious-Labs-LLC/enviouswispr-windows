using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Input;

/// <summary>Shared Win32 window class, process-name, focus identity, and restore/focus helpers for input services.</summary>
internal static class WindowsWindowIdentity
{
    private const int SwRestore = 9;
    internal const string Win32FocusPrefix = "win32:";

    private static readonly HashSet<string> ProtectedForegroundProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "consent",
        "LockApp",
        "LogonUI",
        "CredentialUIBroker",
        "ScreenClippingHost",
    };

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

    internal delegate uint WindowThreadProcessIdReader(nint window, out uint processId);

    internal static void RestoreAndFocus(nint window) =>
        _ = TryRestoreAndFocus(window);

    internal static bool TryRestoreAndFocus(nint window) =>
        RestoreAndFocusCore(
            window,
            isWindow: IsWindow,
            isIconic: IsIconic,
            showWindowRestore: static handle => _ = ShowWindow(handle, SwRestore),
            getForegroundWindow: GetForegroundWindow,
            getWindowThreadProcessId: GetWindowThreadProcessId,
            getCurrentThreadId: GetCurrentThreadId,
            tryGetProcessName: TryGetProcessName,
            bringWindowToTop: BringWindowToTop,
            setForegroundWindow: SetForegroundWindow,
            setActiveWindow: SetActiveWindow,
            attachThreadInput: AttachThreadInput);

    /// <summary>
    /// Brings <paramref name="window"/> back (restoring if minimized) using direct focus calls first
    /// and falling back cleanly to bounded <c>AttachThreadInput</c> only when needed, never stealing
    /// focus from Windows security/lock screens and never synthesizing Alt keystrokes.
    /// </summary>
    internal static bool RestoreAndFocusCore(
        nint window,
        Func<nint, bool> isWindow,
        Func<nint, bool> isIconic,
        Action<nint> showWindowRestore,
        Func<nint> getForegroundWindow,
        WindowThreadProcessIdReader getWindowThreadProcessId,
        Func<uint> getCurrentThreadId,
        Func<uint, string?> tryGetProcessName,
        Func<nint, bool> bringWindowToTop,
        Func<nint, bool> setForegroundWindow,
        Func<nint, nint> setActiveWindow,
        Func<uint, uint, bool, bool> attachThreadInput)
    {
        if (window == 0 || !isWindow(window))
        {
            return false;
        }

        if (isIconic(window))
        {
            showWindowRestore(window);
        }

        if (getForegroundWindow() == window)
        {
            return true;
        }

        _ = bringWindowToTop(window);
        _ = setForegroundWindow(window);
        if (getForegroundWindow() == window)
        {
            return true;
        }

        var foreground = getForegroundWindow();
        if (foreground != 0)
        {
            _ = getWindowThreadProcessId(foreground, out var foregroundProcessId);
            if (foregroundProcessId != 0)
            {
                var foregroundProcess = NormalizeProcessName(tryGetProcessName(foregroundProcessId));
                if (ProtectedForegroundProcesses.Contains(foregroundProcess))
                {
                    return false;
                }
            }
        }

        var currentThreadId = getCurrentThreadId();
        var foregroundThreadId = foreground != 0 ? getWindowThreadProcessId(foreground, out _) : 0;
        var targetThreadId = getWindowThreadProcessId(window, out _);
        if (currentThreadId == 0 || (foregroundThreadId == 0 && targetThreadId == 0))
        {
            return getForegroundWindow() == window;
        }

        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedForeground = attachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != 0 && targetThreadId != currentThreadId && targetThreadId != foregroundThreadId)
            {
                attachedTarget = attachThreadInput(currentThreadId, targetThreadId, true);
            }

            _ = bringWindowToTop(window);
            _ = setForegroundWindow(window);
            _ = setActiveWindow(window);
        }
        finally
        {
            if (attachedTarget)
            {
                _ = attachThreadInput(currentThreadId, targetThreadId, false);
            }

            if (attachedForeground)
            {
                _ = attachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }

        return getForegroundWindow() == window;
    }

    private static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        var trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
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

    /// <summary>
    /// Returns whether <paramref name="targetProcessId"/> runs at a higher Windows integrity level
    /// than this process, or <see langword="null"/> when the process token cannot be queried.
    /// </summary>
    internal static bool? TryIsHigherIntegrityProcess(uint targetProcessId)
    {
        if (targetProcessId == 0)
        {
            return null;
        }

        var ours = GetIntegrityLevel(GetCurrentProcess());
        var targetProcess = OpenProcess(ProcessQueryLimitedInformation, false, targetProcessId);
        if (targetProcess == 0)
        {
            return null;
        }

        try
        {
            var target = GetIntegrityLevel(targetProcess);
            return ours is null || target is null ? null : target > ours;
        }
        finally
        {
            _ = CloseHandle(targetProcess);
        }
    }

    private static uint? GetIntegrityLevel(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            _ = GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var needed);
            if (needed == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                {
                    return null;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == 0)
                {
                    return null;
                }

                var count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return null;
                }

                var authority = GetSidSubAuthority(sid, checked((uint)(count - 1)));
                return authority == 0 ? null : checked((uint)Marshal.ReadInt32(authority));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

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

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token,
        int tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
