using System.Globalization;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Polish;

namespace EnviousWispr.Services.Diagnostics;

/// <summary>Reads current Windows physical RAM and in-use bytes with UAT environment override support.</summary>
public static class SystemMemoryReader
{
    public const string UatMemoryUsedGbEnvVar = "ENVIOUSWISPR_UAT_MEMORY_USED_GB";

    public static ulong? ComputeUsedBytes(ulong totalPhysical, ulong availablePhysical)
    {
        if (totalPhysical == 0 || availablePhysical > totalPhysical)
        {
            return null;
        }

        return totalPhysical - availablePhysical;
    }

    public static SystemMemoryReading? ReadCurrent()
    {
        var osReading = ReadOperatingSystemMemory();
        var rawProfile = Environment.GetEnvironmentVariable(HardwareProfileSummary.UatHardwareProfileEnvVar);
        var rawUsed = Environment.GetEnvironmentVariable(UatMemoryUsedGbEnvVar);
        return ApplyOverrides(osReading, rawProfile, rawUsed);
    }

    internal static SystemMemoryReading? ApplyOverrides(
        SystemMemoryReading? osReading,
        string? rawHardwareProfileOverride,
        string? rawUsedGbOverride)
    {
        ulong totalBytes;
        if (HardwareProfileSummary.TryParseUatRamGb(rawHardwareProfileOverride, out var uatRamGb))
        {
            totalBytes = Math.Max(1UL, GbToBytesClamped(uatRamGb));
        }
        else if (osReading is { TotalPhysicalBytes: > 0 } os)
        {
            totalBytes = os.TotalPhysicalBytes;
        }
        else
        {
            return null;
        }

        if (TryParseNonNegativeFiniteDouble(rawUsedGbOverride, out var overrideUsedGb))
        {
            var requestedUsed = GbToBytesClamped(overrideUsedGb);
            return new SystemMemoryReading(totalBytes, Math.Min(requestedUsed, totalBytes));
        }

        if (osReading is { } liveOs)
        {
            return new SystemMemoryReading(totalBytes, Math.Min(liveOs.UsedBytes, totalBytes));
        }

        return null;
    }

    private static SystemMemoryReading? ReadOperatingSystemMemory()
    {
        var status = new MemoryStatusEx { Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()) };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            return null;
        }

        return ComputeUsedBytes(status.TotalPhysical, status.AvailablePhysical) is { } used
            ? new SystemMemoryReading(status.TotalPhysical, used)
            : null;
    }

    private static bool TryParseNonNegativeFiniteDouble(string? raw, out double value)
    {
        value = 0d;
        return !string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) &&
            value >= 0d;
    }

    private static ulong GbToBytesClamped(double gb)
    {
        if (gb <= 0d || !double.IsFinite(gb))
        {
            return 0UL;
        }

        var scaled = Math.Round(gb * EgOneMemoryFacts.BytesPerGiB, MidpointRounding.AwayFromZero);
        if (scaled >= ulong.MaxValue)
        {
            return ulong.MaxValue;
        }

        return scaled <= 0d ? 0UL : (ulong)scaled;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
