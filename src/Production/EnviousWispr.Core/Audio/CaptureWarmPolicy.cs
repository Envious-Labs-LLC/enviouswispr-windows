namespace EnviousWispr.Core.Audio;

/// <summary>What to do with the microphone device between dictations.</summary>
public enum CaptureWarmDecision
{
    /// <summary>Do nothing. Whatever is held now stays held, and nothing new is opened.</summary>
    Leave,

    /// <summary>Open the selected device now, so the next key press does not have to.</summary>
    Warm,

    /// <summary>Let go of the device that is held. It is the wrong one, or it is no longer earning its keep.</summary>
    Release,
}

/// <summary>
/// Decides whether to hold the microphone device open between dictations.
/// </summary>
/// <remarks>
/// WHAT THIS BUYS. Opening a microphone is the slowest thing a key press does. The device has to be
/// found, a client made against it, and buffers allocated, and all of that happens after the user
/// has already started talking. Doing it in advance means the press only has to start a stream that
/// is already open, and the first word stops being the one at risk.
///
/// OPEN IS NOT LISTENING, AND THE WHOLE FEATURE RESTS ON THAT. A device that has been opened but not
/// started delivers no audio: there is nothing to read, nothing buffered, and nothing that could
/// reach a transcript. This is deliberately NOT the pre-roll buffer, which keeps a rolling half
/// second of real audio and is a genuinely different question that only the founder can answer.
///
/// THE PREMISE ABOVE IS NOT YET EXECUTED. Whether Windows shows the microphone-in-use indicator when
/// a device is OPENED, or only when a stream is STARTED, decides whether this ships on by default.
/// It is being checked on the rig against a real build. If the indicator appears on open, then to a
/// user this is indistinguishable from always listening whatever the audio says, and it becomes a
/// founder decision rather than an optimisation. <see cref="WarmingAllowedByDefault"/> is the single place
/// that answer lands.
///
/// THE DANGEROUS CASE IS NOT A SLOW PRESS, IT IS THE WRONG MICROPHONE. A device held from before the
/// user changed their input would record the old one, and they would not find out until they read a
/// transcript of a microphone they are not talking into. That is the one outcome here that loses
/// words rather than delaying them, so the stale check runs before everything except the recording
/// guard, and it releases rather than trying to be clever.
/// </remarks>
public static class CaptureWarmPolicy
{
    /// <summary>What the app passes for <c>warmingAllowed</c> today.</summary>
    /// <remarks>
    /// FALSE UNTIL THE INDICATOR QUESTION IS ANSWERED ON A REAL BUILD. One constant decides whether
    /// the feature does anything, so the answer turns it on in a single edit, and until then the app
    /// behaves exactly as it does now.
    ///
    /// IT IS A DEFAULT AND NOT A CONDITION BAKED INTO THE POLICY, and that distinction is the whole
    /// reason this is a parameter. A policy whose switch is a compile-time constant has half its
    /// branches unreachable, so every test of those branches passes without executing anything and
    /// the feature can be dead while looking finished. That already happened once on this project,
    /// to the hands-free lock, and the only thing that caught it was a test written to fail.
    ///
    /// Off is the honest default rather than the cautious one. A user who sees a microphone
    /// indicator they did not expect has been told something untrue about their own machine, and no
    /// amount of speed buys that back.
    /// </remarks>
    public const bool WarmingAllowedByDefault = false;

    /// <summary>How long an unused open device is kept before it is given back.</summary>
    /// <remarks>
    /// Shorter than the transcription model's idle, and for a different reason. The model is
    /// expensive to reload, so holding it longer pays off. A device is cheap to reopen, so the only
    /// thing a long hold buys is a rare fast press, while the cost - a handle on hardware the user
    /// may want to give to something else - is continuous.
    ///
    /// Five minutes covers dictating in bursts, which is how people actually use this: several in a
    /// row while writing a reply, then nothing for an hour.
    /// </remarks>
    public static readonly TimeSpan IdleBeforeRelease = TimeSpan.FromMinutes(5);

    /// <summary>How many failed opens in a row stop it trying again.</summary>
    /// <remarks>
    /// A device that will not open is usually held exclusively by something else, or has just been
    /// unplugged. Retrying on a timer would spend the machine's attention on a device that is not
    /// coming back, and would do it silently.
    ///
    /// The count is reset by a device change or a successful dictation, so this stops the retrying
    /// without permanently giving up: the user plugging the headset back in is a device change, and
    /// that is precisely when trying again is worth it.
    /// </remarks>
    public const int FailuresBeforeGivingUp = 3;

    /// <summary>
    /// What to do with the microphone device right now.
    /// </summary>
    /// <param name="warmingAllowed">Whether holding a device open is permitted at all.</param>
    /// <param name="isRecording">True while a dictation is in flight.</param>
    /// <param name="warmDeviceId">The device currently held open, or null if none is.</param>
    /// <param name="selectedDeviceId">The device a dictation would use, or null if none is chosen.</param>
    /// <param name="idle">How long since the last dictation ended.</param>
    /// <param name="consecutiveFailures">Opens that failed in a row since the last success or device change.</param>
    /// <remarks>
    /// RELEASE MEANS RELEASE AND ASK AGAIN, rather than release and wait for the next tick. A device
    /// that is stale wants replacing, not merely dropping, and a caller that drops it and goes to
    /// sleep leaves the user paying full price on the next press for no reason. The two-step is
    /// deliberate: this returns one thing at a time so that "let go of what you have" and "open what
    /// you should" are separately visible in a log, rather than one compound action nobody can tell
    /// apart when it goes wrong.
    /// </remarks>
    public static CaptureWarmDecision Decide(
        bool warmingAllowed,
        bool isRecording,
        string? warmDeviceId,
        string? selectedDeviceId,
        TimeSpan idle,
        int consecutiveFailures)
    {
        // FIRST, ALWAYS. A dictation in flight owns the device, and releasing it here would end a
        // recording the user is still speaking into. This is the founder's stated order: dictation
        // works, before any kind of faster.
        if (isRecording)
        {
            return CaptureWarmDecision.Leave;
        }

        var held = !string.IsNullOrEmpty(warmDeviceId);

        // Turning the feature off has to give back anything already held, not merely stop opening
        // more. A switch that only applies to future opens leaves a device held indefinitely by a
        // feature the user has switched off.
        if (!warmingAllowed)
        {
            return held ? CaptureWarmDecision.Release : CaptureWarmDecision.Leave;
        }

        // The wrong microphone, which is the failure that costs words rather than time. Before the
        // idle check, because a stale device is wrong immediately rather than eventually.
        if (held && !string.Equals(warmDeviceId, selectedDeviceId, StringComparison.Ordinal))
        {
            return CaptureWarmDecision.Release;
        }

        if (held)
        {
            return idle >= IdleBeforeRelease
                ? CaptureWarmDecision.Release
                : CaptureWarmDecision.Leave;
        }

        if (string.IsNullOrEmpty(selectedDeviceId))
        {
            return CaptureWarmDecision.Leave;
        }

        return consecutiveFailures >= FailuresBeforeGivingUp
            ? CaptureWarmDecision.Leave
            : CaptureWarmDecision.Warm;
    }
}
