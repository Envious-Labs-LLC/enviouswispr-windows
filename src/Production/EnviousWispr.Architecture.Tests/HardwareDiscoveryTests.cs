using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class HardwareDiscoveryTests
{
    [Fact]
    public async Task RealProbeReturnsContentFreeConsistentCapabilities()
    {
        var discovery = new WindowsHardwareDiscovery();

        var result = await discovery.ProbeAsync();

        Assert.NotEqual(ProcessorArchitectureKind.Unknown, result.Architecture);
        Assert.True(result.PhysicalCoreCount > 0);
        Assert.True(result.LogicalProcessorCount >= result.PhysicalCoreCount);
        Assert.True(result.TotalPhysicalMemoryBytes > 0);
        Assert.All(result.GraphicsAdapters, adapter =>
        {
            Assert.True(Enum.IsDefined(adapter.Vendor));
            Assert.Equal(
                adapter.IsActive && adapter.Vendor != GraphicsVendor.Unknown,
                adapter.IsDirectMlCandidate);
        });
        Assert.True(result.Cuda.DeviceCount >= 0);
        Assert.Equal(result.Cuda.DeviceCount > 0, result.Cuda.IsDriverAvailable);
    }

    [Fact]
    public void HardwareContractsCannotHoldDeviceNamesIdentifiersOrPaths()
    {
        var propertyNames = typeof(HardwareSnapshot).GetProperties()
            .Concat(typeof(GraphicsAdapterCapability).GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("name", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("identifier", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("deviceId", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("serial", StringComparison.OrdinalIgnoreCase));
    }
}
