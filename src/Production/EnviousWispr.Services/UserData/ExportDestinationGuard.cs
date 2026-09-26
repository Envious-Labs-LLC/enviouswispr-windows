using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EnviousWispr.Services.UserData;

/// <summary>Refuses an export whose destination is the app's own data folder or anything inside it.</summary>
/// <remarks>
/// AN EXPORT WRITTEN OVER THE SETTINGS FILE ERASES THE SETTINGS, snippets and words included (macOS refuses its live
/// store for the same reason: <c>SnippetsExportAction.refusedLiveStore</c>). Windows goes one step further and
/// refuses the whole data folder, because a list file saved next to the settings is a file the app's own
/// "Delete all data" would then remove without the person knowing it was theirs.
///
/// COMPARED AS THE FILE SYSTEM SEES IT, NOT AS TYPED. A path can name the same place several ways: other case,
/// relative segments, an 8.3 short name, a junction or a symbolic link. Each existing part of the path is opened and
/// asked for its final name (<c>GetFinalPathNameByHandle</c>), which resolves all of those at once; the part that
/// does not exist yet is appended as written. Then the destination must not be the folder or start with the folder
/// followed by a separator - a bare prefix test would call a sibling folder named the same plus a suffix "inside".
/// </remarks>
public static class ExportDestinationGuard
{
    /// <summary>True when <paramref name="destination"/> is <paramref name="dataDirectory"/> itself or anything under it.</summary>
    public static bool IsInsideDataDirectory(string destination, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var target = Canonical(destination);
        var root = Canonical(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(target, root, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path as the file system names it: the deepest existing part resolved, the rest appended.</summary>
    internal static string Canonical(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var pending = new Stack<string>();
        var existing = full;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return full;
            }

            pending.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = FinalPath(existing) ?? existing;
        while (pending.Count > 0)
        {
            resolved = Path.Combine(resolved, pending.Pop());
        }

        return resolved.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string? FinalPath(string path)
    {
        const uint FileShareAll = 0x1 | 0x2 | 0x4;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
