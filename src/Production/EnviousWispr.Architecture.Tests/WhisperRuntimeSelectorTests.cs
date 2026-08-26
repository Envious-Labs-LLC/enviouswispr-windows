using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class WhisperRuntimeSelectorTests
{
    [Fact]
    public void AutomaticNvidiaPrefersCudaAndFullPrecisionModel()
    {
        var result = WhisperRuntimeSelector.Select(
            Hardware(cuda: true),
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: true));

        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeProviderKind.Cuda, result.Provider);
        Assert.Equal(WhisperModelPack.FullPrecision, result.ModelPack);
        Assert.Equal(WhisperRuntimeSelectionReason.NvidiaCudaWithFullPrecisionModel, result.Reason);
    }

    [Fact]
    public void AutomaticCpuPrefersQuantizedModel()
    {
        var result = WhisperRuntimeSelector.Select(
            Hardware(cuda: false),
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: true));

        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeProviderKind.Cpu, result.Provider);
        Assert.Equal(WhisperModelPack.Quantized, result.ModelPack);
        Assert.Equal(8, result.ThreadCount);
    }

    [Fact]
    public void RequestedUnavailableCudaReturnsTypedFailure()
    {
        var result = WhisperRuntimeSelector.Select(
            Hardware(cuda: false),
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: false),
            RuntimeProviderPreference.Cuda);

        Assert.False(result.Succeeded);
        Assert.Equal(WhisperRuntimeSelectionReason.RequestedProviderUnavailable, result.Reason);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void DirectMlIsNotClaimedAsWhisperCppProvider()
    {
        var result = WhisperRuntimeSelector.Select(
            Hardware(cuda: false),
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: false),
            RuntimeProviderPreference.DirectMl);

        Assert.False(result.Succeeded);
        Assert.Equal(WhisperRuntimeSelectionReason.RequestedProviderUnavailable, result.Reason);
    }

    private static HardwareSnapshot Hardware(bool cuda) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Intel,
        PhysicalCoreCount: 24,
        LogicalProcessorCount: 32,
        TotalPhysicalMemoryBytes: 64UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(cuda, cuda ? 1 : 0, cuda ? 13_000 : null));
}
