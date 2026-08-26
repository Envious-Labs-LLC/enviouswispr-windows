using EnviousWispr.Core.Reliability;
using Microsoft.Win32;

namespace EnviousWispr.Services.Lifecycle;

public sealed class WindowsSystemLifecycleMonitor : ISystemLifecycleMonitor
{
    private bool _disposed;

    public WindowsSystemLifecycleMonitor()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public event EventHandler<SystemLifecycleTransition>? Transitioned;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    internal static SystemLifecycleTransition? Map(PowerModes mode) => mode switch
    {
        PowerModes.Suspend => SystemLifecycleTransition.Suspending,
        PowerModes.Resume => SystemLifecycleTransition.Resumed,
        _ => null,
    };

    internal static SystemLifecycleTransition? Map(SessionSwitchReason reason) => reason switch
    {
        SessionSwitchReason.SessionLock => SystemLifecycleTransition.SessionLocked,
        SessionSwitchReason.SessionUnlock => SystemLifecycleTransition.SessionUnlocked,
        _ => null,
    };

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (Map(args.Mode) is { } transition)
        {
            Transitioned?.Invoke(this, transition);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (Map(args.Reason) is { } transition)
        {
            Transitioned?.Invoke(this, transition);
        }
    }
}
