using System.Collections.Concurrent;
using System.Threading.Channels;
using EnviousWispr.Core.Settings;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EnviousWispr.Audio;

public enum RecordingSoundMoment
{
    Start,
    Stop,
}

public sealed class RecordingSoundCueCoordinator
{
    private readonly object _gate = new();
    private readonly Func<RecordingSoundPairing, RecordingSoundMoment, bool> _playback;
    private RecordingSoundPairing? _activePairing;
    private ulong _generation;

    public RecordingSoundCueCoordinator(
        Func<RecordingSoundPairing, RecordingSoundMoment, bool> playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        _playback = playback;
    }

    public ulong CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public void Handle(
        bool isRecording,
        bool enabled,
        RecordingSoundPairing selectedPairing)
    {
        ulong? startGeneration = null;
        RecordingSoundPairing? stopPairing = null;
        lock (_gate)
        {
            if (isRecording)
            {
                if (_activePairing is not null || !enabled)
                {
                    return;
                }

                startGeneration = ++_generation;
                _activePairing = selectedPairing;
            }
            else
            {
                if (_activePairing is not { } activePairing)
                {
                    return;
                }

                _activePairing = null;
                ++_generation;
                stopPairing = activePairing;
            }
        }

        if (startGeneration is { } expectedGeneration)
        {
            var started = _playback(selectedPairing, RecordingSoundMoment.Start);
            if (!started)
            {
                lock (_gate)
                {
                    if (_generation == expectedGeneration && _activePairing == selectedPairing)
                    {
                        _activePairing = null;
                        ++_generation;
                    }
                }
            }

            return;
        }

        if (stopPairing is { } active)
        {
            _ = _playback(active, RecordingSoundMoment.Stop);
        }
    }

    /// <summary>
    /// Clears an active recording cue pairing without emitting a stop cue (for failed or
    /// rolled-back starts before capture actually began).
    /// </summary>
    public bool CancelActive()
    {
        lock (_gate)
        {
            if (_activePairing is null)
            {
                return false;
            }

            _activePairing = null;
            ++_generation;
            return true;
        }
    }

    /// <summary>
    /// Rolls back an armed pairing if a matching async start attempt fails before another
    /// transition advances the generation.
    /// </summary>
    public bool RollbackStart(RecordingSoundPairing pairing, ulong expectedGeneration)
    {
        lock (_gate)
        {
            if (_activePairing != pairing || _generation != expectedGeneration)
            {
                return false;
            }

            _activePairing = null;
            ++_generation;
            return true;
        }
    }
}

/// <summary>
/// Plays the original EnviousWispr recording confirmations shared with the
/// macOS product. The files are local, deterministic product assets.
/// </summary>
public sealed class RecordingSoundCuePlayer : IDisposable
{
    private const int CueLatencyMs = 40;

    private readonly string _assetDirectory;
    private readonly Action<RecordingSoundPairing, RecordingSoundMoment, Exception>? _onPlaybackFailed;
    private readonly Func<RecordingSoundAsset, CancellationToken, Task>? _playbackOverride;
    private readonly ConcurrentDictionary<(RecordingSoundPairing, RecordingSoundMoment), RecordingSoundAsset> _assets = new();
    private readonly object _gate = new();
    private readonly Channel<QueuedCue> _cueQueue = Channel.CreateUnbounded<QueuedCue>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<WasapiPlayer, EventHandler<StoppedEventArgs>> _active = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _pendingStartCts;
    private bool _consumerStarted;
    private bool _disposed;

    public RecordingSoundCuePlayer(
        string? assetDirectory = null,
        Action<RecordingSoundPairing, RecordingSoundMoment, Exception>? onPlaybackFailed = null)
        : this(assetDirectory, onPlaybackFailed, playbackOverride: null)
    {
    }

