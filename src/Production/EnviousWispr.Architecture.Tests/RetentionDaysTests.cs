using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// What a day field SHOWS and what it KEEPS must be the same number.
/// </summary>
public sealed class RetentionDaysTests
{
    private static int History(double value) => RetentionDays.FromField(
        value,
        fallback: 30,
        RetentionDays.HistoryMinimum,
        RetentionDays.HistoryMaximum);

    /// <summary>
    /// The defect, measured on the running app: a user typed 12.7, the field displayed 13, and
    /// settings.json stored 12. The field rounds half up for display and the save path truncated.
    /// </summary>
    /// <remarks>
    /// The expected values are LITERALS chosen to match what the field displays. Deriving them
    /// with Math.Round here would compare the save path against itself and pass either way.
    /// </remarks>
    [Theory]
    [InlineData(12.7, 13)]
    [InlineData(3.5, 4)]
    [InlineData(12.2, 12)]
    [InlineData(0.5, 1)]
    [InlineData(45, 45)]
    public void AFractionIsKeptAsTheNumberTheFieldShows(double typed, int kept)
    {
        Assert.Equal(kept, History(typed));
    }

    /// <summary>
    /// The control for the theory above, and it is the whole point of this file: truncation and
    /// rounding AGREE on every whole number, so a suite built only from whole inputs passes
    /// against the defect. 12.7 is the smallest thing that tells them apart.
    /// </summary>
    [Fact]
    public void TruncationAndRoundingDisagreeOnTheValuesThisGuards()
    {
        Assert.Equal(12, (int)12.7);
        Assert.Equal(13, History(12.7));
    }

    [Fact]
    public void AnEmptyFieldMeansTheFallbackRatherThanZero()
    {
        Assert.Equal(30, History(double.NaN));
    }

    [Theory]
    [InlineData(99999, RetentionDays.HistoryMaximum)]
    [InlineData(-5, RetentionDays.HistoryMinimum)]
    [InlineData(-0.5, RetentionDays.HistoryMinimum)]
    public void EveryValueIsBroughtInsideItsBounds(double typed, int kept)
    {
        Assert.Equal(kept, History(typed));
    }

    /// <summary>
    /// The diagnostics field has its own bounds and they are not the history field's. A single
    /// pair of constants used for both would pass every test above and store a zero-day
    /// diagnostics retention, which its own validator then rejects.
    /// </summary>
    [Fact]
    public void TheDiagnosticsFieldKeepsItsOwnFloorOfOneDay()
    {
        var kept = RetentionDays.FromField(
            0,
            fallback: 14,
            RetentionDays.DiagnosticMinimum,
            RetentionDays.DiagnosticMaximum);

        Assert.Equal(1, kept);
        Assert.NotEqual(RetentionDays.HistoryMinimum, RetentionDays.DiagnosticMinimum);
    }

    /// <summary>
    /// Anything this converter can produce must survive the validator, or a user can save a value
    /// the app then refuses to load.
    /// </summary>
    [Theory]
    [InlineData(-5)]
    [InlineData(0.4)]
    [InlineData(99999)]
    [InlineData(12.7)]
    public void EveryValueTheConverterProducesPassesTheValidator(double typed)
    {
        var history = History(typed);
        var diagnostics = RetentionDays.FromField(
            typed,
            fallback: 14,
            RetentionDays.DiagnosticMinimum,
            RetentionDays.DiagnosticMaximum);

        Assert.InRange(history, RetentionDays.HistoryMinimum, RetentionDays.HistoryMaximum);
        Assert.InRange(diagnostics, RetentionDays.DiagnosticMinimum, RetentionDays.DiagnosticMaximum);
    }
}
