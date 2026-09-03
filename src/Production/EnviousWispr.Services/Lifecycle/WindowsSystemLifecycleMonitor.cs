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
        // WINDOWS ANNOUNCES ITS OWN ENDINGS AND NOBODY WAS LISTENING. A shutdown, a restart or a log
        // off kills the process before it can record that it exited cleanly, so every one of them was
        // written down as an interruption - the same trace a crash leaves. Ref: #93.
        SystemEvents.SessionEnding += OnSessionEnding;
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
        SystemEvents.SessionEnding -= OnSessionEnding;
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

    /// <remarks>
    /// EVERY REASON IS THE SAME ANSWER HERE, so the reason is not read. Windows distinguishes a
    /// logoff from a system shutdown; this record only needs to say that the ending was expected
    /// rather than unexplained, and both are.
    ///
    /// IT DOES NOT CANCEL THE ENDING. `SessionEndingEventArgs.Cancel` exists and is deliberately not
    /// touched: a dictation tool must never be the reason somebody's shutdown does not happen.
    /// </remarks>
    private void OnSessionEnding(object sender, SessionEndingEventArgs args) =>
        Transitioned?.Invoke(this, SystemLifecycleTransition.SessionEnding);

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
