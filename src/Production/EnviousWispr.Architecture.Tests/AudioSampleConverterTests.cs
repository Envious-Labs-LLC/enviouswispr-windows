using System.Buffers.Binary;
using EnviousWispr.Audio;

namespace EnviousWispr.Architecture.Tests;

public sealed class AudioSampleConverterTests
{
    [Fact]
    public void EmptyInputProducesCanonicalEmptyAudio()
    {
        var result = AudioSampleConverter.ConvertToMono16Khz(
            ReadOnlySpan<byte>.Empty,
            new AudioBufferFormat(48_000, 2, AudioSampleEncoding.Ieee754Single));

        Assert.Empty(result.Samples);
        Assert.Equal(0, result.Peak);
        Assert.Equal(0, result.RootMeanSquare);
    }

    [Fact]
    public void StereoFloatIsMixedClippedAndDownsampled()
    {
        var bytes = new byte[480 * 2 * sizeof(float)];
        for (var frame = 0; frame < 480; frame++)
        {
            WriteSingle(bytes.AsSpan((frame * 8), 4), 2f);
            WriteSingle(bytes.AsSpan((frame * 8) + 4, 4), 0.5f);
        }

        var result = AudioSampleConverter.ConvertToMono16Khz(
            bytes,
            new AudioBufferFormat(48_000, 2, AudioSampleEncoding.Ieee754Single));

        Assert.Equal(160, result.Samples.Length);
        Assert.All(result.Samples, sample => Assert.Equal(0.75f, sample, precision: 5));
        Assert.Equal(0.75f, result.Peak, precision: 5);
        Assert.Equal(0.75f, result.RootMeanSquare, precision: 5);
    }

    [Fact]
    public void Pcm16IsUpsampledAndNormalized()
    {
        var bytes = new byte[80 * sizeof(short)];
        for (var sample = 0; sample < 80; sample++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(sample * 2, 2), 16_384);
        }

        var result = AudioSampleConverter.ConvertToMono16Khz(
            bytes,
            new AudioBufferFormat(8_000, 1, AudioSampleEncoding.SignedPcm16));

        Assert.Equal(160, result.Samples.Length);
        Assert.All(result.Samples, sample => Assert.Equal(0.5f, sample, precision: 5));
    }

    [Theory]
    [InlineData(AudioSampleEncoding.SignedPcm24, 3)]
    [InlineData(AudioSampleEncoding.SignedPcm32, 4)]
    public void SignedPcmMinimumMapsToNegativeOne(AudioSampleEncoding encoding, int bytesPerSample)
    {
        var bytes = new byte[bytesPerSample];
        bytes[^1] = 0x80;

        var result = AudioSampleConverter.ConvertToMono16Khz(
            bytes,
            new AudioBufferFormat(16_000, 1, encoding));

        Assert.Equal(-1f, Assert.Single(result.Samples));
        Assert.Equal(1f, result.Peak);
        Assert.Equal(1f, result.RootMeanSquare);
    }

    [Fact]
    public void PartialFrameIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            AudioSampleConverter.ConvertToMono16Khz(
                new byte[3],
                new AudioBufferFormat(16_000, 1, AudioSampleEncoding.Ieee754Single)));
    }

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));
}
