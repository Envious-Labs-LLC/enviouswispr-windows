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
            PolishOutputGuard.Evaluate(
                RealTranscript,
                "I think we should ship the Windows build this week, and see what people say about it."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void NothingComingBackIsRefused(string output)
    {
        Assert.Equal(PolishOutputVerdict.RefusedEmpty, PolishOutputGuard.Evaluate(RealTranscript, output));
    }

    [Fact]
    public void AModelStuckOnOneWordIsRefused()
    {
        Assert.Equal(
            PolishOutputVerdict.RefusedRepetition,
            PolishOutputGuard.Evaluate(RealTranscript, "We should ship ship ship ship ship it."));
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
            PolishOutputGuard.Evaluate(
                RealTranscript,
                "in the end in the end in the end in the end in the end"));
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
        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Evaluate(RealTranscript, output));
    }

    [Fact]
    public void OutputWildlyLongerThanTheInputIsRefused()
    {
        var runaway = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"word{i}"));

        Assert.Equal(
            PolishOutputVerdict.RefusedRunaway,
            PolishOutputGuard.Evaluate(RealTranscript, runaway));
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
        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Evaluate(input, output));
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

        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Evaluate(input, justUnder));
        Assert.Equal(PolishOutputVerdict.RefusedRunaway, PolishOutputGuard.Evaluate(input, justOver));
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

        Assert.Equal(PolishOutputVerdict.Accepted, PolishOutputGuard.Evaluate(spoken, polished));
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
            PolishOutputGuard.Evaluate(RealTranscript, stuckAndLong));
    }
}
