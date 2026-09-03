using System.Buffers.Binary;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Architecture.Tests;

public sealed class WasapiAudioCaptureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(25);
    private static readonly float[] PreviewTail = [0.3f, 0.4f];
    private static readonly float[] CompletePreviewFixture = [0.1f, 0.2f, 0.3f, 0.4f];

    [Fact]
    public async Task ClientStopReturnsCompletedBufferedAudio()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        var started = await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.25f, -0.5f);
        var result = await capture.StopAsync();

        Assert.True(started.Succeeded);
        Assert.False(capture.IsCapturing);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(AudioCaptureOutcome.Completed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(new[] { 0.25f, -0.5f }, result.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    /// <summary>An empty callback is not a packet, and it must not publish a level.</summary>
    /// <remarks>
    /// MEASURED ON A REAL MACHINE AND IT BLINDED EVERY METER IN THE APP. This recorder delivers a
    /// zero-length buffer on roughly half its callbacks, each within a millisecond of a real one, so
    /// every true level was immediately published over with a zero. Both meters read the latest
    /// level, so both read zero on virtually every look, and the recording pill sat at its floor
    /// through dictations that transcribed perfectly.
    /// </remarks>
    [Fact]
    public async Task EmptyCallbacksDoNotCountAsPacketsOrPublishSilence()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var levels = new List<AudioLevel>();
        capture.LevelChanged += (_, level) => levels.Add(level);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.5f, -0.5f);
        recorder.EmitBytes([]);
        var result = await capture.StopAsync();

        Assert.Equal(1, capture.LastPacketCount);
        Assert.Equal(0, capture.LastSilentPacketCount);
        // THE LEVEL FROM THE REAL BUFFER SURVIVES, which is the whole point. A second event here
        // would be the defect: a zero published a millisecond after the truth.
        Assert.Single(levels);
        Assert.Equal(0.5f, levels[0].Peak);
        Assert.Equal(0.5f, levels[0].RootMeanSquare, 5);
        Assert.Equal(0.5f, capture.LastPeak);
        Assert.Equal(0.5f, capture.LastRootMeanSquare, 5);
        Assert.Equal(new[] { 0.5f, -0.5f }, result.Samples.ToArray());
        Assert.Equal(AudioCaptureOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task PreviewSnapshotIsBoundedAndDoesNotConsumeFinalAudio()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.1f, 0.2f, 0.3f, 0.4f);
        var snapshot = capture.GetSnapshot(TimeSpan.FromSeconds(2d / 16_000));
        var final = await capture.StopAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(PreviewTail, snapshot.Samples.ToArray());
        Assert.Equal(CompletePreviewFixture, final.Samples.ToArray());
        Assert.Null(capture.GetSnapshot(TimeSpan.FromSeconds(1)));
    }

    /// <summary>Asking for the whole recording returns it instead of throwing.</summary>
    /// <remarks>
    /// THE STREAMING HEAD START ASKS FOR EXACTLY THIS AND HAS NEVER ONCE SURVIVED IT. Its loop wants
    /// the whole recording rather than a window - a commit is a range measured from the start, so a
    /// rolling window would make those indices mean something different on every poll - and it says
    /// so by passing `TimeSpan.MaxValue`. That is about 9.2e11 seconds; multiplied by the sample rate
    /// it is about 1.5e16, and the `checked` cast to `int` threw `OverflowException` on the FIRST
    /// poll of every recording ever made.
    ///
    /// THE EXCEPTION WAS INVISIBLE BECAUSE THE FEATURE IS DESIGNED TO GIVE UP QUIETLY. Any failure
    /// abandons the head start and the release transcribes the whole take, which is correct and is
    /// why nothing on screen was ever wrong. Measured 2026-09-03: `StreamingAbandoned` 511 ms after
    /// the recording started, matching the poll interval, with no error code because an
    /// `OverflowException` is not a transcription failure. Ref: #96.
    ///
    /// A CEILING THAT CANNOT BE REACHED IS NOT A LIMIT. `maximumDuration` only ever trims, so
    /// saturating it changes nothing for any duration a caller could mean and removes the one value
    /// that turned "give me everything" into a crash.
    /// </remarks>
    [Fact]
    public async Task AskingForTheWholeRecordingReturnsItRatherThanOverflowing()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.1f, 0.2f, 0.3f, 0.4f);
        var snapshot = capture.GetSnapshot(TimeSpan.MaxValue);

        Assert.NotNull(snapshot);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(CompletePreviewFixture, snapshot.Samples.ToArray());
    }

    /// <summary>Every duration a caller could mean is accepted, including the extremes.</summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue / 2)]
    [InlineData(TimeSpan.TicksPerDay * 365)]
    [InlineData(TimeSpan.TicksPerSecond * 20)]
    public async Task NoReachableDurationThrows(long ticks)
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.1f, 0.2f, 0.3f, 0.4f);

        Assert.NotNull(capture.GetSnapshot(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public async Task UnexpectedStopPreservesTakeAsRetryableInterruption()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.4f, -0.2f, 0.1f);
        recorder.Interrupt();
        var result = await capture.StopAsync();

        Assert.False(capture.IsCapturing);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
        Assert.True(result.Error?.CanRetry);
        Assert.Equal(new[] { 0.4f, -0.2f, 0.1f }, result.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task OverlappingStartIsRejectedWithoutReplacingActiveRecorder()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);

        var first = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        var second = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(AppErrorCode.CaptureAlreadyActive, second.Error?.Code);
        Assert.Equal(1, recorder.StartCount);
        await capture.CancelAsync();
    }

    [Fact]
    public async Task CancelClearsBufferedAudioAndEmitsSilentLevel()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var levels = new List<AudioLevel>();
        capture.LevelChanged += (_, level) => levels.Add(level);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.75f);
        var cancelled = await capture.CancelAsync();
        var afterCancel = await capture.StopAsync();

        Assert.True(cancelled.Succeeded);
        Assert.False(capture.IsCapturing);
        Assert.Equal(AudioLevel.Silent, levels[^1]);
        Assert.Equal(AppErrorCode.InvalidTransition, afterCancel.Error?.Code);
        Assert.Empty(afterCancel.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task StopTimeoutReturnsInterruptionAndStillDisposesRecorder()
    {
        var recorder = new FakeRecorderSession { CompleteWhenStopped = false };
        await using var capture = CreateCapture(recorder);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.6f);
        var result = await capture.StopAsync();

        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
        Assert.Single(result.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task UnavailableDeviceIsReturnedAsTypedStartFailure()
    {
        var factory = new FakeRecorderFactory(
            new InvalidOperationException("Synthetic unavailable endpoint."));
        await using var capture = new WasapiAudioCapture(factory, TestTimeout);

        var result = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));

        Assert.False(result.Succeeded);
        Assert.Equal(AppErrorCode.AudioDeviceUnavailable, result.Error?.Code);
        Assert.True(result.Error?.CanRetry);
    }

    [Fact]
    public async Task ImmediateBackendStopDuringStartLeavesCaptureInactiveAndRecoverable()
    {
        var recorder = new FakeRecorderSession { InterruptWhenStarted = true };
        await using var capture = CreateCapture(recorder);

        var started = await capture.StartAsync(
            new AudioCaptureRequest(DictationSessionId.Create()));
        var result = await capture.StopAsync();

        Assert.True(started.Succeeded);
        Assert.False(capture.IsCapturing);
        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
    }

    [Fact]
    public async Task MalformedCallbackInterruptsCaptureWithoutCorruptingPriorAudio()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.3f);
        recorder.EmitBytes(new byte[3]);
        var result = await capture.StopAsync();

        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
        Assert.Equal(0.3f, Assert.Single(result.Samples.ToArray()), precision: 5);
    }

    private static WasapiAudioCapture CreateCapture(FakeRecorderSession recorder) =>
        new(new FakeRecorderFactory(recorder), TestTimeout);

    private sealed class FakeRecorderFactory : IAudioRecorderFactory
    {
        private readonly IAudioRecorderSession? _recorder;
        private readonly Exception? _exception;

        public FakeRecorderFactory(IAudioRecorderSession recorder) => _recorder = recorder;

        public FakeRecorderFactory(Exception exception) => _exception = exception;

        public Task<IAudioRecorderSession> CreateAsync(
            AudioDeviceId? deviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _exception is null
                ? Task.FromResult(_recorder!)
                : Task.FromException<IAudioRecorderSession>(_exception);
        }
    }

    private sealed class FakeRecorderSession : IAudioRecorderSession
    {
        public event EventHandler<AudioRecorderData>? DataAvailable;

        public event EventHandler<AudioRecorderStopped>? Stopped;

        public bool CompleteWhenStopped { get; init; } = true;

        public bool InterruptWhenStarted { get; init; }

        public int StartCount { get; private set; }

        public bool Disposed { get; private set; }

        public void Start()
        {
            StartCount++;
            if (InterruptWhenStarted)
            {
                Interrupt();
            }
        }

        public void Stop()
        {
            if (CompleteWhenStopped)
            {
                Stopped?.Invoke(this, new AudioRecorderStopped(Exception: null));
            }
        }

        public void Emit(params float[] samples)
        {
            var bytes = new byte[samples.Length * sizeof(float)];
            for (var index = 0; index < samples.Length; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    bytes.AsSpan(index * sizeof(float), sizeof(float)),
                    BitConverter.SingleToInt32Bits(samples[index]));
            }

            EmitBytes(bytes);
        }

        public void EmitBytes(byte[] bytes) =>
            DataAvailable?.Invoke(this, new AudioRecorderData(bytes, IsSilent: false));

        public void Interrupt(Exception? exception = null) =>
            Stopped?.Invoke(this, new AudioRecorderStopped(exception));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
