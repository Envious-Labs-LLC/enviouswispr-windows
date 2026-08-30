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
    bool IsOnnxRuntimeCudaDependencySetAvailable,
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

public sealed record WhisperModelInventory(
    bool QuantizedComplete,
    bool FullPrecisionComplete,
    bool PreviewSmallComplete = false);

public interface IWhisperModelProbe
{
    WhisperModelInventory Probe(string modelDirectory);
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

public enum WhisperModelPack
{
    Quantized,
    FullPrecision,
    PreviewSmall,
}

public static class WhisperModelFileNames
{
    public const string Quantized = "ggml-large-v3-turbo-q5_0.bin";
    public const string FullPrecision = "ggml-large-v3-turbo.bin";
    public const string PreviewSmall = "ggml-small-q5_1.bin";

    public static string For(WhisperModelPack modelPack) => modelPack switch
    {
        WhisperModelPack.Quantized => Quantized,
        WhisperModelPack.FullPrecision => FullPrecision,
        WhisperModelPack.PreviewSmall => PreviewSmall,
        _ => throw new ArgumentOutOfRangeException(nameof(modelPack)),
    };
}

public static class ParakeetModelIds
{
    public const string Final = "parakeet-tdt-0.6b-v3";
}

public static class WhisperModelIds
{
    public const string Final = "whisper-large-v3-turbo";
    public const string Preview = "whisper-small";

    public static string For(WhisperModelPack modelPack) => modelPack switch
    {
        WhisperModelPack.PreviewSmall => Preview,
        WhisperModelPack.Quantized or WhisperModelPack.FullPrecision => Final,
        _ => throw new ArgumentOutOfRangeException(nameof(modelPack)),
    };
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

public enum WhisperRuntimeSelectionReason
{
    NvidiaCudaWithFullPrecisionModel,
    NvidiaCudaWithQuantizedModel,
    TunedCpuWithQuantizedModel,
    TunedCpuWithFullPrecisionModel,
    ManualProviderAccepted,
    RequestedProviderUnavailable,
    RequiredModelPackMissing,
    UnsupportedProcessorArchitecture,
}

public sealed record WhisperRuntimeSelection(
    bool Succeeded,
    RuntimeProviderKind? Provider,
    WhisperModelPack? ModelPack,
    int ThreadCount,
    WhisperRuntimeSelectionReason Reason,
    AppError? Error = null);
