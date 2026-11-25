using System.Globalization;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Diagnostics;

/// <summary>Snapshot of this PC's memory and NVIDIA GPU availability for EG-1 sizing.</summary>
public sealed record HardwareProfile(
    double TotalRamGb,
    bool HasNvidiaGpu,
    string SummaryText,
    string RecommendedEgOneModelId);

/// <summary>Pure helper that summarizes local RAM and NVIDIA GPU support for EG-1 model sizing.</summary>
public static class HardwareProfileSummary
{
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;

    public static HardwareProfile GetCurrent()
    {
        var totalRamGb = GetTotalRamGb();
        var hasNvidiaGpu = DetectNvidiaGpu();
        return new HardwareProfile(
            totalRamGb,
            hasNvidiaGpu,
            FormatSummary(totalRamGb, hasNvidiaGpu),
            RecommendEgOneModelId(totalRamGb));
    }

    public static double GetTotalRamGb()
    {
        var status = new MemoryStatusEx { Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()) };
        if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0)
        {
            return status.TotalPhysical / BytesPerGigabyte;
        }

        var fallbackBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return fallbackBytes > 0 ? fallbackBytes / BytesPerGigabyte : 0d;
    }

    public static string FormatSummary(double totalRamGb, bool hasNvidiaGpu)
    {
        var suffix = hasNvidiaGpu ? " · NVIDIA GPU" : string.Empty;
        if (totalRamGb <= 0d || double.IsNaN(totalRamGb) || double.IsInfinity(totalRamGb))
        {
            return $"This PC: Local Memory{suffix}";
        }

        var roundedGb = Math.Max(1, (int)Math.Round(totalRamGb, MidpointRounding.AwayFromZero));
        return string.Format(
            CultureInfo.InvariantCulture,
            "This PC: {0} GB RAM{1}",
            roundedGb,
            suffix);
    }

    public static string RecommendEgOneModelId(double totalRamGb)
    {
        if (totalRamGb >= 24d)
        {
            return "eg-1";
        }

        if (totalRamGb >= 12d)
        {
            return "eg-1-mini";
        }

        return "eg-1-micro";
    }

    public static bool DetectNvidiaGpu()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var library))
        {
            return false;
        }

        try
        {
            var initialize = Marshal.GetDelegateForFunctionPointer<CudaInitialize>(
                NativeLibrary.GetExport(library, "cuInit"));
            var getDeviceCount = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCount>(
                NativeLibrary.GetExport(library, "cuDeviceGetCount"));

            return initialize(0) == 0 && getDeviceCount(out var deviceCount) == 0 && deviceCount > 0;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or ArgumentException)
        {
            return false;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaInitialize(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaGetDeviceCount(out int count);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
