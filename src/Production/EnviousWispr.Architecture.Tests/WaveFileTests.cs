using System.Buffers.Binary;
using EnviousWispr.Core.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The archive exists so a bad transcript can be replayed. A file that will not open, or opens at
/// the wrong speed, is worse than no archive - it sends the reader to the wrong conclusion.
/// </summary>
public sealed class WaveFileTests
{
    private static byte[] Encode(int sampleCount = 100, int sampleRate = 16_000) =>
        WaveFile.EncodeMono(new float[sampleCount], sampleRate);

    [Fact]
    public void TheFileIsARiffWave()
    {
        var file = Encode();

        Assert.Equal("RIFF"u8.ToArray(), file[0..4]);
        Assert.Equal("WAVE"u8.ToArray(), file[8..12]);
        Assert.Equal("fmt "u8.ToArray(), file[12..16]);
        Assert.Equal("data"u8.ToArray(), file[36..40]);
    }

    /// <summary>
    /// A header claiming a rate the samples were not captured at plays back at the wrong speed, and
    /// sounds like a fault in the recogniser rather than in the file - the wrong conclusion for a
    /// tool whose entire job is telling you what the recogniser heard.
    /// </summary>
    [Theory]
    [InlineData(8_000)]
    [InlineData(16_000)]
    [InlineData(48_000)]
    public void TheHeaderCarriesTheRateItWasGiven(int sampleRate)
    {
        var file = Encode(sampleRate: sampleRate);

        Assert.Equal(sampleRate, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24)));
        Assert.Equal(sampleRate * 2, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(28)));
    }

    /// <summary>
    /// Both declared lengths must agree with the file's real size, or a reader either stops early
    /// or runs off the end - and which of those happens depends on the reader, so the same file
    /// behaves differently in different tools.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16_000)]
    public void TheDeclaredLengthsMatchTheRealFile(int sampleCount)
    {
        var file = Encode(sampleCount);

        Assert.Equal(44 + (sampleCount * 2), file.Length);
        Assert.Equal(file.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal(sampleCount * 2, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(40)));
    }

    /// <summary>
    /// Clamping must happen BEFORE scaling. A sample slightly over 1 scales past the maximum and
    /// wraps to a large negative, so the loudest moment of a recording becomes its quietest and the
    /// file crackles exactly where the user was speaking up.
    /// </summary>
    [Fact]
    public void ASampleAboveOneBecomesTheLoudestValueRatherThanWrapping()
    {
        var file = WaveFile.EncodeMono([2.0f, -2.0f], 16_000);

        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(44)));
        Assert.Equal(-short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(46)));
    }

    /// <summary>
    /// The control for the clamp test. An in-range sample must scale rather than being clamped, or
    /// an encoder that returned the maximum for everything would pass the test above.
    /// </summary>
    [Fact]
    public void AnOrdinarySampleIsScaledRatherThanClamped()
    {
        var file = WaveFile.EncodeMono([0.5f], 16_000);

        var written = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(44));
        Assert.InRange(written, (short)(short.MaxValue / 2 - 2), (short)(short.MaxValue / 2 + 2));
    }

    [Fact]
    public void SilenceEncodesAsZeroRatherThanAsAnOffset()
    {
        var file = Encode(sampleCount: 4);

        for (var offset = 44; offset < file.Length; offset += 2)
        {
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(offset)));
        }
    }

    [Fact]
    public void AnImpossibleSampleRateIsRefusedRatherThanWrittenIntoTheHeader()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveFile.EncodeMono([0f], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveFile.EncodeMono([0f], -1));
    }
}
