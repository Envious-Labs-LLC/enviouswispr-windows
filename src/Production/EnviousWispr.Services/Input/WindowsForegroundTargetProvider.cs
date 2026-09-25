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
        return new TargetWindowId(handle, processId);
    }

    public TargetWindowId? CaptureForegroundTarget()
    {
        var handle = GetForegroundWindow();
        if (handle == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        return new TargetWindowId(handle, processId, TryCaptureFocusedElementId(processId));
    }

    private static string? TryCaptureFocusedElementId(uint processId)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            return focused is not null && focused.Current.ProcessId == processId
                ? string.Join('.', focused.GetRuntimeId())
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

    /// <summary>Brings a remembered window back and names the field Windows gives the focus to, as a key press would.</summary>
    /// <remarks>
    /// FOR A TARGET REMEMBERED WITHOUT ITS FIELD - the window the person was in before they reached for the tray.
    /// The delivery refuses a target whose focused field it was not told, because that id is how it notices the
    /// person moved to another field; so the field is read HERE, after the window is back in front and Windows has
    /// restored its focus, exactly as a dictation reads it at its key press - and every delivery check still runs.
    /// Null when the window will not come forward or its focus does not come back inside it in time. Ref: #206.
    /// </remarks>
    public static async Task<TargetWindowId?> ReacquireAsync(
        TargetWindowId window,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!window.IsValid)
        {
            return null;
        }

        _ = SetForegroundWindow(window.Value);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() == window.Value)
            {
                var current = new WindowsForegroundTargetProvider().CaptureForegroundTarget();
                if (current is { FocusedElementId: not null } captured &&
                    captured.Value == window.Value &&
                    captured.ProcessId == window.ProcessId)
                {
                    return captured;
                }
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
