using EnviousWispr.Polish;

namespace EnviousWispr.Tests;

/// The Mac's activation probe: GREEN must require the FULL transformation
/// (filler gone, self-correction resolved) — an HTTP 200 with the raw echo is
/// not a working polish leg.
public class EgOneProbeTests
{
    [Theory]
    [InlineData("Move the meeting to Friday.")]
    [InlineData("So move the meeting to Friday.")] // v3-en's A/B output — a prefix must not flip the verdict
    public void Green_On_Full_Transform(string polished)
    {
        var (green, _) = EgOneProbe.Evaluate(polished);
        Assert.True(green);
    }

    [Theory]
    [InlineData("")]
    [InlineData("so um move the meeting to thursday no wait friday")] // raw echo
    [InlineData("Move the meeting to Thursday.")]
    [InlineData("Move the meeting to Friday, no wait, Thursday.")]
    public void NotGreen_When_Transformation_Missing(string polished)
    {
        var (green, _) = EgOneProbe.Evaluate(polished);
        Assert.False(green);
    }

    [Fact]
    public void Null_Renders_Skipped_Marker()
    {
        var (green, output) = EgOneProbe.Evaluate(null);
        Assert.False(green);
        Assert.Equal("<skipped>", output);
    }

    [Fact]
    public void Um_Matches_Whole_Words_Only()
    {
        var (green, _) = EgOneProbe.Evaluate("Summit moved to Friday.");
        Assert.True(green);
    }

    [Fact]
    public void ProbeTranscript_Is_The_Mac_Constant()
    {
        Assert.Equal("so um move the meeting to thursday no wait friday", EgOneProbe.ProbeTranscript);
    }
}
