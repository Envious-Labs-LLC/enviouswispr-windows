using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Input;

/// <summary>Shared Win32 window class, process-name, and restore/focus helpers for input services.</summary>
internal static class WindowsWindowIdentity
{
    private const int SwRestore = 9;

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

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
