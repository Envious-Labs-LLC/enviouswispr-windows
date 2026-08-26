using System.Runtime.InteropServices;
using EnviousWispr.Core.Reliability;

namespace EnviousWispr.Services.Reliability;

public sealed class WindowsSystemResourceProbe : ISystemResourceProbe
{
    private readonly string _dataDirectory;

    public WindowsSystemResourceProbe(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public SystemResourceSnapshot Probe()
    {
        try
        {
            var root = Path.GetPathRoot(_dataDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return Unavailable();
            }

            var memory = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(ref memory))
            {
                return Unavailable();
            }

            var drive = new DriveInfo(root);
            return new SystemResourceSnapshot(
                drive.AvailableFreeSpace,
                memory.AvailablePhysical,
                memory.MemoryLoad);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            return Unavailable();
        }
    }

    private static SystemResourceSnapshot Unavailable() => new(
        AvailableDiskBytes: 0,
        AvailablePhysicalMemoryBytes: 0,
        MemoryLoadPercent: 0,
        IsAvailable: false);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>());
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;

        public MemoryStatusEx()
        {
        }
    }

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
