namespace EnviousWispr.Core.Diagnostics;

/// <summary>
/// What a set of timings looks like, in the shape a speed claim can be made from.
/// </summary>
/// <remarks>
/// THE MEDIAN AND THE WORST CASE ARE DIFFERENT PRODUCTS. A median tells you what dictation usually
/// feels like; the 95th percentile tells you how often it feels broken. Reporting only the mean
/// hides both - one slow run in twenty barely moves it, and one slow run in twenty is exactly what
/// a user remembers.
///
/// THE MEAN IS DELIBERATELY ABSENT. It is the number people reach for and the least useful one
/// here: it is dragged by the cold start that every run of this kind contains, and it describes a
/// dictation nobody had.
/// </remarks>
public sealed record LatencySummary(
    int Count,
    double MinMilliseconds,
    double MedianMilliseconds,
    double Percentile95Milliseconds,
    double MaxMilliseconds)
{
    /// <summary>
    /// Summarises a set of measured durations.
    /// </summary>
    /// <remarks>
    /// NEAREST-RANK PERCENTILE, and it is chosen rather than defaulted. Interpolating between two
    /// samples invents a duration that was never measured, which is the wrong thing to publish in a
    /// speed claim: every number here should be one that actually happened. Nearest-rank always
    /// returns a real observation.
    ///
    /// The consequence is stated rather than hidden: with fewer than 20 samples the 95th percentile
    /// IS the maximum, because there is no 20th value for it to be. That is honest - a claim about
    /// the worst 5% needs at least twenty runs to mean anything - and a caller comparing the two
    /// columns can see it rather than being given an interpolated number that looks like more
    /// evidence than exists.
    /// </remarks>
    public static LatencySummary From(IReadOnlyList<double> milliseconds)
    {
        ArgumentNullException.ThrowIfNull(milliseconds);
        if (milliseconds.Count == 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0);
        }

        var sorted = milliseconds.Order().ToArray();
        return new LatencySummary(
            sorted.Length,
            sorted[0],
            Median(sorted),
            NearestRank(sorted, 0.95),
            sorted[^1]);
    }

    /// <summary>
    /// True when the 95th percentile rests on too few samples to describe a tail.
    /// </summary>
    /// <remarks>
    /// Exposed rather than left for the reader to work out from Count, so a report can say so in
    /// words. A percentile silently equal to the maximum reads as a measurement and is an artefact
    /// of the sample size, which is the plausible-value trap in a number nobody would question.
    /// </remarks>
    public bool Percentile95IsJustTheMaximum => Count < 20;

    private static double Median(double[] sorted) =>
        sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            // An even count has no middle sample, so this is the one place a value between two
            // observations is the honest answer: the midpoint is what "half were faster" means when
            // no single run sits on the line.
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;

    private static double NearestRank(double[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
