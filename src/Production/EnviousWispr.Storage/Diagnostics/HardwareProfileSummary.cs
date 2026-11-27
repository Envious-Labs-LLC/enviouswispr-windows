using System.Globalization;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Services.Diagnostics;

/// <summary>Snapshot of this PC's memory, GPU, CPU cores, architecture, and free storage (content-free).</summary>
public sealed record HardwareProfile(
    double TotalRamGb,
    bool HasNvidiaGpu,
    string SummaryText,
    string RecommendedEgOneModelId,
    bool HasVulkanGpu = false,
    GpuAccelerationKind GpuKind = GpuAccelerationKind.None,
    double? DedicatedVramGb = null,
    int PhysicalCoreCount = 0,
    int PerformanceCoreCount = 0,
    ProcessorArchitectureKind Architecture = ProcessorArchitectureKind.Unknown,
    double? AvailableDiskGb = null);

/// <summary>Pure helper that summarizes local RAM, GPU, VRAM, and CPU core topology for EG-1 sizing and runtime tuning.</summary>
public static class HardwareProfileSummary
{
    public const string UatHardwareProfileEnvVar = "ENVIOUSWISPR_UAT_HARDWARE_PROFILE";

    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;
    private const int CpuSetInformation = 0;
    private const uint VkStructureTypeInstanceCreateInfo = 1;
    private const uint VkPhysicalDeviceTypeIntegratedGpu = 1;
    private const uint VkPhysicalDeviceTypeDiscreteGpu = 2;
    private const uint VkPhysicalDeviceTypeVirtualGpu = 3;
    private const uint VkMemoryHeapDeviceLocalBit = 0x00000001u;
    private const int VkPhysicalDeviceMemoryPropertiesBytes = 520;
    private const int VkMemoryHeapCountOffset = 260;
    private const int VkMemoryHeapsOffset = 264;
    private const int VkMemoryHeapStride = 16;
    private const uint VkMaxMemoryHeaps = 16;

    public static HardwareProfile GetCurrent()
    {
        var envOverride = Environment.GetEnvironmentVariable(UatHardwareProfileEnvVar);
        if (TryParseUatOverride(
                envOverride,
                ProbeProcessArchitecture(),
                ProbeAvailableDiskGb(),
                out var overriddenProfile))
        {
            return overriddenProfile!;
        }

        var totalRamGb = GetTotalRamGb();
        var (hasNvidiaGpu, cudaVramGb) = TryDetectNvidiaCuda();
        var vulkan = TryDetectVulkanCapabilities();
        var hasVulkanGpu = vulkan.HasAnyGpu;

        var gpuKind = hasNvidiaGpu
            ? GpuAccelerationKind.NvidiaCuda
            : vulkan.HasDiscreteGpu
                ? GpuAccelerationKind.DiscreteVulkan
                : vulkan.HasIntegratedGpu
                    ? GpuAccelerationKind.IntegratedVulkan
                    : GpuAccelerationKind.None;

        var dedicatedVramGb = gpuKind switch
        {
            GpuAccelerationKind.NvidiaCuda => cudaVramGb ?? vulkan.MaxDiscreteVramGb,
            GpuAccelerationKind.DiscreteVulkan => vulkan.MaxDiscreteVramGb,
            _ => null,
        };

        var (performanceCores, physicalCores) = ResolveCoreCounts();
        var architecture = ProbeProcessArchitecture();
        var availableDiskGb = ProbeAvailableDiskGb();

        return new HardwareProfile(
            TotalRamGb: totalRamGb,
            HasNvidiaGpu: hasNvidiaGpu,
            SummaryText: FormatSummary(totalRamGb, gpuKind, dedicatedVramGb, physicalCores),
            RecommendedEgOneModelId: RecommendEgOneModelId(totalRamGb, gpuKind, dedicatedVramGb, physicalCores),
            HasVulkanGpu: hasVulkanGpu,
            GpuKind: gpuKind,
            DedicatedVramGb: dedicatedVramGb,
            PhysicalCoreCount: physicalCores,
            PerformanceCoreCount: performanceCores,
            Architecture: architecture,
            AvailableDiskGb: availableDiskGb);
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
        return FormatMemoryPrefix(totalRamGb) + suffix;
    }

    public static string FormatSummary(
        double totalRamGb,
        GpuAccelerationKind gpuKind,
        double? dedicatedVramGb,
        int physicalCoreCount)
    {
        var suffix = gpuKind switch
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
        return FormatMemoryPrefix(totalRamGb) + suffix;
    }

