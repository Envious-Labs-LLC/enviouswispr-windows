using EnviousWispr.ASR;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// When the NVIDIA graphics runtime download is offered: only where the engine in use would run on the card
/// with it, never on a PC without NVIDIA, and never twice.
/// </summary>
public sealed class GraphicsRuntimeOfferTests
{
    private static readonly WhisperModelInventory WhisperInstalled = new(QuantizedComplete: true, FullPrecisionComplete: false);
    private static readonly ParakeetModelInventory ParakeetAsDelivered = new(Int8Complete: true, Fp32Complete: false);
    private static readonly ParakeetModelInventory ParakeetFullPrecision = new(Int8Complete: true, Fp32Complete: true);

    public static TheoryData<GraphicsVendor> NotNvidia => [GraphicsVendor.Amd, GraphicsVendor.Intel, GraphicsVendor.Unknown];

    [Theory]
    [MemberData(nameof(NotNvidia))]
    public void APcWithoutAnNvidiaCardIsNeverOfferedIt(GraphicsVendor vendor)
    {
        // EVERY ENGINE, EVERY MODEL STATE, INSTALLED OR NOT: nothing about the rest of the machine can make a
        // PC with no NVIDIA driver and no NVIDIA adapter worth 1.3 GB of NVIDIA libraries.
        var hardware = Snapshot(driver: false, vendor, runtimeFiles: false);
        foreach (var engine in Enum.GetValues<FinalAsrEngine>())
        {
            foreach (var installed in new[] { false, true })
            {
                Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, engine, ParakeetFullPrecision, WhisperInstalled, installed));
                Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, engine, ParakeetAsDelivered, WhisperInstalled, installed));
            }
        }
    }

    [Fact]
    public void ADriverAnsweringWithoutAnActiveNvidiaAdapterIsNotEnough()
    {
        var hardware = Snapshot(driver: true, GraphicsVendor.Intel, runtimeFiles: false);

        Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, FinalAsrEngine.Whisper, null, WhisperInstalled, runtimeInstalled: false));
    }

    [Fact]
    public void AnNvidiaCardRunningWhisperOnTheProcessorForWantOfTheRuntimeIsOffered()
    {
        var hardware = Snapshot(driver: true, GraphicsVendor.Nvidia, runtimeFiles: false);
        Assert.Equal(
            RuntimeProviderKind.Cpu,
            WhisperRuntimeSelector.Select(hardware, WhisperInstalled).Provider);

        Assert.True(GraphicsRuntimeOffer.ShouldOffer(hardware, FinalAsrEngine.Whisper, null, WhisperInstalled, runtimeInstalled: false));
    }

    [Fact]
    public void AnInstalledPackIsNotOfferedAgain()
    {
        var hardware = Snapshot(driver: true, GraphicsVendor.Nvidia, runtimeFiles: false);

        Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, FinalAsrEngine.Whisper, null, WhisperInstalled, runtimeInstalled: true));
    }

    [Fact]
    public void ACardAlreadyOnTheRuntimeFromElsewhereIsNotOffered()
    {
        // A hand-provisioned runtime folder, or the environment override: the card is already chosen.
        var hardware = Snapshot(driver: true, GraphicsVendor.Nvidia, runtimeFiles: true);

        Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, FinalAsrEngine.Whisper, null, WhisperInstalled, runtimeInstalled: false));
        Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, FinalAsrEngine.Parakeet, ParakeetFullPrecision, null, runtimeInstalled: false));
    }

    [Theory]
    [InlineData(FinalAsrEngine.Parakeet)]
    [InlineData(FinalAsrEngine.Automatic)]
    public void ParakeetAsTheStoreDeliversItWouldStayOnTheProcessorSoItIsNotOffered(FinalAsrEngine engine)
    {
        // PARAKEET STAYS ON THE PROCESSOR BY FOUNDER DECISION (2026-09-25): its full-precision model, the only
        // one that uses the card, is neither offered nor delivered. With the quantized pack, 1.3 GB would change
        // nothing, so nothing is offered; the full-precision case shows the rule still asks the selector.
        var hardware = Snapshot(driver: true, GraphicsVendor.Nvidia, runtimeFiles: false);

        Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, engine, ParakeetAsDelivered, null, runtimeInstalled: false));
        Assert.True(GraphicsRuntimeOffer.ShouldOffer(hardware, engine, ParakeetFullPrecision, null, runtimeInstalled: false));
    }

    [Fact]
    public void NoModelInventoryForTheEngineInUseIsNoOffer()
    {
        var hardware = Snapshot(driver: true, GraphicsVendor.Nvidia, runtimeFiles: false);

        Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, FinalAsrEngine.Whisper, ParakeetFullPrecision, null, runtimeInstalled: false));
        Assert.False(GraphicsRuntimeOffer.ShouldOffer(hardware, FinalAsrEngine.Parakeet, null, WhisperInstalled, runtimeInstalled: false));
    }

    private static HardwareSnapshot Snapshot(bool driver, GraphicsVendor vendor, bool runtimeFiles) => new(
        HardwareProbeStatus.Complete,
        ProcessorArchitectureKind.X64,
        ProcessorVendor.Intel,
        PhysicalCoreCount: 24,
        LogicalProcessorCount: 32,
        TotalPhysicalMemoryBytes: 64UL * 1024 * 1024 * 1024,
        GraphicsAdapters: [new GraphicsAdapterCapability(vendor, IsActive: true, IsDirectMlCandidate: true)],
        IsDirectMlRuntimeAvailable: true,
        new CudaDriverCapability(driver, driver ? 1 : 0, driver ? 13_000 : null),
        IsOnnxRuntimeCudaDependencySetAvailable: runtimeFiles,
        IsWhisperCudaDependencySetAvailable: runtimeFiles);
}