    internal RecordingSoundCuePlayer(
        string? assetDirectory,
        Action<RecordingSoundPairing, RecordingSoundMoment, Exception>? onPlaybackFailed,
        Func<RecordingSoundAsset, CancellationToken, Task>? playbackOverride)
    {
        _assetDirectory = assetDirectory ?? Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);
        _onPlaybackFailed = onPlaybackFailed;
        _playbackOverride = playbackOverride;
    }

    internal int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    internal bool HasCachedAsset(RecordingSoundPairing pairing, RecordingSoundMoment moment) =>
        _assets.ContainsKey((pairing, moment));

    /// <summary>
    /// Decodes and caches both start and stop assets for <paramref name="pairing"/> in memory so
    /// subsequent press/release cues avoid synchronous disk I/O.
    /// </summary>
    public void Preload(RecordingSoundPairing pairing)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        PreloadMoment(pairing, RecordingSoundMoment.Start);
        PreloadMoment(pairing, RecordingSoundMoment.Stop);
    }

    /// <summary>
    /// Cancels any pending start cue initialization that has not yet reached audio playback
    /// (for example, when capture fails to open after optimistic press UI).
    /// </summary>
    public void CancelPendingStart()
    {
        lock (_gate)
        {
            _pendingStartCts?.Cancel();
        }
    }

    public bool Play(RecordingSoundPairing pairing, RecordingSoundMoment moment) =>
        Play(pairing, moment, onFailure: null);

    public bool Play(RecordingSoundPairing pairing, RecordingSoundMoment moment, Action? onFailure)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            CancellationToken token;
            CancellationTokenSource? ownedStartCts = null;
            if (moment == RecordingSoundMoment.Start)
            {
                _pendingStartCts?.Cancel();
                ownedStartCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                _pendingStartCts = ownedStartCts;
                token = ownedStartCts.Token;
            }
            else
            {
                token = _disposeCts.Token;
            }

            if (!_cueQueue.Writer.TryWrite(new QueuedCue(pairing, moment, onFailure, ownedStartCts, token)))
            {
                if (ReferenceEquals(_pendingStartCts, ownedStartCts))
                {
                    _pendingStartCts = null;
                }

                ownedStartCts?.Dispose();
                return false;
            }

            if (!_consumerStarted)
            {
                _consumerStarted = true;
                _ = Task.Run(ProcessCueQueueAsync);
            }
        }

        return true;
    }

    public void Dispose()
    {
        KeyValuePair<WasapiPlayer, EventHandler<StoppedEventArgs>>[] active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            active = [.. _active];
            _active.Clear();
            _pendingStartCts?.Cancel();
            _disposeCts.Cancel();
            _cueQueue.Writer.TryComplete();
        }

        while (_cueQueue.Reader.TryRead(out var dropped))
        {
            ReleaseStartToken(dropped.OwnedStartCts);
        }

        foreach (var (output, handler) in active)
        {
            output.PlaybackStopped -= handler;
            SafeDispose(output);
        }
    }

    internal static IWaveProvider BuildAdaptedProvider(RecordingSoundAsset asset, WaveFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(targetFormat);

        var source = new FinitePcmWaveProvider(asset.Format, asset.Pcm);
        if (source.WaveFormat.Equals(targetFormat))
        {
            return source;
        }

        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.SampleRate != targetFormat.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, targetFormat.SampleRate);
        }

        if (samples.WaveFormat.Channels != targetFormat.Channels)
        {
            samples = new ChannelAdaptingSampleProvider(samples, targetFormat.Channels);
        }

        if (targetFormat.Encoding == WaveFormatEncoding.Pcm)
        {
            return targetFormat.BitsPerSample switch
            {
                16 => new SampleToWaveProvider16(samples),
                24 => new SampleToWaveProvider24(samples),
                _ => new SampleToWaveProvider(samples),
            };
        }

        return new SampleToWaveProvider(samples);
    }

    internal RecordingSoundAsset GetOrLoadAsset(RecordingSoundPairing pairing, RecordingSoundMoment moment)
    {
        var key = (pairing, moment);
        if (_assets.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var loaded = RecordingSoundAssetCatalog.Load(_assetDirectory, pairing, moment);
        _assets[key] = loaded;
        return loaded;
    }

    private void PreloadMoment(RecordingSoundPairing pairing, RecordingSoundMoment moment)
    {
        try
        {
            _ = GetOrLoadAsset(pairing, moment);
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
            _onPlaybackFailed?.Invoke(pairing, moment, exception);
        }
    }

    private async Task ProcessCueQueueAsync()
    {
        await foreach (var cue in _cueQueue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await PlayAsync(cue).ConfigureAwait(false);
        }
    }

    private void ReleaseStartToken(CancellationTokenSource? ownedStartCts)
    {
        if (ownedStartCts is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_pendingStartCts, ownedStartCts))
            {
                _pendingStartCts = null;
            }
        }

        ownedStartCts.Dispose();
    }

    private async Task PlayAsync(QueuedCue cue)
    {
        var pairing = cue.Pairing;
        var moment = cue.Moment;
        var onFailure = cue.OnFailure;
        var ownedStartCts = cue.OwnedStartCts;
        var cancellationToken = cue.CancellationToken;
        WasapiPlayer? output = null;
        EventHandler<StoppedEventArgs>? stoppedHandler = null;
        try
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed))
            {
                return;
            }

            var asset = GetOrLoadAsset(pairing, moment);
            if (_playbackOverride is not null)
            {
                await _playbackOverride(asset, cancellationToken).ConfigureAwait(false);
                return;
            }

            output = await new WasapiPlayerBuilder()
                .WithDefaultDeviceStreamRouting()
                .WithSharedMode()
                .WithLatency(CueLatencyMs)
                .WithEventSync()
                .BuildAsync()
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed))
            {
                SafeDispose(output);
                output = null;
                return;
            }

            var targetFormat = output.DeviceMixFormat.AsStandardWaveFormat();
            var provider = BuildAdaptedProvider(asset, targetFormat);
            var capability = output.GetPlaybackCapability(provider.WaveFormat);
            if (!capability.Supported && provider.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                provider = BuildAdaptedProvider(
                    asset,
                    WaveFormat.CreateIeeeFloatWaveFormat(
                        targetFormat.SampleRate,
                        targetFormat.Channels));
            }

            output.Init(provider);

            var localOutput = output;
            stoppedHandler = (_, args) =>
            {
                Release(localOutput);
                if (args.Exception is { } playbackException &&
                    !Volatile.Read(ref _disposed))
                {
                    NotifyFailure(pairing, moment, playbackException, onFailure);
                }
            };

            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    SafeDispose(output);
                    output = null;
                    return;
                }

                output.PlaybackStopped += stoppedHandler;
                _active.Add(output, stoppedHandler);
            }

            output.Play();
            // Ownership transfers to _active / PlaybackStopped once Play starts.
            output = null;
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
            if (output is not null)
            {
                if (stoppedHandler is not null)
                {
                    output.PlaybackStopped -= stoppedHandler;
                }

                Release(output);
            }

            if (exception is OperationCanceledException &&
                (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed)))
            {
                return;
            }

            NotifyFailure(pairing, moment, exception, onFailure);
        }
        finally
        {
            ReleaseStartToken(ownedStartCts);
        }
    }

    private readonly record struct QueuedCue(
        RecordingSoundPairing Pairing,
        RecordingSoundMoment Moment,
        Action? OnFailure,
        CancellationTokenSource? OwnedStartCts,
        CancellationToken CancellationToken);

    private void NotifyFailure(
        RecordingSoundPairing pairing,
        RecordingSoundMoment moment,
        Exception exception,
        Action? onFailure)
    {
        onFailure?.Invoke();
        _onPlaybackFailed?.Invoke(pairing, moment, exception);
    }

    private void Release(WasapiPlayer output)
    {
        EventHandler<StoppedEventArgs>? handler;
        lock (_gate)
        {
            _active.Remove(output, out handler);
        }

        if (handler is not null)
        {
            output.PlaybackStopped -= handler;
        }

        // Dispatch disposal off NAudio's playback callback thread to avoid self-join contention.
        _ = Task.Run(() => SafeDispose(output));
    }

    private static void SafeDispose(WasapiPlayer output)
    {
        try
        {
            output.Dispose();
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
        }
    }
}

