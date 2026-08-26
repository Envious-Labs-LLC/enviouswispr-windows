using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Dictation;

public readonly record struct DictationSessionId(Guid Value)
{
    public static DictationSessionId Create() => new(Guid.NewGuid());
}

public enum AudioCaptureOutcome
{
    Completed,
    Interrupted,
    Cancelled,
}

public sealed record CapturedAudio(
    DictationSessionId SessionId,
    ReadOnlyMemory<float> Samples,
    int SampleRate,
    int Channels,
    AudioCaptureOutcome Outcome = AudioCaptureOutcome.Completed,
    AppError? Error = null);

public sealed record TranscriptTokenTiming(
    string Text,
    TimeSpan Start,
    TimeSpan End);

public sealed record Transcript(
    DictationSessionId SessionId,
    string Text,
    string EngineId,
    IReadOnlyList<TranscriptTokenTiming>? TokenTimings = null,
    bool UsedFallback = false,
    AppError? DegradedError = null,
    string? DetectedLanguage = null);

public sealed record ProcessedText(DictationSessionId SessionId, string Text);

public sealed record DeliveryResult(DictationSessionId SessionId, bool Delivered, bool ClipboardFallback);

public interface ITranscriptionEngine
{
    string EngineId { get; }

    Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default);
}

public interface IDeterministicTextProcessor
{
    ProcessedText Process(Transcript transcript);
}

public enum PolishAttemptStatus
{
    Polished,
    Unchanged,
    Unavailable,
    InputTooLarge,
    TimedOut,
    Failed,
}

public sealed record PolishRequest(
    ProcessedText Input,
    string? DetectedLanguage = null);

public sealed record PolishResult(
    ProcessedText Output,
    PolishAttemptStatus Status,
    AppError? Error = null,
    long ElapsedMilliseconds = 0)
{
    public bool UsedFallback => Status is not (PolishAttemptStatus.Polished or PolishAttemptStatus.Unchanged);
}

public interface IPolishProvider : IAsyncDisposable
{
    string ProviderId { get; }

    Task<PolishResult> TryPolishAsync(
        PolishRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITextDelivery
{
    Task<DeliveryResult> DeliverAsync(ProcessedText text, CancellationToken cancellationToken = default);
}

public interface IModelCatalog
{
    Task<IReadOnlyList<string>> GetAvailableModelIdsAsync(CancellationToken cancellationToken = default);
}
