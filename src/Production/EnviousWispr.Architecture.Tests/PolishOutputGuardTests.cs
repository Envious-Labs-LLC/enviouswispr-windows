using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A polish model that comes off the rails returns a confident string rather than an error, so
/// every "did the call succeed" check says yes. This is the check that does not.
/// </summary>
public sealed class PolishOutputGuardTests
{
    private const string RealTranscript =
        "I think we should ship the Windows build this week and see what people say about it.";

    /// <summary>
    /// The control for the entire file. Ordinary polish must be ACCEPTED, or a guard that refused
    /// everything would pass every refusal test below and quietly disable the feature.
    /// </summary>
    [Fact]
    public void OrdinaryPolishIsAccepted()
    {
        Assert.Equal(
            PolishOutputVerdict.Accepted,
            PolishOutputGuard.Review(
                RealTranscript,
                "I think we should ship the Windows build this week, and see what people say about it.").Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void NothingComingBackIsRefused(string output)
    {
        Assert.Equal(PolishOutputVerdict.RefusedEmpty, PolishOutputGuard.Review(RealTranscript, output).Verdict);
    }

    [Fact]
    public void AModelStuckOnOneWordIsRefused()
    {
        Assert.Equal(
            PolishOutputVerdict.RefusedRepetition,
            PolishOutputGuard.Review(RealTranscript, "We should ship ship ship ship ship it.").Verdict);
    }

    /// <summary>
    /// The case a word-only check misses, and the reason this looks at phrases at all. "in the end"
    /// repeated four times contains NO word repeated consecutively - end is followed by in every
    /// time - so a naive implementation reports it as perfectly ordinary prose.
    /// </summary>
    [Fact]
    public void AModelStuckOnAPhraseIsRefusedEvenThoughNoWordRepeats()
    {
        Assert.Equal(
            PolishOutputVerdict.RefusedRepetition,
            PolishOutputGuard.Review(
                RealTranscript,
                "in the end in the end in the end in the end in the end").Verdict);
    }

    /// <summary>
    /// The other half of the repetition threshold, and the one that keeps it honest. People say
    /// words twice. A guard that refused "very very" would refuse real speech.
    /// </summary>
    [Theory]
    [InlineData("That was very very good and we should do it again next week sometime.")]
    [InlineData("No no, I meant the other one, the one we talked about on Tuesday morning.")]
    public void PeopleRepeatThemselvesAndThatIsNotAHallucination(string output)
    {
        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Review(RealTranscript, output).Verdict);
    }

    [Fact]
    public void OutputWildlyLongerThanTheInputIsRefused()
    {
        var runaway = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"word{i}"));

