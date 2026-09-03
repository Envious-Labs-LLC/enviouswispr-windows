namespace EnviousWispr.Core.Presentation;

/// <summary>How often Live Preview may put words on screen, and when the next pass is due.</summary>
/// <remarks>
/// THE CADENCE WAS AN ADDITION AND IT NEEDED TO BE A FLOOR. The loop waited the full interval and
/// only then started work, so the real period was the interval PLUS the cost of a pass rather than
/// the larger of the two. Measured on a 7.9-second dictation: the interval is 2500 ms, a pass cost
/// 2374 ms, and the first update reached the screen at 5421 ms against a prediction of 5419 - two
/// milliseconds apart, so the loop was behaving exactly as written and there was nothing
/// intermittent to chase. The second update was due at 10295 ms. The recording ended at 7873 ms.
/// It was not slow; it was impossible.
///
/// THE ARITHMETIC IS THE WHOLE DEFECT. Updates a take can produce is (duration - startup) / (interval
/// + cost), so eight seconds bought one, ten seconds bought one, and fifteen bought two - for a
/// feature whose entire promise is keeping up with a speaker.
///
/// WAITING AFTER THE WORK RATHER THAN BEFORE IT ALSO ANSWERS THE FIRST UPDATE. Nothing could reach
/// the screen before interval plus cost however fast the engine became, so a person watched
/// "Listening..." for four seconds and read it as broken. A pass that starts as soon as there is
/// enough audio to transcribe puts the first words up in about the cost alone.
///
/// A FLOOR RATHER THAN NO LIMIT AT ALL, because the pass is not free and this is a limb. Live
/// preview is display-only and can never change the final transcript; it must not be allowed to
/// spend the machine that the final transcript is waiting on.
/// </remarks>
public static class LivePreviewCadence
{
    /// <summary>The shortest gap between two updates reaching the screen.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(2_500);

    /// <summary>How long to wait after a pass that took <paramref name="passCost"/>.</summary>
    /// <remarks>
    /// NEVER NEGATIVE, AND THAT IS NOT DEFENSIVENESS. A pass slower than the interval is the normal
    /// case on a processor rather than a graphics card - 2374 ms against 2500 ms was measured on the
    /// machine this shipped from, and a slower machine exceeds it outright. Subtracting without a
    /// floor would hand `Task.Delay` a negative span and throw inside the loop, turning a slow engine
    /// into a preview that stops entirely.
    /// </remarks>
    public static TimeSpan DelayAfter(TimeSpan passCost) =>
        passCost >= Interval ? TimeSpan.Zero : Interval - passCost;
}
