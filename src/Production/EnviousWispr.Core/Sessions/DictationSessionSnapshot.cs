using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Core.Sessions;

public enum DictationSessionState
{
    Recording,
    Finalizing,
    Delivering,
    Completed,
    Cancelled,
    Failed,
}

public sealed record DictationSessionSnapshot(
    DictationSessionId Id,
    DictationSessionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null,
    AppError? Error = null)
{
    public static DictationSessionSnapshot Start(DateTimeOffset startedAt) => new(
        DictationSessionId.Create(),
        DictationSessionState.Recording,
        startedAt);
}
