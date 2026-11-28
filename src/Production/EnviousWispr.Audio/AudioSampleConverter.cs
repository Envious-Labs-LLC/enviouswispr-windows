using System.Buffers.Binary;
using System.Runtime.InteropServices;

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
        AudioBufferFormat format,
        int selectedChannelIndex = 0)
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

        if (BitConverter.IsLittleEndian &&
            format.Encoding == AudioSampleEncoding.Ieee754Single &&
            format.Channels == 1 &&
            format.SampleRate == TargetSampleRate)
        {
            return ConvertNativeMono16KhzFloat(MemoryMarshal.Cast<byte, float>(source));
        }

        var channelIndex = Math.Clamp(selectedChannelIndex, 0, format.Channels - 1);
        var mono = new float[frameCount];
        double sumSquares = 0;
        var peak = 0f;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sampleOffset = (frame * bytesPerFrame) + (channelIndex * bytesPerSample);
            var rawSample = Decode(source.Slice(sampleOffset, bytesPerSample), format.Encoding);
            var sample = Math.Clamp(rawSample, -1f, 1f);
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

    private static AudioConversionResult ConvertNativeMono16KhzFloat(ReadOnlySpan<float> source)
    {
        var samples = new float[source.Length];
        double sumSquares = 0;
        var peak = 0f;
        for (var index = 0; index < source.Length; index++)
        {
            var sample = Math.Clamp(source[index], -1f, 1f);
            samples[index] = sample;
            peak = Math.Max(peak, Math.Abs(sample));
            sumSquares += sample * sample;
        }

        return new AudioConversionResult(
            samples,
            peak,
            (float)Math.Sqrt(sumSquares / source.Length));
    }

    private static float[] Resample(float[] source, int sourceSampleRate) =>
        ResampleMonoTo16Khz(source, sourceSampleRate, source);

    internal static float[] ResampleMonoTo16Khz(ReadOnlySpan<float> source, int sourceSampleRate) =>
        ResampleMonoTo16Khz(source, sourceSampleRate, exactCopySource: null);

    private static float[] ResampleMonoTo16Khz(
        ReadOnlySpan<float> source,
        int sourceSampleRate,
        float[]? exactCopySource)
    {
        if (sourceSampleRate == TargetSampleRate || source.Length == 0)
        {
            return exactCopySource ?? source.ToArray();
        }

        var outputLength = Math.Max(
            1,
            (int)Math.Round(source.Length * (double)TargetSampleRate / sourceSampleRate));
        var output = new float[outputLength];
        var sourceStep = sourceSampleRate / (double)TargetSampleRate;
        var cutoffRatio = Math.Min(1.0, TargetSampleRate / (double)sourceSampleRate);
        const int SincZeroCrossings = 12;
        var radius = Math.Max(SincZeroCrossings, (int)Math.Ceiling(SincZeroCrossings / cutoffRatio));

        for (var index = 0; index < output.Length; index++)
        {
            var sourcePosition = index * sourceStep;
            var center = (int)Math.Floor(sourcePosition);
            var start = Math.Max(0, center - radius);
            var end = Math.Min(source.Length - 1, center + radius);
            double weightedSum = 0;
            double totalWeight = 0;

            for (var n = start; n <= end; n++)
            {
                var distance = n - sourcePosition;
                if (Math.Abs(distance) > radius)
                {
                    continue;
                }

                var hannWindow = 0.5 * (1.0 + Math.Cos(Math.PI * distance / radius));
                var sinc = Math.Abs(distance) < 1e-12
                    ? cutoffRatio
                    : Math.Sin(Math.PI * cutoffRatio * distance) / (Math.PI * distance);
                var weight = sinc * hannWindow;
                weightedSum += source[n] * weight;
                totalWeight += weight;
            }

            if (totalWeight > 1e-12)
            {
                output[index] = Math.Clamp((float)(weightedSum / totalWeight), -1f, 1f);
            }
            else
            {
                output[index] = source[Math.Clamp(center, 0, source.Length - 1)];
            }
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
