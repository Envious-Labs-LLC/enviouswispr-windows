using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The microphone test's lifecycle without the page or a microphone: a dictation outranks a test,
/// one test at a time, a cancel closes the device, a device that vanishes is reported as such, and
/// a frame from a finished test is refused.
/// </summary>
public sealed class MicrophoneTestControllerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ATestListensForItsDurationThenReportsWhatArrived()
    {
        var (controller, capture, clock) = Build();
        capture.Packets = 40;
        capture.RootMeanSquare = 0.01f;

        var run = controller.RunAsync(new AudioDeviceId("mic-1"), recordingInProgress: false);
        await capture.Started.Task.WaitAsync(Patience);
        Assert.Equal("mic-1", capture.RequestedDevice?.Value);
        Assert.True(controller.IsRunning);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        Assert.Equal(MicrophoneTestController.Duration, clock.NextDue);
        Assert.False(run.IsCompleted, "the test listens for its whole duration");

        clock.Advance(MicrophoneTestController.Duration);
        var result = await run.WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.Completed, result.Outcome);
        Assert.Equal(MicrophoneTestVerdict.For(40, 0, 0.01f), result.Verdict);
        Assert.True(capture.Stopped);
        Assert.True(capture.Disposed, "the controller lets the device go after every test");
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task ADictationInProgressRefusesTheTestBeforeTheDeviceIsTouched()
    {
        var (controller, capture, _) = Build();

        var result = await controller.RunAsync(null, recordingInProgress: true).WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.RecordingInProgress, result.Outcome);
        Assert.Equal(0, capture.Opened);
    }

    [Fact]
    public async Task ASecondPressWhileOneIsListeningIsIgnored()
    {
        var (controller, capture, clock) = Build();
        var first = controller.RunAsync(null, recordingInProgress: false);
        await capture.Started.Task.WaitAsync(Patience);

        var second = await controller.RunAsync(null, recordingInProgress: false).WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.AlreadyRunning, second.Outcome);
        Assert.Equal(1, capture.Opened);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(MicrophoneTestController.Duration);
        Assert.Equal(MicrophoneTestOutcome.Completed, (await first.WaitAsync(Patience)).Outcome);
    }

    [Fact]
    public async Task ADictationStartingMidTestCancelsItAndTheDeviceIsClosedNotLeftOpen()
    {
        // THE STOP IS NOT CANCELLED, DELIBERATELY. The whole reason a recording cancels a test is to
        // take the microphone back; a cancel that left the device open would defeat itself.
        var (controller, capture, clock) = Build();
        var run = controller.RunAsync(null, recordingInProgress: false);
        await capture.Started.Task.WaitAsync(Patience);
        await clock.WhenRegistered(1).WaitAsync(Patience);

        controller.Cancel();
        var result = await run.WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.Cancelled, result.Outcome);
        Assert.True(capture.Disposed);
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task ACancelWhileTheDeviceIsStillOpeningReachesTheOpen()
    {
        // THE TOKEN GOES ALL THE WAY IN: a test that did not forward its cancellation would keep
        // opening a device the app has already decided somebody else should have.
        var (controller, capture, _) = Build();
        capture.HoldStart = true;
        var run = controller.RunAsync(null, recordingInProgress: false);
        await capture.Started.Task.WaitAsync(Patience);

        controller.Cancel();
        var result = await run.WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.Cancelled, result.Outcome);
        Assert.True(capture.StartSawCancellation);
        Assert.True(capture.Disposed);
    }

    [Fact]
    public async Task ADeviceWindowsWillNotOpenIsSaidSo()
    {
        var (controller, capture, _) = Build();
        capture.RefuseStart = true;

        var result = await controller.RunAsync(null, recordingInProgress: false).WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.DeviceWouldNotOpen, result.Outcome);
        Assert.True(capture.Disposed);
    }

    [Fact]
    public async Task ADeviceThatVanishedMidTestIsNotReportedAsHealthyByItsCounts()
    {
        // WHAT THE STOP SAID, BEFORE WHAT THE PACKETS SAID. Forty loud packets and then the device
        // was unplugged: the counts read as healthy and the stop says otherwise.
        var (controller, capture, clock) = Build();
        capture.Packets = 40;
        capture.RootMeanSquare = 0.01f;
        capture.StopOutcome = AudioCaptureOutcome.Interrupted;
        var run = controller.RunAsync(null, recordingInProgress: false);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(MicrophoneTestController.Duration);

        var result = await run.WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.StoppedPartWay, result.Outcome);
        Assert.Null(result.Verdict);
    }

    [Fact]
    public async Task AFrameFromAFinishedTestIsRefusedAndOnlyOneFramePerIntervalIsRaised()
    {
        var (controller, capture, clock) = Build();
        var frames = new List<MicrophoneTestFrame>();
        controller.Frame += (_, frame) => frames.Add(frame);
        var run = controller.RunAsync(null, recordingInProgress: false);
        await capture.Started.Task.WaitAsync(Patience);
        await clock.WhenRegistered(1).WaitAsync(Patience);

        // A burst of levels inside one meter interval is one frame, carrying the loudest.
        capture.RaiseLevel(0.001f);
        capture.RaiseLevel(0.004f);
        capture.RaiseLevel(0.002f);
        Assert.Single(frames);
        var frame = frames[0];
        Assert.True(controller.IsCurrent(frame.TestId), "a frame from the running test is drawn");

        clock.Advance(MicrophoneTestController.Duration);
        await run.WaitAsync(Patience);

        Assert.False(controller.IsCurrent(frame.TestId), "a frame posted before the test ended is refused after it");
    }

    private static (MicrophoneTestController Controller, FakeCapture Capture, Deterministic.ManualClock Clock) Build()
    {
        var capture = new FakeCapture();
        var clock = new Deterministic.ManualClock();
        return (new MicrophoneTestController(() => capture, clock), capture, clock);
    }

    private sealed class FakeCapture : IMicrophoneTestCapture
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged;

        public bool IsCapturing { get; private set; }

        public int Opened { get; private set; }

        public bool HoldStart { get; set; }

        public bool RefuseStart { get; set; }

        public bool StartSawCancellation { get; private set; }

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public AudioDeviceId? RequestedDevice { get; private set; }

        public AudioCaptureOutcome StopOutcome { get; set; } = AudioCaptureOutcome.Completed;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Packets { get; set; }

        public int SilentPackets { get; set; }

        public float RootMeanSquare { get; set; }

        public int LastPacketCount => Packets;

        public int LastSilentPacketCount => SilentPackets;

        public float LastPeak => RootMeanSquare;

        public float LastRootMeanSquare => RootMeanSquare;

        public void RaiseLevel(float rootMeanSquare) =>
            LevelChanged?.Invoke(this, new AudioLevel(rootMeanSquare, rootMeanSquare));

        public async Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            Opened++;
            _sessionId = request.SessionId;
            RequestedDevice = request.DeviceId;
            Started.TrySetResult();
            if (HoldStart)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    StartSawCancellation = true;
                    throw;
                }
            }

            if (RefuseStart)
            {
                return new AudioOperationResult(Succeeded: false);
            }

            IsCapturing = true;
            return new AudioOperationResult(Succeeded: true);
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            Stopped = true;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1, Outcome: StopOutcome));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
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
