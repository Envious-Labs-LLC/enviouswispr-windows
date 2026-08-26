using System.Buffers.Binary;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Architecture.Tests;

public sealed class WasapiAudioCaptureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(25);

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
