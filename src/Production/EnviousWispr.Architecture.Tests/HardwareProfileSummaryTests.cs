using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

public sealed class HardwareProfileSummaryTests
{
    [Theory]
    [InlineData(64d, GpuAccelerationKind.NvidiaCuda, 24d, 24, "This PC: 64 GB RAM \u00B7 NVIDIA GPU (24 GB VRAM)")]
    [InlineData(32d, GpuAccelerationKind.NvidiaCuda, null, 16, "This PC: 32 GB RAM \u00B7 NVIDIA GPU")]
    [InlineData(24d, GpuAccelerationKind.DiscreteVulkan, 8d, 12, "This PC: 24 GB RAM \u00B7 Discrete GPU (8 GB VRAM)")]
    [InlineData(16d, GpuAccelerationKind.IntegratedVulkan, null, 8, "This PC: 16 GB RAM \u00B7 Integrated GPU")]
    [InlineData(16d, GpuAccelerationKind.None, null, 8, "This PC: 16 GB RAM \u00B7 8-core CPU")]
    [InlineData(0d, GpuAccelerationKind.None, null, 0, "This PC: Local Memory")]
    public void FormatSummaryProducesContentFreeStringsWithoutDashes(
        double totalRamGb,
        GpuAccelerationKind gpuKind,
        double? dedicatedVramGb,
        int physicalCoreCount,
        string expected)
    {
        var formatted = HardwareProfileSummary.FormatSummary(totalRamGb, gpuKind, dedicatedVramGb, physicalCoreCount);

        Assert.Equal(expected, formatted);
        Assert.DoesNotContain('\u2014', formatted);
        Assert.DoesNotContain('\u2013', formatted);
    }

    [Fact]
    public void TryParseUatOverrideParsesLowSpecAndHighSpecProfilesDeterministically()
    {
        Assert.True(HardwareProfileSummary.TryParseUatOverride(
            "ram=8;gpu=none;cores=4",
            ProcessorArchitectureKind.X64,
            120d,
            out var lowSpec));
        Assert.NotNull(lowSpec);
        Assert.Equal(8d, lowSpec.TotalRamGb);
        Assert.False(lowSpec.HasNvidiaGpu);
        Assert.False(lowSpec.HasVulkanGpu);
        Assert.Equal(GpuAccelerationKind.None, lowSpec.GpuKind);
        Assert.Null(lowSpec.DedicatedVramGb);
        Assert.Equal(4, lowSpec.PhysicalCoreCount);
        Assert.Equal("This PC: 8 GB RAM \u00B7 4-core CPU", lowSpec.SummaryText);
        Assert.Equal(EgOneModelCatalog.MicroId, lowSpec.RecommendedEgOneModelId);
        Assert.Equal(120d, lowSpec.AvailableDiskGb);

        Assert.True(HardwareProfileSummary.TryParseUatOverride(
            "ram=64;gpu=nvidia;vram=24;cores=24;pcores=8;arch=x64;disk=450",
            ProcessorArchitectureKind.Unknown,
            null,
            out var workstation));
        Assert.NotNull(workstation);
        Assert.Equal(64d, workstation.TotalRamGb);
        Assert.True(workstation.HasNvidiaGpu);
        Assert.True(workstation.HasVulkanGpu);
        Assert.Equal(GpuAccelerationKind.NvidiaCuda, workstation.GpuKind);
        Assert.Equal(24d, workstation.DedicatedVramGb);
        Assert.Equal(24, workstation.PhysicalCoreCount);
        Assert.Equal(8, workstation.PerformanceCoreCount);
        Assert.Equal(ProcessorArchitectureKind.X64, workstation.Architecture);
        Assert.Equal(450d, workstation.AvailableDiskGb);
        Assert.Equal("This PC: 64 GB RAM \u00B7 NVIDIA GPU (24 GB VRAM)", workstation.SummaryText);
        Assert.Equal(EgOneModelCatalog.FullId, workstation.RecommendedEgOneModelId);
    }

    [Fact]
    public void GetCurrentHonorsUatHardwareProfileEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable(HardwareProfileSummary.UatHardwareProfileEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                HardwareProfileSummary.UatHardwareProfileEnvVar,
                "ram=24;gpu=discrete;vram=8;cores=12");

            var profile = HardwareProfileSummary.GetCurrent();
            Assert.Equal(24d, profile.TotalRamGb);
            Assert.Equal(GpuAccelerationKind.DiscreteVulkan, profile.GpuKind);
            Assert.Equal(8d, profile.DedicatedVramGb);
            Assert.Equal(12, profile.PhysicalCoreCount);
            Assert.Equal(EgOneModelCatalog.FullId, profile.RecommendedEgOneModelId);
            Assert.Equal("This PC: 24 GB RAM \u00B7 Discrete GPU (8 GB VRAM)", profile.SummaryText);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HardwareProfileSummary.UatHardwareProfileEnvVar, previous);
        }
    }

    [Fact]
    public void HardwareProfileExposesOnlyContentFreeProperties()
    {
        string[] forbiddenTokens = ["name", "identifier", "deviceid", "path", "serial"];
        var propertyNames = typeof(HardwareProfile).GetProperties().Select(p => p.Name.ToLowerInvariant());

        Assert.All(propertyNames, propertyName =>
            Assert.All(forbiddenTokens, token =>
                Assert.DoesNotContain(token, propertyName, StringComparison.Ordinal)));
    }
}
