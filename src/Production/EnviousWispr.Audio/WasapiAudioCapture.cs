using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using System.Runtime.InteropServices;

namespace EnviousWispr.Audio;

public sealed class WasapiAudioCapture : IAudioCapture, IAudioSnapshotSource
{
    private static readonly AudioBufferFormat CaptureFormat = new(
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioSampleEncoding.Ieee754Single);

    private readonly object _bufferGate = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly MemoryStream _capturedBytes = new();
    private readonly IAudioRecorderFactory _recorderFactory;
    private readonly TimeSpan _stopTimeout;
    private IAudioRecorderSession? _recorder;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private AudioCaptureRequest? _request;
    private volatile bool _isCapturing;
    private volatile bool _clientRequestedStop;
    private volatile bool _backendStopped;
    private volatile bool _unexpectedStop;
    private bool _disposed;

    public WasapiAudioCapture()
        : this(new WasapiRecorderFactory(), TimeSpan.FromSeconds(2))
    {
    }

    internal WasapiAudioCapture(IAudioRecorderFactory recorderFactory, TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(recorderFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);

        _recorderFactory = recorderFactory;
        _stopTimeout = stopTimeout;
    }

    public event EventHandler<AudioLevel>? LevelChanged;

    public bool IsCapturing => _isCapturing;

