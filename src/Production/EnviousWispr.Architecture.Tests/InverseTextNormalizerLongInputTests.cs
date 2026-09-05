using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Inverse text normalisation survives a long dictation.</summary>
/// <remarks>
/// THE BOUNDARY IS REAL, REACHABLE, AND MEASURED RATHER THAN INFERRED FROM ONE CI FLAKE. The patterns
/// that recognise spoken numbers are repeated alternations, and they cost quadratic time to FAIL -
/// which is the common case, because most text is not a number. Measured on the development machine
/// against text of the form "one two three ... nine" repeated, warm, three runs each:
///
///     400 words    540, 553, 543 ms
///     800 words    1581, 1533, 1617 ms
///     1600 words   RegexMatchTimeoutException, every time
///
/// **AND THE EXPENSIVE INPUT IS NOT CHANGED AT ALL.** The passage comes back exactly as it went in:
/// the whole cost is patterns scanning and failing. That is the shape of the defect and the reason
/// the assertion below is an equality rather than a transformation.
///
/// Production degrades rather than loses - the deterministic stage catches the timeout and returns the
/// previous text marked degraded - so a user dictating sixteen hundred spoken numbers keeps their
/// words and quietly stops getting any of them formatted.
///
/// THE CLIFF IS GONE, AND THE NUMBERS BELOW SAY WHERE IT WENT. Every pattern is built once and on the
/// non-backtracking engine wherever that engine allows it, which makes a scan linear in the input;
/// the four patterns that opened with a number run behind a lookbehind - the two money forms and the
/// two clock forms - were rewritten to consume the guarded character instead, because a lookbehind
/// keeps a pattern on the backtracking engine. Measured warm on the development machine after that:
///
///     400 words     ~1 ms
///     800 words     ~2 ms
///     1600 words    ~4 ms      (used to throw, every time)
///     6400 words    ~16 ms
///
/// The second test pins a length four times the old cliff. It is still well inside the per-pattern
/// guard on a linear cost, so a busy runner cannot flake it; only a return of the quadratic term
/// could push it over, which is what it is for.
/// </remarks>
public sealed class InverseTextNormalizerLongInputTests
{
    [Fact]
    public void ALongPassageOfSpokenNumbersComesBackIntactInsteadOfTimingOut()
    {
        var text = string.Join(' ', Enumerable.Repeat(
            "one two three four five six seven eight nine",
            30));

        Assert.Equal(text, InverseTextNormalizer.Normalize(text));
    }

    [Fact]
    public void FourTimesTheOldCliffComesBackIntactBecauseTheScanIsLinear()
    {
        var text = string.Join(' ', Enumerable.Repeat(
            "one two three four five six seven eight nine",
            6400 / 9));

        Assert.Equal(text, InverseTextNormalizer.Normalize(text));
    }

    [Theory]
    [InlineData("I paid twenty five dollars and ten cents", "I paid $25.10")]
    [InlineData("it cost five cents", "it cost $0.05")]
    [InlineData("meet at seven thirty p m", "meet at 7:30 PM")]
    [InlineData("meet at seven o'clock", "meet at 7:00")]
    [InlineData("3.5 dollars", "3.5 dollars")]
    public void TheGuardedCharacterIsPutBackWhereALookbehindUsedToLeaveIt(string spoken, string expected)
    {
        // THE FOUR REWRITTEN PATTERNS. The character that must not precede a money or clock form is now
        // consumed by the match rather than looked behind at, so each of these proves it is returned
        // to the text, and the last one proves the guard still refuses a digit or a dot before the run.
        Assert.Equal(expected, InverseTextNormalizer.Normalize(spoken));
    }
}
