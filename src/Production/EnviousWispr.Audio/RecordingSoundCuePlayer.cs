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
/// Plays short, original procedural recording confirmations. The catalog mirrors
/// the macOS product, while synthesis is performed locally so no sampled or
/// licensed sound assets are required by the Windows build.
/// </summary>
public sealed class RecordingSoundCuePlayer : IDisposable
{
    private const int SampleRate = 44_100;
    private readonly object _gate = new();
    private readonly HashSet<WasapiPlayer> _active = [];
    private bool _disposed;

    public bool Play(RecordingSoundPairing pairing, RecordingSoundMoment moment)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        WasapiPlayer? output = null;
        try
        {
            var samples = RecordingSoundSynthesizer.Create(pairing, moment, SampleRate);
            var provider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
            {
                DiscardOnBufferOverflow = true,
            };
            provider.AddSamples(samples, 0, samples.Length);

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

internal static class RecordingSoundSynthesizer
{
    internal static byte[] Create(
        RecordingSoundPairing pairing,
        RecordingSoundMoment moment,
        int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        var profile = ProfileFor(pairing);
        var duration = moment == RecordingSoundMoment.Start
            ? profile.StartDuration
            : profile.StopDuration;
        var sampleCount = Math.Max(1, (int)Math.Round(sampleRate * duration));
        var bytes = new byte[sampleCount * sizeof(short)];
        var direction = moment == RecordingSoundMoment.Start ? 1d : -1d;
        var seed = unchecked((uint)((long)(int)pairing * 2_654_435_761L) +
            (moment == RecordingSoundMoment.Start ? 0x13579BDFu : 0x2468ACE0u));
        var filteredNoise = 0d;

        for (var index = 0; index < sampleCount; index++)
        {
            var normalized = index / (double)Math.Max(1, sampleCount - 1);
            var attack = Math.Min(1d, normalized / Math.Max(0.015, profile.AttackFraction));
            var release = Math.Pow(Math.Max(0d, 1d - normalized), profile.ReleasePower);
            var envelope = attack * release;
            var sweptFrequency = profile.Frequency *
                (1d + direction * profile.Sweep * (normalized - 0.5));
            var phase = 2d * Math.PI * sweptFrequency * index / sampleRate;
            var sine = Math.Sin(phase);
            var triangle = 2d / Math.PI * Math.Asin(sine);
            var second = Math.Sin(
                2d * Math.PI * sweptFrequency * profile.SecondRatio * index / sampleRate);

            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            var whiteNoise = seed / (double)uint.MaxValue * 2d - 1d;
            filteredNoise += profile.NoiseFilter * (whiteNoise - filteredNoise);

            var tonal = sine * (1d - profile.TriangleMix) + triangle * profile.TriangleMix;
            tonal = tonal * (1d - profile.SecondMix) + second * profile.SecondMix;
            var value = envelope * profile.Gain *
                (tonal * (1d - profile.NoiseMix) + filteredNoise * profile.NoiseMix);
            var sample = (short)Math.Clamp(
                Math.Round(value * short.MaxValue),
                short.MinValue,
                short.MaxValue);
            bytes[index * 2] = (byte)(sample & 0xFF);
            bytes[index * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return bytes;
    }

    private static SoundProfile ProfileFor(RecordingSoundPairing pairing) => pairing switch
    {
        RecordingSoundPairing.DustMote => new(0.050, 0.050, 260, 0.00, 0.15, 0.06, 0.95, 0.28, 0.0, 0.0, 0.12, 2.8),
        RecordingSoundPairing.VelvetHush => new(0.120, 0.120, 420, 0.08, 0.08, 0.10, 0.32, 1.06, 0.35, 0.0, 0.06, 2.0),
        RecordingSoundPairing.MutedConfirm => new(0.090, 0.075, 520, 0.00, 0.09, 0.08, 0.12, 1.00, 0.0, 0.25, 0.05, 2.4),
        RecordingSoundPairing.WhisperTick => new(0.055, 0.055, 1_150, 0.03, 0.08, 0.055, 0.42, 1.00, 0.0, 0.25, 0.04, 3.2),
        RecordingSoundPairing.RoundPebble => new(0.140, 0.140, 340, 0.06, 0.07, 0.11, 0.08, 1.50, 0.15, 0.40, 0.08, 2.1),
        RecordingSoundPairing.PaperTap => new(0.070, 0.070, 730, 0.01, 0.18, 0.09, 0.60, 1.00, 0.0, 0.55, 0.04, 3.8),
        RecordingSoundPairing.SoftHush => new(0.130, 0.130, 300, 0.02, 0.10, 0.10, 0.78, 1.00, 0.0, 0.0, 0.10, 1.7),
        RecordingSoundPairing.LowNod => new(0.150, 0.150, 220, 0.04, 0.07, 0.13, 0.10, 1.25, 0.20, 0.25, 0.08, 1.7),
        RecordingSoundPairing.CloudPop => new(0.090, 0.100, 480, 0.06, 0.16, 0.12, 0.72, 1.00, 0.0, 0.10, 0.04, 2.8),
        RecordingSoundPairing.VelvetTap => new(0.110, 0.130, 390, 0.02, 0.08, 0.12, 0.25, 1.00, 0.0, 0.45, 0.05, 2.4),
        RecordingSoundPairing.SatinShift => new(0.200, 0.220, 510, 0.16, 0.06, 0.12, 0.10, 1.25, 0.30, 0.10, 0.08, 1.6),
        RecordingSoundPairing.AirGlint => new(0.150, 0.160, 980, 0.18, 0.05, 0.13, 0.30, 1.50, 0.25, 0.10, 0.05, 1.8),
        _ => throw new ArgumentOutOfRangeException(nameof(pairing)),
    };

    private sealed record SoundProfile(
        double StartDuration,
        double StopDuration,
        double Frequency,
        double Sweep,
        double AttackFraction,
        double Gain,
        double NoiseMix,
        double SecondRatio,
        double SecondMix,
        double TriangleMix,
        double NoiseFilter,
        double ReleasePower);
}
