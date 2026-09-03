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
/// THIS GUARDS THE HEADROOM, NOT THE CLIFF. It runs a length comfortably inside the boundary, because
/// pinning one near the limit would make the suite fail on a busy runner, which is precisely the
/// flake that started #91. A regression that made the cost materially worse pushes even this length
/// over. The cliff itself is NOT fixed, is deliberately not asserted here, and #91 stays open for it.
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
}
