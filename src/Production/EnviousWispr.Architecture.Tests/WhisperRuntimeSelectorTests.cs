using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class WhisperRuntimeSelectorTests
{
    [Fact]
    public void AutomaticNvidiaPrefersMeasuredQuantizedModel()
    {
        var result = WhisperRuntimeSelector.Select(
            Hardware(cuda: true),
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: true));

        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeProviderKind.Cuda, result.Provider);
        Assert.Equal(WhisperModelPack.Quantized, result.ModelPack);
        Assert.Equal(WhisperRuntimeSelectionReason.NvidiaCudaWithQuantizedModel, result.Reason);
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

    /// <summary>Asking for the card by name: only the device count and whisper.cpp's own files decide.</summary>
    [Theory]
    [InlineData(1, true, false, true)]
    [InlineData(2, true, false, true)]
    [InlineData(1, true, true, true)]
    [InlineData(2, false, true, false)]
    [InlineData(1, false, false, false)]
    [InlineData(0, true, true, false)]
    public void RequestedCudaFollowsTheCardAndWhispersOwnFiles(
        int deviceCount,
        bool whisperFiles,
        bool onnxRuntimeFiles,
        bool accepted)
    {
        var hardware = Hardware(cuda: true) with
        {
            Cuda = new CudaDriverCapability(IsDriverAvailable: true, deviceCount, DriverVersion: 13_000),
            IsWhisperCudaDependencySetAvailable = whisperFiles,
            IsOnnxRuntimeCudaDependencySetAvailable = onnxRuntimeFiles,
        };

        var result = WhisperRuntimeSelector.Select(
            hardware,
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: false),
            RuntimeProviderPreference.Cuda);

        Assert.Equal(accepted, result.Succeeded);
        Assert.Equal(
            accepted ? WhisperRuntimeSelectionReason.ManualProviderAccepted : WhisperRuntimeSelectionReason.RequestedProviderUnavailable,
            result.Reason);
    }

    /// <summary>A card whose whisper.cpp files are missing is refused when asked for by name.</summary>
    [Fact]
    public void RequestedCudaWithoutWhispersFilesReturnsTypedFailure()
    {
        var hardware = Hardware(cuda: true) with { IsWhisperCudaDependencySetAvailable = false };

        var result = WhisperRuntimeSelector.Select(
            hardware,
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: false),
            RuntimeProviderPreference.Cuda);

        Assert.False(result.Succeeded);
        Assert.Equal(WhisperRuntimeSelectionReason.RequestedProviderUnavailable, result.Reason);
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
        new CudaDriverCapability(cuda, cuda ? 1 : 0, cuda ? 13_000 : null),
        IsOnnxRuntimeCudaDependencySetAvailable: false,
        IsWhisperCudaDependencySetAvailable: cuda);
}
