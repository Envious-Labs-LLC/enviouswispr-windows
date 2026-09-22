using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Live Preview picks its processor by asking about the files its engine actually runs on.</summary>
public sealed class WhisperPreviewRuntimeTests
{
    /// <summary>The case #163 reported: a working card, but no CUDA runtime files.</summary>
    /// <remarks>
    /// A machine can have a working driver and a device and still not have the `runtime/cuda` folder
    /// (the cuBLAS/cuDNN/cuDART files whisper.cpp loads). Selecting the card there starts a worker
    /// that cannot load its runtime, so the preview comes up red. The preview must stand down and take
    /// the processor instead, so it agrees with the final engine about what this machine can run.
    /// Ref: #163.
    /// </remarks>
    [Fact]
    public void AWorkingCardWithoutTheCudaRuntimeFilesIsNotACard()
    {
        var hardware = Hardware(cuda: true, cudaRuntimeFiles: false);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>A working card WITH the CUDA runtime files is used.</summary>
    [Fact]
    public void AWorkingCardWithTheCudaRuntimeFilesIsACard()
    {
        var hardware = Hardware(cuda: true, cudaRuntimeFiles: true);

        Assert.Equal(RuntimeProviderKind.Cuda, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>The presence of the runtime files never promotes a machine with no card.</summary>
    [Fact]
    public void TheCudaRuntimeFilesDoNotPromoteAMachineWithNoCard()
    {
        var hardware = Hardware(cuda: false, cudaRuntimeFiles: true);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>A driver with no device is not a card.</summary>
    [Fact]
    public void ADriverWithNoDeviceIsNotACard()
    {
        var hardware = Hardware(cuda: true, cudaRuntimeFiles: true) with
        {
            Cuda = new CudaDriverCapability(IsDriverAvailable: true, DeviceCount: 0, DriverVersion: 13_000),
        };

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>The preview stands down when the final engine already fell back.</summary>
    [Fact]
    public void ThePreviewDoesNotClaimACardTheFinalEngineAlreadyFailedToGet()
    {
        var hardware = Hardware(cuda: true, cudaRuntimeFiles: true);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware, forceCpu: true));
    }

    [Fact]
    public void AnUnsupportedProcessorArchitectureStaysOnTheProcessor()
    {
        var hardware = Hardware(cuda: true, cudaRuntimeFiles: true) with
        {
            Architecture = ProcessorArchitectureKind.Arm64,
        };

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>The preview and the final Whisper engine agree about this machine.</summary>
    /// <remarks>
    /// TWO ANSWERS TO ONE QUESTION WAS THE DEFECT, so the fix is worth asserting as an agreement
    /// rather than only as a corrected condition. If the final selector's rule changes and this one
    /// does not, they part company again and this fails.
    /// </remarks>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ThePreviewAgreesWithTheFinalWhisperEngineAboutTheCard(
        bool cuda,
        bool cudaRuntimeFiles)
    {
        var hardware = Hardware(cuda, cudaRuntimeFiles);
        var final = WhisperRuntimeSelector.Select(
            hardware,
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: true));

        Assert.Equal(final.Provider, WhisperPreviewRuntime.Select(hardware));
    }

    private static HardwareSnapshot Hardware(bool cuda, bool cudaRuntimeFiles) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Intel,
        PhysicalCoreCount: 24,
        LogicalProcessorCount: 32,
        TotalPhysicalMemoryBytes: 64UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(cuda, cuda ? 1 : 0, cuda ? 13_000 : null),
        cudaRuntimeFiles);
}
