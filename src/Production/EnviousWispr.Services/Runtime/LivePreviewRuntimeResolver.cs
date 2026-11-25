using System.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Services.Runtime;

/// <summary>
/// Resolves which live preview runtime runs for a given preference and model inventory (#127).
/// </summary>
/// <remarks>
/// Mirrors macOS <c>LivePreviewEngineChoice</c> and <c>appleStandingIn</c>:
/// <list type="bullet">
/// <item><description><see cref="LivePreviewEngineChoice.Parakeet"/> runs quantized INT8 Parakeet on CPU (<c>BelowNormal</c>, 2-8 threads, 0 VRAM, no FP32 fallback).</description></item>
/// <item><description><see cref="LivePreviewEngineChoice.Whisper"/> runs <c>whisper-small</c> on CUDA when available (else CPU); when <c>whisper-small</c> is not yet installed, <c>Parakeet</c> stands in automatically if installed.</description></item>
/// </list>
/// </remarks>
public sealed record LivePreviewRuntimePlan(
    bool Succeeded,
    LivePreviewEngineChoice RequestedEngine,
    LivePreviewEngineChoice? EffectiveEngine = null,
    bool IsStandingIn = false,
    RuntimeWorkerTranscriptionOptions? Options = null,
    AppErrorCode? UnavailableReason = null);

public static class LivePreviewRuntimeResolver
{
    public static string RequiredModelId(LivePreviewEngineChoice engine) =>
        engine == LivePreviewEngineChoice.Whisper
            ? WhisperModelIds.Preview
            : ParakeetModelIds.Final;

    public static int ChoosePreviewThreads(HardwareSnapshot hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        return Math.Clamp(
            hardware.PhysicalCoreCount > 0
                ? hardware.PhysicalCoreCount
                : Math.Max(1, hardware.LogicalProcessorCount / 2),
            2,
            8);
    }

    public static LivePreviewRuntimePlan Resolve(
        LivePreviewEngineChoice requestedChoice,
        string workerExecutable,
        HardwareSnapshot hardware,
        string? parakeetModelDirectory,
        bool parakeetInt8Complete,
        string? whisperPreviewModelDirectory,
        bool whisperPreviewComplete,
        RuntimeProviderKind whisperProvider,
        string whisperLanguage = "auto",
        Func<string?>? currentWhisperLanguage = null,
        string? cudaRuntimeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutable);
        ArgumentNullException.ThrowIfNull(hardware);

        var parakeetReady = !string.IsNullOrWhiteSpace(parakeetModelDirectory) && parakeetInt8Complete;
        var whisperReady = !string.IsNullOrWhiteSpace(whisperPreviewModelDirectory) && whisperPreviewComplete;

        if (requestedChoice == LivePreviewEngineChoice.Whisper)
        {
            if (whisperReady)
            {
                return new LivePreviewRuntimePlan(
                    Succeeded: true,
                    RequestedEngine: requestedChoice,
                    EffectiveEngine: LivePreviewEngineChoice.Whisper,
                    IsStandingIn: false,
                    Options: CreateWhisperPreviewOptions(
                        workerExecutable,
                        whisperPreviewModelDirectory!,
                        whisperProvider,
                        hardware,
                        whisperLanguage,
                        currentWhisperLanguage,
                        cudaRuntimeDirectory));
            }

            if (parakeetReady)
            {
                return new LivePreviewRuntimePlan(
                    Succeeded: true,
                    RequestedEngine: requestedChoice,
                    EffectiveEngine: LivePreviewEngineChoice.Parakeet,
                    IsStandingIn: true,
                    Options: CreateParakeetPreviewOptions(
                        workerExecutable,
                        parakeetModelDirectory!,
                        hardware,
                        whisperLanguage,
                        currentWhisperLanguage));
            }

            return new LivePreviewRuntimePlan(
                Succeeded: false,
                RequestedEngine: requestedChoice,
                UnavailableReason: AppErrorCode.ModelPackUnavailable);
        }

        if (parakeetReady)
        {
            return new LivePreviewRuntimePlan(
                Succeeded: true,
                RequestedEngine: LivePreviewEngineChoice.Parakeet,
                EffectiveEngine: LivePreviewEngineChoice.Parakeet,
                IsStandingIn: false,
                Options: CreateParakeetPreviewOptions(
                    workerExecutable,
                    parakeetModelDirectory!,
                    hardware,
                    whisperLanguage,
                    currentWhisperLanguage));
        }

        if (whisperReady)
        {
            return new LivePreviewRuntimePlan(
                Succeeded: true,
                RequestedEngine: LivePreviewEngineChoice.Parakeet,
                EffectiveEngine: LivePreviewEngineChoice.Whisper,
                IsStandingIn: true,
                Options: CreateWhisperPreviewOptions(
                    workerExecutable,
                    whisperPreviewModelDirectory!,
                    whisperProvider,
                    hardware,
                    whisperLanguage,
                    currentWhisperLanguage,
                    cudaRuntimeDirectory));
        }

        return new LivePreviewRuntimePlan(
            Succeeded: false,
            RequestedEngine: LivePreviewEngineChoice.Parakeet,
            UnavailableReason: AppErrorCode.ModelPackUnavailable);
    }

    public static RuntimeWorkerTranscriptionOptions CreateParakeetPreviewOptions(
        string workerExecutable,
        string modelDirectory,
        HardwareSnapshot hardware,
        string whisperLanguage = "auto",
        Func<string?>? currentWhisperLanguage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(hardware);

        var threads = ChoosePreviewThreads(hardware);
        return new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            RuntimeProviderKind.Cpu,
            ParakeetModelPack.Quantized,
            threads,
            InterOpThreads: 1,
            CpuFallbackThreads: threads,
            StartupTimeout: TimeSpan.FromSeconds(15),
            TranscriptionTimeout: TimeSpan.FromSeconds(15),
            MaximumWorkerRestarts: 0,
            Engine: FinalAsrEngine.Parakeet,
            Language: string.IsNullOrWhiteSpace(whisperLanguage) ? LanguageCatalog.AutomaticCode : whisperLanguage,
            CurrentLanguage: currentWhisperLanguage,
            WorkerPriority: ProcessPriorityClass.BelowNormal,
            AllowFullPrecisionFallback: false);
    }

    public static RuntimeWorkerTranscriptionOptions CreateWhisperPreviewOptions(
        string workerExecutable,
        string modelDirectory,
        RuntimeProviderKind provider,
        HardwareSnapshot hardware,
        string language = "auto",
        Func<string?>? currentLanguage = null,
        string? cudaRuntimeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(hardware);

        var threads = ChoosePreviewThreads(hardware);
        return new RuntimeWorkerTranscriptionOptions(
            workerExecutable,
            modelDirectory,
            provider,
            ParakeetModelPack.Quantized,
            threads,
            InterOpThreads: 1,
            CpuFallbackThreads: threads,
            CudaRuntimeDirectory: cudaRuntimeDirectory,
            StartupTimeout: TimeSpan.FromSeconds(15),
            TranscriptionTimeout: TimeSpan.FromSeconds(15),
            MaximumWorkerRestarts: 0,
            Engine: FinalAsrEngine.Whisper,
            WhisperPack: WhisperModelPack.PreviewSmall,
            Language: language,
            WorkerPriority: ProcessPriorityClass.BelowNormal,
            CurrentLanguage: currentLanguage);
    }
}
