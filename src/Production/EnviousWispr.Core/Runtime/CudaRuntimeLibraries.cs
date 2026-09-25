namespace EnviousWispr.Core.Runtime;

/// <summary>The CUDA runtime files each speech engine loads, named once for the app, the probe and the resolver.</summary>
/// <remarks>
/// TWO ENGINES, TWO FILE SETS, AND NEITHER MAY BORROW THE OTHER'S ANSWER. Parakeet runs on onnxruntime,
/// whose CUDA provider needs cuBLAS, cuFFT, the CUDA runtime and all of cuDNN. whisper.cpp's CUDA backend
/// imports cuBLAS alone and cuBLAS imports cuBLASLt (read from the PE import tables of the pinned Whisper.net
/// 1.9.1 CUDA runtime). Asking Whisper about the onnxruntime set put a working card on the processor for
/// want of cuDNN (#99); asking it nothing put a card with no files on the card and the worker failed to
/// start (#163).
///
/// `cudart64_13` IS REQUIRED BY THE MANAGED LOADER, NOT BY THE NATIVE IMPORTS. None of the three native
/// libraries imports it; Whisper.net's own `CudaHelper` loads it by name to decide whether CUDA is
/// available at all (the pinned `Whisper.net.dll` carries the name `cudart64_13`), so without it the
/// CUDA runtime is never chosen.
/// </remarks>
public static class CudaRuntimeLibraries
{
    /// <summary>The onnxruntime (Parakeet) set, which is also exactly what the graphics runtime pack delivers.</summary>
    public static IReadOnlyList<string> Required { get; } =
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

    /// <summary>The whisper.cpp set.</summary>
    public static IReadOnlyList<string> Whisper { get; } =
    [
        "cublasLt64_13.dll",
        "cublas64_13.dll",
        "cudart64_13.dll",
    ];

    /// <summary>Whether one folder holds every file of the full set.</summary>
    public static bool AllPresentIn(string directory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        return !string.IsNullOrWhiteSpace(directory) &&
            Required.All(library => fileExists(Path.Combine(directory, library)));
    }
}
