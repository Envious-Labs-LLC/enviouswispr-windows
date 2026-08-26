using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Runtime;

public enum ProcessorArchitectureKind
{
    Unknown,
    X64,
    Arm64,
}

public enum ProcessorVendor
{
    Unknown,
    Intel,
    Amd,
    Qualcomm,
}

public enum GraphicsVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

public enum HardwareProbeStatus
{
    Complete,
    Partial,
}

public sealed record GraphicsAdapterCapability(
    GraphicsVendor Vendor,
    bool IsActive,
    bool IsDirectMlCandidate);

public sealed record CudaDriverCapability(
    bool IsDriverAvailable,
    int DeviceCount,
    int? DriverVersion);

public sealed record HardwareSnapshot(
    HardwareProbeStatus Status,
    ProcessorArchitectureKind Architecture,
    ProcessorVendor ProcessorVendor,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    ulong TotalPhysicalMemoryBytes,
    IReadOnlyList<GraphicsAdapterCapability> GraphicsAdapters,
    bool IsDirectMlRuntimeAvailable,
    CudaDriverCapability Cuda,
    AppError? Error = null)
{
    public bool HasActiveAdapter(GraphicsVendor vendor) =>
        GraphicsAdapters.Any(adapter => adapter.IsActive && adapter.Vendor == vendor);

    public bool HasDirectMlCandidate(GraphicsVendor vendor) =>
        IsDirectMlRuntimeAvailable &&
        GraphicsAdapters.Any(adapter =>
            adapter.IsActive && adapter.IsDirectMlCandidate && adapter.Vendor == vendor);
}

public interface IHardwareDiscovery
{
    Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record ParakeetModelInventory(bool Int8Complete, bool Fp32Complete);

public interface IParakeetModelProbe
{
    ParakeetModelInventory Probe(string modelDirectory);
}

public enum RuntimeProviderPreference
{
    Automatic,
    Cpu,
    Cuda,
    DirectMl,
}

public enum RuntimeProviderKind
{
    Cpu,
    Cuda,
    DirectMl,
}

public enum ParakeetModelPack
{
    Quantized,
    FullPrecision,
}

public enum RuntimeSelectionReason
{
    NvidiaCudaWithQdqFreeModel,
    TunedCpuUniversalFallback,
    ManualProviderAccepted,
    RequestedProviderUnavailable,
    RequiredModelPackMissing,
    DirectMlIncompatibleWithParakeetDecoder,
    UnsupportedProcessorArchitecture,
}

public sealed record RuntimeSelection(
    bool Succeeded,
    RuntimeProviderKind? Provider,
    ParakeetModelPack? ModelPack,
    int IntraOpThreads,
    int InterOpThreads,
    RuntimeSelectionReason Reason,
    AppError? Error = null);
