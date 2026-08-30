namespace EnviousWispr.Services.Runtime;

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

    public static bool IsComplete(string? preferredRuntimeDirectory)
    {
        var searchDirectories = new List<string>();
        AddDirectory(searchDirectories, preferredRuntimeDirectory);
        AddDirectory(searchDirectories, AppContext.BaseDirectory);
        foreach (var pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddDirectory(searchDirectories, pathEntry);
        }

        return IsCompleteInDirectories(searchDirectories);
    }

    internal static bool IsCompleteInDirectories(IEnumerable<string> searchDirectories)
    {
        var resolvedDirectories = searchDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(TryResolveExistingDirectory)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return RequiredLibraryNames.All(library =>
            resolvedDirectories.Any(directory => File.Exists(Path.Combine(directory, library))));
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