/// <summary>
/// Reads an immutable PCM byte array exactly once and returns 0 at EOF so WASAPI stops and
/// fires <see cref="WasapiPlayer.PlaybackStopped"/> instead of emitting trailing silence.
/// </summary>
internal sealed class FinitePcmWaveProvider : IWaveProvider
{
    private readonly byte[] _pcm;
    private int _position;

    public FinitePcmWaveProvider(WaveFormat waveFormat, byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);
        ArgumentNullException.ThrowIfNull(pcm);
        WaveFormat = waveFormat;
        _pcm = pcm;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> buffer)
    {
        var remaining = _pcm.Length - _position;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var toCopy = Math.Min(buffer.Length, remaining);
        var blockAlign = WaveFormat.BlockAlign;
        if (blockAlign > 1 && toCopy >= blockAlign)
        {
            toCopy -= toCopy % blockAlign;
        }

        if (toCopy <= 0)
        {
            return 0;
        }

        _pcm.AsSpan(_position, toCopy).CopyTo(buffer);
        _position += toCopy;
        return toCopy;
    }
}

internal sealed class ChannelAdaptingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private readonly int _targetChannels;
    private float[] _sourceBuffer = [];

    public ChannelAdaptingSampleProvider(ISampleProvider source, int targetChannels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChannels);

        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        _targetChannels = targetChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            source.WaveFormat.SampleRate,
            targetChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<float> buffer)
    {
        var framesRequested = buffer.Length / _targetChannels;
        if (framesRequested <= 0)
        {
            return 0;
        }

        var sourceSamplesNeeded = framesRequested * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
        {
            _sourceBuffer = new float[sourceSamplesNeeded];
        }

        var sourceSamplesRead = _source.Read(_sourceBuffer.AsSpan(0, sourceSamplesNeeded));
        if (sourceSamplesRead <= 0)
        {
            return 0;
        }

        var framesRead = sourceSamplesRead / _sourceChannels;
        if (framesRead <= 0)
        {
            return 0;
        }

        var outIndex = 0;
        var inIndex = 0;
        if (_sourceChannels == 1)
        {
            for (var frame = 0; frame < framesRead; frame++)
            {
                var sample = _sourceBuffer[inIndex++];
                for (var channel = 0; channel < _targetChannels; channel++)
                {
                    buffer[outIndex++] = sample;
                }
            }
        }
        else if (_targetChannels == 1)
        {
            var scale = 1f / _sourceChannels;
            for (var frame = 0; frame < framesRead; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _sourceChannels; channel++)
                {
                    sum += _sourceBuffer[inIndex++];
                }

                buffer[outIndex++] = sum * scale;
            }
        }
        else
        {
            var copyChannels = Math.Min(_sourceChannels, _targetChannels);
            for (var frame = 0; frame < framesRead; frame++)
            {
                for (var channel = 0; channel < copyChannels; channel++)
                {
                    buffer[outIndex + channel] = _sourceBuffer[inIndex + channel];
                }

                for (var channel = copyChannels; channel < _targetChannels; channel++)
                {
                    buffer[outIndex + channel] = 0f;
                }

                inIndex += _sourceChannels;
                outIndex += _targetChannels;
            }
        }

        return framesRead * _targetChannels;
    }
}

