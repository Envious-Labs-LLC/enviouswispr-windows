using EnviousWispr.Core.Audio;
using Microsoft.Win32;

namespace EnviousWispr.Audio;

/// <summary>Reads the Windows per-user microphone privacy switch.</summary>
/// <remarks>
/// READ ONLY, AND FROM THE USER'S OWN HIVE. Nothing here writes, and nothing here asks Windows for
/// permission: a desktop app cannot raise the microphone consent prompt a packaged app can, so the
/// honest thing is to report the switch and offer to open the page where it lives.
///
/// UNKNOWN IS A REAL ANSWER AND THE DEFAULT ONE. The key is absent on a machine where nobody has
/// ever touched the setting, policy can hide it, and a locked-down hive can refuse the read. In all
/// three the truthful report is that the switch could not be read, which the sentence then avoids
/// mentioning. Guessing "allowed" would put a cause on screen that nothing checked.
/// </remarks>
public static class WindowsMicrophoneConsent
{
    private const string ConsentKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>What the switches say, or Unknown when neither can be read.</summary>
    /// <remarks>
    /// TWO SWITCHES, AND EITHER ONE CAN REFUSE. The per-user value is the one somebody sets in the
    /// Settings app. The machine value is the one an administrator or a workplace policy sets, and it
    /// overrules the user, so a report that read only the user's own hive would tell a managed
    /// machine the microphone is fine while every attempt to open it is denied.
    ///
    /// A DENY ANYWHERE WINS, and an unreadable half is not a yes. Allowed is claimed only when the
    /// switches that could be read all said Allow and none said Deny.
    /// </remarks>
    public static MicrophoneConsent Read() => Combine(
        ReadFrom(Registry.LocalMachine),
        ReadFrom(Registry.CurrentUser));

    /// <summary>Resolves two switch readings into one answer. Public so both orders are testable.</summary>
    public static MicrophoneConsent Combine(MicrophoneConsent machine, MicrophoneConsent user)
    {
        if (machine == MicrophoneConsent.Blocked || user == MicrophoneConsent.Blocked)
        {
            return MicrophoneConsent.Blocked;
        }

        return machine == MicrophoneConsent.Unknown || user == MicrophoneConsent.Unknown
            ? MicrophoneConsent.Unknown
            : MicrophoneConsent.Allowed;
    }

    private static MicrophoneConsent ReadFrom(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(ConsentKey);
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
