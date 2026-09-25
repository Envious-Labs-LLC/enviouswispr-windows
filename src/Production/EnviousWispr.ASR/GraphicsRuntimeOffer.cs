using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.ASR;

/// <summary>
/// Whether to offer the NVIDIA graphics runtime download: only when the engine this PC is set to use would
/// run on its NVIDIA card if the runtime were here, and it is not.
/// </summary>
/// <remarks>
/// THE RUNTIME IS NOT IN THE STORE PACKAGE (founder decision 2026-09-25): about 1.3 GB that only NVIDIA owners
/// can use arrives as a separate download, like a speech model, so the package stays small for everyone.
/// Offering it where it cannot help would cost a person that download for nothing, so the question is asked
/// of the SELECTORS THEMSELVES rather than restated here: the same hardware with the runtime present, put to
/// the engine's own selector, must come back on the card. A rule written out a second time would drift from
/// the selectors the first time either changed.
///
/// NEVER WITHOUT AN NVIDIA CARD. The selectors already require the NVIDIA driver and a device; the offer also
/// requires an active NVIDIA adapter, so a machine whose driver answers but whose display adapter list does
/// not show the card is not asked for 1.3 GB on the strength of one probe.
///
/// NOT FOR PARAKEET AS THE STORE DELIVERS IT. Parakeet runs on the card only with its full-precision model,
/// and the delivered Parakeet pack is the quantized one, so on a Store install with Parakeet selected the
/// selector keeps the processor whatever the runtime - and this says so by not offering.
/// </remarks>
public static class GraphicsRuntimeOffer
{
    public static bool ShouldOffer(
        HardwareSnapshot hardware,
        FinalAsrEngine engine,
        ParakeetModelInventory? parakeetModels,
        WhisperModelInventory? whisperModels,
        bool runtimeInstalled)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        if (runtimeInstalled ||
            !hardware.Cuda.IsDriverAvailable ||
            hardware.Cuda.DeviceCount <= 0 ||
            !hardware.HasActiveAdapter(GraphicsVendor.Nvidia))
        {
            return false;
        }

        var withRuntime = hardware with
        {
            IsOnnxRuntimeCudaDependencySetAvailable = true,
            IsWhisperCudaDependencySetAvailable = true,
        };
        return engine == FinalAsrEngine.Whisper
            ? whisperModels is not null &&
                WhisperRuntimeSelector.Select(withRuntime, whisperModels).Provider == RuntimeProviderKind.Cuda &&
                WhisperRuntimeSelector.Select(hardware, whisperModels).Provider != RuntimeProviderKind.Cuda
            : parakeetModels is not null &&
                ParakeetRuntimeSelector.Select(withRuntime, parakeetModels).Provider == RuntimeProviderKind.Cuda &&
                ParakeetRuntimeSelector.Select(hardware, parakeetModels).Provider != RuntimeProviderKind.Cuda;
    }
}
