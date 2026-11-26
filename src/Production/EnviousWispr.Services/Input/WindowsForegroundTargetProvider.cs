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
        return new TargetWindowId(
            handle,
            processId,
            TryCaptureFocusedElementId(processId),
            TryCaptureProcessName(processId));
    }

    private static string? TryCaptureProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }

    private static string? TryCaptureFocusedElementId(uint processId)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || focused.Current.ProcessId != processId)
            {
                return null;
            }

            var runtimeId = focused.GetRuntimeId();
            return runtimeId is { Length: > 0 }
                ? string.Join('.', runtimeId)
                : null;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // A focused-element runtime ID improves target-change detection, but it is optional.
            // UI Automation can fail with provider-specific non-fatal exception types, especially
            // across application/runtime boundaries. Preserve the frozen HWND/process target and
            // let delivery fall back safely instead of aborting the recording before capture starts.
            return null;
        }
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
                    if (captured.FocusedElementId is not null)
                    {
                        return captured;
                    }

                    var graceElapsed = now - firstForegroundAt.Value >= focusedElementGraceMs || now >= deadline;
                    if (graceElapsed && CanFallbackWithoutFocusedElement(captured, getWindowClassName(captured.Value)))
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
