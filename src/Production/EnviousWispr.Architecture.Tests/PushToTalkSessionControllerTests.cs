using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

public sealed class PushToTalkSessionControllerTests
{
    private static readonly float[] OneSample = [0.2f];

    [Fact]
    public async Task PressFreezesTargetAndRejectsOverlap()
    {
        var audio = new FakeAudioCapture();
        var targets = new FakeTargetProvider(101);
        await using var controller = new PushToTalkSessionController(audio, targets);

        var started = await controller.PressAsync();
        targets.Window = new TargetWindowId(202);
        var overlap = await controller.PressAsync();
        var released = await controller.ReleaseAsync();

        Assert.Equal(SessionTransitionKind.Started, started.Kind);
        Assert.Equal(new TargetWindowId(101), started.Session?.Target);
        Assert.Equal(SessionTransitionKind.Ignored, overlap.Kind);
        Assert.Equal(1, audio.StartCount);
        Assert.Equal(new TargetWindowId(101), released.Session?.Target);
    }

    [Fact]
    public async Task EscapeCancellationDeliversNothingAndAllowsReset()
    {
        var audio = new FakeAudioCapture();
        await using var controller = new PushToTalkSessionController(
            audio,
            new FakeTargetProvider(101));

        await controller.PressAsync();
        var cancelled = await controller.CancelAsync();
        var staleRelease = await controller.ReleaseAsync();
        var reset = await controller.ResetAsync();

        Assert.Equal(SessionTransitionKind.Cancelled, cancelled.Kind);
        Assert.Equal(DictationSessionState.Cancelled, cancelled.Session?.State);
        Assert.NotNull(cancelled.Session?.FinishedAt);
        Assert.Equal(1, audio.CancelCount);
        Assert.Equal(0, audio.StopCount);
        Assert.Equal(SessionTransitionKind.Ignored, staleRelease.Kind);
        Assert.Equal(SessionTransitionKind.Reset, reset.Kind);
        Assert.Null(controller.CurrentSession);
    }

    [Fact]
    public async Task FocuslessPressFailsBeforeAudioStarts()
    {
        var audio = new FakeAudioCapture();
        await using var controller = new PushToTalkSessionController(
            audio,
            new FakeTargetProvider(window: 0));

        var result = await controller.PressAsync();

        Assert.Equal(SessionTransitionKind.Failed, result.Kind);
        Assert.Equal(AppErrorCode.TargetUnavailable, result.Error?.Code);
        Assert.Equal(0, audio.StartCount);
    }

    [Fact]
    public async Task BufferedInterruptionCanContinueToDeliveryForFrozenTarget()
    {
        var audio = new FakeAudioCapture
        {
            StopResultFactory = sessionId => new CapturedAudio(
                sessionId,
                OneSample,
                SampleRate: 16_000,
                Channels: 1,
                AudioCaptureOutcome.Interrupted,
                new AppError(
                    AppErrorCode.AudioDeviceLost,
                    AppErrorStage.AudioCapture,
                    CanRetry: true)),
        };
        await using var controller = new PushToTalkSessionController(
            audio,
            new FakeTargetProvider(303));

        var started = await controller.PressAsync();
        var released = await controller.ReleaseAsync();
        var delivering = await controller.BeginDeliveryAsync(started.Session!.Id);
        var completed = await controller.CompleteAsync(started.Session.Id);

        Assert.Equal(SessionTransitionKind.FinalizeReady, released.Kind);
        Assert.Equal(AppErrorCode.AudioDeviceLost, released.Error?.Code);
        Assert.Equal(SessionTransitionKind.Delivering, delivering.Kind);
        Assert.Equal(new TargetWindowId(303), delivering.Session?.Target);
        Assert.Equal(SessionTransitionKind.Completed, completed.Kind);
        Assert.Equal(DictationSessionState.Completed, completed.Session?.State);
    }

    [Fact]
    public async Task EmptyInterruptedCaptureFailsQuietlyAndRejectsStaleCompletion()
    {
        var audio = new FakeAudioCapture
        {
            StopResultFactory = sessionId => new CapturedAudio(
                sessionId,
                ReadOnlyMemory<float>.Empty,
                SampleRate: 16_000,
                Channels: 1,
                AudioCaptureOutcome.Interrupted,
                new AppError(
                    AppErrorCode.AudioDeviceLost,
                    AppErrorStage.AudioCapture,
                    CanRetry: false)),
        };
        await using var controller = new PushToTalkSessionController(
            audio,
            new FakeTargetProvider(404));

        await controller.PressAsync();
        var released = await controller.ReleaseAsync();
        var stale = await controller.CompleteAsync(DictationSessionId.Create());

        Assert.Equal(SessionTransitionKind.Failed, released.Kind);
        Assert.Equal(DictationSessionState.Failed, released.Session?.State);
        Assert.Equal(SessionTransitionKind.Ignored, stale.Kind);
    }

    [Fact]
    public async Task DisposalCancelsAnActiveSessionAndDisposesAudio()
    {
        var audio = new FakeAudioCapture();
        var controller = new PushToTalkSessionController(audio, new FakeTargetProvider(505));
        await controller.PressAsync();

        await controller.DisposeAsync();

        Assert.Equal(1, audio.CancelCount);
        Assert.True(audio.Disposed);
    }

    private sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
    {
        public TargetWindowId Window { get; set; } = new(window);

        public TargetWindowId? CaptureForegroundTarget() => Window.IsValid ? Window : null;
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int CancelCount { get; private set; }

        public bool Disposed { get; private set; }

        public Func<DictationSessionId, CapturedAudio>? StopResultFactory { get; init; }

        public Task<AudioOperationResult> StartAsync(
            AudioCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            _sessionId = request.SessionId;
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            IsCapturing = false;
            return Task.FromResult(StopResultFactory?.Invoke(_sessionId) ?? new CapturedAudio(
                _sessionId,
                OneSample,
                SampleRate: 16_000,
                Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelCount++;
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
