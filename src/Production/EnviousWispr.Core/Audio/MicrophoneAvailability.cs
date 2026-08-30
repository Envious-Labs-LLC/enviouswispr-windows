namespace EnviousWispr.Core.Audio;

/// <summary>Whether Windows lets this user's apps reach a microphone at all.</summary>
/// <remarks>
/// A SETTING, NOT A DEVICE. Windows keeps one switch per user that turns microphone access off for
/// every desktop app at once, and while it is off the app sees exactly what it sees when nothing is
/// plugged in: no capture devices. Reading the switch is the only way to tell the two apart.
/// </remarks>
public enum MicrophoneConsent
{
    /// <summary>The switch could not be read. Say nothing about it rather than guessing.</summary>
    Unknown,

    /// <summary>Windows allows desktop apps to use the microphone.</summary>
    Allowed,

    /// <summary>Windows is refusing every desktop app, this one included.</summary>
    Blocked,
}

/// <summary>What the microphone card says, and whether it offers a way to fix it.</summary>
/// <param name="Sentence">The line shown to the person.</param>
/// <param name="OffersPrivacySettings">
/// True when Windows privacy settings are the place the problem is fixed.
/// </param>
/// <param name="IsReady">True when a recording started right now would reach a microphone.</param>
public sealed record MicrophoneReadiness(
    string Sentence,
    bool OffersPrivacySettings,
    bool IsReady);

/// <summary>Turns what is known about the microphone into one sentence.</summary>
/// <remarks>
/// TWO CAUSES WORE THE SAME SENTENCE, AND ONLY ONE OF THEM WAS THE USER'S TO FIX. "No active
/// recording device found. Windows microphone privacy or device settings may need attention" was
/// shown both to somebody with no microphone plugged in and to somebody whose Windows privacy switch
/// is off. The first needs hardware and the second needs one click, and naming both every time meant
/// naming neither.
///
/// A DECISION IN CORE, SO IT CAN BE CHECKED WITHOUT A MICROPHONE. The registry read and the device
/// enumeration are the parts that need a real machine; which sentence follows from them is not, and
/// keeping that here is what makes every branch testable on any machine.
/// </remarks>
public static class MicrophoneReadinessReport
{
    /// <param name="consent">What the Windows privacy switch says, if it could be read.</param>
    /// <param name="defaultDeviceName">The device a recording would use, or null if there is none.</param>
    /// <param name="enumerationFailed">True when Windows refused to list devices at all.</param>
    public static MicrophoneReadiness For(
        MicrophoneConsent consent,
        string? defaultDeviceName,
        bool enumerationFailed = false)
    {
        // BLOCKED OUTRANKS EVERY OTHER SENTENCE, INCLUDING A FAILED ENUMERATION. A refusal to list
        // devices is one of the things a blocked switch CAUSES, so answering with the generic
        // "Windows could not list microphones" there hides the cause behind its own symptom.
        //
        // AND IT IS CHECKED BEFORE THE DEVICE LIST for the same reason: while the switch is off the
        // list is empty for a reason that has nothing to do with the devices, and "no microphone
        // found" sends somebody to look at their hardware over a setting.
        if (consent == MicrophoneConsent.Blocked)
        {
            return new MicrophoneReadiness(
                "Windows is blocking microphone access for desktop apps, so dictation cannot hear "
                    + "you. Turn it on in microphone privacy settings.",
                OffersPrivacySettings: true,
                IsReady: false);
        }

        if (enumerationFailed)
        {
            return new MicrophoneReadiness(
                "Windows could not list microphones. Settings remain available and dictation will "
                    + "fail safely.",
                OffersPrivacySettings: false,
                IsReady: false);
        }

        if (string.IsNullOrWhiteSpace(defaultDeviceName))
        {
            // NOT "check your privacy settings" ANY MORE. Either the switch is on, or it could not
            // be read; in the first case privacy is not the problem and in the second nothing is
            // known about it, and inventing a cause is what the old sentence did.
            return new MicrophoneReadiness(
                consent == MicrophoneConsent.Allowed
                    ? "No microphone is available. Plug one in, or switch it on in Windows sound "
                        + "settings."
                    : "No microphone is available. Check that one is plugged in and that Windows "
                        + "allows microphone access.",
                OffersPrivacySettings: consent != MicrophoneConsent.Allowed,
                IsReady: false);
        }

        return new MicrophoneReadiness(
            $"{defaultDeviceName} is available.",
            OffersPrivacySettings: false,
            IsReady: true);
    }
}
