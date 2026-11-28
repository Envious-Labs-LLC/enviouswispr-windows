using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EnviousWispr.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EnviousWispr.Audio;

/// <summary>
/// Thread-safe registry mapping capture device IDs to preferred 0-based channel indices.
/// </summary>
public static class AudioInputChannelRegistry
{
    private static ImmutableDictionary<string, int> _channelsByDeviceId =
        ImmutableDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase);

    public static void Configure(IReadOnlyDictionary<string, int>? channelsByDeviceId)
    {
        if (channelsByDeviceId is null || channelsByDeviceId.Count == 0)
        {
            Volatile.Write(
                ref _channelsByDeviceId,
                ImmutableDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (deviceId, channel) in channelsByDeviceId)
        {
            if (!string.IsNullOrWhiteSpace(deviceId) && channel >= 0)
            {
                builder[deviceId.Trim()] = Math.Clamp(channel, 0, 31);
            }
        }

        Volatile.Write(ref _channelsByDeviceId, builder.ToImmutable());
    }

    public static int GetSelectedChannel(AudioDeviceId? deviceId)
    {
        if (deviceId is null || string.IsNullOrWhiteSpace(deviceId.Value.Value))
        {
            return 0;
        }

        var map = Volatile.Read(ref _channelsByDeviceId);
        return map.TryGetValue(deviceId.Value.Value.Trim(), out var channel)
            ? Math.Clamp(channel, 0, 31)
            : 0;
    }
}

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

    private readonly Func<AudioDeviceId?, int> _selectedChannelResolver;

    public WasapiRecorderFactory()
        : this(AudioInputChannelRegistry.GetSelectedChannel)
    {
    }

    internal WasapiRecorderFactory(Func<AudioDeviceId?, int> selectedChannelResolver)
    {
        _selectedChannelResolver = selectedChannelResolver;
    }

    public async Task<IAudioRecorderSession> CreateAsync(
        AudioDeviceId? deviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AudioBufferFormat? sourceConversionFormat = null;
        var selectedChannelIndex = 0;

        WasapiRecorder recorder;
        if (deviceId is null)
        {
            AudioDeviceId? defaultId = null;
            WaveFormat? nativeCaptureWaveFormat = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                defaultId = new AudioDeviceId(defaultDevice.ID);
                selectedChannelIndex = _selectedChannelResolver(defaultId);
                if (selectedChannelIndex > 0)
                {
                    TryResolveNativeMultiChannelFormat(
                        defaultDevice,
                        selectedChannelIndex,
                        out nativeCaptureWaveFormat,
                        out sourceConversionFormat);
                }
            }
            catch
            {
                // Best-effort lookup for default endpoint; fall back cleanly to default mono capture.
            }

            var builder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(20)
                .WithFormat(nativeCaptureWaveFormat ?? CaptureFormat)
                .WithMmcssThreadPriority("Audio");

            recorder = await builder
                .WithDefaultDeviceStreamRouting()
                .BuildAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            deviceId ??= defaultId;
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId.Value.Value);
            selectedChannelIndex = _selectedChannelResolver(deviceId);
            WaveFormat? nativeCaptureWaveFormat = null;
            if (selectedChannelIndex > 0)
            {
                TryResolveNativeMultiChannelFormat(
                    device,
                    selectedChannelIndex,
                    out nativeCaptureWaveFormat,
                    out sourceConversionFormat);
            }

            var builder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(20)
                .WithFormat(nativeCaptureWaveFormat ?? CaptureFormat)
                .WithMmcssThreadPriority("Audio");

            recorder = builder.WithDevice(device).Build();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new NAudioRecorderSession(recorder, deviceId, sourceConversionFormat, selectedChannelIndex);
    }

    private static void TryResolveNativeMultiChannelFormat(
        MMDevice device,
        int selectedChannelIndex,
        out WaveFormat? waveFormat,
        out AudioBufferFormat? bufferFormat)
    {
        waveFormat = null;
        bufferFormat = null;
        try
        {
            using var audioClient = device.CreateAudioClient();
            var mixFormat = audioClient?.MixFormat;
            if (mixFormat is null || mixFormat.Channels <= 1)
            {
                return;
            }

            var channels = Math.Clamp(mixFormat.Channels, 1, 32);
            if (selectedChannelIndex < 0)
            {
                return;
            }

            if (mixFormat.Encoding == WaveFormatEncoding.Pcm &&
                mixFormat.BitsPerSample is 16 or 24 or 32)
            {
                waveFormat = new WaveFormat(mixFormat.SampleRate, mixFormat.BitsPerSample, channels);
                var encoding = mixFormat.BitsPerSample switch
                {
                    16 => AudioSampleEncoding.SignedPcm16,
                    24 => AudioSampleEncoding.SignedPcm24,
                    _ => AudioSampleEncoding.SignedPcm32,
                };
                bufferFormat = new AudioBufferFormat(mixFormat.SampleRate, channels, encoding);
                return;
            }

            waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, channels);
            bufferFormat = new AudioBufferFormat(
                mixFormat.SampleRate,
                channels,
                AudioSampleEncoding.Ieee754Single);
        }
        catch
        {
            waveFormat = null;
            bufferFormat = null;
        }
    }
}

internal sealed class NAudioRecorderSession : IAudioRecorderSession
{
    private readonly WasapiRecorder _recorder;
    private readonly AudioDeviceId? _deviceId;
    private readonly AudioBufferFormat? _sourceConversionFormat;
    private readonly int _selectedChannelIndex;

    public NAudioRecorderSession(
        WasapiRecorder recorder,
        AudioDeviceId? deviceId = null,
        AudioBufferFormat? sourceConversionFormat = null,
        int selectedChannelIndex = 0)
    {
        _recorder = recorder;
        _deviceId = deviceId;
        _sourceConversionFormat = sourceConversionFormat;
        _selectedChannelIndex = selectedChannelIndex;
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
        var isSilent = (flags & AudioClientBufferFlags.Silent) != 0;
        if (_sourceConversionFormat is { } sourceFormat && !data.IsEmpty)
        {
            var converted = AudioSampleConverter.ConvertToMono16Khz(
                data,
                sourceFormat,
                _selectedChannelIndex);
            var convertedBytes = new byte[converted.Samples.Length * sizeof(float)];
            MemoryMarshal.AsBytes(converted.Samples.AsSpan()).CopyTo(convertedBytes);
            DataAvailable?.Invoke(
                this,
                new AudioRecorderData(convertedBytes, isSilent));
            return;
        }

        var bytes = data.ToArray();
        DataAvailable?.Invoke(
            this,
            new AudioRecorderData(bytes, isSilent));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args) =>
        Stopped?.Invoke(this, new AudioRecorderStopped(args.Exception));
}
