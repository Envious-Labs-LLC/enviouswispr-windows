using System.Runtime.InteropServices;

namespace EnviousWispr.ASR;

/// <summary>What <see cref="NativeRuntimeSearchPath.Configure(string?)"/> did to the process.</summary>
public enum NativeRuntimeSearchPathOutcome
{
    /// <summary>No directory was given, so the process search order was left exactly as it was.</summary>
    NoDirectory,

    /// <summary>A directory was given and is not there, so nothing was changed.</summary>
    DirectoryMissing,

    /// <summary>The directory is now searched by every later load that asks by module name.</summary>
    Added,

    /// <summary>The operating system refused; the process search order is unchanged.</summary>
    Refused,
}

/// <summary>
/// Makes the CUDA runtime folder visible to the native libraries the speech engines load, in a way a
/// packaged process honours.
/// </summary>
/// <remarks>
/// PATH IS NOT ENOUGH, AND UNDER THE STORE PACKAGE IT IS NOTHING. A packaged (MSIX) process never searches
/// PATH for a DLL (Microsoft, "Dynamic-link library search order", packaged apps). The worker used to add
/// the CUDA folder by prepending it to PATH, which works for an unpackaged build and did nothing for the
/// packaged one: measured 2026-09-25 on the development PC, with the files in the data folder the card
/// failed to start under package identity and the worker fell back to the processor, and with the same
/// files copied beside the executable it started. Now that the libraries arrive as a separate download into
/// the data folder, beside the executable is not available.
///
/// THE MECHANISM IS THE PROCESS-WIDE ONE BECAUSE NEITHER ENGINE LETS US CHOOSE THE FLAGS. onnxruntime loads
/// its CUDA provider and whisper.cpp's CUDA backend is loaded by Whisper.net; each of those then imports
/// cuBLAS, cuFFT, the CUDA runtime or cuDNN BY MODULE NAME, and cuDNN loads its own sub-libraries by name at
/// run time. So the only lever is the process's default search: <c>SetDefaultDllDirectories</c> with
/// <c>LOAD_LIBRARY_SEARCH_DEFAULT_DIRS</c> (the application folder, System32, and every folder added with
/// <c>AddDllDirectory</c>), then <c>AddDllDirectory</c> for the CUDA folder. It behaves the same packaged and
/// unpackaged, which is the point: one layout, one answer, both ways the app runs.
///
/// IT MUST RUN BEFORE ANY ENGINE LOADS A NATIVE LIBRARY. The worker calls it first thing in engine
/// creation. It deliberately drops PATH and the current directory from the default search, which nothing
/// in the worker relies on: .NET and Whisper.net load their own libraries by full path, and the display
/// driver's <c>nvcuda.dll</c> lives in System32.
/// </remarks>
public static class NativeRuntimeSearchPath
{
    /// <summary><c>LOAD_LIBRARY_SEARCH_DEFAULT_DIRS</c>: application folder, System32, user-added folders.</summary>
    internal const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    public static NativeRuntimeSearchPathOutcome Configure(string? runtimeDirectory) =>
        Configure(runtimeDirectory, Directory.Exists, Kernel32DllDirectories.Instance);

    internal static NativeRuntimeSearchPathOutcome Configure(
        string? runtimeDirectory,
        Func<string, bool> directoryExists,
        INativeDllDirectories nativeDirectories)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(nativeDirectories);
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return NativeRuntimeSearchPathOutcome.NoDirectory;
        }

        var fullPath = Path.GetFullPath(runtimeDirectory);
        if (!directoryExists(fullPath))
        {
            return NativeRuntimeSearchPathOutcome.DirectoryMissing;
        }

        // THE DEFAULT FIRST, THEN THE FOLDER. A folder added while the process still uses the standard
        // search order is consulted only by loads that pass LOAD_LIBRARY_SEARCH_USER_DIRS themselves, which
        // neither engine does; it is the default that makes every by-name load see it.
        if (!nativeDirectories.SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs))
        {
            return NativeRuntimeSearchPathOutcome.Refused;
        }

        return nativeDirectories.AddDllDirectory(fullPath)
            ? NativeRuntimeSearchPathOutcome.Added
            : NativeRuntimeSearchPathOutcome.Refused;
    }
}

/// <summary>The two kernel32 calls, behind an interface so the order and the refusals can be tested.</summary>
internal interface INativeDllDirectories
{
    bool SetDefaultDllDirectories(uint flags);

    bool AddDllDirectory(string directory);
}

internal sealed class Kernel32DllDirectories : INativeDllDirectories
{
    public static Kernel32DllDirectories Instance { get; } = new();

    private Kernel32DllDirectories()
    {
    }

    bool INativeDllDirectories.SetDefaultDllDirectories(uint flags) => SetDefaultDllDirectories(flags);

    bool INativeDllDirectories.AddDllDirectory(string directory) => AddDllDirectory(directory) != IntPtr.Zero;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