    public static string RecommendEgOneModelId(double totalRamGb) =>
        RecommendEgOneModelId(totalRamGb, hasGpuAcceleration: true);

    public static string RecommendEgOneModelId(double totalRamGb, bool hasGpuAcceleration)
    {
        if (totalRamGb >= 24d && hasGpuAcceleration)
        {
            return EgOneModelCatalog.FullId;
        }

        if (totalRamGb >= 12d)
        {
            return EgOneModelCatalog.MiniId;
        }

        return EgOneModelCatalog.MicroId;
    }

    public static string RecommendEgOneModelId(
        double totalRamGb,
        GpuAccelerationKind gpuKind,
        double? dedicatedVramGb,
        int physicalCoreCount)
    {
        var hasDiscreteGpu = gpuKind is GpuAccelerationKind.NvidiaCuda or GpuAccelerationKind.DiscreteVulkan;
        if (hasDiscreteGpu && (dedicatedVramGb ?? 0d) >= 6d && totalRamGb >= 24d)
        {
            return EgOneModelCatalog.FullId;
        }

        var effectiveVram = dedicatedVramGb ?? 4d;
        if ((hasDiscreteGpu && effectiveVram >= 4d && totalRamGb >= 16d) ||
            (totalRamGb >= 32d && physicalCoreCount >= 8))
        {
            return EgOneModelCatalog.MiniId;
        }

        if (totalRamGb >= 16d && physicalCoreCount >= 4)
        {
            return EgOneModelCatalog.MiniId;
        }

        return EgOneModelCatalog.MicroId;
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

    public static (int PerformanceCores, int PhysicalCores) ResolveCoreCounts()
    {
        var (performanceCores, physicalCores) = TryReadCoreCounts();
        if (physicalCores > 0)
        {
            return (performanceCores > 0 ? performanceCores : physicalCores, physicalCores);
        }

        var estimatedPhysical = Math.Max(1, Environment.ProcessorCount / 2);
        return (estimatedPhysical, estimatedPhysical);
    }

    public static bool DetectNvidiaGpu() => TryDetectNvidiaCuda().HasNvidiaGpu;

    public static bool DetectVulkanGpu() => TryDetectVulkanCapabilities().HasAnyGpu;

    internal static bool TryParseUatOverride(
        string? raw,
        ProcessorArchitectureKind fallbackArchitecture,
        double? fallbackDiskGb,
        out HardwareProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var totalRamGb = 16d;
        var gpuKind = GpuAccelerationKind.None;
        double? dedicatedVramGb = null;
        int? cores = null;
        int? pcores = null;
        var architecture = fallbackArchitecture;
        var diskGb = fallbackDiskGb;
        var sawAnyRecognizedField = false;

        foreach (var segment in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = segment[..separator].Trim().ToLowerInvariant();
            var val = segment[(separator + 1)..].Trim().ToLowerInvariant();
            switch (key)
            {
                case "ram" when double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRam) &&
                                double.IsFinite(parsedRam) && parsedRam > 0d:
                    totalRamGb = parsedRam;
                    sawAnyRecognizedField = true;
                    break;

                case "gpu":
                    gpuKind = val switch
                    {
                        "none" or "cpu" or "false" or "0" => GpuAccelerationKind.None,
                        "integrated" or "integratedvulkan" or "integrated_vulkan" => GpuAccelerationKind.IntegratedVulkan,
                        "discrete" or "discretevulkan" or "discrete_vulkan" or "vulkan" => GpuAccelerationKind.DiscreteVulkan,
                        "nvidia" or "cuda" or "nvidiacuda" or "nvidia_cuda" or "true" or "1" => GpuAccelerationKind.NvidiaCuda,
                        _ => gpuKind,
                    };
                    sawAnyRecognizedField = true;
                    break;

                case "vram" when val is "none" or "null":
                    dedicatedVramGb = null;
                    sawAnyRecognizedField = true;
                    break;

                case "vram" when double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedVram) &&
                                 double.IsFinite(parsedVram) && parsedVram >= 0d:
                    dedicatedVramGb = parsedVram;
                    sawAnyRecognizedField = true;
                    break;

                case "cores" when int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCores) &&
                                  parsedCores > 0:
                    cores = parsedCores;
                    sawAnyRecognizedField = true;
                    break;

                case "pcores" when int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPCores) &&
                                   parsedPCores > 0:
                    pcores = parsedPCores;
                    sawAnyRecognizedField = true;
                    break;

