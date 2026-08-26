using System.Buffers.Binary;

namespace EnviousWispr.Audio;

public enum AudioSampleEncoding
{
    Ieee754Single,
    SignedPcm16,
    SignedPcm24,
    SignedPcm32,
}

public readonly record struct AudioBufferFormat(
    int SampleRate,
    int Channels,
    AudioSampleEncoding Encoding);

public sealed record AudioConversionResult(float[] Samples, float Peak, float RootMeanSquare);

public static class AudioSampleConverter
{
    public const int TargetSampleRate = 16_000;

    public static AudioConversionResult ConvertToMono16Khz(
        ReadOnlySpan<byte> source,
        AudioBufferFormat format)
    {
        Validate(format);
        var bytesPerSample = BytesPerSample(format.Encoding);
        var bytesPerFrame = checked(bytesPerSample * format.Channels);
        if (source.Length % bytesPerFrame != 0)
        {
            throw new ArgumentException("Audio data must contain complete frames.", nameof(source));
        }

        var frameCount = source.Length / bytesPerFrame;
        if (frameCount == 0)
        {
            return new AudioConversionResult([], Peak: 0, RootMeanSquare: 0);
        }

        var mono = new float[frameCount];
        double sumSquares = 0;
        var peak = 0f;
        for (var frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            var frameOffset = frame * bytesPerFrame;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleOffset = frameOffset + (channel * bytesPerSample);
                sum += Decode(source.Slice(sampleOffset, bytesPerSample), format.Encoding);
            }

            var sample = Math.Clamp((float)(sum / format.Channels), -1f, 1f);
            mono[frame] = sample;
            peak = Math.Max(peak, Math.Abs(sample));
            sumSquares += sample * sample;
        }

        var rms = (float)Math.Sqrt(sumSquares / frameCount);
        return new AudioConversionResult(
            Resample(mono, format.SampleRate),
            peak,
            rms);
    }

    private static float[] Resample(float[] source, int sourceSampleRate)
    {
        if (sourceSampleRate == TargetSampleRate || source.Length == 0)
        {
            return source;
        }

        var outputLength = Math.Max(
            1,
            (int)Math.Round(source.Length * (double)TargetSampleRate / sourceSampleRate));
        var output = new float[outputLength];
        var sourceStep = sourceSampleRate / (double)TargetSampleRate;

        for (var index = 0; index < output.Length; index++)
        {
            var sourcePosition = index * sourceStep;
            var lower = Math.Min((int)sourcePosition, source.Length - 1);
            var upper = Math.Min(lower + 1, source.Length - 1);
            var fraction = sourcePosition - lower;
            output[index] = (float)(source[lower] + ((source[upper] - source[lower]) * fraction));
        }

        return output;
    }

    private static float Decode(ReadOnlySpan<byte> bytes, AudioSampleEncoding encoding) => encoding switch
    {
        AudioSampleEncoding.Ieee754Single => Math.Clamp(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            -1f,
            1f),
        AudioSampleEncoding.SignedPcm16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        AudioSampleEncoding.SignedPcm24 => DecodePcm24(bytes) / 8_388_608f,
        AudioSampleEncoding.SignedPcm32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2_147_483_648f,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    private static int DecodePcm24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }

    private static int BytesPerSample(AudioSampleEncoding encoding) => encoding switch
    {
        AudioSampleEncoding.Ieee754Single or AudioSampleEncoding.SignedPcm32 => 4,
        AudioSampleEncoding.SignedPcm24 => 3,
        AudioSampleEncoding.SignedPcm16 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    private static void Validate(AudioBufferFormat format)
    {
        if (format.SampleRate is < 8_000 or > 384_000)
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Sample rate is outside the supported range.");
        }

        if (format.Channels is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(format), "Channel count is outside the supported range.");
        }

        _ = BytesPerSample(format.Encoding);
    }
}
