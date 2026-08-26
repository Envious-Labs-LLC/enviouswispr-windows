using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class WhisperEngineFactoryTests
{
    [Fact]
    public async Task ConstructorFailureFallsBackToCpuAndKeepsLanguage()
    {
        var factory = new WhisperEngineFactory(options =>
            options.Provider == RuntimeProviderKind.Cuda
                ? throw new TranscriptionEngineException(new AppError(
                    AppErrorCode.RuntimeProviderUnavailable,
                    AppErrorStage.FinalAsr,
                    CanRetry: true))
                : new FakeEngine("whisper:cpu"));
        var creation = factory.Create(
            Options(RuntimeProviderKind.Cuda),
            Options(RuntimeProviderKind.Cpu));

        var transcript = await creation.Engine.TranscribeAsync(Audio());

        Assert.True(creation.UsedFallback);
        Assert.Equal("fr", transcript.DetectedLanguage);
        Assert.Equal(AppErrorCode.RuntimeProviderUnavailable, creation.DegradedError?.Code);
    }

    [Fact]
    public void MissingModelReturnsTypedUnavailableErrorBeforeNativeLoad()
    {
        var exception = Assert.Throws<TranscriptionEngineException>(() =>
            new WhisperTranscriptionEngine(new WhisperEngineOptions(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin"),
                RuntimeProviderKind.Cpu,
                WhisperModelPack.Quantized,
                ThreadCount: 2)));

        Assert.Equal(AppErrorCode.ModelPackUnavailable, exception.Error.Code);
    }

    private static WhisperEngineOptions Options(RuntimeProviderKind provider) => new(
        "unused.bin",
        provider,
        WhisperModelPack.Quantized,
        ThreadCount: 2);

    private static CapturedAudio Audio() => new(
        DictationSessionId.Create(),
        new float[160],
        SampleRate: 16_000,
        Channels: 1);

    private sealed class FakeEngine(string engineId) : ITranscriptionEngine
    {
        public string EngineId { get; } = engineId;

        public Task<Transcript> TranscribeAsync(
            CapturedAudio audio,
            CancellationToken cancellationToken = default) => Task.FromResult(new Transcript(
            audio.SessionId,
            "bonjour",
            EngineId,
            [],
            DetectedLanguage: "fr"));
    }
}
