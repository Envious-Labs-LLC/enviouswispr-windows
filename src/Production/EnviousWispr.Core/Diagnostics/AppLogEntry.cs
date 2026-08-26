using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Diagnostics;

public sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    AppEventCode Event,
    AppFailureCategory Failure = AppFailureCategory.None,
    long? ElapsedMilliseconds = null,
    string? Provider = null,
    AppErrorCode? ErrorCode = null);
