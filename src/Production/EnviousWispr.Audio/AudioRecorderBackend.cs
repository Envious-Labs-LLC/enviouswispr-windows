using EnviousWispr.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EnviousWispr.Audio;

internal sealed record AudioRecorderData(ReadOnlyMemory<byte> Bytes, bool IsSilent);

internal sealed record AudioRecorderStopped(Exception? Exception);

internal interface IAudioRecorderSession : IAsyncDisposable
{
    event EventHandler<AudioRecorderData>? DataAvailable;

    event EventHandler<AudioRecorderStopped>? Stopped;

    AudioBufferFormat Format => new(
        AudioSampleConverter.TargetSampleRate,
        Channels: 1,
        AudioSampleEncoding.Ieee754Single);

    bool IsEndpointMuted() => false;

    void Start();

    void Stop();
}

internal interface IAudioRecorderFactory
{
    Task<IAudioRecorderSession> CreateAsync(
        AudioDeviceId? deviceId,
        CancellationToken cancellationToken = default);
}

internal sealed class WasapiRecorderFactory : IAudioRecorderFactory
{
    private static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(
        AudioSampleConverter.TargetSampleRate,
        channels: 1);

    public async Task<IAudioRecorderSession> CreateAsync(
        AudioDeviceId? deviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var builder = new WasapiRecorderBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(20)
            .WithFormat(CaptureFormat)
            .WithMmcssThreadPriority("Audio");

        WasapiRecorder recorder;
        if (deviceId is null)
        {
            recorder = await builder
                .WithDefaultDeviceStreamRouting()
                .BuildAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId.Value.Value);
            recorder = builder.WithDevice(device).Build();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new NAudioRecorderSession(recorder, deviceId);
    }
}

internal sealed class NAudioRecorderSession : IAudioRecorderSession
{
    private readonly WasapiRecorder _recorder;
    private readonly AudioDeviceId? _deviceId;

    public NAudioRecorderSession(WasapiRecorder recorder, AudioDeviceId? deviceId = null)
    {
        _recorder = recorder;
        _deviceId = deviceId;
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public event EventHandler<AudioRecorderData>? DataAvailable;

    public event EventHandler<AudioRecorderStopped>? Stopped;

    public bool IsEndpointMuted()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = _deviceId is { } id
                ? enumerator.GetDevice(id.Value)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.AudioEndpointVolume?.Mute ?? false;
        }
        catch
        {
            return false;
        }
    }

    public void Start() => _recorder.StartRecording();

    public void Stop() => _recorder.StopRecording();

    public async ValueTask DisposeAsync()
    {
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;
        await _recorder.DisposeAsync().ConfigureAwait(false);
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> data,
        AudioClientBufferFlags flags,
        long devicePosition,
        long performanceCounterPosition)
    {
        var bytes = data.ToArray();
        DataAvailable?.Invoke(
            this,
            new AudioRecorderData(bytes, (flags & AudioClientBufferFlags.Silent) != 0));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args) =>
        Stopped?.Invoke(this, new AudioRecorderStopped(args.Exception));
}
