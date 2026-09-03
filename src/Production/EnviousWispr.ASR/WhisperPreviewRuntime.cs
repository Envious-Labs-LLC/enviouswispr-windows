using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

/// <summary>Which processor Live Preview's engine runs on.</summary>
/// <remarks>
/// IT USED TO ASK ABOUT THE WRONG LIBRARY. Live Preview runs whisper.cpp, and the decision required
/// `IsOnnxRuntimeCudaDependencySetAvailable` - an onnxruntime probe. onnxruntime is what PARAKEET
/// uses; whisper.cpp ships its own CUDA build and neither knows nor cares whether onnxruntime's
/// dependency set is present. So a machine whose graphics card works perfectly well for whisper.cpp
/// was put on the processor because a different library's files were missing.
///
/// THE CONDITION IS NOW THE ONE `WhisperRuntimeSelector` ALREADY USES for the final Whisper engine -
/// a driver and at least one device - so the preview and the final transcription agree about what
/// this machine can do. Two answers to one question was the defect; there is now one answer.
///
/// LIFTED OUT OF THE APP SO IT CAN BE TESTED AT ALL. It lived inline in a WinUI startup path that no
/// test in this repository can reach, which is why a probe for the wrong runtime sat there
/// unnoticed. Ref: #99.
///
/// THIS IS NOT A SPEED CLAIM ON THE DEVELOPMENT MACHINE. Both probes are true there, so the fix
/// changes nothing locally and could not be measured by running it. What it changes is the machine
/// where they disagree, and the tests below are that machine.
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
            hardware.Cuda.DeviceCount > 0
                ? RuntimeProviderKind.Cuda
                : RuntimeProviderKind.Cpu;
    }
}
