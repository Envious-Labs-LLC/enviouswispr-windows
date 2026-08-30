using System.Text.Json;
using EnviousWispr.Polish;

namespace EnviousWispr.Tests;

/// Parser contract ported from EGOneConnector.parseSuccess: every failure must
/// read as "no polish this time" (raw transcript used), never a throw.
/// Internal methods, reached via InternalsVisibleTo.
public class EgOnePolisherTests
{
    private static string Completion(string content, string finishReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = finishReason, message = new { role = "assistant", content } } },
        });

    [Fact]
    public void ParseSuccess_ValidCompletion_Returns_Content()
    {
        Assert.Equal("Clean text.", EgOnePolisher.ParseSuccess(Completion("Clean text.")));
    }

    [Fact]
    public void ParseSuccess_LengthFinishReason_Is_A_Truncation()
    {
        // Generation stopped at the max_tokens cap → partial rewrite → bypass.
        Assert.Null(EgOnePolisher.ParseSuccess(Completion("partial", "length")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<TRANSCRIPT></TRANSCRIPT>")]
    public void ParseSuccess_Empty_After_Cleaning_Bypasses(string content)
    {
        Assert.Null(EgOnePolisher.ParseSuccess(Completion(content)));
    }

    [Theory]
    [InlineData("not json {")]
    [InlineData("{\"choices\":[] }")]
    [InlineData("{\"other\":1}")]
    public void ParseSuccess_Malformed_Bypasses(string raw)
    {
        Assert.Null(EgOnePolisher.ParseSuccess(raw));
    }

    [Fact]
    public void ParseSuccess_Strips_Echoed_Tag_Sandwich()
    {
        Assert.Equal(
            "Clean text.",
            EgOnePolisher.ParseSuccess(Completion("<TRANSCRIPT>Clean text.</TRANSCRIPT>")));
        // Neutralized (ZWSP after '<') sandwich, as BuildUserMessage's escape hatch produces
        Assert.Equal(
            "Clean.",
            EgOnePolisher.ParseSuccess(Completion("<\u200CTRANSCRIPT>Clean.<\u200C/TRANSCRIPT>")));
    }

    [Fact]
    public void CleanPolishedText_Trims_And_Strips_Plain_Sandwich()
    {
        Assert.Equal("Clean.", EgOnePolisher.CleanPolishedText("  <TRANSCRIPT>Clean.</TRANSCRIPT>  "));
        Assert.Equal("Clean.", EgOnePolisher.CleanPolishedText("<transcript>Clean.</transcript>"));
    }

    [Fact]
    public void CleanPolishedText_Zwsp_Sandwich_Stripped()
    {
        Assert.Equal("Clean.", EgOnePolisher.CleanPolishedText("<\u200CTRANSCRIPT>Clean.<\u200C/TRANSCRIPT>"));
        Assert.Equal("Clean.", EgOnePolisher.CleanPolishedText("<\u200Ctranscript>Clean.<\u200C/transcript>"));
    }

    [Fact]
    public void CleanPolishedText_MidText_Tags_Are_Not_A_Sandwich()
    {
        var s = "before <TRANSCRIPT> middle";
        Assert.Equal(s, EgOnePolisher.CleanPolishedText(s));
    }
}
