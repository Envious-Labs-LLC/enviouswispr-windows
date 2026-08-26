using EnviousWispr.Core.Input;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Input;

public sealed class WindowsForegroundTargetProvider : IForegroundTargetProvider
{
    public TargetWindowId? CaptureForegroundTarget()
    {
        var handle = GetForegroundWindow();
        return handle == 0 ? null : new TargetWindowId(handle);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
