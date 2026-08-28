using System.Buffers.Binary;

namespace EnviousWispr.Core.Diagnostics;

/// <summary>
/// Writes captured audio as a RIFF WAVE file, so a bad transcript can be replayed.
/// </summary>
/// <remarks>
/// WRITTEN RATHER THAN TAKEN FROM A LIBRARY because the whole format needed here is 44 bytes of
/// header and the samples. A dependency for that would be more surface than the thing it replaces.
///
/// SIXTEEN-BIT PCM, which is what the capture pipeline already resamples to and what every tool
/// that might open one of these expects. Floats are what the pipeline carries internally; a float
/// WAVE file is legal and half the tools that would be used to listen to it will not open one.
///
/// THE SAMPLE RATE IS A PARAMETER RATHER THAN A CONSTANT even though the pipeline only ever uses
/// one. A file whose header says 16kHz when its samples are 48kHz plays back at a third speed and
/// sounds like a fault in the recogniser rather than in the file - which is exactly the wrong
/// conclusion for a tool whose entire job is telling you what the recogniser heard.
/// </remarks>
public static class WaveFile
{
    private const int HeaderBytes = 44;
    private const short PcmFormat = 1;
    private const short BitsPerSample = 16;

    /// <summary>
    /// Encodes mono float samples as a 16-bit PCM WAVE file.
    /// </summary>
    /// <param name="samples">Samples in the range -1 to 1. Values outside it are clamped.</param>
    /// <param name="sampleRate">The rate the samples were captured at.</param>
    public static byte[] EncodeMono(ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var dataBytes = samples.Length * sizeof(short);
        var file = new byte[HeaderBytes + dataBytes];
        var span = file.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], HeaderBytes - 8 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], PcmFormat);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        for (var i = 0; i < samples.Length; i++)
        {
            // Clamped BEFORE scaling. A sample slightly over 1 scales past short.MaxValue and wraps
            // to a large negative - so the loudest moment of a recording becomes its quietest, and
            // the file sounds like a crackle exactly where the user was speaking up.
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(
                span[(HeaderBytes + (i * sizeof(short)))..],
                (short)(clamped * short.MaxValue));
        }

        return file;
    }
}
