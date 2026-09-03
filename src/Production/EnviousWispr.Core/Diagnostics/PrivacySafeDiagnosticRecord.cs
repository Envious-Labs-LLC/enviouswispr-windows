using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Diagnostics;

public enum DiagnosticProvider
{
    EgOne,
    Ollama,
    OpenAi,
    Anthropic,
    Gemini,
}

public static class DiagnosticProviderIds
{
    public static DiagnosticProvider? FromProviderId(string? providerId) =>
        providerId?.Trim().ToLowerInvariant() switch
        {
            "eg-one" or "eg-1" => DiagnosticProvider.EgOne,
            "ollama" => DiagnosticProvider.Ollama,
            "openai" => DiagnosticProvider.OpenAi,
            "anthropic" => DiagnosticProvider.Anthropic,
            "gemini" => DiagnosticProvider.Gemini,
            _ => null,
        };
}

public enum DiagnosticEngineChoice
{
    Parakeet,
    Whisper,
}

public enum DiagnosticHardwareClass
{
    Unknown,
    CpuOnly,
    GpuPresent,
    NvidiaCuda,
}

/// <summary>Which processing path a run ended up on, and what put it there.</summary>
/// <remarks>
/// THE ANSWER TO "WHY IS THIS SLOW", WRITTEN AT THE MOMENT IT IS DECIDED. The engine selectors have
/// always computed a reason and returned it; nothing carried it as far as the log, so a machine that
/// spent days transcribing on the processor beside an idle graphics card had nothing anywhere saying
/// which of "no card", "you asked for this" and "the card was chosen and would not start" was true.
/// Those three are one sentence apart for a reader and a different investigation each. Ref: #102.
///
/// A CATEGORY, NEVER A MESSAGE. Members are fixed and few, so this may cross the network beside
/// Engine and HardwareClass; an exception string never could, and deliberately is not carried here.
///
/// PROCESSOR-AFTER-GPU-FAILED IS THE MEMBER NO SELECTOR CAN PRODUCE. The selection succeeds and the
/// runtime then refuses to start, which is one layer below anything a selector can see, so the app
/// writes that member itself at the point it swaps the engine out.
/// </remarks>
public enum DiagnosticRuntimeSelectionReason
{
    /// <summary>The graphics card was chosen and the run is on it.</summary>
    GpuSelected,

    /// <summary>No usable graphics path was available, so the processor was chosen.</summary>
    ProcessorSelectedNoGpuAvailable,

    /// <summary>The user asked for the processor explicitly.</summary>
    ProcessorSelectedByUserChoice,

    /// <summary>The graphics card was chosen, failed to start, and the processor took over.</summary>
    ProcessorSelectedAfterGpuFailedToStart,

    /// <summary>Nothing was selected: the model pack is missing.</summary>
    SelectionFailedModelPackMissing,

    /// <summary>Nothing was selected: the requested provider is not available here.</summary>
    SelectionFailedProviderUnavailable,

    /// <summary>Nothing was selected: this processor architecture is not supported.</summary>
    SelectionFailedUnsupportedProcessorArchitecture,
}

public sealed record PrivacySafeDiagnosticRecord(
    DateTimeOffset Timestamp,
    AppEventCode Event,
    AppFailureCategory Failure,
    long? ElapsedMilliseconds = null,
    DiagnosticProvider? Provider = null,
    AppErrorCode? ErrorCode = null,
    DiagnosticEngineChoice? Engine = null,
    DiagnosticHardwareClass? HardwareClass = null,
    DeterministicTextStage? Stage = null,
    DeterministicStageStatus? StageStatus = null,
    bool? Changed = null,
    DiagnosticRuntimeSelectionReason? RuntimeSelection = null)
{
    public const long MaximumElapsedMilliseconds = 86_400_000;

    public static PrivacySafeDiagnosticRecord From(AppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new PrivacySafeDiagnosticRecord(
            entry.Timestamp,
            Enum.IsDefined(entry.Event) ? entry.Event : AppEventCode.UnhandledFailure,
            Enum.IsDefined(entry.Failure) ? entry.Failure : AppFailureCategory.Unknown,
            entry.ElapsedMilliseconds is >= 0 and <= MaximumElapsedMilliseconds
                ? entry.ElapsedMilliseconds
                : null,
            entry.Provider is { } provider && Enum.IsDefined(provider) ? provider : null,
            entry.ErrorCode is { } errorCode && Enum.IsDefined(errorCode) ? errorCode : null,
            entry.Engine is { } engine && Enum.IsDefined(engine) ? engine : null,
            entry.HardwareClass is { } hardwareClass && Enum.IsDefined(hardwareClass)
                ? hardwareClass
                : null,
            // ALL THREE ARE CATEGORIES, WHICH IS WHY THEY MAY CROSS THE NETWORK. A stage name and a
            // status are fixed enum members and Changed is a boolean; none of them can carry a word
            // somebody said.
            entry.Stage is { } stage && Enum.IsDefined(stage) ? stage : null,
            entry.StageStatus is { } stageStatus && Enum.IsDefined(stageStatus) ? stageStatus : null,
            entry.Changed,
            entry.RuntimeSelection is { } runtimeSelection && Enum.IsDefined(runtimeSelection)
                ? runtimeSelection
                : null);
    }
}
