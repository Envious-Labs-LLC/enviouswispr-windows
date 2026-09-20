using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The coordinator is what stands between a key-up and a dropped recording, so every case here holds a
/// command open with a barrier and asks what happened to the next one. No sleeps: a test that waits on
/// time proves the machine was slow enough, not that the order was right.
/// </summary>
public sealed class DictationSessionCoordinatorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReleaseDuringPressStartupIsKeptAndRunsOnceAfterThePress()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);

        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        Assert.False(release.IsCompleted);
        Assert.Equal([PushToTalkSignal.Pressed], executor.Seen);

        executor.Finish(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Released);

        var kept = await release.WaitAsync(Patience);
        Assert.Equal(SessionCommandDisposition.Applied, kept.Disposition);
        Assert.True(kept.WasQueued, "the release waited behind the press and must say so");
        Assert.False((await press).WasQueued, "the press ran first and waited for nothing");
        Assert.Equal([PushToTalkSignal.Pressed, PushToTalkSignal.Released], executor.Seen);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public async Task CancelDuringPressStartupIsKeptAndRunsAfterThePress()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        var cancel = coordinator.SubmitAsync(PushToTalkSignal.Cancelled);

        executor.Finish(PushToTalkSignal.Pressed);
        await press.WaitAsync(Patience);
        await executor.Started(PushToTalkSignal.Cancelled).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Cancelled);

        Assert.Equal(SessionCommandDisposition.Applied, (await cancel.WaitAsync(Patience)).Disposition);
        Assert.Equal([PushToTalkSignal.Pressed, PushToTalkSignal.Cancelled], executor.Seen);
    }

    [Fact]
    public async Task PressWhileAnotherCommandIsRunningIsRefusedNotQueued()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var first = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        var second = await coordinator.SubmitAsync(PushToTalkSignal.Pressed);

        Assert.Equal(SessionCommandDisposition.Busy, second.Disposition);
        executor.Finish(PushToTalkSignal.Pressed);
        await first.WaitAsync(Patience);
        Assert.Equal([PushToTalkSignal.Pressed], executor.Seen);
    }

    [Fact]
    public async Task PressWhileATerminalIsQueuedBehindAPressIsRefused()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        var secondPress = await coordinator.SubmitAsync(PushToTalkSignal.Pressed);

        Assert.Equal(SessionCommandDisposition.Busy, secondPress.Disposition);
        executor.Finish(PushToTalkSignal.Pressed);
        await press.WaitAsync(Patience);
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Released);
        await release.WaitAsync(Patience);
        Assert.Equal([PushToTalkSignal.Pressed, PushToTalkSignal.Released], executor.Seen);
    }

    [Fact]
    public async Task DuplicateTerminalSignalsCollapseToOne()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        var duplicateRelease = await coordinator.SubmitAsync(PushToTalkSignal.Released);
        var lateCancel = await coordinator.SubmitAsync(PushToTalkSignal.Cancelled);

        Assert.Equal(SessionCommandDisposition.Ignored, duplicateRelease.Disposition);
        Assert.Equal(SessionCommandDisposition.Ignored, lateCancel.Disposition);

        executor.Finish(PushToTalkSignal.Pressed);
        await press.WaitAsync(Patience);
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Released);
        await release.WaitAsync(Patience);
        Assert.Equal([PushToTalkSignal.Pressed, PushToTalkSignal.Released], executor.Seen);
    }

    [Fact]
    public async Task ATerminalSignalDuringFinalizationIsIgnoredNotQueued()
    {
        // The release that ended the recording is still running (finalising). A second release or an
        // Escape arriving now used to be dropped by the gate; it is now refused explicitly and never
        // runs, so nothing can re-enter the state machine mid-finalisation.
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        var escape = await coordinator.SubmitAsync(PushToTalkSignal.Cancelled);

        Assert.Equal(SessionCommandDisposition.Ignored, escape.Disposition);
        executor.Finish(PushToTalkSignal.Released);
        await release.WaitAsync(Patience);
        Assert.Equal([PushToTalkSignal.Released], executor.Seen);
    }

    [Fact]
    public async Task ATerminalWithNothingPendingStillRunsSoTheStateMachineDecides()
    {
        // A release with no recording is the controller's call (it answers Ignored), and the shell
        // sets an idle status afterwards. The coordinator must not pre-empt that.
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Released);

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
    }

    [Fact]
    public async Task AdmissionReopensAfterEachCommandCompletes()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        foreach (var round in Enumerable.Range(0, 3))
        {
            var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
            await executor.Started(PushToTalkSignal.Pressed, occurrence: round).WaitAsync(Patience);
            executor.Finish(PushToTalkSignal.Pressed, occurrence: round);
            Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);

            var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
            await executor.Started(PushToTalkSignal.Released, occurrence: round).WaitAsync(Patience);
            executor.Finish(PushToTalkSignal.Released, occurrence: round);
            Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        }

        Assert.Equal(6, executor.Seen.Count);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public async Task ExecutorResultReachesTheSubmitter()
    {
        var snapshot = DictationSessionSnapshot.Start(DateTimeOffset.UnixEpoch);
        var executor = new BarrierExecutor
        {
            Answer = new SessionCommandResult(SessionCommandDisposition.Applied, snapshot),
        };
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Pressed);

        var result = await press.WaitAsync(Patience);
        Assert.Same(snapshot, result.Session);
    }

    [Fact]
    public async Task AnExecutorExceptionFaultsThatSubmitterAndTheLoopSurvives()
    {
        var executor = new BarrierExecutor { ThrowOn = PushToTalkSignal.Pressed };
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Pressed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => press.WaitAsync(Patience));

        // The gate was handed back and admission reopened: the next command runs.
        Assert.True(coordinator.IsIdle);
        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Released);
        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public async Task APressWhileTheSessionIsHeldIsRefusedAndTheHoldIsNotTouched()
    {
        // An update check holds the session through a whole download. A press admitted then would
        // open a microphone minutes after the finger left the key. It is refused on the spot, and
        // the hold is exactly as it was.
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var hold = coordinator.TryHold();
        Assert.NotNull(hold);
        var press = await coordinator.SubmitAsync(PushToTalkSignal.Pressed);

        Assert.Equal(SessionCommandDisposition.Busy, press.Disposition);
        Assert.Equal(0, coordinator.PendingCount);
        Assert.False(coordinator.IsIdle);
        Assert.Empty(executor.Seen);

        hold.Dispose();
        Assert.True(coordinator.IsIdle);
        var afterwards = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        Assert.False(coordinator.IsIdle);
        executor.Finish(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, (await afterwards.WaitAsync(Patience)).Disposition);
        Assert.True(coordinator.IsIdle);
    }

    [Fact]
    public async Task AHoldIsRefusedWhileAnythingIsPendingOrRunningOrHeld()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        Assert.Null(coordinator.TryHold());
        executor.Finish(PushToTalkSignal.Pressed);
        await press.WaitAsync(Patience);

        using var first = coordinator.TryHold();
        Assert.NotNull(first);
        Assert.Null(coordinator.TryHold());
    }

    [Fact]
    public async Task ACommandWaitsForTheHoldToBeReleasedInsteadOfBeingDropped()
    {
        // A signal that arrives while the update check holds the session used to be discarded by a
        // zero-timeout probe. It waits, and says it waited.
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var hold = coordinator.TryHold();
        Assert.NotNull(hold);
        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        Assert.Equal(1, coordinator.PendingCount);
        Assert.False(executor.Started(PushToTalkSignal.Released).IsCompleted);

        await UntilAsync(() => coordinator.GateWaitsEntered == 1);
        hold.Dispose();
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        Assert.False(coordinator.IsIdle);
        executor.Finish(PushToTalkSignal.Released);
        var kept = await release.WaitAsync(Patience);
        Assert.True(kept.WasQueued, "the release waited on the hold and must say so");
        Assert.True(coordinator.IsIdle);
    }

    [Fact]
    public async Task StopRefusesTheQueueLetsTheRunningCommandFinishAndClosesAdmission()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);

        var stop = coordinator.StopAsync(Patience);
        Assert.False(stop.IsCompleted);
        var afterClose = await coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        Assert.Equal(SessionCommandDisposition.Stopping, afterClose.Disposition);

        executor.Finish(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await release.WaitAsync(Patience)).Disposition);
        Assert.True(await stop.WaitAsync(Patience));
        Assert.Equal([PushToTalkSignal.Pressed], executor.Seen);
        Assert.True(coordinator.IsIdle);
    }

    [Fact]
    public async Task StopWhileTheConsumerWaitsOnTheHoldReleasesTheWaiter()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var hold = coordinator.TryHold();
        Assert.NotNull(hold);
        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        // The consumer has to be PARKED on the gate before the stop, or this proves that stopping an
        // idle coordinator works, which was never in doubt.
        await UntilAsync(() => coordinator.GateWaitsEntered == 1);
        Assert.False(release.IsCompleted);

        Assert.True(await coordinator.StopAsync(Patience));
        Assert.Equal(SessionCommandDisposition.Stopping, (await release.WaitAsync(Patience)).Disposition);
        Assert.Empty(executor.Seen);
        Assert.False(coordinator.IsIdle);
        hold.Dispose();
    }

    [Fact]
    public async Task AnImmediateRetryAfterAnExecutorFaultIsAdmitted()
    {
        // The fault's continuation may run before the consumer's next statement. Admission is restored
        // BEFORE the submitter is told, so a retry that runs on that continuation is not refused.
        var executor = new BarrierExecutor { ThrowOn = PushToTalkSignal.Released };
        await using var coordinator = new DictationSessionCoordinator(executor);

        var first = coordinator.SubmitAsync(PushToTalkSignal.Released);
        await executor.Started(PushToTalkSignal.Released).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Released);
        SessionCommandDisposition retry = default;
        var retried = first.ContinueWith(
            _ => coordinator.SubmitAsync(PushToTalkSignal.Cancelled),
            TaskScheduler.Default).Unwrap();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Patience));
        await executor.Started(PushToTalkSignal.Cancelled).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Cancelled);
        retry = (await retried.WaitAsync(Patience)).Disposition;

        Assert.Equal(SessionCommandDisposition.Applied, retry);
    }

    [Fact]
    public async Task ThroughTheRealControllerAReleaseDuringMicrophoneOpenStillEndsTheRecording()
    {
        // The whole point, proven against the state machine that ships rather than a fake that says
        // Applied: the microphone is still opening when the key-up lands, and the recording still
        // finalises exactly once, with its audio, and the target it was pressed against.
        var capture = new BlockingCapture();
        var targets = new FakeTargetProvider(101);
        await using var controller = new PushToTalkSessionController(capture, targets, minimumHoldDuration: TimeSpan.Zero);
        var executor = new ControllerExecutor(controller);
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await capture.Opening.Task.WaitAsync(Patience);
        var release = coordinator.SubmitAsync(PushToTalkSignal.Released);
        Assert.False(release.IsCompleted);
        targets.Window = new TargetWindowId(202);

        capture.Open.SetResult();
        var started = await press.WaitAsync(Patience);
        var ended = await release.WaitAsync(Patience);

        Assert.Equal(SessionTransitionKind.Started, executor.Transitions[0].Kind);
        Assert.Equal(SessionTransitionKind.FinalizeReady, executor.Transitions[1].Kind);
        Assert.Equal(2, executor.Transitions.Count);
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(1, capture.StopCount);
        Assert.False(capture.IsCapturing);
        var audio = executor.Transitions[1].Audio;
        Assert.NotNull(audio);
        Assert.Equal(started.Session?.Id, audio.SessionId);
        Assert.Equal([0.2f], audio.Samples.ToArray());
        Assert.Equal(new TargetWindowId(101), started.Session?.Target);
        Assert.Equal(new TargetWindowId(101), ended.Session?.Target);
        Assert.True(ended.WasQueued);
    }

    [Fact]
    public async Task ThroughTheRealControllerThePressCapturesItsTargetBeforeTheQueueHop()
    {
        // The window under the caret and the delivery choice belong to the instant of the press. The
        // executor is held BEFORE it runs, the world changes, and the recording still has what the
        // press saw. Without capture at admission this test fails: the controller would ask again.
        var capture = new BlockingCapture();
        var targets = new FakeTargetProvider(101);
        var copyInsteadOfPaste = false;
        await using var controller = new PushToTalkSessionController(
            capture,
            targets,
            deliveryOptions: () => TextDeliveryOptions.Default with { CopyInsteadOfPaste = copyInsteadOfPaste });
        var executor = new ControllerExecutor(controller);
        await using var coordinator = new DictationSessionCoordinator(executor, controller.CaptureStartContext);

        executor.BeforeEachCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Entered.Task.WaitAsync(Patience);
        targets.Window = new TargetWindowId(202);
        copyInsteadOfPaste = true;
        executor.BeforeEachCommand.SetResult();
        capture.Open.SetResult();
        var started = await press.WaitAsync(Patience);

        Assert.Equal(SessionTransitionKind.Started, executor.Transitions[0].Kind);
        Assert.Equal(new TargetWindowId(101), started.Session?.Target);
        Assert.False(started.Session?.DeliveryOptions.CopyInsteadOfPaste);
    }

    [Fact]
    public async Task ThroughTheRealControllerACancelDuringMicrophoneOpenCancelsWithoutAudio()
    {
        var capture = new BlockingCapture();
        await using var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101));
        var executor = new ControllerExecutor(controller);
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await capture.Opening.Task.WaitAsync(Patience);
        var cancel = coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        capture.Open.SetResult();
        await press.WaitAsync(Patience);
        await cancel.WaitAsync(Patience);

        Assert.Equal(2, executor.Transitions.Count);
        Assert.Equal(SessionTransitionKind.Started, executor.Transitions[0].Kind);
        Assert.Equal(SessionTransitionKind.Cancelled, executor.Transitions[1].Kind);
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(1, capture.CancelCount);
        Assert.Equal(0, capture.StopCount);
        Assert.False(capture.IsCapturing);
        Assert.Null(executor.Transitions[1].Audio);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Yield();
        }
    }

    [Fact]
    public async Task StopReportsFalseWhenTheRunningCommandOutlivesTheTimeout()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);

        Assert.False(await coordinator.StopAsync(TimeSpan.Zero));
        executor.Finish(PushToTalkSignal.Pressed);
        await press.WaitAsync(Patience);
    }

    [Fact]
    public async Task ACaptureThatThrowsAtAdmissionHandsTheGateBack()
    {
        var executor = new BarrierExecutor();
        var throwOnce = true;
        await using var coordinator = new DictationSessionCoordinator(executor, () =>
        {
            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("synthetic capture failure");
            }

            return new RecordingStartContext(new TargetWindowId(101), TextDeliveryOptions.Default);
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        });
        Assert.Equal(0, coordinator.PendingCount);
        Assert.True(coordinator.IsIdle);

        var next = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Started(PushToTalkSignal.Pressed).WaitAsync(Patience);
        executor.Finish(PushToTalkSignal.Pressed);
        Assert.Equal(SessionCommandDisposition.Applied, (await next.WaitAsync(Patience)).Disposition);
        Assert.True(coordinator.IsIdle);
    }

    [Fact]
    public async Task QuickAddIsNotASessionCommand()
    {
        var executor = new BarrierExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor);

        // The refusal is synchronous - SubmitAsync throws before it hands back a task - so the
        // exception is caught by an ordinary delegate, not awaited off a faulted task.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = coordinator.SubmitAsync(PushToTalkSignal.QuickAdd);
        });
        Assert.Equal(0, coordinator.PendingCount);
    }

    /// <summary>The shell's signal switch, without the shell: press, release, cancel onto the real controller.</summary>
    private sealed class ControllerExecutor(PushToTalkSessionController controller) : ISessionCommandExecutor
    {
        public List<SessionTransitionResult> Transitions { get; } = [];

        /// <summary>Completed when the executor is entered, before it touches the controller.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, the executor waits on it after entry and before the controller.</summary>
        public TaskCompletionSource? BeforeEachCommand { get; set; }

        public async Task<SessionCommandResult> ExecuteAsync(
            SessionCommand command,
            CancellationToken stoppingToken)
        {
            // Not forwarded, like the shell's adapter: a transition in flight finishes on its own terms.
            _ = stoppingToken;
            Entered.TrySetResult();
            if (BeforeEachCommand is { } barrier)
            {
                await barrier.Task.ConfigureAwait(false);
            }

            var result = command.Signal switch
            {
                PushToTalkSignal.Pressed when command.StartContext is { } context =>
                    await controller.PressAsync(context, CancellationToken.None).ConfigureAwait(false),
                PushToTalkSignal.Pressed => await controller.PressAsync(CancellationToken.None).ConfigureAwait(false),
                PushToTalkSignal.Released => await controller.ReleaseAsync(CancellationToken.None).ConfigureAwait(false),
                PushToTalkSignal.Cancelled => await controller.CancelAsync(CancellationToken.None).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported signal."),
            };
            Transitions.Add(result);
            return new SessionCommandResult(SessionCommandDisposition.Applied, result.Session);
        }
    }

    private sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
    {
        public TargetWindowId Window { get; set; } = new(window);

        public TargetWindowId? CaptureForegroundTarget() => Window.IsValid ? Window : null;
    }

    /// <summary>A microphone that does not finish opening until the test lets it.</summary>
    private sealed class BlockingCapture : IAudioCapture
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public TaskCompletionSource Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsCapturing { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int CancelCount { get; private set; }

        public async Task<AudioOperationResult> StartAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            _sessionId = request.SessionId;
            Opening.TrySetResult();
            await Open.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            IsCapturing = true;
            return new AudioOperationResult(Succeeded: true);
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCount++;
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Holds each command open until the test says otherwise, and records the order it saw them in.
    /// </summary>
    private sealed class BarrierExecutor : ISessionCommandExecutor
    {
        private readonly object _lock = new();
        private readonly Dictionary<(PushToTalkSignal, int), TaskCompletionSource> _started = new();
        private readonly Dictionary<(PushToTalkSignal, int), TaskCompletionSource> _finish = new();
        private readonly Dictionary<PushToTalkSignal, int> _occurrences = new();
        private readonly List<PushToTalkSignal> _seen = [];

        public IReadOnlyList<PushToTalkSignal> Seen
        {
            get
            {
                lock (_lock)
                {
                    return [.. _seen];
                }
            }
        }

        public SessionCommandResult Answer { get; init; } = new(SessionCommandDisposition.Applied);

        public PushToTalkSignal? ThrowOn { get; init; }

        public Task Started(PushToTalkSignal signal, int occurrence = 0) =>
            Source(_started, signal, occurrence).Task;

        public void Finish(PushToTalkSignal signal, int occurrence = 0) =>
            Source(_finish, signal, occurrence).SetResult();

        public async Task<SessionCommandResult> ExecuteAsync(
            SessionCommand command,
            CancellationToken stoppingToken)
        {
            int occurrence;
            lock (_lock)
            {
                occurrence = _occurrences.GetValueOrDefault(command.Signal);
                _occurrences[command.Signal] = occurrence + 1;
                _seen.Add(command.Signal);
            }

            // THE STOPPING TOKEN IS NOT HONOURED HERE ON PURPOSE. The shell's adapter drops it, because a
            // command in flight owns a microphone or a transcription and finishes on its own terms; a
            // fake that abandoned the barrier on shutdown would prove a contract the product does not have.
            _ = stoppingToken;
            Source(_started, command.Signal, occurrence).SetResult();
            await Source(_finish, command.Signal, occurrence).Task.ConfigureAwait(false);
            if (ThrowOn == command.Signal)
            {
                throw new InvalidOperationException("synthetic executor failure");
            }

            return Answer;
        }

        private TaskCompletionSource Source(
            Dictionary<(PushToTalkSignal, int), TaskCompletionSource> table,
            PushToTalkSignal signal,
            int occurrence)
        {
            lock (_lock)
            {
                if (!table.TryGetValue((signal, occurrence), out var source))
                {
                    source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    table[(signal, occurrence)] = source;
                }

                return source;
            }
        }
    }
}
