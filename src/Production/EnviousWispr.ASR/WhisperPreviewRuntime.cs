using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

/// <summary>Which processor Live Preview's engine runs on.</summary>
/// <remarks>
/// THE PREVIEW MUST NOT RUN WHERE THE FINAL ENGINE CANNOT. Live Preview runs whisper.cpp on the card,
/// and running on the card needs three things at once: a driver, a device, and the CUDA runtime files
/// themselves (the `runtime/cuda` folder). The selector previously checked only the first two, so on a
/// machine with a working card but no CUDA runtime it chose the card, the worker then failed to start
/// with the runtime missing, and the preview came up red. Ref: #163.
///
/// THE RUNTIME-FILES CHECK IS THE ONE THE FINAL ENGINE ALREADY USES. `IsOnnxRuntimeCudaDependencySetAvailable`
/// is populated by `CudaRuntimeDependencyProbe` (the `runtime/cuda` file probe) in the hardware
/// discovery, so the preview and the final transcription now agree about what this machine can do -
/// not just "is there a card" but "is there a card with the files to run on it".
///
/// LIFTED OUT OF THE APP SO IT CAN BE TESTED AT ALL. It lived inline in a WinUI startup path that no
/// test in this repository can reach, which is why a check for the wrong thing sat there unnoticed.
/// Ref: #99, #163.
/// </remarks>
public static class WhisperPreviewRuntime
{
    /// <param name="forceCpu">
    /// Set when the final engine has already fallen back to the processor. The preview must not then
    /// claim the card: it would be competing with a transcription that already failed to get it.
    /// </param>
    public static RuntimeProviderKind Select(HardwareSnapshot hardware, bool forceCpu = false)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        return !forceCpu &&
            hardware.Architecture == ProcessorArchitectureKind.X64 &&
            hardware.Cuda.IsDriverAvailable &&
            hardware.Cuda.DeviceCount > 0 &&
            hardware.IsOnnxRuntimeCudaDependencySetAvailable
                ? RuntimeProviderKind.Cuda
                : RuntimeProviderKind.Cpu;
    }
}
