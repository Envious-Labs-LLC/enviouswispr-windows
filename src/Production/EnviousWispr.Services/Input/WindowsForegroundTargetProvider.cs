using EnviousWispr.Core.Input;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace EnviousWispr.Services.Input;

public sealed class WindowsForegroundTargetProvider : IForegroundTargetProvider
{
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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
