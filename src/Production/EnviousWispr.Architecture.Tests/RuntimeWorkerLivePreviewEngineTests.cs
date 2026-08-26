using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class RuntimeWorkerLivePreviewEngineTests
{
    private static readonly float[] SampleAudio = [0.2f];

    [Fact]
    public async Task StopReleasesPreviewResourceBeforeFinalAsr()
    {
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime();
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            runtime,
            arbiter,
            RuntimeResourceKind.Cpu);
        var sessionId = DictationSessionId.Create();

        var started = await preview.StartAsync();
        var update = await preview.PreviewAsync(
            new AudioSnapshot(sessionId, SampleAudio, 16_000, 1),
            sequence: 3);
        var blockedFinal = await arbiter.AcquireAsync(
            RuntimeResourceKind.Cpu,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.Zero);
        await preview.StopAsync();
        var final = await arbiter.AcquireAsync(
            RuntimeResourceKind.Cpu,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.Zero);

        Assert.True(started.Succeeded);
        Assert.True(update.Succeeded);
        Assert.Equal(sessionId.Value, update.SessionId);
        Assert.Equal(3, update.Sequence);
        Assert.Equal("preview only", update.Text);
        Assert.False(blockedFinal.Succeeded);
        Assert.True(runtime.Stopped);
        Assert.True(final.Succeeded);
        await final.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task FailedPreviewStartDoesNotHoldResource()
    {
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime(startSucceeds: false);
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            runtime,
            arbiter,
            RuntimeResourceKind.Accelerator);

        var started = await preview.StartAsync();
        var final = await arbiter.AcquireAsync(
            RuntimeResourceKind.Accelerator,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.Zero);

        Assert.False(started.Succeeded);
        Assert.True(final.Succeeded);
        await final.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task CancelledPreviewStartDoesNotHoldResource()
    {
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime(cancelStart: true);
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            runtime,
            arbiter,
            RuntimeResourceKind.Cpu);

        await Assert.ThrowsAsync<OperationCanceledException>(() => preview.StartAsync());
        var final = await arbiter.AcquireAsync(
            RuntimeResourceKind.Cpu,
            RuntimeWorkloadKind.FinalAsr,
            TimeSpan.Zero);

        Assert.True(final.Succeeded);
        await final.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task DisabledPreviewReturnsTypedFailureWithoutCallingRuntime()
    {
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime();
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            runtime,
            arbiter,
            RuntimeResourceKind.Cpu);
        var sessionId = DictationSessionId.Create();

        var update = await preview.PreviewAsync(
            new AudioSnapshot(sessionId, SampleAudio, 16_000, 1),
            sequence: 0);

        Assert.False(update.Succeeded);
        Assert.Equal(AppErrorCode.RuntimeResourceBusy, update.Error?.Code);
        Assert.Equal(0, runtime.TranscriptionCount);
    }

    private sealed class FakePreviewRuntime(
        bool startSucceeds = true,
        bool cancelStart = false) : IWorkerTranscriptionRuntime
    {
        public string EngineId => "whisper-small:cpu:isolated";

        public bool Stopped { get; private set; }

        public int TranscriptionCount { get; private set; }

        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
            cancelStart
                ? Task.FromException<RuntimeWorkerResult>(new OperationCanceledException())
                : Task.FromResult(startSucceeds
                ? new RuntimeWorkerResult(true, RuntimeWorkerState.Ready)
                : new RuntimeWorkerResult(
                    false,
                    RuntimeWorkerState.Faulted,
                    new AppError(
                        AppErrorCode.RuntimeWorkerFailed,
                        AppErrorStage.RuntimeWorker,
                        CanRetry: true)));

        public Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped));
        }

        public Task<Transcript> TranscribeAsync(
            CapturedAudio audio,
            CancellationToken cancellationToken = default)
        {
            TranscriptionCount++;
            return Task.FromResult(new Transcript(
                audio.SessionId,
                "preview only",
                EngineId,
                DetectedLanguage: "en"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
