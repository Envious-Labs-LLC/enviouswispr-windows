using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Core.Compatibility;

public enum PhysicalMemoryTier
{
    Below8GiB,
    From8To15GiB,
    From16To31GiB,
    From32To63GiB,
    AtLeast64GiB,
}

public enum DisplayScaleTier
{
    Below100Percent,
    From100To124Percent,
    From125To149Percent,
    From150To199Percent,
    AtLeast200Percent,
}

public enum PrimaryResolutionTier
{
    Below1080p,
    FullHd,
    QuadHd,
    FourKOrHigher,
}

public sealed record WindowsCompatibilitySnapshot(
    int SchemaVersion,
    int OperatingSystemBuild,
    int OperatingSystemRevision,
    bool IsWindows11OrLater,
    ProcessorArchitectureKind Architecture,
    ProcessorVendor ProcessorVendor,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    PhysicalMemoryTier MemoryTier,
    IReadOnlyList<GraphicsVendor> ActiveGraphicsVendors,
    bool IsDirectMlRuntimeAvailable,
    bool IsCudaDriverAvailable,
    bool IsOnnxRuntimeCudaDependencySetAvailable,
    int CudaDeviceCount,
    int? CudaDriverVersion,
    int DisplayCount,
    DisplayScaleTier PrimaryDisplayScale,
    PrimaryResolutionTier PrimaryResolution,
    int CaptureDeviceCount,
    bool HasDefaultCaptureDevice,
    bool EndpointSecurityProbeAvailable,
    int EndpointSecurityProviderCount,
    HardwareProbeStatus HardwareProbeStatus);

public static class CompatibilityBuckets
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    public static PhysicalMemoryTier ClassifyMemory(ulong bytes) => bytes switch
    {
        < 8 * GiB => PhysicalMemoryTier.Below8GiB,
        < 16 * GiB => PhysicalMemoryTier.From8To15GiB,
        < 32 * GiB => PhysicalMemoryTier.From16To31GiB,
        < 64 * GiB => PhysicalMemoryTier.From32To63GiB,
        _ => PhysicalMemoryTier.AtLeast64GiB,
    };

    public static DisplayScaleTier ClassifyDisplayScale(uint dpi)
    {
        var percent = checked((int)Math.Round(dpi * 100d / 96d));
        return percent switch
        {
            < 100 => DisplayScaleTier.Below100Percent,
            < 125 => DisplayScaleTier.From100To124Percent,
            < 150 => DisplayScaleTier.From125To149Percent,
            < 200 => DisplayScaleTier.From150To199Percent,
            _ => DisplayScaleTier.AtLeast200Percent,
        };
    }

    public static PrimaryResolutionTier ClassifyResolution(int width, int height)
    {
        var shortEdge = Math.Min(width, height);
        var longEdge = Math.Max(width, height);
        if (shortEdge >= 2160 && longEdge >= 3840)
        {
            return PrimaryResolutionTier.FourKOrHigher;
        }

        if (shortEdge >= 1440 && longEdge >= 2560)
        {
            return PrimaryResolutionTier.QuadHd;
        }

        return shortEdge >= 1080 && longEdge >= 1920
            ? PrimaryResolutionTier.FullHd
            : PrimaryResolutionTier.Below1080p;
    }
}
