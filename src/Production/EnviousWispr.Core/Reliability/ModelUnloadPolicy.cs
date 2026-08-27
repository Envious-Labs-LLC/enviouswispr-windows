namespace EnviousWispr.Core.Reliability;

/// <summary>Whether to give back the memory the transcription model is holding.</summary>
public enum ModelUnloadDecision
{
    /// <summary>Keep it loaded. The next dictation starts immediately.</summary>
    Keep,

    /// <summary>Unload it. The next dictation pays to load it again.</summary>
    Unload,
}

/// <summary>
/// Decides when a loaded transcription model is costing more than it is saving.
/// </summary>
/// <remarks>
/// THE TRADE IS ENTIRELY ONE-SIDED IN THE SHORT TERM, which is why this policy is conservative.
/// Keeping the model loaded costs memory and nothing else. Unloading it costs the NEXT dictation a
/// cold start, and a slow first word is the thing this product is built to avoid. So the question
/// is never "could we free memory" - the answer to that is always yes - but "is this memory now
/// worth more to the rest of the machine than the next dictation's speed".
///
/// TWO REASONS QUALIFY AND THEY ARE NOT THE SAME REASON.
/// A long idle means the user has stopped dictating, so the cold start is probably not imminent and
/// probably not noticed. That is an OPTIMISATION.
/// Memory pressure means something else on the machine needs the memory now, and holding it may be
/// making the whole system slower - including, eventually, us. That is a DUTY, and it applies at a
/// much shorter idle, because a machine that is swapping is not one where our warm model is helping
/// anybody.
///
/// IT NEVER UNLOADS DURING A RECORDING, and that guard is first rather than folded in with the
/// others. Unloading the model out from under a running dictation is the one outcome that loses a
/// user's words rather than merely delaying them, and the founder's stated order puts "dictation
/// works" above every kind of faster.
/// </remarks>
public static class ModelUnloadPolicy
{
    /// <summary>How long an idle machine keeps the model before it is given back.</summary>
    /// <remarks>
    /// Long enough that ordinary gaps between dictations never reach it - reading a reply, switching
    /// windows, thinking - and short enough that a model is not held all night because someone left
    /// the app open.
    /// </remarks>
    public static readonly TimeSpan IdleBeforeUnload = TimeSpan.FromMinutes(10);

    /// <summary>How long an idle machine UNDER MEMORY PRESSURE keeps the model.</summary>
    /// <remarks>
    /// Much shorter, because here the memory is wanted by something else right now. Still not zero:
    /// a momentary spike while the user is mid-thought between two dictations should not cost them
    /// a cold start on the second one.
    /// </remarks>
    public static readonly TimeSpan IdleBeforeUnloadUnderPressure = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Below this much free memory, the machine is treated as under pressure.
    /// </summary>
    /// <remarks>
    /// Deliberately well above the floor at which a dictation is refused to start. This policy is
    /// meant to act BEFORE the machine reaches the point where the user is turned away, not to be a
    /// second opinion about the same emergency.
    /// </remarks>
    public const ulong MemoryPressureBytes = 1_536UL * 1024 * 1024;

    /// <summary>
    /// Whether to unload the model now.
    /// </summary>
    /// <param name="isRecording">True while a dictation is in flight.</param>
    /// <param name="idle">How long since the last dictation ended.</param>
    /// <param name="snapshot">The machine's memory, or null when it could not be read.</param>
    /// <remarks>
    /// A snapshot that could not be read is treated as NO pressure rather than as pressure. An
    /// unreadable probe is a fact about the probe, and letting it stand in for a memory emergency
    /// would make an instrument failure slow down the user's next dictation - the plausible-value
    /// trap, where a missing reading becomes a confident one.
    /// </remarks>
    public static ModelUnloadDecision Decide(
        bool isRecording,
        TimeSpan idle,
        SystemResourceSnapshot? snapshot)
    {
        if (isRecording)
        {
            return ModelUnloadDecision.Keep;
        }

        var underPressure = snapshot is { IsAvailable: true } &&
            snapshot.AvailablePhysicalMemoryBytes < MemoryPressureBytes;

        var threshold = underPressure ? IdleBeforeUnloadUnderPressure : IdleBeforeUnload;
        return idle >= threshold ? ModelUnloadDecision.Unload : ModelUnloadDecision.Keep;
    }
}
