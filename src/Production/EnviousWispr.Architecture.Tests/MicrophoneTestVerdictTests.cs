using EnviousWispr.Core.Audio;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The page where somebody confirms their microphone works could not tell them.
/// </summary>
/// <remarks>
/// IT NAMED A DEVICE AND STOPPED THERE, so an app receiving pure digital silence looked exactly like
/// one that was working. That is not hypothetical: it happened on the development machine, the
/// recording meter sat at its floor for seventy consecutive frames, nothing transcribed from clearly
/// audible speech, and finding it took a day of measuring. A person would have seen it in three
/// seconds if this page had answered.
/// </remarks>
public sealed class MicrophoneTestVerdictTests
{
    [Fact]
    public void ADeviceThatSentNothingIsNotADeviceThatIsQuiet()
    {
        var said = MicrophoneTestVerdict.For(packets: 0, silentPackets: 0, rootMeanSquare: 0f);

        Assert.Contains("nothing at all", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plugged in", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsSayingItIsSilentPointsAtTheMuteRatherThanTheCable()
    {
        // Every packet flagged is Windows reporting that there is nothing to hear, which is a muted
        // or unrouted device rather than a broken one.
        var said = MicrophoneTestVerdict.For(packets: 60, silentPackets: 60, rootMeanSquare: 0f);

        Assert.Contains("muted", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealPacketsOfZeroesAreTheirOwnFaultAndSaySo()
    {
        // THIS IS THE ONE THAT COST A DAY. The device is open, Windows is not claiming silence, and
        // what arrives is zeroes - which every other surface in the app reported as working.
        var said = MicrophoneTestVerdict.For(packets: 60, silentPackets: 0, rootMeanSquare: 0f);

        Assert.Contains("no signal", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quiet room", said.Replace("not a quiet room", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWorkingMicrophoneSaysSoWithoutHedging()
    {
        var said = MicrophoneTestVerdict.For(packets: 60, silentPackets: 0, rootMeanSquare: 0.4f);

        Assert.Contains("working", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SomethingVeryQuietIsNotReportedAsBroken()
    {
        // A microphone turned down is a different answer from a microphone that is not there, and
        // sending somebody to unplug a working device is the wrong help.
        var said = MicrophoneTestVerdict.For(packets: 60, silentPackets: 0, rootMeanSquare: 0.0005f);

        Assert.Contains("quietly", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unplug", said, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.0004f)]
    [InlineData(0.0001f)]
    [InlineData(float.Epsilon)]
    public void AnythingThatIsNotExactlyNothingIsSomething(float rootMeanSquare)
    {
        // A THRESHOLD HERE CONDEMNED WORKING HARDWARE. A quiet or low-gain microphone was told it
        // was sending nothing but zeroes while the meter beside it lit a bar, which sends somebody
        // to replace a device that works.
        var said = MicrophoneTestVerdict.For(packets: 60, silentPackets: 0, rootMeanSquare);

        Assert.DoesNotContain("no signal", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quietly", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryOutcomeGetsADifferentSentence()
    {
        // Four faults sharing one sentence is the same as having no sentence: the whole value here
        // is that the words point at where to look.
        var sentences = new[]
        {
            MicrophoneTestVerdict.For(0, 0, 0f),
            MicrophoneTestVerdict.For(60, 60, 0f),
            MicrophoneTestVerdict.For(60, 0, 0f),
            MicrophoneTestVerdict.For(60, 0, 0.0005f),
            MicrophoneTestVerdict.For(60, 0, 0.4f),
        };

        Assert.Equal(sentences.Length, sentences.Distinct(StringComparer.Ordinal).Count());
    }
}