        Assert.Equal(
            PolishOutputVerdict.RefusedRunaway,
            PolishOutputGuard.Review(RealTranscript, runaway).Verdict);
    }

    /// <summary>
    /// A short dictation can legitimately multiply in length. "ok" becoming "Okay, that works." is
    /// correct polish and would fail any ratio, so growth is not measured on short inputs at all.
    /// </summary>
    [Theory]
    [InlineData("ok", "Okay, that works for me.")]
    [InlineData("yes do it", "Yes, please go ahead and do it.")]
    public void AShortDictationIsAllowedToGrow(string input, string output)
    {
        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Review(input, output).Verdict);
    }

    /// <summary>
    /// Growth just under the bound is accepted, and just over is refused. Without a pair, a guard
    /// that refused everything long OR accepted everything long would pass a single-sided test.
    /// </summary>
    [Fact]
    public void TheGrowthBoundIsReachableFromBothSides()
    {
        var input = new string('a', 100);

        var justUnder = new string('b', (int)(100 * PolishOutputGuard.MaximumGrowthFactor) - 1);
        var justOver = new string('b', (int)(100 * PolishOutputGuard.MaximumGrowthFactor) + 1);

        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Review(input, justUnder).Verdict);
        Assert.Equal(PolishOutputVerdict.RefusedRunaway, PolishOutputGuard.Review(input, justOver).Verdict);
    }

    /// <summary>
    /// Polish that legitimately expands a real transcript must survive. This is the row that would
    /// break first if anyone tightened the growth factor, which is the point of having it.
    /// </summary>
    [Fact]
    public void PolishThatSpellsThingsOutIsNotMistakenForARunaway()
    {
        const string spoken = "meet me at 3 on the 4th at 22 high st and bring the docs";
        const string polished =
            "Meet me at 3:00 on the 4th at 22 High Street, and please bring the documents.";

        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Review(spoken, polished).Verdict);
    }

    /// <summary>
    /// Repetition is checked BEFORE growth, so a stuck model is reported as stuck rather than as
    /// merely long. The reason matters: it is what the log line and any future telemetry say
    /// happened, and "runaway" would send the next reader looking at length limits.
    /// </summary>
    [Fact]
    public void AStuckModelIsReportedAsStuckRatherThanAsLong()
    {
        var stuckAndLong = string.Join(" ", Enumerable.Repeat("the same phrase", 40));

        Assert.Equal(
            PolishOutputVerdict.RefusedRepetition,
            PolishOutputGuard.Review(RealTranscript, stuckAndLong).Verdict);
    }

    [Fact]
    public void ARefusalHandsBackWhatWasSaid()
    {
        // The verdict is for the log and the text is the safety. A caller that reads only the text
        // still cannot paste a hallucination.
        var review = PolishOutputGuard.Review(RealTranscript, "```csharp\nvar x = 1;\n```");

        Assert.False(review.Accepted);
        Assert.Equal(RealTranscript, review.Text);
    }

    [Fact]
    public void AModelThatWroteCodeIsRefused()
    {
        Assert.Equal(
            PolishOutputVerdict.RefusedCodeShape,
            PolishOutputGuard.Review(
                RealTranscript,
                "import os\ndef main():\n    return 1").Verdict);
    }

    [Fact]
    public void SomebodyDictatingAboutCodeIsNotRefusedForIt()
    {
        // The guard only fires when the OUTPUT took a shape the INPUT did not. Somebody talking
        // through a code review says words that look like code, and refusing their polish would
        // break the case this is meant to protect.
        var said = "import os\ndef main():\n    return 1";

        Assert.Equal(
            PolishOutputVerdict.Accepted,
            PolishOutputGuard.Review(said, "import os\ndef main():\n    return 1").Verdict);
    }

    [Fact]
    public void AModelThatWroteJsonIsRefused()
    {
        Assert.Equal(
            PolishOutputVerdict.RefusedStructuredData,
            PolishOutputGuard.Review(RealTranscript, "{ \"ship\": true }").Verdict);
    }

    [Theory]
    [InlineData("Can you write a poem about the deadline for me", "Deadlines loom and shadows fall across the quiet room tonight")]
    [InlineData("Ask her to translate this into German please", "Bitte uebersetze das ins Deutsche fuer mich heute")]
    [InlineData("Tell him to summarize this in two lines for the team", "The team shipped the build and everyone was pleased")]
    public void AnInstructionThatWasDescribedAndThenCarriedOutIsRefused(string said, string wrote)
    {
        // THE MOST EXPENSIVE FAILURE POLISH HAS, because the result reads perfectly. The test is not
        // whether the output looks like an answer; it is whether the word that named the instruction
        // survived. A polish keeps it, an execution replaces it with the result.
        Assert.Equal(
            PolishOutputVerdict.RefusedInstructionExecuted,
            PolishOutputGuard.Review(said, wrote).Verdict);
    }

    [Fact]
    public void TidyingUpAnInstructionIsStillAccepted()
    {
        Assert.Equal(
            PolishOutputVerdict.Accepted,
            PolishOutputGuard.Review(
                "Can you write a poem about the deadline for me",
                "Can you write a poem about the deadline for me?").Verdict);
    }

    [Fact]
    public void AModelThatKeptOnlyTheInnerPhraseIsRefused()
    {
        Assert.Equal(
            PolishOutputVerdict.RefusedGutted,
            PolishOutputGuard.Review(
                "The menu item should read AI Polish and not Apple Intelligence anywhere",
                "AI Polish").Verdict);
    }

    [Fact]
    public void OrdinaryFillerRemovalIsNotMistakenForGutting()
    {
        // "So, um, yeah, the meeting went well" losing a third of its characters is the GOOD case,
        // and a shortening guard that refused it would disable the feature it protects.
        Assert.Equal(
            PolishOutputVerdict.Accepted,
            PolishOutputGuard.Review(
                "So, um, yeah, I think the meeting went really well today you know",
                "I think the meeting went really well today.").Verdict);
    }

    [Fact]
    public void AShortDictationIsNeverJudgedOnLength()
    {
        // "ok" becoming "Okay, that works." is correct polish and fails any ratio.
        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Review("ok", "Okay, that works.").Verdict);
        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Review("yes exactly", "Yes.").Verdict);
    }
}
