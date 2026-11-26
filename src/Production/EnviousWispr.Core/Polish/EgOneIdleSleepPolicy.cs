using EnviousWispr.Core.Settings;

namespace EnviousWispr.Core.Polish;

/// <summary>
/// Maps user idle-unload preferences to <c>llama-server --sleep-idle-seconds</c> values (ref: macOS #3853).
/// </summary>
public static class EgOneIdleSleepPolicy
{
    /// <summary>
    /// "Immediately" uses ten seconds rather than one so multi-step operations (such as file
    /// transcription polish) do not unload weights between consecutive requests.
    /// </summary>
    public const int ImmediatelySeconds = 10;

    /// <summary>Returns the idle timeout in seconds, or null when the model should stay loaded.</summary>
    public static int? SleepIdleSeconds(PolishUnloadPolicy policy) => policy switch
    {
        PolishUnloadPolicy.Immediately => ImmediatelySeconds,
        PolishUnloadPolicy.TwoMinutes => 120,
        PolishUnloadPolicy.FiveMinutes => 300,
        PolishUnloadPolicy.TenMinutes => 600,
        PolishUnloadPolicy.FifteenMinutes => 900,
        PolishUnloadPolicy.SixtyMinutes => 3_600,
        _ => null,
    };
}
