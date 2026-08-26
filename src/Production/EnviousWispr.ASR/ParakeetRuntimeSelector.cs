using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.ASR;

public static class ParakeetRuntimeSelector
{
    public static RuntimeSelection Select(
        HardwareSnapshot hardware,
        ParakeetModelInventory models,
        RuntimeProviderPreference preference = RuntimeProviderPreference.Automatic)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(models);

        if (hardware.Architecture != ProcessorArchitectureKind.X64)
        {
            return Failure(
                RuntimeSelectionReason.UnsupportedProcessorArchitecture,
                AppErrorCode.RuntimeProviderUnavailable);
        }

        return preference switch
        {
            RuntimeProviderPreference.Automatic => SelectAutomatic(hardware, models),
            RuntimeProviderPreference.Cpu => SelectCpu(hardware, models, manual: true),
            RuntimeProviderPreference.Cuda => SelectCuda(hardware, models, manual: true),
            RuntimeProviderPreference.DirectMl => Failure(
                RuntimeSelectionReason.DirectMlIncompatibleWithParakeetDecoder,
                AppErrorCode.RuntimeProviderIncompatible),
            _ => Failure(
                RuntimeSelectionReason.RequestedProviderUnavailable,
                AppErrorCode.RuntimeProviderUnavailable),
        };
    }

    public static int ChooseCpuIntraOpThreads(HardwareSnapshot hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        var physicalCores = hardware.PhysicalCoreCount > 0
            ? hardware.PhysicalCoreCount
            : Math.Max(1, hardware.LogicalProcessorCount / 2);
        return Math.Clamp(physicalCores / 2, 2, 8);
    }

    private static RuntimeSelection SelectAutomatic(
        HardwareSnapshot hardware,
        ParakeetModelInventory models)
    {
        if (hardware.Cuda.IsDriverAvailable &&
            hardware.Cuda.DeviceCount > 0 &&
            hardware.HasActiveAdapter(GraphicsVendor.Nvidia) &&
            models.Fp32Complete)
        {
            return Success(
                RuntimeProviderKind.Cuda,
                ParakeetModelPack.FullPrecision,
                intraOpThreads: 1,
                RuntimeSelectionReason.NvidiaCudaWithQdqFreeModel);
        }

        return SelectCpu(hardware, models, manual: false);
    }

    private static RuntimeSelection SelectCpu(
        HardwareSnapshot hardware,
        ParakeetModelInventory models,
        bool manual)
    {
        if (!models.Int8Complete)
        {
            return Failure(
                RuntimeSelectionReason.RequiredModelPackMissing,
                AppErrorCode.ModelPackUnavailable);
        }

        return Success(
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            ChooseCpuIntraOpThreads(hardware),
            manual
                ? RuntimeSelectionReason.ManualProviderAccepted
                : RuntimeSelectionReason.TunedCpuUniversalFallback);
    }

    private static RuntimeSelection SelectCuda(
        HardwareSnapshot hardware,
        ParakeetModelInventory models,
        bool manual)
    {
        if (!hardware.Cuda.IsDriverAvailable ||
            hardware.Cuda.DeviceCount == 0 ||
            !hardware.HasActiveAdapter(GraphicsVendor.Nvidia))
        {
            return Failure(
                RuntimeSelectionReason.RequestedProviderUnavailable,
                AppErrorCode.RuntimeProviderUnavailable);
        }

        if (!models.Fp32Complete)
        {
            return Failure(
                RuntimeSelectionReason.RequiredModelPackMissing,
                AppErrorCode.ModelPackUnavailable);
        }

        return Success(
            RuntimeProviderKind.Cuda,
            ParakeetModelPack.FullPrecision,
            intraOpThreads: 1,
            manual
                ? RuntimeSelectionReason.ManualProviderAccepted
                : RuntimeSelectionReason.NvidiaCudaWithQdqFreeModel);
    }

    private static RuntimeSelection Success(
        RuntimeProviderKind provider,
        ParakeetModelPack modelPack,
        int intraOpThreads,
        RuntimeSelectionReason reason) => new(
        Succeeded: true,
        provider,
        modelPack,
        intraOpThreads,
        InterOpThreads: 1,
        reason);

    private static RuntimeSelection Failure(
        RuntimeSelectionReason reason,
        AppErrorCode errorCode) => new(
        Succeeded: false,
        Provider: null,
        ModelPack: null,
        IntraOpThreads: 0,
        InterOpThreads: 0,
        reason,
        new AppError(errorCode, AppErrorStage.RuntimeSelection, CanRetry: true));
}
