using EnviousWispr.Core.Reliability;
using EnviousWispr.Services.Reliability;

namespace EnviousWispr.Architecture.Tests;

public sealed class SystemResourceAdmissionPolicyTests
{
    [Fact]
    public void CriticalMemoryPressureRefusesBeforeCaptureAllocation()
    {
        var result = SystemResourceAdmissionPolicy.Evaluate(new SystemResourceSnapshot(
            AvailableDiskBytes: 10L * 1024 * 1024 * 1024,
            AvailablePhysicalMemoryBytes:
                SystemResourceAdmissionPolicy.MinimumDictationMemoryBytes - 1,
            MemoryLoadPercent: 99));

        Assert.Equal(DictationAdmissionStatus.LowMemory, result.Status);
        Assert.False(result.CanStart);
    }

    [Fact]
    public void LowDiskKeepsDictationUsableButDisablesDurableRecovery()
    {
        var result = SystemResourceAdmissionPolicy.Evaluate(new SystemResourceSnapshot(
            AvailableDiskBytes: SystemResourceAdmissionPolicy.MinimumRecoveryDiskBytes - 1,
            AvailablePhysicalMemoryBytes: 2UL * 1024 * 1024 * 1024,
            MemoryLoadPercent: 50));

        Assert.Equal(DictationAdmissionStatus.LowDisk, result.Status);
        Assert.True(result.CanStart);
        Assert.False(result.CanPersistRecovery);
    }

    [Fact]
    public void ProbeFailureDoesNotDisableAnOtherwiseUsableApp()
    {
        var result = SystemResourceAdmissionPolicy.Evaluate(new SystemResourceSnapshot(
            AvailableDiskBytes: 0,
            AvailablePhysicalMemoryBytes: 0,
            MemoryLoadPercent: 0,
            IsAvailable: false));

        Assert.Equal(DictationAdmissionStatus.Unavailable, result.Status);
        Assert.True(result.CanStart);
    }

    [Fact]
    public void NativeProbeReturnsAWellFormedSnapshotForTheCurrentDataDrive()
    {
        var snapshot = new WindowsSystemResourceProbe(Path.GetTempPath()).Probe();

        Assert.True(snapshot.IsAvailable);
        Assert.True(snapshot.AvailableDiskBytes > 0);
        Assert.True(snapshot.AvailablePhysicalMemoryBytes > 0);
        Assert.InRange(snapshot.MemoryLoadPercent, 0u, 100u);
    }
}
