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

        var dataBytes = checked(samples.Length * sizeof(short));
        var file = new byte[HeaderBytes + dataBytes];
        var span = file.AsSpan();
        WriteHeader(span[..HeaderBytes], sampleRate, dataBytes);
        EncodePcm16(samples, span[HeaderBytes..]);
        return file;
    }

    /// <summary>
    /// Streams mono float samples directly as a 16-bit PCM WAVE file without allocating a whole-file array.
    /// </summary>
    public static void WriteMono(Stream destination, ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var dataBytes = checked(samples.Length * sizeof(short));
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, sampleRate, dataBytes);
        destination.Write(header);

        const int chunkSamples = 32_768;
        var buffer = new byte[chunkSamples * sizeof(short)];
        var offset = 0;
        while (offset < samples.Length)
        {
            var count = Math.Min(chunkSamples, samples.Length - offset);
            var byteCount = count * sizeof(short);
            EncodePcm16(samples.Slice(offset, count), buffer.AsSpan(0, byteCount));
            destination.Write(buffer, 0, byteCount);
            offset += count;
        }
    }

    private static void WriteHeader(Span<byte> span, int sampleRate, int dataBytes)
    {
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
    }

    private static void EncodePcm16(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            // Clamped BEFORE scaling. A sample slightly over 1 scales past short.MaxValue and wraps
            // to a large negative - so the loudest moment of a recording becomes its quietest, and
            // the file sounds like a crackle exactly where the user was speaking up.
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[(i * sizeof(short))..],
                (short)(clamped * short.MaxValue));
        }
    }

    /// <summary>Computes duration in whole milliseconds for a captured sample count and sample rate.</summary>
    public static long ComputeDurationMs(int sampleCount, int sampleRate)
    {
        if (sampleCount <= 0 || sampleRate <= 0)
        {
            return 0;
        }

        return (long)Math.Round(sampleCount * 1000.0 / sampleRate);
    }

    /// <summary>
    /// Reads a local WAV file's duration and downsampled normalized waveform peak bars in [0..1].
    /// </summary>
    public static bool TryAnalyzeFile(string path, int barCount, out float[] bars, out long durationMs)
    {
        bars = [];
        durationMs = 0;
        if (string.IsNullOrWhiteSpace(path) || barCount <= 0)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryAnalyzeStream(stream, barCount, out bars, out durationMs);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            bars = [];
            durationMs = 0;
            return false;
        }
    }

    /// <summary>
    /// Parses WAV bytes and extracts duration in milliseconds and normalized waveform peaks in [0..1].
    /// </summary>
    public static bool TryAnalyze(ReadOnlySpan<byte> wavBytes, int barCount, out float[] bars, out long durationMs)
    {
        bars = [];
        durationMs = 0;
        if (barCount <= 0 || wavBytes.Length < HeaderBytes)
        {
            return false;
        }

        using var stream = new MemoryStream(wavBytes.ToArray(), writable: false);
        return TryAnalyzeStream(stream, barCount, out bars, out durationMs);
    }

    private static bool TryAnalyzeStream(Stream stream, int barCount, out float[] bars, out long durationMs)
    {
        bars = [];
        durationMs = 0;
        if (!stream.CanRead || !stream.CanSeek || stream.Length < HeaderBytes)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[12];
        if (!TryReadExact(stream, header)
            || !header[..4].SequenceEqual("RIFF"u8)
            || !header[8..12].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        bool hasFormat = false;

        long dataOffset = -1;
        long dataBytes = 0;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            if (!TryReadExact(stream, chunkHeader))
            {
                break;
            }

            var chunkId = chunkHeader[..4];
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var payloadStart = stream.Position;
            var remaining = Math.Max(0L, stream.Length - payloadStart);
            var availableSize = (long)Math.Min(chunkSize, (ulong)remaining);

            if (chunkId.SequenceEqual("fmt "u8) && availableSize >= 16)
            {
                if (!TryReadExact(stream, fmt))
                {
                    return false;
                }

                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(fmt[0..2]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..4]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..8]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..16]);
                hasFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataOffset = payloadStart;
                dataBytes = availableSize;
                if (hasFormat)
                {
                    break;
                }
            }

            var paddedSize = (long)chunkSize + (chunkSize % 2);
            var nextPos = payloadStart + paddedSize;
            if (nextPos <= payloadStart || nextPos > stream.Length)
            {
                break;
            }

            stream.Position = nextPos;
        }

        if (!hasFormat || dataOffset < 0 || channels <= 0 || sampleRate <= 0)
        {
            return false;
        }

        var isPcm16 = audioFormat == PcmFormat && bitsPerSample == 16;
        var isFloat32 = audioFormat == 3 && bitsPerSample == 32;
        if (!isPcm16 && !isFloat32)
        {
            return false;
        }

        var bytesPerSample = bitsPerSample / 8;
        var blockAlign = bytesPerSample * channels;
        if (blockAlign <= 0)
        {
            return false;
        }

        var frameCount = dataBytes / blockAlign;
        if (frameCount <= 0)
        {
            bars = new float[barCount];
            durationMs = 0;
            return true;
        }

        durationMs = (long)Math.Round(frameCount * 1000.0 / sampleRate);
        var peaks = new float[barCount];

        stream.Position = dataOffset;
        const int bufferFrames = 2048;
        var readBuffer = new byte[bufferFrames * blockAlign];
        long frameIndex = 0;
        var bytesLeft = frameCount * blockAlign;
        while (bytesLeft > 0)
        {
            var toRead = (int)Math.Min(readBuffer.Length, bytesLeft);
            toRead -= toRead % blockAlign;
            if (toRead <= 0)
            {
                break;
            }

            var read = stream.Read(readBuffer, 0, toRead);
            read -= read % blockAlign;
            if (read <= 0)
            {
                break;
            }

            var framesInChunk = read / blockAlign;
            for (var f = 0; f < framesInChunk; f++)
            {
                var bucket = (int)Math.Min(barCount - 1, frameIndex * barCount / frameCount);
                var frameBase = f * blockAlign;
                var frameAmp = 0f;
                for (var ch = 0; ch < channels; ch++)
                {
                    var sampleOffset = frameBase + (ch * bytesPerSample);
                    var amplitude = isPcm16
                        ? Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(readBuffer.AsSpan(sampleOffset, 2)) / 32768f)
                        : Math.Clamp(Math.Abs(BitConverter.ToSingle(readBuffer, sampleOffset)), 0f, 1f);
                    if (amplitude > frameAmp)
                    {
                        frameAmp = amplitude;
                    }
                }

                if (frameAmp > peaks[bucket])
                {
                    peaks[bucket] = frameAmp;
                }

                frameIndex++;
            }

            bytesLeft -= read;
        }

        bars = peaks;
        return true;
    }

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
