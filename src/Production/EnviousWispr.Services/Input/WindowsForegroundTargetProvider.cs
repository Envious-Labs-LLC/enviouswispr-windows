using EnviousWispr.Core.Input;
using System.ComponentModel;
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
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
