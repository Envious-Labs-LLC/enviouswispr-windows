using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;

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
    TargetWindowId Target,
    TextDeliveryOptions DeliveryOptions,
    DateTimeOffset? FinishedAt = null,
    AppError? Error = null)
{
    public static DictationSessionSnapshot Start(DateTimeOffset startedAt) => new(
        DictationSessionId.Create(),
        DictationSessionState.Recording,
        startedAt,
        default,
        TextDeliveryOptions.Default);

    public static DictationSessionSnapshot Start(
        DictationSessionId id,
        DateTimeOffset startedAt,
        TargetWindowId target,
        TextDeliveryOptions? deliveryOptions = null) => new(
        id,
        DictationSessionState.Recording,
        startedAt,
        target,
        deliveryOptions ?? TextDeliveryOptions.Default);
}
