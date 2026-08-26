namespace EnviousWispr.Core.Dictation;

public readonly record struct DictationSessionId(Guid Value)
{
    public static DictationSessionId Create() => new(Guid.NewGuid());
}

public sealed record CapturedAudio(
    DictationSessionId SessionId,
    ReadOnlyMemory<float> Samples,
    int SampleRate,
    int Channels);

public sealed record Transcript(DictationSessionId SessionId, string Text, string EngineId);

public sealed record ProcessedText(DictationSessionId SessionId, string Text);

public sealed record DeliveryResult(DictationSessionId SessionId, bool Delivered, bool ClipboardFallback);

public interface IAudioCapture
{
    Task StartAsync(DictationSessionId sessionId, CancellationToken cancellationToken = default);

    Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}

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
