using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Services.Runtime;

/// <summary>Whether the CUDA runtime files each engine loads are on this machine.</summary>
/// <remarks>
/// THE FILE SETS ARE <see cref="CudaRuntimeLibraries"/>'S, where the reason each file is required is kept.
///
/// THE SEARCH IS WHERE THE WORKER CAN FIND THEM: the configured runtime folder (which the worker adds to
/// its DLL search with <c>AddDllDirectory</c>), the app's own folder, and the folder the CUDA backend itself
/// is loaded from (`runtimes/cuda/win-x64`, which the loader searches for that library's dependencies).
/// NOT PATH, WHICH THE WORKER NO LONGER SEARCHES: a packaged process never searches PATH for a DLL, and
/// the worker now switches its default search to the application folder, System32 and the folders it
/// adds, packaged or not (<c>NativeRuntimeSearchPath</c>). A probe that still counted PATH would put a
/// card on files the worker cannot load. System32 is not searched here either; no shipped layout puts
/// cuBLAS there.
/// </remarks>
public static class CudaRuntimeDependencyProbe
{
    internal static IReadOnlyList<string> RequiredLibraryNames => CudaRuntimeLibraries.Required;

    internal static string WhisperCudaBackendDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "win-x64");

    internal static IReadOnlyList<string> WhisperRequiredLibraryNames => CudaRuntimeLibraries.Whisper;

    /// <summary>The onnxruntime (Parakeet) set.</summary>
    public static bool IsComplete(string? preferredRuntimeDirectory) =>
        IsCompleteInDirectories(SearchDirectories(preferredRuntimeDirectory, whisperBackend: false), RequiredLibraryNames);

    /// <summary>The whisper.cpp set.</summary>
    public static bool IsWhisperComplete(string? preferredRuntimeDirectory) =>
        IsCompleteInDirectories(SearchDirectories(preferredRuntimeDirectory, whisperBackend: true), WhisperRequiredLibraryNames);

    internal static bool IsCompleteInDirectories(IEnumerable<string> searchDirectories) =>
        IsCompleteInDirectories(searchDirectories, RequiredLibraryNames);

    internal static bool IsCompleteInDirectories(
        IEnumerable<string> searchDirectories,
        IReadOnlyList<string> requiredLibraryNames)
    {
        var resolvedDirectories = searchDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(TryResolveExistingDirectory)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return requiredLibraryNames.All(library =>
            resolvedDirectories.Any(directory => File.Exists(Path.Combine(directory, library))));
    }

    private static List<string> SearchDirectories(string? preferredRuntimeDirectory, bool whisperBackend)
    {
        var searchDirectories = new List<string>();
        AddDirectory(searchDirectories, preferredRuntimeDirectory);
        AddDirectory(searchDirectories, AppContext.BaseDirectory);
        if (whisperBackend)
        {
            // Where Whisper.net loads ggml-cuda from; the loader searches a library's own folder for its
            // dependencies. onnxruntime loads nothing from here, so the Parakeet set does not look.
            AddDirectory(searchDirectories, WhisperCudaBackendDirectory);
        }

        return searchDirectories;
    }

    private static string? TryResolveExistingDirectory(string candidate)
    {
        try
        {
            var resolved = Path.GetFullPath(candidate);
            return Directory.Exists(resolved) ? resolved : null;
        }
        catch (Exception exception) when (exception is
                                          ArgumentException or
                                          IOException or
                                          NotSupportedException or
                                          UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddDirectory(List<string> directories, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            directories.Add(candidate);
        }
    }
}
