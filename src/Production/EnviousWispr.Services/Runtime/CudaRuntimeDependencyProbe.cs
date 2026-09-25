namespace EnviousWispr.Services.Runtime;

/// <summary>Whether the CUDA runtime files each engine loads are on this machine.</summary>
/// <remarks>
/// TWO ENGINES, TWO FILE SETS, AND NEITHER MAY BORROW THE OTHER'S ANSWER. Parakeet runs on onnxruntime,
/// whose CUDA provider needs cuBLAS, cuFFT, the CUDA runtime and all of cuDNN. whisper.cpp's CUDA backend
/// imports cuBLAS alone; cuBLAS pulls cuBLASLt, and cuBLASLt loads the CUDA runtime on its first call
/// (measured from the import tables of the pinned Whisper.net 1.9.1 CUDA runtime). Asking Whisper about
/// the onnxruntime set put a working card on the processor for want of cuDNN (#99); asking it nothing
/// put a card with no files on the card and the worker failed to start (#163).
/// </remarks>
public static class CudaRuntimeDependencyProbe
{
    internal static IReadOnlyList<string> RequiredLibraryNames { get; } =
    [
        "cublasLt64_13.dll",
        "cublas64_13.dll",
        "cufft64_12.dll",
        "cudart64_13.dll",
        "cudnn64_9.dll",
        "cudnn_adv64_9.dll",
        "cudnn_engines_precompiled64_9.dll",
        "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_engines_tensor_ir64_9.dll",
        "cudnn_graph64_9.dll",
        "cudnn_heuristic64_9.dll",
        "cudnn_ops64_9.dll",
    ];

    internal static IReadOnlyList<string> WhisperRequiredLibraryNames { get; } =
    [
        "cublasLt64_13.dll",
        "cublas64_13.dll",
        "cudart64_13.dll",
    ];

    /// <summary>The onnxruntime (Parakeet) set.</summary>
    public static bool IsComplete(string? preferredRuntimeDirectory) =>
        IsCompleteInDirectories(SearchDirectories(preferredRuntimeDirectory), RequiredLibraryNames);

    /// <summary>The whisper.cpp set.</summary>
    public static bool IsWhisperComplete(string? preferredRuntimeDirectory) =>
        IsCompleteInDirectories(SearchDirectories(preferredRuntimeDirectory), WhisperRequiredLibraryNames);

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

    private static List<string> SearchDirectories(string? preferredRuntimeDirectory)
    {
        var searchDirectories = new List<string>();
        AddDirectory(searchDirectories, preferredRuntimeDirectory);
        AddDirectory(searchDirectories, AppContext.BaseDirectory);
        foreach (var pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddDirectory(searchDirectories, pathEntry);
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
