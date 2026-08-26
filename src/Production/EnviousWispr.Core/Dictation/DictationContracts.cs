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
    AppError? DegradedError = null);

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

public interface IPolishProvider
{
    string ProviderId { get; }

    Task<ProcessedText?> TryPolishAsync(ProcessedText input, CancellationToken cancellationToken = default);
}

public interface ITextDelivery
{
    Task<DeliveryResult> DeliverAsync(ProcessedText text, CancellationToken cancellationToken = default);
}

public interface IModelCatalog
{
    Task<IReadOnlyList<string>> GetAvailableModelIdsAsync(CancellationToken cancellationToken = default);
}
