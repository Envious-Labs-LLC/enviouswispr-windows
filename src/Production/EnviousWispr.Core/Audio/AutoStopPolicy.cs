namespace EnviousWispr.Core.Audio;

/// <summary>What the auto-stop watcher decided about a recording that is still running.</summary>
public enum AutoStopDecision
{
    /// <summary>Keep recording.</summary>
    KeepRecording,

    /// <summary>The speaker has finished. End the recording as if they had released the key.</summary>
    Stop,
}

/// <summary>
/// Decides whether a running recording should end because the speaker has stopped talking.
/// </summary>
/// <remarks>
/// THIS FEATURE CAN CUT SOMEONE OFF MID-SENTENCE, which puts it against the first rule this
/// product has: dictation works one hundred percent of the time it physically can. Every decision
/// below is shaped by that, and where the two pull against each other, not stopping wins.
///
/// SO IT ONLY EVER APPLIES IN TOGGLE MODE. In push-to-talk the user is holding the key and the
/// release IS the signal; ending a recording underneath a held key would take the decision away
/// from someone who is actively making it.
///
/// AND IT NEVER FIRES BEFORE ANYONE HAS SPOKEN. A user who starts a toggle recording and then
/// thinks for four seconds has not finished - they have not begun. Without this clause the
/// feature would stop a recording that contains nothing, every time somebody paused to collect a
/// thought, and the symptom would be "it cancels itself before I say anything".
///
/// THE THRESHOLD IS THE CALLER'S. This type does not own how long a pause has to be, because that
/// is a product judgement and a setting; it owns the two conditions that make the question safe to
/// ask at all.
/// </remarks>
public static class AutoStopPolicy
{
    /// <summary>
    /// The shortest pause that may end a recording, whatever a caller asks for.
    /// </summary>
    /// <remarks>
    /// A floor rather than a default. Ordinary speech contains pauses of well over a second at
    /// sentence boundaries and while thinking, so anything shorter would end recordings in the
    /// middle of what a person considers one thought. A caller passing less gets this instead,
    /// rather than an exception: a setting that is out of range must not stop dictation working.
    /// </remarks>
    public static readonly TimeSpan MinimumSilence = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Whether a recording that has run this way should now end.
    /// </summary>
    /// <param name="enabled">The user's setting. Off means this never returns Stop.</param>
    /// <param name="isToggleMode">
    /// False in push-to-talk, where the held key is the user's own live decision.
    /// </param>
    /// <param name="hasHeardSpeech">
    /// Whether any speech has been detected at all in this recording so far.
    /// </param>
    /// <param name="trailingSilence">How long the recording has been quiet at its end.</param>
    /// <param name="requiredSilence">
    /// How long a pause must last, from the user's setting. Clamped up to
    /// <see cref="MinimumSilence"/>.
    /// </param>
    public static AutoStopDecision Decide(
        bool enabled,
        bool isToggleMode,
        bool hasHeardSpeech,
        TimeSpan trailingSilence,
        TimeSpan requiredSilence)
    {
        if (!enabled || !isToggleMode || !hasHeardSpeech)
        {
            return AutoStopDecision.KeepRecording;
        }

        var threshold = requiredSilence < MinimumSilence ? MinimumSilence : requiredSilence;
        return trailingSilence >= threshold
            ? AutoStopDecision.Stop
            : AutoStopDecision.KeepRecording;
    }
}
