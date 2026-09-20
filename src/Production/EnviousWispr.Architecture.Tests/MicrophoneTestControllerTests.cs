using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Presentation;
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
    public async Task ACancelMidTestEndsItAndTheDeviceIsReleasedThroughDisposal()
    {
        // A cancel while listening skips the stop and lets the capture go through its disposal, which
        // is what closes the device; the controller stays busy until that disposal has finished.
        var (controller, capture, clock) = Build();
        capture.HoldDispose = true;
        var run = controller.RunAsync(null, recordingInProgress: false);
        await capture.Started.Task.WaitAsync(Patience);
        await clock.WhenRegistered(1).WaitAsync(Patience);

        controller.Cancel();
        await capture.DisposeEntered.Task.WaitAsync(Patience);
        Assert.True(controller.IsRunning, "the test is not over until the device has been let go of");
        capture.AllowDisposeExit.SetResult();
        var result = await run.WaitAsync(Patience);

        Assert.Equal(MicrophoneTestOutcome.Cancelled, result.Outcome);
        Assert.True(capture.Disposed);
        Assert.False(capture.IsCapturing);
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task TheStopAtTheEndOfAListenIsNeverCancelled()
    {
        // THE STOP IS NOT CANCELLED, DELIBERATELY: a cancelled stop leaves the device open, which is the
        // opposite of what a cancel is for. The token the stop receives is nobody's.
        var (controller, capture, clock) = Build();
        var run = controller.RunAsync(null, recordingInProgress: false);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(MicrophoneTestController.Duration);
        await run.WaitAsync(Patience);

        Assert.Equal(CancellationToken.None, capture.StopToken);
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
    public async Task OneFramePerIntervalCarriesTheLoudestAndAFrameFromAFinishedTestIsRefused()
    {
        var capture = new FakeCapture();
        var captures = new CaptureSource { Next = capture };
        var clock = new Deterministic.ManualClock();
        var controller = new MicrophoneTestController(() => captures.Next, clock);
        var frames = new List<MicrophoneTestFrame>();
        controller.Frame += (_, frame) => frames.Add(frame);
        var run = controller.RunAsync(null, recordingInProgress: false);
        await capture.Started.Task.WaitAsync(Patience);
        await clock.WhenRegistered(1).WaitAsync(Patience);

        // THE FIRST LEVEL IS A FRAME OF ITS OWN; the burst that follows inside the interval is one
        // frame, and it carries the loudest of the burst, not the first - a quiet level after the
        // interval boundary is what releases it.
        capture.RaiseLevel(0.001f);
        capture.RaiseLevel(0.004f);
        capture.RaiseLevel(0.002f);
        Assert.Single(frames);
        clock.Advance(RecordingLevelHistory.SampleInterval);
        capture.RaiseLevel(0.0005f);
        Assert.Equal(2, frames.Count);
        Assert.Equal(RecordingLevelHistory.Normalize(0.004f), frames[1].Level);
        Assert.Equal(frames[0].TestId, frames[1].TestId);
        Assert.True(controller.IsCurrent(frames[0].TestId), "a frame from the running test is drawn");

        clock.Advance(MicrophoneTestController.Duration);
        await run.WaitAsync(Patience);
        Assert.False(controller.IsCurrent(frames[0].TestId), "a frame posted before the test ended is refused after it");

        // And the next test on the SAME controller does not revive it: a fresh capture, a new id that
        // is current, and the old id still refused while the new one runs.
        var next = new FakeCapture();
        captures.Next = next;
        var again = controller.RunAsync(null, recordingInProgress: false);
        await next.Started.Task.WaitAsync(Patience);
        next.RaiseLevel(0.003f);
        Assert.Equal(3, frames.Count);
        Assert.NotEqual(frames[0].TestId, frames[2].TestId);
        Assert.True(controller.IsCurrent(frames[2].TestId));
        Assert.False(controller.IsCurrent(frames[0].TestId));
        controller.Cancel();
        await again.WaitAsync(Patience);
    }

    /// <summary>Which capture the factory hands out next; a controller opens a fresh one per test.</summary>
    private sealed class CaptureSource
    {
        public required FakeCapture Next { get; set; }
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

        public bool HoldDispose { get; set; }

        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowDisposeExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken? StopToken { get; private set; }

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
            StopToken = cancellationToken;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1, Outcome: StopOutcome));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            if (HoldDispose)
            {
                await AllowDisposeExit.Task;
            }

            // Disposal is what releases the device, as the real capture's does.
            IsCapturing = false;
            Disposed = true;
        }
    }
}
