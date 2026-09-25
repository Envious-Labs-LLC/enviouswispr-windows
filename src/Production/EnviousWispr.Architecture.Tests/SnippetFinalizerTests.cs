using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The mandatory sentinel resolver, ported from macOS <c>SnippetFinalizerTests</c>.</summary>
/// <remarks>
/// WHEN THIS FAILS A DOCUMENT RECEIVES A RAW INTERNAL TOKEN, OR SAVED TEXT TWICE. The assertion every
/// case shares is the invariant the whole sentinel design rests on - nothing this returns contains a
/// sentinel - because it is the one failure that cannot be taken back once the text is pasted, kept in
/// History and written to the recovery copy.
/// </remarks>
public sealed class SnippetFinalizerTests
{
    private static readonly SnippetExpansionRecord Email = new("EWSNIPAAA", "sam@example.com");
    private static readonly SnippetExpansionRecord SignOff = new("EWSNIPBBB", "Thanks,\nSam");
    private static readonly SnippetExpansionRecord EmailSuppressing = new("EWSNIPCCC", "sam@example.com", SuppressFollowingSentenceEnding: true);

    private static SnippetResolution Resolve(string text, string? polished, params SnippetExpansionRecord[] records)
    {
        var resolution = SnippetFinalizer.Resolve(text, polished, records);
        Assert.DoesNotContain("EWSNIP", resolution.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("EWSNIP", resolution.PolishedText ?? string.Empty, StringComparison.Ordinal);
        return resolution;
    }

    [Fact]
    public void WithNoRecordsTheTextIsLeftExactlyAsItWas()
    {
        var resolution = SnippetFinalizer.Resolve("email me at home", "Email me at home.", []);

        Assert.Equal(new SnippetResolution("email me at home", "Email me at home.", RejectedPolish: false), resolution);
    }

    [Fact]
    public void EverySentinelIntactResolvesBothTheDeterministicAndThePolishedText()
    {
        Assert.Equal(
            new SnippetResolution("email me at sam@example.com", "Email me at sam@example.com.", RejectedPolish: false),
            Resolve("email me at EWSNIPAAA", "Email me at EWSNIPAAA.", Email));
    }

    [Fact]
    public void APeriodTheModelAddedAfterASuppressingSentinelDoesNotReachThePerson()
    {
        Assert.Equal(
            new SnippetResolution("sam@example.com", "sam@example.com", RejectedPolish: false),
            Resolve("EWSNIPCCC", "EWSNIPCCC.", EmailSuppressing));
    }

    [Fact]
    public void WithoutTheFlagTheSameAddedPeriodIsKept()
    {
        Assert.Equal("Email me at sam@example.com.", Resolve("email me at EWSNIPAAA", "Email me at EWSNIPAAA.", Email).PolishedText);
    }

    [Fact]
    public void ASavedTextThatEndsASentenceDoesNotGainASecondStopFromPolish()
    {
        var signOff = new SnippetExpansionRecord("EWSNIPDDD", "Let me know if that works.", SuppressFollowingSentenceEnding: true);

        Assert.Equal(
            "Tell them Let me know if that works. Then send it.",
            Resolve("tell them EWSNIPDDD then send it", "Tell them EWSNIPDDD. Then send it.", signOff).PolishedText);
    }

    [Fact]
    public void ACommaAfterASuppressingSentinelIsLeftAlone()
    {
        Assert.Equal("sam@example.com, and more.", Resolve("EWSNIPCCC and more", "EWSNIPCCC, and more.", EmailSuppressing).PolishedText);
    }

    [Fact]
    public void APeriodBehindAClosingBracketTheModelKeptIsStillDropped()
    {
        Assert.Equal("(sam@example.com)", Resolve("(EWSNIPCCC)", "(EWSNIPCCC).", EmailSuppressing).PolishedText);
    }

    [Fact]
    public void APeriodBehindAClosingQuoteTheModelKeptIsStillDropped()
    {
        Assert.Equal(
            "“sam@example.com”",
            Resolve("“EWSNIPCCC”", "“EWSNIPCCC”.", EmailSuppressing).PolishedText);
    }

    [Fact]
    public void TheClosingMarkItselfIsKeptWhenThePeriodBehindItIsDropped()
    {
        Assert.Equal("(sam@example.com) And more.", Resolve("(EWSNIPCCC)", "(EWSNIPCCC). And more.", EmailSuppressing).PolishedText);
    }

    [Fact]
    public void TheDeterministicTextIsUnchangedByTheTerminatorRule()
    {
        Assert.Equal(new SnippetResolution("sam@example.com", null, RejectedPolish: false), Resolve("EWSNIPCCC", null, EmailSuppressing));
    }

    [Fact]
    public void SeveralSentinelsAllIntactAreAllResolvedLineBreaksIncluded()
    {
        Assert.Equal(
            new SnippetResolution("sam@example.com and Thanks,\nSam", "sam@example.com and Thanks,\nSam.", RejectedPolish: false),
            Resolve("EWSNIPAAA and EWSNIPBBB", "EWSNIPAAA and EWSNIPBBB.", Email, SignOff));
    }

    /// <summary>A lost sentinel costs the WHOLE polish, never a repair at a guessed position.</summary>
    [Fact]
    public void ASentinelTheModelDroppedCostsTheWholePolishedVersion()
    {
        Assert.Equal(
            new SnippetResolution("email me at sam@example.com", null, RejectedPolish: true),
            Resolve("email me at EWSNIPAAA", "Email me at your address.", Email));
    }

    [Fact]
    public void ASentinelTheModelDuplicatedAlsoRejectsThePolish()
    {
        Assert.Equal(
            new SnippetResolution("sam@example.com", null, RejectedPolish: true),
            Resolve("EWSNIPAAA", "EWSNIPAAA and EWSNIPAAA", Email));
    }

    [Fact]
    public void OneLostSentinelRejectsThePolishEvenWhenItsSiblingsSurvived()
    {
        Assert.Equal(
            new SnippetResolution("sam@example.com and Thanks,\nSam", null, RejectedPolish: true),
            Resolve("EWSNIPAAA and EWSNIPBBB", "EWSNIPAAA and the sign off.", Email, SignOff));
    }

    /// <summary>Two placeholders swapped would paste each snippet where the other was said: refused like a drop.</summary>
    [Fact]
    public void TwoSentinelsSwappedByTheModelRejectThePolish()
    {
        Assert.Equal(
            new SnippetResolution("sam@example.com and Thanks,\nSam", null, RejectedPolish: true),
            Resolve("EWSNIPAAA and EWSNIPBBB", "EWSNIPBBB and EWSNIPAAA.", Email, SignOff));
    }

    [Fact]
    public void AMangledSentinelIsALostSentinel()
    {
        // A model that lower-cased or split the token did not carry it; ordinal matching says so.
        Assert.True(Resolve("email me at EWSNIPAAA", "Email me at ewsnipaaa.", Email).RejectedPolish);
        Assert.True(Resolve("email me at EWSNIPAAA", "Email me at EWSNIP AAA.", Email).RejectedPolish);
    }
}
