using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Diagnostics;

public sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    AppEventCode Event,
    AppFailureCategory Failure = AppFailureCategory.None,
    long? ElapsedMilliseconds = null,
    DiagnosticProvider? Provider = null,
    AppErrorCode? ErrorCode = null,
    DiagnosticEngineChoice? Engine = null,
    DiagnosticHardwareClass? HardwareClass = null,
    DeterministicTextStage? Stage = null,
    DeterministicStageStatus? StageStatus = null,
    bool? Changed = null);
