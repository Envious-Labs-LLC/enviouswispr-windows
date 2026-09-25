using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

/// <summary>Which processor Live Preview's engine runs on.</summary>
/// <remarks>
/// THE CARD COUNTS ONLY WITH THE FILES WHISPER.CPP LOADS, AND ONLY THOSE. A driver and a device are
/// not enough: with no cuBLAS on the machine the preview worker starts on the card, fails to load its
/// runtime, and the preview never appears (#163). And onnxruntime's files are not the question: they
/// belong to PARAKEET, and requiring them put a card whisper.cpp could use on the processor for want
/// of cuDNN, which it never loads (#99). `IsWhisperCudaDependencySetAvailable` is the one answer, and
/// `WhisperRuntimeSelector` asks it too, so the preview and the final transcription agree.
///
/// LIFTED OUT OF THE APP SO IT CAN BE TESTED AT ALL. It lived inline in a WinUI startup path that no
/// test in this repository can reach, which is why a probe for the wrong runtime sat there
/// unnoticed. Ref: #99, #163.
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
            hardware.IsWhisperCudaDependencySetAvailable
                ? RuntimeProviderKind.Cuda
                : RuntimeProviderKind.Cpu;
    }
}
