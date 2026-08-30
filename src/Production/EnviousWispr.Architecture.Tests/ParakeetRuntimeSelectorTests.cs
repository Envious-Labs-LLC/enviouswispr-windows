using EnviousWispr.ASR;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class ParakeetRuntimeSelectorTests
{
    [Fact]
    public void AutomaticSelectsCudaOnlyWithNvidiaDriverAndQdqFreePack()
    {
        var result = ParakeetRuntimeSelector.Select(
            Snapshot(cuda: true, GraphicsVendor.Nvidia),
            new ParakeetModelInventory(Int8Complete: true, Fp32Complete: true));

        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeProviderKind.Cuda, result.Provider);
        Assert.Equal(ParakeetModelPack.FullPrecision, result.ModelPack);
        Assert.Equal(RuntimeSelectionReason.NvidiaCudaWithQdqFreeModel, result.Reason);
        Assert.Equal(1, result.IntraOpThreads);
    }

    [Fact]
    public void AutomaticFallsBackToTunedCpuWhenQdqFreePackIsMissing()
    {
        var result = ParakeetRuntimeSelector.Select(
            Snapshot(cuda: true, GraphicsVendor.Nvidia),
            new ParakeetModelInventory(Int8Complete: true, Fp32Complete: false));

        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeProviderKind.Cpu, result.Provider);
        Assert.Equal(ParakeetModelPack.Quantized, result.ModelPack);
        Assert.Equal(RuntimeSelectionReason.TunedCpuUniversalFallback, result.Reason);
        Assert.Equal(8, result.IntraOpThreads);
        Assert.Equal(1, result.InterOpThreads);
    }

    [Fact]
    public void AutomaticFallsBackToCpuWhenCudaDependencySetIsIncomplete()
    {
        var result = ParakeetRuntimeSelector.Select(
            Snapshot(cuda: true, GraphicsVendor.Nvidia) with
            {
                IsOnnxRuntimeCudaDependencySetAvailable = false,
            },
            new ParakeetModelInventory(Int8Complete: true, Fp32Complete: true));

        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeProviderKind.Cpu, result.Provider);
        Assert.Equal(RuntimeSelectionReason.TunedCpuUniversalFallback, result.Reason);
    }

    [Fact]
    public void ManualCudaRejectsMissingQdqFreeModelInsteadOfUsingSlowQdqGraph()
    {
        var result = ParakeetRuntimeSelector.Select(
            Snapshot(cuda: true, GraphicsVendor.Nvidia),
            new ParakeetModelInventory(Int8Complete: true, Fp32Complete: false),
            RuntimeProviderPreference.Cuda);

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeSelectionReason.RequiredModelPackMissing, result.Reason);
        Assert.Equal(AppErrorCode.ModelPackUnavailable, result.Error?.Code);
    }

    [Theory]
    [InlineData(GraphicsVendor.Amd)]
    [InlineData(GraphicsVendor.Intel)]
    [InlineData(GraphicsVendor.Nvidia)]
    public void DirectMlIsDiscoveredButRejectedForParakeetDecoder(GraphicsVendor vendor)
    {
        var result = ParakeetRuntimeSelector.Select(
            Snapshot(cuda: false, vendor),
            new ParakeetModelInventory(Int8Complete: true, Fp32Complete: true),
            RuntimeProviderPreference.DirectMl);

        Assert.False(result.Succeeded);
        Assert.Equal(
            RuntimeSelectionReason.DirectMlIncompatibleWithParakeetDecoder,
            result.Reason);
        Assert.Equal(AppErrorCode.RuntimeProviderIncompatible, result.Error?.Code);
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    [InlineData(24, 8)]
    public void CpuThreadPolicyIsBoundedByMeasuredHybridSafeRange(int physicalCores, int expected)
    {
        var hardware = Snapshot(cuda: false, GraphicsVendor.Intel) with
        {
            PhysicalCoreCount = physicalCores,
            LogicalProcessorCount = physicalCores * 2,
        };

        Assert.Equal(expected, ParakeetRuntimeSelector.ChooseCpuIntraOpThreads(hardware));
    }

    [Fact]
    public void UnsupportedArchitectureFailsBeforeProviderSelection()
    {
        var hardware = Snapshot(cuda: true, GraphicsVendor.Nvidia) with
        {
            Architecture = ProcessorArchitectureKind.Arm64,
        };

        var result = ParakeetRuntimeSelector.Select(
            hardware,
            new ParakeetModelInventory(Int8Complete: true, Fp32Complete: true));

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeSelectionReason.UnsupportedProcessorArchitecture, result.Reason);
    }

    private static HardwareSnapshot Snapshot(bool cuda, GraphicsVendor vendor) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Intel,
        PhysicalCoreCount: 24,
        LogicalProcessorCount: 32,
        TotalPhysicalMemoryBytes: 64UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [new GraphicsAdapterCapability(vendor, true, true)],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(cuda, cuda ? 1 : 0, cuda ? 13_000 : null),
        IsOnnxRuntimeCudaDependencySetAvailable: cuda);
}
