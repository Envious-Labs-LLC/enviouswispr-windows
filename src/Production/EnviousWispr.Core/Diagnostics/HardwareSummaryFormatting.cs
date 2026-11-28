using System.Globalization;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Core.Diagnostics;

/// <summary>Shared hardware summary and GPU/CPU suffix formatting across Storage and Presentation.</summary>
public static class HardwareSummaryFormatting
{
    public static string FormatSummary(
        double totalRamGb,
        GpuAccelerationKind gpuKind,
        double? dedicatedVramGb,
        int physicalCoreCount) =>
        FormatMemoryPrefix(totalRamGb) + FormatGpuOrCpuSuffix(gpuKind, dedicatedVramGb, physicalCoreCount);

    public static string FormatMemoryPrefix(double totalRamGb)
    {
        if (!HasPositiveFiniteValue(totalRamGb))
        {
            return "This PC: Local Memory";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "This PC: {0} GB RAM",
            RoundPositiveWhole(totalRamGb));
    }

    public static string FormatGpuOrCpuSuffix(
        GpuAccelerationKind gpuKind,
        double? dedicatedVramGb,
        int physicalCoreCount) =>
        gpuKind switch
        {
            GpuAccelerationKind.NvidiaCuda when HasPositiveFiniteValue(dedicatedVramGb) =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    " \u00B7 NVIDIA GPU ({0} GB VRAM)",
                    RoundPositiveWhole(dedicatedVramGb!.Value)),
            GpuAccelerationKind.NvidiaCuda => " \u00B7 NVIDIA GPU",
            GpuAccelerationKind.DiscreteVulkan when HasPositiveFiniteValue(dedicatedVramGb) =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    " \u00B7 Discrete GPU ({0} GB VRAM)",
                    RoundPositiveWhole(dedicatedVramGb!.Value)),
            GpuAccelerationKind.DiscreteVulkan => " \u00B7 Discrete GPU",
            GpuAccelerationKind.IntegratedVulkan => " \u00B7 Integrated GPU",
            GpuAccelerationKind.None when physicalCoreCount > 0 =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    " \u00B7 {0}-core CPU",
                    physicalCoreCount),
            _ => string.Empty,
        };

    public static bool HasPositiveFiniteValue(double? value) =>
        value is { } actual && actual > 0d && double.IsFinite(actual);

    public static int RoundPositiveWhole(double value) =>
        Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
}
