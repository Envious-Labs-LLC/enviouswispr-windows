using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Audio;
using EnviousWispr.Core.Compatibility;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Runtime;
using Microsoft.Win32;

var outputPath = ArgumentValue(args, "--output");
var hardware = await new WindowsHardwareDiscovery().ProbeAsync();
using var deviceCatalog = new WasapiDeviceCatalog();
var captureDevices = await deviceCatalog.GetCaptureDevicesAsync();
var operatingSystem = Environment.OSVersion.Version;
var activeGraphicsVendors = hardware.GraphicsAdapters
    .Where(adapter => adapter.IsActive)
    .Select(adapter => adapter.Vendor)
    .Distinct()
    .Order()
    .ToArray();
var (securityProbeAvailable, securityProviderCount) = ProbeEndpointSecurity();

var snapshot = new WindowsCompatibilitySnapshot(
    SchemaVersion: 1,
    OperatingSystemBuild: operatingSystem.Build,
    OperatingSystemRevision: ReadOperatingSystemRevision(operatingSystem.Revision),
    IsWindows11OrLater: OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
    hardware.Architecture,
    hardware.ProcessorVendor,
    hardware.PhysicalCoreCount,
    hardware.LogicalProcessorCount,
    CompatibilityBuckets.ClassifyMemory(hardware.TotalPhysicalMemoryBytes),
    activeGraphicsVendors,
    hardware.IsDirectMlRuntimeAvailable,
    hardware.Cuda.IsDriverAvailable,
    hardware.IsOnnxRuntimeCudaDependencySetAvailable,
    hardware.Cuda.DeviceCount,
    hardware.Cuda.DriverVersion,
    DisplayCount: Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SystemMetricMonitorCount)),
    CompatibilityBuckets.ClassifyDisplayScale(NativeMethods.GetDpiForSystem()),
    CompatibilityBuckets.ClassifyResolution(
        NativeMethods.GetSystemMetrics(NativeMethods.SystemMetricPrimaryWidth),
        NativeMethods.GetSystemMetrics(NativeMethods.SystemMetricPrimaryHeight)),
    CaptureDeviceCount: captureDevices.Count,
    HasDefaultCaptureDevice: captureDevices.Any(device => device.IsDefault),
    securityProbeAvailable,
    securityProviderCount,
    hardware.Status);

var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
});
Console.WriteLine(json);
if (!string.IsNullOrWhiteSpace(outputPath))
{
    var resolvedOutput = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(resolvedOutput)
                              ?? throw new InvalidOperationException("Compatibility output has no directory."));
    await File.WriteAllTextAsync(resolvedOutput, json + Environment.NewLine);
}

return snapshot.IsWindows11OrLater &&
       snapshot.Architecture == ProcessorArchitectureKind.X64 &&
       snapshot.PhysicalCoreCount > 0 &&
       snapshot.LogicalProcessorCount >= snapshot.PhysicalCoreCount &&
       snapshot.DisplayCount > 0 &&
       snapshot.CaptureDeviceCount > 0 &&
       snapshot.HasDefaultCaptureDevice
    ? 0
    : 4;

static string? ArgumentValue(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static (bool Available, int ProviderCount) ProbeEndpointSecurity()
{
    try
    {
        var scope = new ManagementScope(@"\\.\root\SecurityCenter2");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT productState FROM AntiVirusProduct"));
        using var products = searcher.Get();
        return (true, products.Count);
    }
    catch (Exception exception) when (exception is
                                      ManagementException or
                                      COMException or
                                      UnauthorizedAccessException)
    {
        return (false, 0);
    }
}

static int ReadOperatingSystemRevision(int fallback)
{
    try
    {
        using var currentVersion = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            writable: false);
        return currentVersion?.GetValue("UBR") is int updateBuildRevision
            ? updateBuildRevision
            : fallback;
    }
    catch (Exception exception) when (exception is
                                      System.Security.SecurityException or
                                      UnauthorizedAccessException)
    {
        return fallback;
    }
}

internal static partial class NativeMethods
{
    internal const int SystemMetricPrimaryWidth = 0;
    internal const int SystemMetricPrimaryHeight = 1;
    internal const int SystemMetricMonitorCount = 80;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();
}
