using EnviousWispr.Core.Reliability;
using EnviousWispr.Services.Lifecycle;
using Microsoft.Win32;

namespace EnviousWispr.Architecture.Tests;

public sealed class WindowsSystemLifecycleMonitorTests
{
    [Theory]
    [InlineData(PowerModes.Suspend, SystemLifecycleTransition.Suspending)]
    [InlineData(PowerModes.Resume, SystemLifecycleTransition.Resumed)]
    public void PowerTransitionsAreMapped(PowerModes input, SystemLifecycleTransition expected)
    {
        Assert.Equal(expected, WindowsSystemLifecycleMonitor.Map(input));
    }

    [Fact]
    public void PowerStatusNoiseIsIgnored()
    {
        Assert.Null(WindowsSystemLifecycleMonitor.Map(PowerModes.StatusChange));
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionLock, SystemLifecycleTransition.SessionLocked)]
    [InlineData(SessionSwitchReason.SessionUnlock, SystemLifecycleTransition.SessionUnlocked)]
    public void SessionTransitionsAreMapped(
        SessionSwitchReason input,
        SystemLifecycleTransition expected)
    {
        Assert.Equal(expected, WindowsSystemLifecycleMonitor.Map(input));
    }

    [Fact]
    public void UnrelatedSessionTransitionsAreIgnored()
    {
        Assert.Null(WindowsSystemLifecycleMonitor.Map(SessionSwitchReason.ConsoleConnect));
    }
}
