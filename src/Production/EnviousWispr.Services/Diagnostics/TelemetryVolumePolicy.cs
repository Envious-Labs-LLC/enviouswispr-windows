using System.Security.Cryptography;
using System.Text;

namespace EnviousWispr.Services.Diagnostics;

public enum TelemetryVolumeDecisionKind
{
    Keep,
    KeepSampled,
    Drop,
}

public readonly record struct TelemetryVolumeDecision(
    TelemetryVolumeDecisionKind Kind,
    int ThresholdPercent = 100)
{
    public static TelemetryVolumeDecision Keep { get; } = new(TelemetryVolumeDecisionKind.Keep, 100);

    public static TelemetryVolumeDecision Drop { get; } = new(TelemetryVolumeDecisionKind.Drop, 0);

    public static TelemetryVolumeDecision KeepSampled(int thresholdPercent) =>
        new(TelemetryVolumeDecisionKind.KeepSampled, thresholdPercent);
}

/// <summary>
/// Deterministic SHA-256 UUID volume policy mirroring macOS <c>TelemetryVolumePolicy</c> (#2958).
/// Samples high-volume happy-path events at 10% (<c>bucket &lt; 10</c>) and stamps
/// <c>telemetry_policy_version = 6</c> on every kept event.
/// </summary>
public static class TelemetryVolumePolicy
{
    public const int PolicyVersion = 6;
    public const string PolicyVersionKey = "telemetry_policy_version";
    public const int SampleThresholdPercent = 10;

    private static readonly HashSet<string> HappyPathPressActions = new(StringComparer.Ordinal)
    {
        "start",
        "toggle",
    };

    private static readonly HashSet<string> KnownNonFiredProactiveReasons = new(StringComparer.Ordinal)
    {
        "cooldown",
        "auto_checks_off",
        "no_updater",
        "session_in_progress",
    };

    private static readonly HashSet<string> KnownLearnSkipReasons = new(StringComparer.Ordinal)
    {
        "toggle_off",
        "watch_active",
        "model_unavailable",
        "destination_mismatch",
        "secure_field",
        "no_focused_element",
    };

    /// <summary>
    /// Computes the deterministic bucket 0..99 from the SHA-256 digest of the canonical uppercase UUID string
    /// (big-endian first four bytes modulo 100), matching the macOS independent oracle.
    /// </summary>
    public static int Bucket(Guid uuid)
    {
        var uppercase = uuid.ToString("D").ToUpperInvariant();
        Span<byte> utf8 = stackalloc byte[36];
        Encoding.UTF8.GetBytes(uppercase, utf8);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(utf8, digest);
        var word = ((uint)digest[0] << 24)
                 | ((uint)digest[1] << 16)
                 | ((uint)digest[2] << 8)
                 | digest[3];
        return (int)(word % 100);
    }

    public static TelemetryVolumeDecision Decide(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties,
        Guid uuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var props = properties ?? EmptyProperties;

        if (IsSampledHappyPath(eventName, props))
        {
            return Bucket(uuid) < SampleThresholdPercent
                ? TelemetryVolumeDecision.KeepSampled(SampleThresholdPercent)
                : TelemetryVolumeDecision.Drop;
        }

        return eventName switch
        {
            "Application Backgrounded" => TelemetryVolumeDecision.Drop,
            "Application Opened" when props.TryGetValue("from_background", out var fromBg) && fromBg is true =>
                TelemetryVolumeDecision.Drop,
            _ => TelemetryVolumeDecision.Keep,
        };
    }

    public static Dictionary<string, object?>? Apply(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties,
        Guid uuid)
    {
        var decision = Decide(eventName, properties, uuid);
        if (decision.Kind == TelemetryVolumeDecisionKind.Drop)
        {
            return null;
        }

        var output = properties is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(properties, StringComparer.Ordinal);

        output[PolicyVersionKey] = PolicyVersion;
        if (decision.Kind == TelemetryVolumeDecisionKind.KeepSampled)
        {
            output["$sample_type"] = new[] { "sampleByEvent" };
            output["$sample_threshold"] = decision.ThresholdPercent / 100.0;
            output["$sampled_events"] = new[] { eventName };
        }

        return output;
    }

    private static bool IsSampledHappyPath(
        string eventName,
        IReadOnlyDictionary<string, object?> properties)
    {
        switch (eventName)
        {
            case "hotkey.pressed":
                if (properties.TryGetValue("window_timing", out var timing) &&
                    timing is string timingStr &&
                    string.Equals(timingStr, "after_stop_timer", StringComparison.Ordinal))
                {
                    return false;
                }

                return properties.TryGetValue("press_action", out var action) &&
                       action is string actionStr &&
                       HappyPathPressActions.Contains(actionStr);

            case "live_preview.outcome":
                return properties.TryGetValue("outcome", out var outcome) &&
                       outcome is string outcomeStr &&
                       string.Equals(outcomeStr, "started", StringComparison.Ordinal);

            case "audio.input_resolution":
                return properties.TryGetValue("prepare_outcome", out var prepare) &&
                       prepare is string prepareStr &&
                       string.Equals(prepareStr, "succeeded", StringComparison.Ordinal) &&
                       properties.TryGetValue("bind_outcome", out var bind) &&
                       bind is string bindStr &&
                       string.Equals(bindStr, "succeeded", StringComparison.Ordinal) &&
                       properties.TryGetValue("enumeration_outcome", out var enumeration) &&
                       enumeration is string enumerationStr &&
                       (string.Equals(enumerationStr, "succeeded", StringComparison.Ordinal) ||
                        string.Equals(enumerationStr, "not_attempted", StringComparison.Ordinal));

            case "paste.copies_observed":
                return properties.TryGetValue("status", out var status) &&
                       status is string statusStr &&
                       string.Equals(statusStr, "no_before_image", StringComparison.Ordinal);

            case "custom_words.learn_skipped":
                return properties.TryGetValue("reason", out var skipReason) &&
                       skipReason is string skipReasonStr &&
                       KnownLearnSkipReasons.Contains(skipReasonStr);

            case "paste.landing_observed":
                return properties.TryGetValue("observed", out var observed) &&
                       observed is string observedStr &&
                       string.Equals(observedStr, "found", StringComparison.Ordinal) &&
                       properties.TryGetValue("reason", out var landingReason) &&
                       landingReason is string landingReasonStr &&
                       string.Equals(landingReasonStr, "same_field", StringComparison.Ordinal) &&
                       properties.TryGetValue("late_check_status", out var lateCheck) &&
                       lateCheck is string lateCheckStr &&
                       string.Equals(lateCheckStr, "not_applicable", StringComparison.Ordinal) &&
                       (!properties.TryGetValue("late_found_ms", out var lateFoundMs) || lateFoundMs is null);

            case "update.proactive_check_triggered":
                return properties.TryGetValue("fired", out var fired) &&
                       fired is false &&
                       properties.TryGetValue("reason", out var reason) &&
                       reason is string reasonStr &&
                       KnownNonFiredProactiveReasons.Contains(reasonStr);

            default:
                return false;
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
