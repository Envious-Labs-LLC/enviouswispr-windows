using EnviousWispr.Core.Errors;
namespace EnviousWispr.Core.Diagnostics;

/// <summary>One line of the local debug log.</summary>
/// <remarks>
/// A SEPARATE TYPE FROM THE ONE THAT LEAVES THE MACHINE, AND THE SEPARATION IS THE POINT. The
/// telemetry record is an allowlist: a field is sent because somebody added it there, never because
/// it happened to exist upstream. `PrivacySafeObservabilityTests` enforces that from the other side by
/// refusing any property on that record whose name ends in "Id", which is how this change first
/// failed - a dictation id added to the shared record would have travelled off the machine after
/// consent, and the suite said no. It was right to.
///
/// So the join lives HERE, where a person diagnosing their own slow dictation can read it, and does
/// not exist on the record that crosses the network. Aggregate telemetry does not need a per-dictation
/// join to compute stage timings; the person asking "why was that ONE slow" does.
///
/// FLAT RATHER THAN NESTING THE OTHER RECORD, because the log is read back with
/// `JsonUnmappedMemberHandling.Disallow` and a shape change would make every existing line
/// unreadable. A line written before this field existed simply has no `dictationId`, which
/// deserialises as null.
/// </remarks>
public sealed record LocalDiagnosticLine(
    DateTimeOffset Timestamp,
    AppEventCode Event,
    AppFailureCategory Failure,
    long? ElapsedMilliseconds = null,
    DiagnosticProvider? Provider = null,
    AppErrorCode? ErrorCode = null,
    DiagnosticEngineChoice? Engine = null,
    DiagnosticHardwareClass? HardwareClass = null,
    Guid? DictationId = null)
{
    /// <summary>Takes a line that is safe to send and adds what only this machine may know.</summary>
    public static LocalDiagnosticLine From(PrivacySafeDiagnosticRecord record, Guid? dictationId)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new LocalDiagnosticLine(
            record.Timestamp,
            record.Event,
            record.Failure,
            record.ElapsedMilliseconds,
            record.Provider,
            record.ErrorCode,
            record.Engine,
            record.HardwareClass,
            dictationId);
    }
}
