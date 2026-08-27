using EnviousWispr.Core.Settings;
using NAudio.Wave;

namespace EnviousWispr.Audio;

public enum RecordingSoundMoment
{
    Start,
    Stop,
}

public sealed class RecordingSoundCueCoordinator
{
    private readonly Func<RecordingSoundPairing, RecordingSoundMoment, bool> _playback;
    private RecordingSoundPairing? _activePairing;

    public RecordingSoundCueCoordinator(
        Func<RecordingSoundPairing, RecordingSoundMoment, bool> playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        _playback = playback;
    }

    public void Handle(
        bool isRecording,
        bool enabled,
        RecordingSoundPairing selectedPairing)
    {
        if (isRecording)
        {
            if (_activePairing is null && enabled &&
                _playback(selectedPairing, RecordingSoundMoment.Start))
            {
                _activePairing = selectedPairing;
            }

            return;
        }

        if (_activePairing is not { } activePairing)
        {
            return;
        }

        _activePairing = null;
        _playback(activePairing, RecordingSoundMoment.Stop);
    }
}

/// <summary>
/// Plays the original EnviousWispr recording confirmations shared with the
/// macOS product. The files are local, deterministic product assets.
/// </summary>
public sealed class RecordingSoundCuePlayer : IDisposable
{
    private readonly string _assetDirectory;
    private readonly object _gate = new();
    private readonly HashSet<WasapiPlayer> _active = [];
    private bool _disposed;

    public RecordingSoundCuePlayer(string? assetDirectory = null)
    {
        _assetDirectory = assetDirectory ?? Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);
    }

    public bool Play(RecordingSoundPairing pairing, RecordingSoundMoment moment)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        WasapiPlayer? output = null;
        try
        {
            var asset = RecordingSoundAssetCatalog.Load(
                _assetDirectory,
                pairing,
                moment);
            var provider = new BufferedWaveProvider(asset.Format)
            {
                DiscardOnBufferOverflow = true,
            };
            provider.AddSamples(asset.Pcm, 0, asset.Pcm.Length);

            output = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithLatency(40)
                .WithEventSync()
                .Build();
            output.Init(provider);
            output.PlaybackStopped += OnPlaybackStopped;
            lock (_gate)
            {
                if (_disposed)
                {
                    output.Dispose();
                    return false;
                }

                _active.Add(output);
            }

            output.Play();
            return true;
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
            if (output is not null)
            {
                Release(output);
            }

            return false;
        }
    }

    public void Dispose()
    {
        WasapiPlayer[] active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            active = [.. _active];
            _active.Clear();
        }

        foreach (var output in active)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (sender is WasapiPlayer output)
        {
            Release(output);
        }
    }

    private void Release(WasapiPlayer output)
    {
        lock (_gate)
        {
            _active.Remove(output);
        }

        output.PlaybackStopped -= OnPlaybackStopped;
        output.Dispose();
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
