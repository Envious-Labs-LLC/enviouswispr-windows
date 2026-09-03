using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Live Preview picks its processor by asking about the library it actually runs.</summary>
public sealed class WhisperPreviewRuntimeTests
{
    /// <summary>The case the old condition got wrong.</summary>
    /// <remarks>
    /// THIS IS THE WHOLE DEFECT, AND IT IS THE ONLY CASE THAT CHANGES. Live Preview runs whisper.cpp,
    /// which ships its own CUDA build. The decision used to require onnxruntime's CUDA dependency set
    /// as well - the library PARAKEET uses - so a machine with a working card and no onnxruntime
    /// files was put on the processor for a reason that had nothing to do with the engine running
    /// there. On the development machine both probes are true, so this could never have been found by
    /// running it; it needed the two to disagree, which is what this test is. Ref: #99.
    /// </remarks>
    [Fact]
    public void AWorkingCardIsUsedEvenWhenTheOtherRuntimesDependenciesAreMissing()
    {
        var hardware = Hardware(cuda: true, onnxRuntimeCudaDependencies: false);

        Assert.Equal(RuntimeProviderKind.Cuda, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>And the other library's presence never promotes a machine with no card.</summary>
    [Fact]
    public void TheOtherRuntimesDependenciesDoNotPromoteAMachineWithNoCard()
    {
        var hardware = Hardware(cuda: false, onnxRuntimeCudaDependencies: true);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware));
    }

    /// <summary>A driver with no device is not a card.</summary>
    [Fact]
    public void ADriverWithNoDeviceIsNotACard()
    {
        var hardware = Hardware(cuda: true, onnxRuntimeCudaDependencies: true) with
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
        var hardware = Hardware(cuda: true, onnxRuntimeCudaDependencies: true);

        Assert.Equal(RuntimeProviderKind.Cpu, WhisperPreviewRuntime.Select(hardware, forceCpu: true));
    }

    [Fact]
    public void AnUnsupportedProcessorArchitectureStaysOnTheProcessor()
    {
        var hardware = Hardware(cuda: true, onnxRuntimeCudaDependencies: true) with
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
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ThePreviewAgreesWithTheFinalWhisperEngineAboutTheCard(
        bool cuda,
        bool onnxRuntimeCudaDependencies)
    {
        var hardware = Hardware(cuda, onnxRuntimeCudaDependencies);
        var final = WhisperRuntimeSelector.Select(
            hardware,
            new WhisperModelInventory(QuantizedComplete: true, FullPrecisionComplete: true));

        Assert.Equal(final.Provider, WhisperPreviewRuntime.Select(hardware));
    }

    private static HardwareSnapshot Hardware(bool cuda, bool onnxRuntimeCudaDependencies) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Intel,
        PhysicalCoreCount: 24,
        LogicalProcessorCount: 32,
        TotalPhysicalMemoryBytes: 64UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(cuda, cuda ? 1 : 0, cuda ? 13_000 : null),
        onnxRuntimeCudaDependencies);
}
