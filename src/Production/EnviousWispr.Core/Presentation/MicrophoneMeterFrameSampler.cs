namespace EnviousWispr.Core.Presentation;

/// <summary>
/// Turns a level arriving per audio buffer into one level per meter frame, keeping the loudest.
/// </summary>
/// <remarks>
/// A RATE LIMIT ALONE THROWS AWAY THE ONLY LEVELS WORTH SHOWING. Capture reports a level about two
/// hundred times a second and a meter can honestly draw twenty, so something has to choose. Taking
/// the first level after each boundary and discarding the rest chooses at random with respect to
/// loudness, so a short loud packet - the attack of a consonant, which is exactly what somebody
/// looks at a meter to see - vanishes whenever it lands mid-frame. Keeping the loudest of the
/// window costs one comparison and cannot lose a peak.
///
/// AND THE RATE LIMIT ITSELF IS NOT OPTIONAL, WHICH IS WHY BOTH LIVE HERE. Posting every buffer to
/// the UI thread kept its queue permanently busy on a real machine: every callback ran, every
/// property assignment was accepted, and no frame was ever rendered, so both meters in the app read
/// as dead while the audio underneath them transcribed perfectly. The pacing and the choosing are
/// one decision and separating them is how one of them gets dropped later.
///
/// A TYPE RATHER THAN TWO LOCALS IN AN EVENT HANDLER, because the behaviour worth checking is a
/// sequence: what a burst between frames does, what the first level does, and that a level is never
/// counted into two frames. None of that is checkable inside a window that needs a display to exist.
/// </remarks>
public sealed class MicrophoneMeterFrameSampler
{
    private readonly TimeSpan _interval;
    private float _loudestSinceFrame;
    private TimeSpan? _lastFrameAt;

    /// <summary>Uses the meter's own cadence.</summary>
    public MicrophoneMeterFrameSampler()
        : this(RecordingLevelHistory.SampleInterval)
    {
    }

    /// <param name="interval">How long a frame lasts. A test drives this directly.</param>
    public MicrophoneMeterFrameSampler(TimeSpan interval) => _interval = interval;

    /// <summary>Offers one level, and says whether a frame is due.</summary>
    /// <param name="level">The level as capture measured it.</param>
    /// <param name="now">A monotonic reading. Only differences are used.</param>
    /// <param name="frameLevel">The loudest level seen since the previous frame.</param>
    /// <returns>True when a frame is due and <paramref name="frameLevel"/> should be drawn.</returns>
    public bool TryTakeFrame(float level, TimeSpan now, out float frameLevel)
    {
        // NOT-A-NUMBER IS SILENCE, and it is caught before the comparison rather than after. A
        // greater-than against NaN is false, so an unchecked NaN would neither raise the loudest nor
        // be rejected, and would then be carried into a frame the moment anything else did.
        if (float.IsFinite(level) && level > _loudestSinceFrame)
        {
            _loudestSinceFrame = level;
        }

        if (_lastFrameAt is { } last && now - last < _interval)
        {
            frameLevel = 0f;
            return false;
        }

        _lastFrameAt = now;
        frameLevel = _loudestSinceFrame;
        _loudestSinceFrame = 0f;
        return true;
    }
}
