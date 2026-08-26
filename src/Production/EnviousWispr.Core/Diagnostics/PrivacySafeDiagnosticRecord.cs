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

public sealed record PrivacySafeDiagnosticRecord(
    DateTimeOffset Timestamp,
    AppEventCode Event,
    AppFailureCategory Failure,
    long? ElapsedMilliseconds = null,
    DiagnosticProvider? Provider = null,
    AppErrorCode? ErrorCode = null,
    DiagnosticEngineChoice? Engine = null,
    DiagnosticHardwareClass? HardwareClass = null)
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
                : null);
    }
}
