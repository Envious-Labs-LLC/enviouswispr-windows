using EnviousWispr.ASR;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class FallbackTranscriptionEngineTests
{
    [Fact]
    public async Task RuntimeFailureRetriesSameAudioOnCpuAndStaysOnFallback()
    {
        var audio = Audio();
        var primary = new StubEngine(
            "primary",
            (_, _) => throw Failure());
        var fallback = new StubEngine(
            "fallback",
            (captured, _) => Task.FromResult(new Transcript(captured.SessionId, "local result", "fallback")));
        using var engine = new FallbackTranscriptionEngine(primary, () => fallback);

        var first = await engine.TranscribeAsync(audio);
        var second = await engine.TranscribeAsync(audio);

        Assert.True(first.UsedFallback);
        Assert.Equal(AppErrorCode.TranscriptionFailed, first.DegradedError?.Code);
        Assert.True(second.UsedFallback);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(2, fallback.Calls);
        Assert.Same(audio, fallback.LastAudio);
    }

    [Fact]
    public async Task CancellationNeverActivatesFallback()
    {
        var fallbackCreated = false;
        var primary = new StubEngine(
            "primary",
            (_, cancellationToken) => Task.FromCanceled<Transcript>(cancellationToken));
        using var engine = new FallbackTranscriptionEngine(
            primary,
            () =>
            {
                fallbackCreated = true;
                return new StubEngine(
                    "fallback",
                    (audio, _) => Task.FromResult(new Transcript(audio.SessionId, string.Empty, "fallback")));
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.TranscribeAsync(Audio(), cancellation.Token));
        Assert.False(fallbackCreated);
    }

    [Fact]
    public void FactoryFallsBackWhenCudaCannotInitialize()
    {
        var createdProviders = new List<RuntimeProviderKind>();
        var factory = new ParakeetEngineFactory(options =>
        {
            createdProviders.Add(options.Provider);
            if (options.Provider == RuntimeProviderKind.Cuda)
            {
                throw Failure(AppErrorCode.RuntimeProviderUnavailable);
            }

            return new StubEngine(
                "cpu",
                (audio, _) => Task.FromResult(new Transcript(audio.SessionId, string.Empty, "cpu")));
        });

        var result = factory.Create(Options(RuntimeProviderKind.Cuda), Options(RuntimeProviderKind.Cpu));

        Assert.True(result.UsedFallback);
        Assert.Equal(AppErrorCode.RuntimeProviderUnavailable, result.DegradedError?.Code);
        Assert.Equal([RuntimeProviderKind.Cuda, RuntimeProviderKind.Cpu], createdProviders);
    }

    [Fact]
    public void ParakeetRejectsKnownIncompatibleProviderAndPackBeforeLoadingModels()
    {
        var directMl = Assert.Throws<TranscriptionEngineException>(
            () => new ParakeetTranscriptionEngine(Options(RuntimeProviderKind.DirectMl)));
        var cudaInt8 = Assert.Throws<TranscriptionEngineException>(
            () => new ParakeetTranscriptionEngine(
                Options(RuntimeProviderKind.Cuda) with { ModelPack = ParakeetModelPack.Quantized }));

        Assert.Equal(AppErrorCode.RuntimeProviderIncompatible, directMl.Error.Code);
        Assert.Equal(AppErrorCode.RuntimeProviderIncompatible, cudaInt8.Error.Code);
    }

    private static CapturedAudio Audio() => new(
        DictationSessionId.Create(),
        new float[16],
        SampleRate: 16_000,
        Channels: 1);

    private static ParakeetEngineOptions Options(RuntimeProviderKind provider) => new(
        ModelDirectory: "unused",
        provider,
        provider == RuntimeProviderKind.Cuda
            ? ParakeetModelPack.FullPrecision
            : ParakeetModelPack.Quantized,
        IntraOpThreads: provider == RuntimeProviderKind.Cuda ? 1 : 8);

    private static TranscriptionEngineException Failure(
        AppErrorCode code = AppErrorCode.TranscriptionFailed) => new(
        new AppError(code, AppErrorStage.FinalAsr, CanRetry: true));

    private sealed class StubEngine(
        string engineId,
        Func<CapturedAudio, CancellationToken, Task<Transcript>> transcribe) :
        ITranscriptionEngine,
        IDisposable
    {
        public string EngineId => engineId;

        public int Calls { get; private set; }

        public CapturedAudio? LastAudio { get; private set; }

        public Task<Transcript> TranscribeAsync(
            CapturedAudio audio,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAudio = audio;
            return transcribe(audio, cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
