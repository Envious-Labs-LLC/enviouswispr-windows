using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

public static class WhisperRuntimeSelector
{
    public static WhisperRuntimeSelection Select(
        HardwareSnapshot hardware,
        WhisperModelInventory models,
        RuntimeProviderPreference preference = RuntimeProviderPreference.Automatic)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(models);
        if (hardware.Architecture != ProcessorArchitectureKind.X64)
        {
            return Failure(WhisperRuntimeSelectionReason.UnsupportedProcessorArchitecture);
        }

        var threads = Math.Clamp(hardware.PhysicalCoreCount > 0
            ? hardware.PhysicalCoreCount
            : Math.Max(1, hardware.LogicalProcessorCount / 2), 2, 8);
        var cudaAvailable = hardware.Cuda.IsDriverAvailable && hardware.Cuda.DeviceCount > 0;

        if (preference == RuntimeProviderPreference.Cuda)
        {
            if (!cudaAvailable)
            {
                return Failure(WhisperRuntimeSelectionReason.RequestedProviderUnavailable);
            }

            return SelectCuda(models, threads, manual: true);
        }

        if (preference == RuntimeProviderPreference.DirectMl)
        {
            return Failure(WhisperRuntimeSelectionReason.RequestedProviderUnavailable);
        }

        if (preference == RuntimeProviderPreference.Automatic && cudaAvailable)
        {
            var cuda = SelectCuda(models, threads, manual: false);
            if (cuda.Succeeded)
            {
                return cuda;
            }
        }

        if (models.QuantizedComplete)
        {
            return new WhisperRuntimeSelection(
                true,
                RuntimeProviderKind.Cpu,
                WhisperModelPack.Quantized,
                threads,
                preference == RuntimeProviderPreference.Cpu
                    ? WhisperRuntimeSelectionReason.ManualProviderAccepted
                    : WhisperRuntimeSelectionReason.TunedCpuWithQuantizedModel);
        }

        if (models.FullPrecisionComplete)
        {
            return new WhisperRuntimeSelection(
                true,
                RuntimeProviderKind.Cpu,
                WhisperModelPack.FullPrecision,
                threads,
                preference == RuntimeProviderPreference.Cpu
                    ? WhisperRuntimeSelectionReason.ManualProviderAccepted
                    : WhisperRuntimeSelectionReason.TunedCpuWithFullPrecisionModel);
        }

        return Failure(WhisperRuntimeSelectionReason.RequiredModelPackMissing);
    }

    private static WhisperRuntimeSelection SelectCuda(
        WhisperModelInventory models,
        int threads,
        bool manual)
    {
        if (models.FullPrecisionComplete)
        {
            return new WhisperRuntimeSelection(
                true,
                RuntimeProviderKind.Cuda,
                WhisperModelPack.FullPrecision,
                threads,
                manual
                    ? WhisperRuntimeSelectionReason.ManualProviderAccepted
                    : WhisperRuntimeSelectionReason.NvidiaCudaWithFullPrecisionModel);
        }

        if (models.QuantizedComplete)
        {
            return new WhisperRuntimeSelection(
                true,
                RuntimeProviderKind.Cuda,
                WhisperModelPack.Quantized,
                threads,
                manual
                    ? WhisperRuntimeSelectionReason.ManualProviderAccepted
                    : WhisperRuntimeSelectionReason.NvidiaCudaWithQuantizedModel);
        }

        return Failure(WhisperRuntimeSelectionReason.RequiredModelPackMissing);
    }

    private static WhisperRuntimeSelection Failure(WhisperRuntimeSelectionReason reason) => new(
        false,
        Provider: null,
        ModelPack: null,
        ThreadCount: 0,
        reason,
        new AppError(
            reason == WhisperRuntimeSelectionReason.RequiredModelPackMissing
                ? AppErrorCode.ModelPackUnavailable
                : AppErrorCode.RuntimeProviderUnavailable,
            AppErrorStage.FinalAsr,
            CanRetry: true));
}
