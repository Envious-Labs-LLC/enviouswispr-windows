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

    /// <summary>What the switch says, or Unknown when it cannot be read.</summary>
    public static MicrophoneConsent Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConsentKey);
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