                case "arch":
                    architecture = val switch
                    {
                        "x64" or "amd64" => ProcessorArchitectureKind.X64,
                        "arm64" => ProcessorArchitectureKind.Arm64,
                        _ => ProcessorArchitectureKind.Unknown,
                    };
                    sawAnyRecognizedField = true;
                    break;

                case "disk" when double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDisk) &&
                                 double.IsFinite(parsedDisk) && parsedDisk >= 0d:
                    diskGb = parsedDisk;
                    sawAnyRecognizedField = true;
                    break;
            }
        }

        if (!sawAnyRecognizedField)
        {
            return false;
        }

        var physicalCores = cores ?? Math.Max(1, pcores ?? 8);
        var performanceCores = Math.Clamp(pcores ?? physicalCores, 1, physicalCores);
        var hasNvidiaGpu = gpuKind == GpuAccelerationKind.NvidiaCuda;
        var hasVulkanGpu = gpuKind != GpuAccelerationKind.None;

        profile = new HardwareProfile(
            TotalRamGb: totalRamGb,
            HasNvidiaGpu: hasNvidiaGpu,
            SummaryText: FormatSummary(totalRamGb, gpuKind, dedicatedVramGb, physicalCores),
            RecommendedEgOneModelId: RecommendEgOneModelId(totalRamGb, gpuKind, dedicatedVramGb, physicalCores),
            HasVulkanGpu: hasVulkanGpu,
            GpuKind: gpuKind,
            DedicatedVramGb: dedicatedVramGb,
            PhysicalCoreCount: physicalCores,
            PerformanceCoreCount: performanceCores,
            Architecture: architecture,
            AvailableDiskGb: diskGb);
        return true;
    }

    private static string FormatMemoryPrefix(double totalRamGb)
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

    private static bool HasPositiveFiniteValue(double? value) =>
        value is { } actual && actual > 0d && double.IsFinite(actual);

    private static int RoundPositiveWhole(double value) =>
        Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static ProcessorArchitectureKind ProbeProcessArchitecture() =>
        RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => ProcessorArchitectureKind.X64,
            System.Runtime.InteropServices.Architecture.Arm64 => ProcessorArchitectureKind.Arm64,
            _ => ProcessorArchitectureKind.Unknown,
        };

    private static double? ProbeAvailableDiskGb(string? directoryOverride = null)
    {
        try
        {
            var probePath = directoryOverride;
            if (string.IsNullOrWhiteSpace(probePath))
            {
                probePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            if (string.IsNullOrWhiteSpace(probePath))
            {
                probePath = AppContext.BaseDirectory;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(probePath));
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var freeBytes = new DriveInfo(root).AvailableFreeSpace;
            return freeBytes >= 0 ? freeBytes / BytesPerGigabyte : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static (bool HasNvidiaGpu, double? MaxVramGb) TryDetectNvidiaCuda()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var library))
        {
            return (false, null);
        }

        try
        {
            var initialize = Marshal.GetDelegateForFunctionPointer<CudaInitialize>(
                NativeLibrary.GetExport(library, "cuInit"));
            var getDeviceCount = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCount>(
                NativeLibrary.GetExport(library, "cuDeviceGetCount"));

            if (initialize(0) != 0 || getDeviceCount(out var deviceCount) != 0 || deviceCount <= 0)
            {
                return (false, null);
            }

            double? maxVramGb = null;
            if (NativeLibrary.TryGetExport(library, "cuDeviceGet", out var getDevicePtr) &&
                NativeLibrary.TryGetExport(library, "cuDeviceTotalMem_v2", out var totalMemPtr))
            {
                var getDevice = Marshal.GetDelegateForFunctionPointer<CudaDeviceGet>(getDevicePtr);
                var getTotalMem = Marshal.GetDelegateForFunctionPointer<CudaDeviceTotalMem>(totalMemPtr);
                ulong maxBytes = 0;
                var cappedCount = Math.Min(deviceCount, 8);
                for (var ordinal = 0; ordinal < cappedCount; ordinal++)
                {
                    if (getDevice(out var device, ordinal) == 0 &&
                        getTotalMem(out var totalBytes, device) == 0 &&
                        (ulong)totalBytes > maxBytes)
                    {
                        maxBytes = (ulong)totalBytes;
                    }
                }

                if (maxBytes > 0)
                {
                    maxVramGb = maxBytes / BytesPerGigabyte;
                }
            }

            return (true, maxVramGb);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or ArgumentException)
        {
            return (false, null);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private readonly record struct VulkanCapabilities(
        bool HasAnyGpu,
        bool HasDiscreteGpu,
        bool HasIntegratedGpu,
        double? MaxDiscreteVramGb);

    private static VulkanCapabilities TryDetectVulkanCapabilities()
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var library))
        {
            return default;
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
            VkGetPhysicalDeviceMemoryProperties? getMemoryProperties =
                NativeLibrary.TryGetExport(library, "vkGetPhysicalDeviceMemoryProperties", out var memPropsPtr)
                    ? Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDeviceMemoryProperties>(memPropsPtr)
                    : null;

            var createInfo = new VkInstanceCreateInfo { SType = VkStructureTypeInstanceCreateInfo };
            if (createInstance(in createInfo, IntPtr.Zero, out instance) != 0 || instance == IntPtr.Zero)
            {
                return default;
            }

            uint count = 0;
            if (enumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0)
            {
                return default;
            }

            var cappedCount = Math.Min(count, 16u);
            var devices = new nint[cappedCount];
            var enumerateResult = enumeratePhysicalDevices(instance, ref cappedCount, devices);
            if (enumerateResult != 0 && enumerateResult != 5) // VK_SUCCESS or VK_INCOMPLETE
            {
                return default;
            }

            var hasDiscrete = false;
            var hasIntegrated = false;
            ulong maxDiscreteVramBytes = 0;
            var propertiesBuffer = new byte[1024];
            var memoryPropertiesBuffer = new byte[VkPhysicalDeviceMemoryPropertiesBytes];
            for (var index = 0; index < cappedCount; index++)
            {
                if (devices[index] == IntPtr.Zero)
                {
                    continue;
                }

                // Read ONLY numeric deviceType at offset 16 (never inspect deviceName or vendor/device IDs).
                getPhysicalDeviceProperties(devices[index], propertiesBuffer);
                var deviceType = BitConverter.ToUInt32(propertiesBuffer, 16);
                if (deviceType == VkPhysicalDeviceTypeDiscreteGpu)
                {
                    hasDiscrete = true;
                    if (getMemoryProperties is not null)
                    {
                        Array.Clear(memoryPropertiesBuffer);
                        getMemoryProperties(devices[index], memoryPropertiesBuffer);
                        var heapCount = Math.Min(BitConverter.ToUInt32(memoryPropertiesBuffer, VkMemoryHeapCountOffset), VkMaxMemoryHeaps);
                        for (uint heapIndex = 0; heapIndex < heapCount; heapIndex++)
                        {
                            var heapOffset = VkMemoryHeapsOffset + (int)(heapIndex * VkMemoryHeapStride);
                            var heapSize = BitConverter.ToUInt64(memoryPropertiesBuffer, heapOffset);
                            var heapFlags = BitConverter.ToUInt32(memoryPropertiesBuffer, heapOffset + 8);
                            if ((heapFlags & VkMemoryHeapDeviceLocalBit) != 0 && heapSize > maxDiscreteVramBytes)
                            {
                                maxDiscreteVramBytes = heapSize;
                            }
                        }
                    }
                }
                else if (deviceType is VkPhysicalDeviceTypeIntegratedGpu or VkPhysicalDeviceTypeVirtualGpu)
                {
                    hasIntegrated = true;
                }
            }

            double? maxDiscreteVramGb = maxDiscreteVramBytes > 0
                ? maxDiscreteVramBytes / BytesPerGigabyte
                : null;
            return new VulkanCapabilities(
                HasAnyGpu: hasDiscrete || hasIntegrated,
                HasDiscreteGpu: hasDiscrete,
                HasIntegratedGpu: hasIntegrated,
                MaxDiscreteVramGb: maxDiscreteVramGb);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or ArgumentException)
        {
            return default;
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
    private delegate int CudaDeviceGet(out int device, int ordinal);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaDeviceTotalMem(out nuint bytes, int device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkCreateInstance(in VkInstanceCreateInfo createInfo, nint allocator, out nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkDestroyInstance(nint instance, nint allocator);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkEnumeratePhysicalDevices(nint instance, ref uint physicalDeviceCount, [Out] nint[]? physicalDevices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkGetPhysicalDeviceProperties(nint physicalDevice, [Out] byte[] properties);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkGetPhysicalDeviceMemoryProperties(nint physicalDevice, [Out] byte[] memoryProperties);

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
