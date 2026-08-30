using EnviousWispr.Core.Reliability;

namespace EnviousWispr.Architecture.Tests;

/// <summary>What Home says about the previous run, over the whole matrix.</summary>
/// <remarks>
/// THE WHOLE MATRIX, BECAUSE HALF OF IT WAS WRONG IN EACH DIRECTION AT DIFFERENT TIMES. Home once
/// raised "EnviousWispr did not close properly last time" plus a running count on every run that
/// left no clean-exit flag, which accused the product on a number nothing here can justify. The
/// first attempt at a fix deleted the banner outright, which removed the ONE case that genuinely
/// needed it. Only a matrix catches both, because each mistake looks correct from one row.
/// </remarks>
public sealed class StartupNoticeDecisionTests
{
    [Theory]
    // Text came back. That outranks everything, including a run that ended mid-dictation.
    [InlineData(true, true, RecoveryTextLoadStatus.Found, StartupNotice.RecoveredText)]
    [InlineData(false, false, RecoveryTextLoadStatus.Found, StartupNotice.RecoveredText)]
    // A recovery file that cannot be read is specific and actionable, so it keeps its own words.
    [InlineData(true, true, RecoveryTextLoadStatus.Invalid, StartupNotice.RecoveryInvalid)]
    [InlineData(false, false, RecoveryTextLoadStatus.Invalid, StartupNotice.RecoveryInvalid)]
    [InlineData(true, true, RecoveryTextLoadStatus.Unavailable, StartupNotice.RecoveryUnavailable)]
    [InlineData(false, false, RecoveryTextLoadStatus.Unavailable, StartupNotice.RecoveryUnavailable)]
    // THE ROW THE FIRST FIX DELETED. Stopped mid-dictation with nothing saved: the words are gone
    // and only the person who said them can say them again.
    [InlineData(true, true, RecoveryTextLoadStatus.Missing, StartupNotice.DictationMayBeLost)]
    // THE ROWS THE ORIGINAL BANNER GOT WRONG. An interrupted run with no dictation in flight is a
    // closed laptop, a Restart, a log off or Task Manager, and costs the reader nothing.
    [InlineData(true, false, RecoveryTextLoadStatus.Missing, StartupNotice.None)]
    [InlineData(false, false, RecoveryTextLoadStatus.Missing, StartupNotice.None)]
    // A clean previous run cannot produce a warning even if the flag were somehow left set.
    [InlineData(false, true, RecoveryTextLoadStatus.Missing, StartupNotice.None)]
    public void TheNoticeMatchesWhatTheAppActuallyKnows(
        bool previousRunInterrupted,
        bool previousRunWasDictating,
        RecoveryTextLoadStatus recovery,
        StartupNotice expected) =>
        Assert.Equal(
            expected,
            StartupNoticeDecision.For(previousRunInterrupted, previousRunWasDictating, recovery));

    /// <summary>No count reaches this decision, because none is offered to it.</summary>
    /// <remarks>
    /// STRUCTURAL RATHER THAN A RULE SOMEBODY HAS TO REMEMBER. The count used to travel on
    /// ApplicationRunStartResult and was spent on the banner's second sentence. Removing it from the
    /// public result is what makes putting it back on screen a compile error instead of a review
    /// finding, so this asserts the shape of the type rather than the wording of any string.
    /// </remarks>
    [Fact]
    public void TheRunStartResultCarriesNoInterruptedRunCount()
    {
        var carried = typeof(ApplicationRunStartResult)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(int))
            .Select(property => property.Name)
            .ToArray();

        Assert.True(
            carried.Length == 0,
            "ApplicationRunStartResult carries a number again: " + string.Join(", ", carried));
    }
}
