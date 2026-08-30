using EnviousWispr.Core.Audio;
using Microsoft.Win32;

namespace EnviousWispr.Audio;

/// <summary>Reads the Windows switches that decide whether this app may open a microphone.</summary>
/// <remarks>
/// READ ONLY, AND NOTHING HERE ASKS. A desktop app cannot raise the microphone consent prompt a
/// packaged app can, so the honest thing is to report the switches and offer to open the page where
/// they live rather than pretend to request anything.
///
/// UNKNOWN IS A REAL ANSWER AND THE SAFE ONE. A key is absent on a machine where nobody has touched
/// the setting, and a locked-down hive can refuse the read. The truthful report is then that the
/// switch could not be read, which the sentence avoids naming a cause for. Guessing "allowed" would
/// put a clean bill of health on screen that nothing checked.
/// </remarks>
public static class WindowsMicrophoneConsent
{
    private const string ConsentKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>The switch that governs THIS app, because this app is not a packaged one.</summary>
    /// <remarks>
    /// "LET DESKTOP APPS ACCESS YOUR MICROPHONE" IS ITS OWN CONTROL, sitting under the global one in
    /// the Settings app and stored in its own subkey. Reading only the global value missed the switch
    /// that actually decides whether this app can open a microphone, so somebody who had turned
    /// desktop apps off was told everything was fine.
    /// </remarks>
    private const string DesktopConsentKey = ConsentKey + @"\NonPackaged";

    /// <summary>Where a workplace policy refuses the microphone, which is somewhere else entirely.</summary>
    /// <remarks>
    /// NOT THE CONSENT STORE. A managed machine is configured through AppPrivacy, as a number rather
    /// than a word: 2 refuses every app, 1 allows every app, and 0 or a missing value means the
    /// policy has no opinion and the person's own switches decide.
    /// </remarks>
    private const string PolicyKey =
        @"Software\Policies\Microsoft\Windows\AppPrivacy";

    /// <summary>What the switches say, or Unknown when the answer is not certain.</summary>
    /// <remarks>
    /// A POLICY, IF THERE IS ONE, IS THE WHOLE ANSWER. It is set by an administrator and overrules
    /// every switch the person can see, in both directions. Its absence is not a refusal and not an
    /// allowance - it is silence, and the three switches below then decide.
    ///
    /// THREE SWITCHES, AND ANY ONE CAN REFUSE. The device-level value covers everybody on the
    /// machine, the per-user value covers this person, and the desktop-app value covers apps like
    /// this one. A Deny anywhere wins, and an unreadable one is not a yes.
    /// </remarks>
    public static MicrophoneConsent Read()
    {
        var policy = ReadPolicy();
        return policy != MicrophoneConsent.Unknown
            ? policy
            : Combine(
                ReadFrom(Registry.LocalMachine, ConsentKey),
                ReadFrom(Registry.CurrentUser, ConsentKey),
                ReadFrom(Registry.CurrentUser, DesktopConsentKey));
    }

    /// <summary>Resolves several switch readings into one answer. Public so every order is testable.</summary>
    public static MicrophoneConsent Combine(params MicrophoneConsent[] readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Length == 0 || Array.IndexOf(readings, MicrophoneConsent.Blocked) >= 0)
        {
            return MicrophoneConsent.Blocked;
        }

        return Array.IndexOf(readings, MicrophoneConsent.Unknown) >= 0
            ? MicrophoneConsent.Unknown
            : MicrophoneConsent.Allowed;
    }

    /// <summary>Turns the policy number into an answer, or Unknown for "no policy is set".</summary>
    /// <remarks>
    /// UNKNOWN HERE MEANS SILENCE, NOT DOUBT, and that difference is why this is separate from the
    /// switch reader. A missing policy is the ordinary case on every machine nobody manages, and
    /// treating it as an unreadable switch would make every home PC uncertain about its microphone.
    /// </remarks>
    public static MicrophoneConsent InterpretPolicy(object? storedValue) => storedValue switch
    {
        int value when value == 2 => MicrophoneConsent.Blocked,
        int value when value == 1 => MicrophoneConsent.Allowed,
        _ => MicrophoneConsent.Unknown,
    };

    private static MicrophoneConsent ReadPolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return InterpretPolicy(key?.GetValue("LetAppsAccessMicrophone"));
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException
                or System.IO.IOException)
        {
            return MicrophoneConsent.Unknown;
        }
    }

    private static MicrophoneConsent ReadFrom(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return Interpret(key?.GetValue("Value") as string);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException
                or System.IO.IOException)
        {
            return MicrophoneConsent.Unknown;
        }
    }

    /// <summary>Turns the stored word into an answer. Public so every branch is testable.</summary>
    /// <remarks>
    /// ONLY THE TWO WORDS WINDOWS WRITES COUNT. Anything else - a value of another type, a spelling
    /// nobody has seen, an empty string - is Unknown rather than Allowed, because the failure that
    /// matters is telling somebody their microphone is fine when it is switched off.
    /// </remarks>
    public static MicrophoneConsent Interpret(string? storedValue) =>
        storedValue?.Trim().ToUpperInvariant() switch
        {
            "ALLOW" => MicrophoneConsent.Allowed,
            "DENY" => MicrophoneConsent.Blocked,
            _ => MicrophoneConsent.Unknown,
        };
}
