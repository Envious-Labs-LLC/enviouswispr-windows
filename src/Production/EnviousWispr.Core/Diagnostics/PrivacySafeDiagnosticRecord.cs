using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

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

/// <summary>The language a take was recognised in, as the engine was told it: detection or one fixed language.</summary>
/// <remarks>
/// A SETTING, NEVER WHAT WAS SAID. It is the choice the person made in the language picker (or the
/// pill's Lock), carried to the engine; it says nothing about the words and is the same for every
/// take until the choice changes. Written so "did my language change take effect" is answered by the
/// log rather than inferred (#241). An engine that takes no language (Parakeet) writes nothing, and a
/// code outside the picker's list is Other rather than absent.
/// </remarks>
public enum DiagnosticRecognitionLanguage
{
    Automatic,
    English,
    French,
    German,
    Spanish,
    Other,
}

public static class DiagnosticRecognitionLanguages
{
    /// <summary>The category for a language code an engine reports it was told; null when it was told none.</summary>
    public static DiagnosticRecognitionLanguage? From(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return WhisperLanguageCodes.TryNormalize(code, out var normalized)
            ? normalized switch
            {
                "auto" => DiagnosticRecognitionLanguage.Automatic,
                "en" => DiagnosticRecognitionLanguage.English,
                "fr" => DiagnosticRecognitionLanguage.French,
                "de" => DiagnosticRecognitionLanguage.German,
                "es" => DiagnosticRecognitionLanguage.Spanish,
                _ => DiagnosticRecognitionLanguage.Other,
            }
            : DiagnosticRecognitionLanguage.Other;
    }
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
    DiagnosticRuntimeSelectionReason? RuntimeSelection = null,
    DeliveryStage? DeliveryStage = null,
    DeliveryFaultKind? Fault = null,
    DiagnosticRecognitionLanguage? RecognitionLanguage = null,
    DiagnosticSnippetImport? SnippetImport = null,
    DiagnosticWordImport? WordImport = null)
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
                : null,
            // WHERE A DELIVERY FAULTED AND WHAT FAMILY THE FAULT WAS: two fixed enums (plan-2 step
            // 13), never the exception's type name or message.
            entry.DeliveryStage is { } deliveryStage && Enum.IsDefined(deliveryStage) ? deliveryStage : null,
            entry.Fault is { } fault && Enum.IsDefined(fault) ? fault : null,
            // THE PICKER'S CHOICE AS A CATEGORY (#241): one of six fixed members, never a code string.
            entry.RecognitionLanguage is { } recognitionLanguage && Enum.IsDefined(recognitionLanguage)
                ? recognitionLanguage
                : null,
            // A SNIPPET IMPORT AS COUNTS AND CATEGORIES, never a trigger or a snippet's text; dropped whole when any
            // member is out of range rather than trimmed into something that looks true.
            entry.SnippetImport is { } snippetImport && snippetImport.IsWithinBounds() ? snippetImport : null,
            // A WORD IMPORT FROM ANOTHER APP, the same way: counts and categories, never a word or a spelling.
            entry.WordImport is { } wordImport && wordImport.IsWithinBounds() ? wordImport : null);
    }
}
