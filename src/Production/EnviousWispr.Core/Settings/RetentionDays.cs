namespace EnviousWispr.Core.Settings;

/// <summary>
/// A retention setting is a WHOLE number of days, and this is the only place that decides both
/// what the bounds are and how a typed value becomes one.
/// </summary>
/// <remarks>
/// WHY THE CONVERSION LIVES HERE RATHER THAN AT THE CALL SITE. The day fields display a value
/// rounded half up, and the save path used an <c>(int)</c> cast, which TRUNCATES. The two
/// disagreed by a whole day: measured on the running app, a user typed 12.7, the field showed 13,
/// and settings.json stored 12. A field that shows one number and keeps another is worse than one
/// that shows the fraction it is about to discard, because at least that one was self-consistent.
///
/// Rounding away from zero matches the field's own RoundHalfUp for every value a person can
/// commit. The two differ only for a negative half, which cannot survive either bound below.
///
/// The BOUNDS live here for the same reason: they were written out three times, at both call
/// sites and again in the validator's patterns, and nothing connected them.
/// </remarks>
public static class RetentionDays
{
    public const int HistoryMinimum = 0;

    public const int HistoryMaximum = 3_650;

    public const int DiagnosticMinimum = 1;

    public const int DiagnosticMaximum = 90;

    /// <summary>
    /// The whole number of days a day field is asking for, within its bounds.
    /// </summary>
    /// <param name="value">The field's value, which is NaN when the field is empty.</param>
    /// <param name="fallback">What an empty field means.</param>
    public static int FromField(double value, int fallback, int minimum, int maximum) =>
        (int)Math.Clamp(
            Math.Round(double.IsNaN(value) ? fallback : value, MidpointRounding.AwayFromZero),
            minimum,
            maximum);
}
