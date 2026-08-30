using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using System.Management;
using System.Runtime.InteropServices;

namespace EnviousWispr.Services.Runtime;

public sealed class WindowsHardwareDiscovery : IHardwareDiscovery
{
    private readonly string? _cudaRuntimeDirectory;

    public WindowsHardwareDiscovery(string? cudaRuntimeDirectory = null)
    {
        _cudaRuntimeDirectory = string.IsNullOrWhiteSpace(cudaRuntimeDirectory)
            ? null
            : Path.GetFullPath(cudaRuntimeDirectory);
    }

    public Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Probe(cancellationToken), cancellationToken);

    private HardwareSnapshot Probe(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = HardwareProbeStatus.Complete;
        var processorVendor = ProcessorVendor.Unknown;
        var physicalCores = 0;
        var logicalProcessors = Environment.ProcessorCount;
        IReadOnlyList<GraphicsAdapterCapability> graphicsAdapters = [];

        try
        {
            (processorVendor, physicalCores, logicalProcessors) = ProbeProcessors(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableProbeFailure(exception))
        {
            status = HardwareProbeStatus.Partial;
            physicalCores = Math.Max(1, logicalProcessors / 2);
        }

        try
        {
            graphicsAdapters = ProbeGraphicsAdapters(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableProbeFailure(exception))
        {
            status = HardwareProbeStatus.Partial;
        }

        var memoryBytes = ProbePhysicalMemory();
        if (memoryBytes == 0)
        {
            status = HardwareProbeStatus.Partial;
        }

        var directMlAvailable = ProbeNativeLibrary("DirectML.dll");
        var cuda = ProbeCudaDriver();
        var onnxRuntimeCudaDependencies = CudaRuntimeDependencyProbe.IsComplete(
            _cudaRuntimeDirectory ?? Environment.GetEnvironmentVariable("ENVIOUSWISPR_CUDA_RUNTIME_DIR"));
        return new HardwareSnapshot(
            status,
            CurrentArchitecture(),
            processorVendor,
            physicalCores,
            logicalProcessors,
            memoryBytes,
            graphicsAdapters,
            directMlAvailable,
            cuda,
            onnxRuntimeCudaDependencies,
            status == HardwareProbeStatus.Complete
                ? null
                : new AppError(
                    AppErrorCode.HardwareProbeFailed,
                    AppErrorStage.HardwareDiscovery,
                    CanRetry: true));
    }

    private static (ProcessorVendor Vendor, int PhysicalCores, int LogicalProcessors) ProbeProcessors(
        CancellationToken cancellationToken)
    {
        var vendor = ProcessorVendor.Unknown;
        var physicalCores = 0;
        var logicalProcessors = 0;
        using var searcher = new ManagementObjectSearcher(
            "SELECT Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        using var processors = searcher.Get();
        foreach (ManagementBaseObject processor in processors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vendor = MergeVendor(vendor, ClassifyProcessorVendor(processor["Manufacturer"] as string));
            physicalCores += Convert.ToInt32(processor["NumberOfCores"],
                System.Globalization.CultureInfo.InvariantCulture);
            logicalProcessors += Convert.ToInt32(processor["NumberOfLogicalProcessors"],
                System.Globalization.CultureInfo.InvariantCulture);
        }

        return (
            vendor,
            Math.Max(1, physicalCores),
            Math.Max(1, logicalProcessors));
    }

    private static GraphicsAdapterCapability[] ProbeGraphicsAdapters(
        CancellationToken cancellationToken)
    {
        var result = new List<GraphicsAdapterCapability>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT AdapterCompatibility, PNPDeviceID, Status FROM Win32_VideoController");
        using var adapters = searcher.Get();
        foreach (ManagementBaseObject adapter in adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vendor = ClassifyGraphicsVendor(
                adapter["AdapterCompatibility"] as string,
                adapter["PNPDeviceID"] as string);
            var active = string.Equals(adapter["Status"] as string, "OK", StringComparison.OrdinalIgnoreCase);
            result.Add(new GraphicsAdapterCapability(
                vendor,
                active,
                IsDirectMlCandidate: active && vendor != GraphicsVendor.Unknown));
        }

        return result.ToArray();
    }

    private static CudaDriverCapability ProbeCudaDriver()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var library))
        {
            return new CudaDriverCapability(false, DeviceCount: 0, DriverVersion: null);
        }

        try
        {
            var initialize = Marshal.GetDelegateForFunctionPointer<CudaInitialize>(
                NativeLibrary.GetExport(library, "cuInit"));
            var getDeviceCount = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCount>(
                NativeLibrary.GetExport(library, "cuDeviceGetCount"));
            var getDriverVersion = Marshal.GetDelegateForFunctionPointer<CudaGetDriverVersion>(
                NativeLibrary.GetExport(library, "cuDriverGetVersion"));

            if (initialize(0) != 0 || getDeviceCount(out var deviceCount) != 0)
            {
                return new CudaDriverCapability(false, DeviceCount: 0, DriverVersion: null);
            }

            int? driverVersion = getDriverVersion(out var version) == 0 ? version : null;
            return new CudaDriverCapability(
                IsDriverAvailable: deviceCount > 0,
                deviceCount,
                driverVersion);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or ArgumentException)
        {
            return new CudaDriverCapability(false, DeviceCount: 0, DriverVersion: null);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static ulong ProbePhysicalMemory()
    {
        var status = new MemoryStatus { Length = checked((uint)Marshal.SizeOf<MemoryStatus>()) };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 0;
    }

    private static bool ProbeNativeLibrary(string libraryName)
    {
        if (!NativeLibrary.TryLoad(libraryName, out var library))
        {
            return false;
        }

        NativeLibrary.Free(library);
        return true;
    }

    private static ProcessorArchitectureKind CurrentArchitecture() =>
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => ProcessorArchitectureKind.X64,
            Architecture.Arm64 => ProcessorArchitectureKind.Arm64,
            _ => ProcessorArchitectureKind.Unknown,
        };

    private static ProcessorVendor ClassifyProcessorVendor(string? manufacturer)
    {
        if (manufacturer?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ProcessorVendor.Intel;
        }

        if (manufacturer?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true ||
            manufacturer?.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ProcessorVendor.Amd;
        }

        return manufacturer?.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) == true
            ? ProcessorVendor.Qualcomm
            : ProcessorVendor.Unknown;
    }

    private static GraphicsVendor ClassifyGraphicsVendor(string? compatibility, string? pnpDeviceId)
    {
        var evidence = $"{compatibility} {pnpDeviceId}";
        if (evidence.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            evidence.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicsVendor.Nvidia;
        }

        if (evidence.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            evidence.Contains("ATI", StringComparison.OrdinalIgnoreCase) ||
            evidence.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
        {
            return GraphicsVendor.Amd;
        }

        return evidence.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            evidence.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)
            ? GraphicsVendor.Intel
            : GraphicsVendor.Unknown;
    }

    private static ProcessorVendor MergeVendor(ProcessorVendor current, ProcessorVendor next) =>
        current == ProcessorVendor.Unknown ? next : current;

    private static bool IsRecoverableProbeFailure(Exception exception) =>
        exception is ManagementException or COMException or UnauthorizedAccessException;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaGetDriverVersion(out int version);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
