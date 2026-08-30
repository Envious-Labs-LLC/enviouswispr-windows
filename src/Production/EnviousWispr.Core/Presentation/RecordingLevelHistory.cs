namespace EnviousWispr.Core.Presentation;

/// <summary>
/// The last second of microphone levels, one per bar of the recording pill's meter.
/// </summary>
/// <remarks>
/// A HISTORY, NOT A MIRROR, AND THAT IS THE WHOLE DIFFERENCE. The meter this replaces drove every
/// bar from the CURRENT level through a sine wave, so all twenty-four carried the same one number
/// dressed up differently and the shape of what somebody had just said was nowhere on screen.
///
/// A TYPE RATHER THAN THREE FIELDS ON THE WINDOW, because the behaviour worth checking is the
/// sequence: what a second sample does to the first, what a sample too soon does to neither, and
/// what a reset leaves behind. None of that is checkable while it lives inside a window that needs a
/// display to exist, which is how the first version of these tests ended up asking whether the
/// SOURCE FILE mentioned a field.
///
/// THE CLOCK IS PASSED IN, so a test can drive the cadence by hand rather than sleeping through it.
/// </remarks>
public sealed class RecordingLevelHistory
{
    /// <summary>How many levels are kept, which is how many bars the meter draws.</summary>
    /// <remarks>macOS draws twenty-four and this matches it, so the two meters read the same.</remarks>
    public const int Capacity = 24;

    /// <summary>How often a level is taken.</summary>
    /// <remarks>
    /// FIFTY MILLISECONDS, WHICH IS macOS'S FIGURE AND ALSO THE ONLY ONE THAT WORKS. Capture reports
    /// a level per audio buffer, roughly two hundred times a second, so keeping every one would
    /// scroll a second of speech past in an eighth of a second and read as noise. Twenty-four at
    /// this rate is about the last second, which is the span somebody can recognise as the sentence
    /// they just said.
    /// </remarks>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The quietest level the meter shows at all, in decibels below full scale.</summary>
    /// <remarks>
    /// SIXTY DECIBELS IS THE RANGE A LEVEL METER USUALLY SPANS, and it is also where a real room's
    /// noise floor sits comfortably below the bottom bar rather than lighting it.
    /// </remarks>
    public const float FloorDecibels = -60f;

    /// <summary>Turns a raw level into the nought-to-one a bar height is drawn from.</summary>
    /// <remarks>
    /// DECIBELS, BECAUSE A SQUARE ROOT WAS NOT ENOUGH AND THE ARITHMETIC SAYS SO. Ordinary speech
    /// measured on this hardware sits at a root-mean-square of about 0.004. Through the old
    /// sqrt(level * 4) that became 0.125, which on a twenty-four bar rail is a bar three tenths of a
    /// pixel off the floor at ordinary scaling - technically moving, visually flat, and reported by
    /// a person with a screen as a dead meter. Hearing is logarithmic and so is every meter people
    /// are used to reading; the same 0.004 through this is 0.20, which is visibly off the floor.
    ///
    /// A FLOOR RATHER THAN A CURVE THAT REACHES ZERO. Below sixty decibels down there is nothing
    /// worth drawing, and mapping that region onto the bottom bar would light the meter in a silent
    /// room and destroy the one reading that matters: that nothing is arriving.
    ///
    /// NOT-A-NUMBER IS SILENCE, AND IT HAS TO BE CAUGHT HERE RATHER THAN LATER. Math.Max and
    /// Math.Clamp both hand NaN straight back, so a level nobody could measure would pass every
    /// range check and arrive as an opacity and a height that no layout can draw. Infinity is the
    /// same problem wearing a different value.
    /// </remarks>
    public static float Normalize(float level)
    {
        if (!float.IsFinite(level) || level <= 0f)
        {
            return 0f;
        }

        var decibels = 20f * MathF.Log10(level);
        return Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0f, 1f);
    }

    private readonly float[] _levels = new float[Capacity];
    private TimeSpan? _lastSampleAt;

    /// <summary>The kept levels, oldest first.</summary>
    public IReadOnlyList<float> Levels => _levels;

    /// <summary>
    /// Offers one level, and says whether it was kept.
    /// </summary>
    /// <param name="level">The level, clamped into 0 to 1.</param>
    /// <param name="now">A monotonic reading. Only differences are used.</param>
    /// <returns>True when the level was appended and the meter should be redrawn.</returns>
    public bool Sample(float level, TimeSpan now)
    {
        if (_lastSampleAt is { } last && now - last < SampleInterval)
        {
            return false;
        }

        _lastSampleAt = now;

        // OLDEST FALLS OFF THE LEFT, NEWEST ARRIVES ON THE RIGHT, so the rail reads in the order the
        // sentence was said.
        Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
        // NOT-A-NUMBER SURVIVES A CLAMP, WHICH IS THE ONE VALUE THAT MUST NOT REACH A BAR HEIGHT.
        // Math.Clamp hands NaN straight back, and a NaN height is a layout the pill cannot draw.
        // Silence is the honest reading of a level nobody could measure.
        _levels[^1] = float.IsNaN(level) ? 0f : Math.Clamp(level, 0f, 1f);
        return true;
    }

    /// <summary>Forgets everything, so a new recording starts on an empty meter.</summary>
    /// <remarks>
    /// THE PREVIOUS SENTENCE MUST NOT BE ON SCREEN DURING THIS ONE. Leaving the last recording's
    /// shape up shows somebody words they have already finished saying as though they were still
    /// saying them.
    /// </remarks>
    public void Reset()
    {
        Array.Clear(_levels);
        _lastSampleAt = null;
    }
}
