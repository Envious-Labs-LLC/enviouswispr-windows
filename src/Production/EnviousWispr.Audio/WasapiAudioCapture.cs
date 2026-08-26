using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace EnviousWispr.Audio;

public sealed class WasapiAudioCapture : IAudioCapture
{
    private static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(
        AudioSampleConverter.TargetSampleRate,
        channels: 1);

    private readonly object _bufferGate = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly MemoryStream _capturedBytes = new();
    private WasapiRecorder? _recorder;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private AudioCaptureRequest? _request;
    private bool _disposed;

    public event EventHandler<AudioLevel>? LevelChanged;

    public bool IsCapturing { get; private set; }

    public async Task<AudioOperationResult> StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsCapturing)
            {
                return Failure(AppErrorCode.CaptureAlreadyActive, canRetry: false);
            }

            WasapiRecorder recorder;
            try
            {
                recorder = await BuildRecorderAsync(request.DeviceId).ConfigureAwait(false);
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
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;

            try
            {
                recorder.StartRecording();
                IsCapturing = true;
                return new AudioOperationResult(Succeeded: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
                _recorder = null;
                _request = null;
                _recordingStopped = null;
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
            if (!IsCapturing || _recorder is null || _request is null)
            {
                return EmptyFailure(AppErrorCode.InvalidTransition);
            }

            IsCapturing = false;
            var recorder = _recorder;
            var request = _request;
            var stopped = _recordingStopped!;
            Exception? stopException = null;
            try
            {
                recorder.StopRecording();
                stopException = await stopped.Task.WaitAsync(
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException timeoutException)
            {
                stopException = timeoutException;
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                stopException = exception;
            }

            var samples = SnapshotSamples();
            try
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                stopException ??= exception;
            }
            ClearSession();

            return stopException is null
                ? new CapturedAudio(
                    request.SessionId,
                    samples,
                    AudioSampleConverter.TargetSampleRate,
                    Channels: 1)
                : new CapturedAudio(
                    request.SessionId,
                    samples,
                    AudioSampleConverter.TargetSampleRate,
                    Channels: 1,
                    AudioCaptureOutcome.Interrupted,
                    new AppError(
                        AppErrorCode.AudioDeviceLost,
                        AppErrorStage.AudioCapture,
                        CanRetry: samples.Length > 0));
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
            if (_recorder is not null)
            {
                if (IsCapturing)
                {
                    _recorder.StopRecording();
                }

                await ReleaseRecorderAsync(_recorder).ConfigureAwait(false);
            }

            lock (_bufferGate)
            {
                _capturedBytes.SetLength(0);
            }

            ClearSession();
            LevelChanged?.Invoke(this, AudioLevel.Silent);
            return new AudioOperationResult(Succeeded: true);
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

    private static async Task<WasapiRecorder> BuildRecorderAsync(AudioDeviceId? deviceId)
    {
        var builder = new WasapiRecorderBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(50)
            .WithFormat(CaptureFormat)
            .WithMmcssThreadPriority("Audio");

        if (deviceId is null)
        {
            return await builder
                .WithDefaultDeviceStreamRouting()
                .BuildAsync()
                .ConfigureAwait(false);
        }

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId.Value.Value);
        return builder.WithDevice(device).Build();
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> data,
        AudioClientBufferFlags flags,
        long devicePosition,
        long performanceCounterPosition)
    {
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            data = new byte[data.Length];
        }

        lock (_bufferGate)
        {
            _capturedBytes.Write(data);
        }

        try
        {
            var converted = AudioSampleConverter.ConvertToMono16Khz(
                data,
                new AudioBufferFormat(
                    AudioSampleConverter.TargetSampleRate,
                    Channels: 1,
                    AudioSampleEncoding.Ieee754Single));
            LevelChanged?.Invoke(this, new AudioLevel(converted.Peak, converted.RootMeanSquare));
        }
        catch (ArgumentException exception)
        {
            _recordingStopped?.TrySetResult(exception);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args) =>
        _recordingStopped?.TrySetResult(args.Exception);

    private float[] SnapshotSamples()
    {
        byte[] bytes;
        lock (_bufferGate)
        {
            bytes = _capturedBytes.ToArray();
            _capturedBytes.SetLength(0);
        }

        return AudioSampleConverter.ConvertToMono16Khz(
            bytes,
            new AudioBufferFormat(
                AudioSampleConverter.TargetSampleRate,
                Channels: 1,
                AudioSampleEncoding.Ieee754Single)).Samples;
    }

    private async ValueTask ReleaseRecorderAsync(WasapiRecorder recorder)
    {
        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        await recorder.DisposeAsync().ConfigureAwait(false);
    }

    private void ClearSession()
    {
        IsCapturing = false;
        _recorder = null;
        _recordingStopped = null;
        _request = null;
    }

    private static AudioOperationResult Failure(AppErrorCode code, bool canRetry) => new(
        Succeeded: false,
        new AppError(code, AppErrorStage.AudioCapture, canRetry));

    private static CapturedAudio EmptyFailure(AppErrorCode code) => new(
        DictationSessionId.Create(),
        ReadOnlyMemory<float>.Empty,
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioCaptureOutcome.Interrupted,
        new AppError(code, AppErrorStage.AudioCapture, CanRetry: false));
}
