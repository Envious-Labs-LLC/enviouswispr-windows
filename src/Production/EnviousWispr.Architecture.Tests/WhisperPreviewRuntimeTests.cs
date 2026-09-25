using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Live Preview picks its processor by asking about the files its own engine loads.</summary>
public sealed class WhisperPreviewRuntimeTests
{
    /// <summary>The #163 case: a working card, but none of the files whisper.cpp loads.</summary>
    /// <remarks>
    /// Selecting the card here starts a preview worker that cannot load cuBLAS, so the preview never
    /// appears. The preview must take the processor instead.
    /// </remarks>
    [Fact]
    public void AWorkingCardWithoutWhispersCudaFilesIsNotACard()
    {
        var hardware = Hardware(cuda: true, whisperFiles: false, onnxRuntimeFiles: false);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>The #99 case: whisper.cpp's files are present and onnxruntime's are not.</summary>
    /// <remarks>
    /// THE ONNXRUNTIME SET IS PARAKEET'S, and it needs cuDNN, which whisper.cpp never loads. Requiring
    /// it put a card whisper.cpp could use on the processor. This is the case where the two sets
    /// disagree, which is the only case that can tell the right question from the wrong one.
    /// </remarks>
    [Fact]
    public void AWorkingCardIsUsedEvenWhenTheOtherRuntimesFilesAreMissing()
    {
        var hardware = Hardware(cuda: true, whisperFiles: true, onnxRuntimeFiles: false);

        Assert.Equal(RuntimeProviderKind.Cuda, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>And the other runtime's files never stand in for whisper.cpp's own.</summary>
    [Fact]
    public void TheOtherRuntimesFilesDoNotPromoteACardWithoutWhispersFiles()
    {
        var hardware = Hardware(cuda: true, whisperFiles: false, onnxRuntimeFiles: true);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>The files never promote a machine with no card.</summary>
    [Fact]
    public void TheFilesDoNotPromoteAMachineWithNoCard()
    {
        var hardware = Hardware(cuda: false, whisperFiles: true, onnxRuntimeFiles: true);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>A driver that reports no device is not a card either.</summary>
    [Fact]
    public void ADriverWithNoDeviceIsNotACard()
    {
        var hardware = Hardware(cuda: true, whisperFiles: true, onnxRuntimeFiles: true) with
        {
            Cuda = new CudaDriverCapability(IsDriverAvailable: true, DeviceCount: 0, DriverVersion: 13_000),
        };

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>The preview stands down when the final engine already fell back.</summary>
    /// <remarks>
    /// NOT POLITENESS. The final engine falling back means it tried the card and failed; a preview
    /// that then claimed the card would be asking for something already proved unavailable, on the
    /// same machine, for the same reason.
    /// </remarks>
    [Fact]
    public void ThePreviewDoesNotClaimACardTheFinalEngineAlreadyFailedToGet()
    {
        var hardware = Hardware(cuda: true, whisperFiles: true, onnxRuntimeFiles: true);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware, forceCpu: true));
    }

    [Fact]
    public void AnUnsupportedProcessorArchitectureStaysOnTheProcessor()
    {
        var hardware = Hardware(cuda: true, whisperFiles: true, onnxRuntimeFiles: true) with
        {
            Architecture = ProcessorArchitectureKind.Arm64,
        };

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>The preview and the final Whisper engine agree about this machine.</summary>
    /// <remarks>
    /// TWO ANSWERS TO ONE QUESTION WAS THE DEFECT, so the fix is worth asserting as an agreement
    /// rather than only as a corrected condition. If the final selector's rule changes and this one
    /// does not, they part company again and this fails. Every combination of card and both file sets.
    /// </remarks>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void ThePreviewAgreesWithTheFinalWhisperEngineAboutTheCard(
        bool cuda,
        bool whisperFiles,
        bool onnxRuntimeFiles)
    {
        var hardware = Hardware(cuda, whisperFiles, onnxRuntimeFiles);
        var final = WhisperRuntimeSelector.Select(
            hardware,
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: true));

        Assert.Equal(final.Provider, WhisperPreviewRuntime.Select(hardware));
    }

    private static HardwareSnapshot Hardware(bool cuda, bool whisperFiles, bool onnxRuntimeFiles) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Intel,
        PhysicalCoreCount: 24,
        LogicalProcessorCount: 32,
        TotalPhysicalMemoryBytes: 64UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(cuda, cuda ? 1 : 0, cuda ? 13_000 : null),
        IsOnnxRuntimeCudaDependencySetAvailable: onnxRuntimeFiles,
        IsWhisperCudaDependencySetAvailable: whisperFiles);
}
