using System.Buffers.Binary;
using System.Collections.Concurrent;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Audio;

/// <summary>
/// Computes and caches deterministic synthesized mini waveform bars for recording sound pairings.
/// Matches macOS RecordingChimeWaveform (40 bars from start + stop chime audio envelopes).
/// </summary>
public static class RecordingChimeWaveform
{
    public const int BarCount = 40;
    private const int HalfBarCount = BarCount / 2;

    private static readonly ConcurrentDictionary<RecordingSoundPairing, double[]> Cache = new();

    /// <summary>
    /// Gets the 40 normalized waveform bar values (0.0 to 1.0) for the specified chime pairing.
    /// </summary>
    public static IReadOnlyList<double> GetBarHeights(
        RecordingSoundPairing pairing,
        string? assetDirectory = null)
    {
        return Cache.GetOrAdd(pairing, p => ComputeHeights(p, assetDirectory));
    }

    /// <summary>
    /// Computes the normalized waveform envelope from the start and stop WAV assets.
    /// </summary>
    private static double[] ComputeHeights(
        RecordingSoundPairing pairing,
        string? assetDirectory)
    {
        var dir = assetDirectory ?? Path.Combine(
            AppContext.BaseDirectory,
            RecordingSoundAssetCatalog.RelativeDirectory);

        try
        {
            if (Directory.Exists(dir))
            {
                var startAsset = RecordingSoundAssetCatalog.Load(dir, pairing, RecordingSoundMoment.Start);
                var stopAsset = RecordingSoundAssetCatalog.Load(dir, pairing, RecordingSoundMoment.Stop);

                var startEnvelope = ComputePcmEnvelope(startAsset.Pcm, HalfBarCount);
                var stopEnvelope = ComputePcmEnvelope(stopAsset.Pcm, BarCount - HalfBarCount);

                var joined = new double[BarCount];
                Array.Copy(startEnvelope, 0, joined, 0, HalfBarCount);
                Array.Copy(stopEnvelope, 0, joined, HalfBarCount, BarCount - HalfBarCount);

                var peak = 0.0;
                for (var i = 0; i < BarCount; i++)
                {
                    if (joined[i] > peak)
                    {
                        peak = joined[i];
                    }
                }

                if (peak > 0)
                {
                    for (var i = 0; i < BarCount; i++)
                    {
                        joined[i] = Math.Clamp(joined[i] / peak, 0.0, 1.0);
                    }
                    return joined;
                }
            }
        }
        catch
        {
            // Fall back to deterministic synthesized waveform if assets are inaccessible.
        }

        return SynthesizeDeterministicWaveform(pairing);
    }

    /// <summary>
    /// Computes RMS loudness across N equal slices of a 16-bit mono PCM buffer.
    /// </summary>
    private static double[] ComputePcmEnvelope(byte[] pcm, int bars)
    {
        var result = new double[bars];
        if (pcm.Length < 2 || bars <= 0)
        {
            return result;
        }

        var sampleCount = pcm.Length / 2;
        var span = pcm.AsSpan();

        for (var b = 0; b < bars; b++)
        {
            var from = sampleCount * b / bars;
            var to = sampleCount * (b + 1) / bars;
            if (to <= from)
            {
                result[b] = 0;
                continue;
            }

            var sumSquares = 0.0;
            for (var i = from; i < to; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
                var normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }

            result[b] = Math.Sqrt(sumSquares / (to - from));
        }

        return result;
    }

    /// <summary>
    /// Generates a non-zero deterministic synthetic envelope if audio files cannot be read.
    /// </summary>
    private static double[] SynthesizeDeterministicWaveform(RecordingSoundPairing pairing)
    {
        var heights = new double[BarCount];
        var seed = (int)pairing + 1;
        for (var i = 0; i < BarCount; i++)
        {
            // Deterministic smooth bell-like curves representing start and stop chimes
            var phase1 = Math.Sin(Math.PI * (i % HalfBarCount) / HalfBarCount);
            var modulation = 0.5 + 0.5 * Math.Cos((i + seed) * 0.7);
            heights[i] = Math.Clamp(phase1 * modulation, 0.1, 1.0);
        }
        return heights;
    }
}