    public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumDuration, TimeSpan.Zero);
        var maximumSamples = Math.Max(
            1,
            checked((int)Math.Ceiling(
                maximumDuration.TotalSeconds * AudioSampleConverter.TargetSampleRate)));
        var maximumBytes = checked(maximumSamples * sizeof(float));
        byte[] bytes;
        DictationSessionId sessionId;
        lock (_bufferGate)
        {
            if (!_isCapturing || _request is null)
            {
                return null;
            }

            sessionId = _request.SessionId;
            var byteCount = (int)Math.Min(_capturedBytes.Length, maximumBytes);
            if (!_capturedBytes.TryGetBuffer(out var buffer) || buffer.Array is null)
            {
                return null;
            }

            bytes = buffer.Array.AsSpan(
                buffer.Offset + checked((int)_capturedBytes.Length) - byteCount,
                byteCount).ToArray();
        }

        var samples = AudioSampleConverter.ConvertToMono16Khz(bytes, CaptureFormat).Samples;
        return new AudioSnapshot(
            sessionId,
            samples,
            AudioSampleConverter.TargetSampleRate,
            Channels: 1);
    }

    public async Task<AudioOperationResult> StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recorder is not null)
            {
                return Failure(AppErrorCode.CaptureAlreadyActive, canRetry: false);
            }

            IAudioRecorderSession recorder;
            try
            {
                recorder = await _recorderFactory
                    .CreateAsync(request.DeviceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Failure(AppErrorCode.AudioFormatUnsupported, canRetry: true);
            }
            catch (InvalidOperationException)
            {
                return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
            }
            catch (UnauthorizedAccessException)
            {
                return Failure(AppErrorCode.AccessDenied, canRetry: true);
            }
            catch (COMException)
            {
                return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
            }

            lock (_bufferGate)
            {
                _capturedBytes.SetLength(0);
            }

            _request = request;
            _recordingStopped = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _recorder = recorder;
            _clientRequestedStop = false;
            _backendStopped = false;
            _unexpectedStop = false;
            recorder.DataAvailable += OnDataAvailable;
            recorder.Stopped += OnRecordingStopped;

            try
            {
                _isCapturing = true;
                recorder.Start();
                return new AudioOperationResult(Succeeded: true);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ArgumentException or COMException)
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
                ClearSession();
                return Failure(AppErrorCode.AudioDeviceUnavailable, canRetry: true);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recorder is null || _request is null || _recordingStopped is null)
            {
                return EmptyFailure(AppErrorCode.InvalidTransition);
            }

            var recorder = _recorder;
            var request = _request;
            var stopped = _recordingStopped;
            Exception? stopException = null;
            if (!_backendStopped)
            {
                _clientRequestedStop = true;
                _isCapturing = false;
                try
                {
                    recorder.Stop();
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException)
                {
                    stopException = exception;
                }
            }

            if (stopException is null)
            {
                try
                {
                    stopException = await stopped.Task
                        .WaitAsync(_stopTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException timeoutException)
                {
                    stopException = timeoutException;
                }
            }

            var interrupted = _unexpectedStop || stopException is not null;
            var samples = SnapshotSamples();
            try
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                stopException ??= exception;
                interrupted = true;
            }

            ClearSession();
            return interrupted
                ? Interrupted(request.SessionId, samples)
                : new CapturedAudio(
                    request.SessionId,
                    samples,
                    AudioSampleConverter.TargetSampleRate,
                    Channels: 1);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? cleanupException = null;
            if (_recorder is not null)
            {
                var recorder = _recorder;
                var stopped = _recordingStopped;
                if (stopped is not null && !_backendStopped)
                {
                    _clientRequestedStop = true;
                    _isCapturing = false;
                    try
                    {
                        recorder.Stop();
                        await stopped.Task
                            .WaitAsync(_stopTimeout, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or COMException or TimeoutException)
                    {
                        cleanupException = exception;
                    }
                }

                try
                {
                    await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or COMException)
                {
                    cleanupException ??= exception;
                }
            }

            lock (_bufferGate)
            {
                _capturedBytes.SetLength(0);
            }

            ClearSession();
            LevelChanged?.Invoke(this, AudioLevel.Silent);
            return cleanupException is null
                ? new AudioOperationResult(Succeeded: true)
                : Failure(AppErrorCode.AudioDeviceLost, canRetry: true);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CancelAsync().ConfigureAwait(false);
        _disposed = true;
        _capturedBytes.Dispose();
        _transitionGate.Dispose();
    }

    private void OnDataAvailable(object? sender, AudioRecorderData args)
    {
        var bytes = args.IsSilent ? new byte[args.Bytes.Length] : args.Bytes.ToArray();
        try
        {
            var converted = AudioSampleConverter.ConvertToMono16Khz(bytes, CaptureFormat);
            lock (_bufferGate)
            {
                _capturedBytes.Write(bytes);
            }

            LevelChanged?.Invoke(this, new AudioLevel(converted.Peak, converted.RootMeanSquare));
        }
        catch (ArgumentException exception)
        {
            _unexpectedStop = true;
            _isCapturing = false;
            _recordingStopped?.TrySetResult(exception);
        }
    }

    private void OnRecordingStopped(object? sender, AudioRecorderStopped args)
    {
        _backendStopped = true;
        if (!_clientRequestedStop)
        {
            _unexpectedStop = true;
            _isCapturing = false;
        }

        _recordingStopped?.TrySetResult(args.Exception);
    }

    private float[] SnapshotSamples()
    {
        byte[] bytes;
        lock (_bufferGate)
        {
            bytes = _capturedBytes.ToArray();
            _capturedBytes.SetLength(0);
        }

        return AudioSampleConverter.ConvertToMono16Khz(bytes, CaptureFormat).Samples;
    }

    private async ValueTask ReleaseRecorderAsync(IAudioRecorderSession recorder)
    {
        recorder.DataAvailable -= OnDataAvailable;
        recorder.Stopped -= OnRecordingStopped;
        await recorder.DisposeAsync().ConfigureAwait(false);
    }

    private void ClearSession()
    {
        _isCapturing = false;
        _recorder = null;
        _recordingStopped = null;
        _request = null;
        _clientRequestedStop = false;
        _backendStopped = false;
        _unexpectedStop = false;
    }

    private static AudioOperationResult Failure(AppErrorCode code, bool canRetry) => new(
        Succeeded: false,
        new AppError(code, AppErrorStage.AudioCapture, canRetry));

    private static CapturedAudio EmptyFailure(AppErrorCode code) => new(
        new DictationSessionId(Guid.Empty),
        ReadOnlyMemory<float>.Empty,
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioCaptureOutcome.Interrupted,
        new AppError(code, AppErrorStage.AudioCapture, CanRetry: false));

    private static CapturedAudio Interrupted(DictationSessionId sessionId, float[] samples) => new(
        sessionId,
        samples,
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioCaptureOutcome.Interrupted,
        new AppError(
            AppErrorCode.AudioDeviceLost,
            AppErrorStage.AudioCapture,
            CanRetry: samples.Length > 0));
}
