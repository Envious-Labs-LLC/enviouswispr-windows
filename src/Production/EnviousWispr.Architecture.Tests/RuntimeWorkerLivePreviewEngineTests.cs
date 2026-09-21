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
    public async Task AbortReachesTheRuntimeAndLetsGoOfTheResource()
    {
        // THE PREVIEW'S ABORT IS THE WORKER'S ABORT, and the resource the preview held for its worker
        // is let go of with it, so the final engine can have it during the shutdown too.
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime();
        await using var preview = new RuntimeWorkerLivePreviewEngine(runtime, arbiter, RuntimeResourceKind.Cpu);
        Assert.True((await preview.StartAsync()).Succeeded);
        var heldByPreview = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);

        var abort = await preview.AbortAsync(TimeSpan.FromSeconds(1));
        var afterAbort = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);

        Assert.False(heldByPreview.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(1), runtime.AbortDeadline);
        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, abort.Outcome);
        Assert.True(afterAbort.Succeeded);
        await afterAbort.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task AnAbortWhoseExitWasNotSeenKeepsTheResource()
    {
        // THE WORKER MAY STILL BE ON THE RESOURCE. A StillRunning outcome leaves the lease with the
        // preview - the final engine must not be put beside a worker nobody has seen leave - and an
        // abort that threw leaves it too.
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime { AbortOutcome = RuntimeWorkerAbortOutcome.StillRunning };
        await using var preview = new RuntimeWorkerLivePreviewEngine(runtime, arbiter, RuntimeResourceKind.Cpu);
        Assert.True((await preview.StartAsync()).Succeeded);

        var abort = await preview.AbortAsync(TimeSpan.FromSeconds(1));
        var stillHeld = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);

        Assert.Equal(RuntimeWorkerAbortOutcome.StillRunning, abort.Outcome);
        Assert.False(stillHeld.Succeeded, "the resource stays with the preview while its worker may still be on it");

        runtime.AbortThrows = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.AbortAsync(TimeSpan.FromSeconds(1)));
        Assert.False((await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero)).Succeeded);

        // Seen gone at last: the lease follows.
        runtime.AbortThrows = false;
        runtime.AbortOutcome = RuntimeWorkerAbortOutcome.Exited;
        Assert.Equal(RuntimeWorkerAbortOutcome.Exited, (await preview.AbortAsync(TimeSpan.FromSeconds(1))).Outcome);
        var freed = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);
        Assert.True(freed.Succeeded);
        await freed.Lease!.DisposeAsync();
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
    public async Task AStartCancelledWhileTheRuntimeIsStartingReleasesTheResourceAndStopStillStopsTheRuntime()
    {
        // Step 8 cancels a preview start that is already inside the worker's own start. The lease
        // was taken before that; it must be released by the cancellation, and the stop that the
        // controller issues afterwards must still reach the runtime so a half-started worker is
        // taken down.
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime(holdStart: true);
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            runtime,
            arbiter,
            RuntimeResourceKind.Cpu);
        using var cancellation = new CancellationTokenSource();

        var start = preview.StartAsync(cancellation.Token);
        await runtime.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var whileStarting = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);
        Assert.False(whileStarting.Succeeded, "the lease is held while the runtime is starting");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));

        var afterCancel = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);
        Assert.True(afterCancel.Succeeded, "the cancelled start released its lease");
        await afterCancel.Lease!.DisposeAsync();

        var stopped = await preview.StopAsync();

        Assert.True(stopped.Succeeded);
        Assert.True(runtime.Stopped, "the runtime is still told to stop after a cancelled start");
    }

    [Fact]
    public async Task AStartCancelledWhileWaitingForTheResourceTakesNothingAndStartsNothing()
    {
        using var arbiter = new RuntimeResourceArbiter();
        var runtime = new FakePreviewRuntime();
        await using var preview = new RuntimeWorkerLivePreviewEngine(
            runtime,
            arbiter,
            RuntimeResourceKind.Cpu,
            resourceTimeout: TimeSpan.FromSeconds(30));
        var holder = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);
        Assert.True(holder.Succeeded);
        using var cancellation = new CancellationTokenSource();

        var start = preview.StartAsync(cancellation.Token);
        Assert.False(start.IsCompleted, "the start is waiting for the resource another workload holds");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(0, runtime.Starts);
        await holder.Lease!.DisposeAsync();
        var afterwards = await arbiter.AcquireAsync(RuntimeResourceKind.Cpu, RuntimeWorkloadKind.FinalAsr, TimeSpan.Zero);
        Assert.True(afterwards.Succeeded, "nothing was left holding the resource");
        await afterwards.Lease!.DisposeAsync();
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
        bool cancelStart = false,
        bool holdStart = false) : IWorkerTranscriptionRuntime
    {
        public string EngineId => "whisper-small:cpu:isolated";

        public bool Stopped { get; private set; }

        public int Starts { get; private set; }

        public int TranscriptionCount { get; private set; }

        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            StartEntered.TrySetResult();
            if (cancelStart)
            {
                throw new OperationCanceledException();
            }

            if (holdStart)
            {
                // The real supervisor's health wait: a cancellable read that throws the caller's cancel.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return startSucceeds
                ? new RuntimeWorkerResult(true, RuntimeWorkerState.Ready)
                : new RuntimeWorkerResult(
                    false,
                    RuntimeWorkerState.Faulted,
                    new AppError(
                        AppErrorCode.RuntimeWorkerFailed,
                        AppErrorStage.RuntimeWorker,
                        CanRetry: true));
        }

        public Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped));
        }

        public TimeSpan? AbortDeadline { get; private set; }

        public RuntimeWorkerAbortOutcome AbortOutcome { get; set; } = RuntimeWorkerAbortOutcome.Exited;

        public bool AbortThrows { get; set; }

        public Task<RuntimeWorkerAbortResult> AbortAsync(TimeSpan deadline)
        {
            AbortDeadline = deadline;
            if (AbortThrows)
            {
                throw new InvalidOperationException("synthetic abort failure");
            }

            return Task.FromResult(new RuntimeWorkerAbortResult(AbortOutcome, 4242));
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
