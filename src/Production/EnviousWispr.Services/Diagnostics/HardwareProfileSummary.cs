using System.Globalization;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Diagnostics;

/// <summary>Snapshot of this PC's memory and GPU availability for EG-1 sizing.</summary>
public sealed record HardwareProfile(
    double TotalRamGb,
    bool HasNvidiaGpu,
    string SummaryText,
    string RecommendedEgOneModelId,
    bool HasVulkanGpu = false);

/// <summary>Pure helper that summarizes local RAM, GPU, and CPU core topology for EG-1 sizing and runtime tuning.</summary>
public static class HardwareProfileSummary
{
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;
    private const int CpuSetInformation = 0;
    private const uint VkStructureTypeInstanceCreateInfo = 1;
    private const uint VkPhysicalDeviceTypeIntegratedGpu = 1;
    private const uint VkPhysicalDeviceTypeDiscreteGpu = 2;
    private const uint VkPhysicalDeviceTypeVirtualGpu = 3;

    public static HardwareProfile GetCurrent()
    {
        var totalRamGb = GetTotalRamGb();
        var hasNvidiaGpu = DetectNvidiaGpu();
        var hasVulkanGpu = DetectVulkanGpu();
        return new HardwareProfile(
            totalRamGb,
            hasNvidiaGpu,
            FormatSummary(totalRamGb, hasNvidiaGpu, hasVulkanGpu),
            RecommendEgOneModelId(totalRamGb, hasNvidiaGpu || hasVulkanGpu),
            hasVulkanGpu);
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

    public static string FormatSummary(double totalRamGb, bool hasNvidiaGpu) =>
        FormatSummary(totalRamGb, hasNvidiaGpu, hasVulkanGpu: false);

    public static string FormatSummary(double totalRamGb, bool hasNvidiaGpu, bool hasVulkanGpu)
    {
        var suffix = hasNvidiaGpu
            ? " · NVIDIA GPU"
            : hasVulkanGpu
                ? " · Vulkan GPU"
                : string.Empty;
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

    public static string RecommendEgOneModelId(double totalRamGb) =>
        RecommendEgOneModelId(totalRamGb, hasGpuAcceleration: true);

    public static string RecommendEgOneModelId(double totalRamGb, bool hasGpuAcceleration)
    {
        if (totalRamGb >= 24d && hasGpuAcceleration)
        {
            return "eg-1";
        }

        if (totalRamGb >= 12d)
        {
            return "eg-1-mini";
        }

        return "eg-1-micro";
    }

    public static (int DecodeThreads, int BatchThreads) ChooseCpuThreadCounts()
    {
        var (performanceCores, physicalCores) = TryReadCoreCounts();
        if (performanceCores > 0 && physicalCores > 0)
        {
            return ChooseCpuThreadCounts(performanceCores, physicalCores);
        }

        var logical = Math.Max(1, Environment.ProcessorCount);
        var estimatedPhysical = Math.Max(1, logical / 2);
        return ChooseCpuThreadCounts(estimatedPhysical, estimatedPhysical);
    }

    internal static (int DecodeThreads, int BatchThreads) ChooseCpuThreadCounts(
        int performanceCores,
        int physicalCores)
    {
        var safePerf = Math.Max(1, performanceCores);
        var safePhysical = Math.Max(safePerf, physicalCores);
        var decodeThreads = Math.Clamp(safePerf, 2, 8);
        var batchThreads = Math.Clamp(safePhysical, decodeThreads, 16);
        return (decodeThreads, batchThreads);
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

    public static bool DetectVulkanGpu()
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var library))
        {
            return false;
        }

        var instance = IntPtr.Zero;
        VkDestroyInstance? destroyInstance = null;
        try
        {
            var createInstance = Marshal.GetDelegateForFunctionPointer<VkCreateInstance>(
                NativeLibrary.GetExport(library, "vkCreateInstance"));
            destroyInstance = Marshal.GetDelegateForFunctionPointer<VkDestroyInstance>(
                NativeLibrary.GetExport(library, "vkDestroyInstance"));
            var enumeratePhysicalDevices = Marshal.GetDelegateForFunctionPointer<VkEnumeratePhysicalDevices>(
                NativeLibrary.GetExport(library, "vkEnumeratePhysicalDevices"));
            var getPhysicalDeviceProperties = Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDeviceProperties>(
                NativeLibrary.GetExport(library, "vkGetPhysicalDeviceProperties"));

            var createInfo = new VkInstanceCreateInfo { SType = VkStructureTypeInstanceCreateInfo };
            if (createInstance(in createInfo, IntPtr.Zero, out instance) != 0 || instance == IntPtr.Zero)
            {
                return false;
            }

            uint count = 0;
            if (enumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0)
            {
                return false;
            }

            var cappedCount = Math.Min(count, 16u);
            var devices = new nint[cappedCount];
            var enumerateResult = enumeratePhysicalDevices(instance, ref cappedCount, devices);
            if (enumerateResult != 0 && enumerateResult != 5) // VK_SUCCESS or VK_INCOMPLETE
            {
                return false;
            }

            var propertiesBuffer = new byte[1024];
            for (var index = 0; index < cappedCount; index++)
            {
                if (devices[index] == IntPtr.Zero)
                {
                    continue;
                }

                getPhysicalDeviceProperties(devices[index], propertiesBuffer);
                var deviceType = BitConverter.ToUInt32(propertiesBuffer, 16);
                if (deviceType is VkPhysicalDeviceTypeIntegratedGpu or VkPhysicalDeviceTypeDiscreteGpu or VkPhysicalDeviceTypeVirtualGpu)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (instance != IntPtr.Zero && destroyInstance is not null)
            {
                destroyInstance(instance, IntPtr.Zero);
            }

            NativeLibrary.Free(library);
        }
    }

    private static (int PerformanceCores, int PhysicalCores) TryReadCoreCounts()
    {
        try
        {
            _ = GetSystemCpuSetInformation(null, 0, out var length, IntPtr.Zero, 0);
            if (length <= 0 || length > 1024 * 1024)
            {
                return (0, 0);
            }

            var buffer = new byte[length];
            if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
            {
                return (0, 0);
            }

            var allCores = new HashSet<(ushort Group, byte Core)>();
            var coreClasses = new Dictionary<(ushort Group, byte Core), byte>();
            for (var offset = 0; offset + 19 <= length;)
            {
                var size = BitConverter.ToInt32(buffer, offset);
                if (size <= 0 || offset + size > length)
                {
                    break;
                }

                if (BitConverter.ToInt32(buffer, offset + 4) == CpuSetInformation)
                {
                    var key = (BitConverter.ToUInt16(buffer, offset + 12), buffer[offset + 15]);
                    var efficiencyClass = buffer[offset + 18];
                    allCores.Add(key);
                    if (!coreClasses.TryGetValue(key, out var existing) || efficiencyClass > existing)
                    {
                        coreClasses[key] = efficiencyClass;
                    }
                }

                offset += size;
            }

            if (allCores.Count == 0)
            {
                return (0, 0);
            }

            var maxClass = coreClasses.Values.Max();
            var perfCores = coreClasses.Values.Count(c => c == maxClass);
            return (perfCores, allCores.Count);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return (0, 0);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public nint PApplicationInfo;
        public uint EnabledLayerCount;
        public nint PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public nint PpEnabledExtensionNames;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaInitialize(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaGetDeviceCount(out int count);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkCreateInstance(in VkInstanceCreateInfo createInfo, nint allocator, out nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkDestroyInstance(nint instance, nint allocator);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkEnumeratePhysicalDevices(nint instance, ref uint physicalDeviceCount, [Out] nint[]? physicalDevices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkGetPhysicalDeviceProperties(nint physicalDevice, [Out] byte[] properties);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        [Out] byte[]? information,
        int bufferLength,
        out int returnedLength,
        nint process,
        uint flags);
}
