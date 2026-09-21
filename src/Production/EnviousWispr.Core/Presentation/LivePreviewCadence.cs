namespace EnviousWispr.Core.Presentation;

/// <summary>How often Live Preview may put words on screen, and when the next pass is due.</summary>
/// <remarks>
/// THE INTERVAL IS A FLOOR ON THE GAP BETWEEN UPDATES, NOT A WAIT BEFORE EACH PASS. A pass starts as
/// soon as there is enough audio, and the next is due an interval after the last update reached the
/// screen - so the period is the larger of the interval and a pass's cost, not their sum. Waiting the
/// interval first made the period interval + cost (2500 + 2374 ms on a measured 7.9-second take: one
/// update at 5.4 s, the next due at 10.3 s, after the recording had ended) and put the first words up
/// no sooner than that, however fast the engine was.
///
/// A FLOOR RATHER THAN NO LIMIT, because a pass is not free: preview is display-only, never changes
/// the final transcript, and must not spend the machine the final transcript is waiting on.
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