internal sealed record RecordingSoundAsset(WaveFormat Format, byte[] Pcm);

internal static class RecordingSoundAssetCatalog
{
    internal static readonly string RelativeDirectory = Path.Combine(
        "Assets",
        "RecordingSounds");

    internal static RecordingSoundAsset Load(
        string assetDirectory,
        RecordingSoundPairing pairing,
        RecordingSoundMoment moment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDirectory);
        var path = Path.Combine(assetDirectory, FileNameFor(pairing, moment));
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
            reader.WaveFormat.SampleRate != 44_100 ||
            reader.WaveFormat.BitsPerSample != 16 ||
            reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                $"Recording sound asset has an unsupported format: {Path.GetFileName(path)}");
        }

        var pcm = new byte[checked((int)reader.Length)];
        var offset = 0;
        while (offset < pcm.Length)
        {
            var read = reader.Read(pcm, offset, pcm.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset != pcm.Length)
        {
            throw new EndOfStreamException(
                $"Recording sound asset ended early: {Path.GetFileName(path)}");
        }

        return new RecordingSoundAsset(reader.WaveFormat, pcm);
    }

    internal static string FileNameFor(
        RecordingSoundPairing pairing,
        RecordingSoundMoment moment)
    {
        var stem = pairing switch
        {
            RecordingSoundPairing.DustMote => "dustMote",
            RecordingSoundPairing.VelvetHush => "velvetHush",
            RecordingSoundPairing.MutedConfirm => "mutedConfirm",
            RecordingSoundPairing.WhisperTick => "whisperTick",
            RecordingSoundPairing.RoundPebble => "roundPebble",
            RecordingSoundPairing.PaperTap => "paperTap",
            RecordingSoundPairing.SoftHush => "softHush",
            RecordingSoundPairing.LowNod => "lowNod",
            RecordingSoundPairing.CloudPop => "cloudPop",
            RecordingSoundPairing.VelvetTap => "velvetTap",
            RecordingSoundPairing.SatinShift => "satinShift",
            RecordingSoundPairing.AirGlint => "airGlint",
            _ => throw new ArgumentOutOfRangeException(nameof(pairing)),
        };
        var suffix = moment switch
        {
            RecordingSoundMoment.Start => "start",
            RecordingSoundMoment.Stop => "stop",
            _ => throw new ArgumentOutOfRangeException(nameof(moment)),
        };
        return $"{stem}_{suffix}.wav";
    }
}
