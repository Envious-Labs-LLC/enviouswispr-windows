using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Lifecycle;

/// <summary>
/// Keeps auxiliary overlay windows out of the Windows taskbar and Alt+Tab switcher.
/// </summary>
public static class WindowsToolWindowStyle
{
    private const int ENotImplHResult = unchecked((int)0x80004001);
    private const int GwlExStyle = -20;
    internal const nint WsExToolWindow = 0x00000080;
    internal const nint WsExAppWindow = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// Hides the window from task switchers via <paramref name="setShownInSwitchers"/>, falling
    /// back to Win32 <c>WS_EX_TOOLWINDOW</c> when the OS or Windows App SDK throws <c>E_NOTIMPL</c>.
    /// </summary>
    public static bool HideFromTaskSwitchers(Action setShownInSwitchers, nint windowHandle)
    {
        ArgumentNullException.ThrowIfNull(setShownInSwitchers);

        try
        {
            setShownInSwitchers();
            return true;
        }
        catch (NotImplementedException)
        {
            // Windows 11 25H2 (26200.10075) with Windows App SDK 2.4.0 can raise E_NOTIMPL from
            // SetShownInSwitchers; fall back to Win32 WS_EX_TOOLWINDOW so the overlay stays out of
            // the taskbar and Alt+Tab.
        }
        catch (COMException exception) when (exception.HResult == ENotImplHResult)
        {
        }

        return TryApplyToolWindowExtendedStyle(windowHandle);
    }

    internal static nint ApplyToolWindowFlags(nint currentStyle) =>
        (currentStyle | WsExToolWindow) & ~WsExAppWindow;

    internal static bool TryApplyToolWindowExtendedStyle(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        SetLastError(0);
        var currentStyle = GetWindowLongPtrW(windowHandle, GwlExStyle);
        if (currentStyle == 0 && Marshal.GetLastWin32Error() != 0)
        {
            return false;
        }

        var updatedStyle = ApplyToolWindowFlags(currentStyle);
        if (updatedStyle == currentStyle)
        {
            return true;
        }

        SetLastError(0);
        var previousStyle = SetWindowLongPtrW(windowHandle, GwlExStyle, updatedStyle);
        if (previousStyle == 0 && Marshal.GetLastWin32Error() != 0)
        {
            return false;
        }

        _ = SetWindowPos(
            windowHandle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        return true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint windowHandle, int index, nint newLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfterHandle,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);
}
