using EnviousWispr.Core.Input;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace EnviousWispr.Services.Input;

public sealed class WindowsForegroundTargetProvider : IForegroundTargetProvider
{
    /// <summary>The window in front and its process, read in microseconds - no UI Automation.</summary>
    public static TargetWindowId? ForegroundWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        return new TargetWindowId(handle, processId, null, TryCaptureProcessName(processId));
    }

    public TargetWindowId? CaptureForegroundTarget()
    {
        var handle = GetForegroundWindow();
        if (handle == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        // Skip UI Automation upfront when the foreground window is elevated above us: UIPI blocks
        // cross-integrity element queries, and probing on hotkey down would stall the hook for nothing.
        var focusedElementId = WindowsWindowIdentity.TryIsHigherIntegrityProcess(processId) == true
            ? null
            : TryCaptureFocusedElementId(handle, processId);
        return new TargetWindowId(
            handle,
            processId,
            focusedElementId,
            WindowsWindowIdentity.TryGetProcessName(processId));
    }

    private static string? TryCaptureProcessName(uint processId) =>
        WindowsWindowIdentity.TryGetProcessName(processId);

    private static string? TryCaptureFocusedElementId(nint windowHandle, uint processId)
    {
        var win32ChildFocusId = WindowsWindowIdentity.TryCaptureDistinctWin32ChildFocusId(windowHandle, processId);
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null &&
                WindowsWindowIdentity.IsMatchingFocusProcess(windowHandle, processId, checked((uint)focused.Current.ProcessId)))
            {
                var runtimeId = focused.GetRuntimeId();
                if (runtimeId is { Length: > 0 })
                {
                    return string.Join('.', runtimeId);
                }
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // UI Automation can fail across process/runtime boundaries; fall through to the
            // distinct Win32 child HWND focus check below.
        }

        // Only accept a distinct child control HWND (never top-level HWND); otherwise return null so
        // live dictation fails closed rather than missing intra-window shifts.
        return win32ChildFocusId;
    }

    /// <summary>Brings a remembered window back (restoring it if minimized) and captures its focused field when available.</summary>
    /// <remarks>
    /// FOR A TARGET REMEMBERED WITHOUT ITS FIELD - the window the person was in before they reached for History
    /// or the tray. Restores minimized targets (<c>SW_RESTORE</c>), brings the window forward, and reads UIA
    /// <see cref="TargetWindowId.FocusedElementId"/> when published. Chromium/Electron and similar hosts may omit
    /// UIA focus across process/rendering boundaries: after a brief UIA wake-up grace window while the target
    /// stays foreground, the confirmed HWND/process target is returned so window-level paste can proceed instead
    /// of falsely refusing with <c>NoTarget</c>.
    /// </remarks>
    public static Task<TargetWindowId?> ReacquireAsync(
        TargetWindowId window,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ReacquireCoreAsync(
            window,
            timeout,
            isWindowValid: target =>
            {
                if (!IsWindow(target.Value))
                {
                    return false;
                }

                _ = GetWindowThreadProcessId(target.Value, out var ownerProcessId);
                return ownerProcessId != 0 && (target.ProcessId == 0 || ownerProcessId == target.ProcessId);
            },
            restoreAndFocus: WindowsWindowIdentity.RestoreAndFocus,
            isIconic: WindowsWindowIdentity.IsIconic,
            getForegroundWindow: GetForegroundWindow,
            captureCurrentTarget: () => new WindowsForegroundTargetProvider().CaptureForegroundTarget(),
            getWindowClassName: WindowsWindowIdentity.TryGetClassName,
            cancellationToken);

    internal static async Task<TargetWindowId?> ReacquireCoreAsync(
        TargetWindowId window,
        TimeSpan timeout,
        Func<TargetWindowId, bool> isWindowValid,
        Action<nint> restoreAndFocus,
        Func<nint, bool> isIconic,
        Func<nint> getForegroundWindow,
        Func<TargetWindowId?> captureCurrentTarget,
        Func<nint, string?> getWindowClassName,
        CancellationToken cancellationToken,
        int pollIntervalMs = 25,
        int focusedElementGraceMs = 200)
    {
        if (!window.IsValid || !isWindowValid(window))
        {
            return null;
        }

        restoreAndFocus(window.Value);
        var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
        long? firstForegroundAt = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (getForegroundWindow() == window.Value)
            {
                var now = Environment.TickCount64;
                firstForegroundAt ??= now;
                var current = captureCurrentTarget();
                if (current is { } captured &&
                    captured.Value == window.Value &&
                    (window.ProcessId == 0 || captured.ProcessId == window.ProcessId))
                {
                    var hasUiaFocus = captured.FocusedElementId is not null &&
                        !captured.FocusedElementId.StartsWith(WindowsWindowIdentity.Win32FocusPrefix, StringComparison.Ordinal);
                    if (hasUiaFocus)
                    {
                        return captured;
                    }

                    var graceElapsed = now - firstForegroundAt.Value >= focusedElementGraceMs || now >= deadline;
                    if (graceElapsed &&
                        (captured.FocusedElementId is not null ||
                         CanFallbackWithoutFocusedElement(captured, getWindowClassName(captured.Value))))
                    {
                        return captured;
                    }
                }
            }
            else if (isIconic(window.Value))
            {
                restoreAndFocus(window.Value);
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool CanFallbackWithoutFocusedElement(TargetWindowId target, string? windowClassName) =>
        WindowsPastePolicy.Resolve(
            target.ProcessName,
            windowClassName,
            elementClassName: null,
            isControlEdit: false,
            isFullscreen: false).AllowsWindowFallbackWithoutFocusedElement;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
}
