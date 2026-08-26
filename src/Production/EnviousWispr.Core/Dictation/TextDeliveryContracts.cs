using EnviousWispr.Core.Input;

namespace EnviousWispr.Core.Dictation;

public enum TextTargetKind
{
    StandardEdit,
    Browser,
    Office,
    Chat,
    Terminal,
    Game,
    Unknown,
}

public enum TargetContextStatus
{
    Available,
    TargetUnavailable,
    TargetChanged,
    Protected,
    Elevated,
    AccessibilityUnavailable,
}

public enum TextDeliveryRoute
{
    None,
    UiAutomationValue,
    ClipboardPaste,
    ClipboardOnly,
}

public enum TextDeliveryRefusalReason
{
    None,
    TargetUnavailable,
    TargetChanged,
    ProtectedField,
    ElevatedTarget,
    AccessibilityUnavailable,
    UnsupportedTarget,
    UnsafeMultilineTarget,
    ClipboardUnavailable,
    InputStateUnsafe,
    InputBlocked,
    DirectWriteUnverified,
    Cancelled,
}

public enum CursorRepairDisposition
{
    LegacyPayload,
    ContextApplied,
}

public sealed record TextDeliveryOptions(
    bool RestoreClipboardAfterPaste,
    int ContextWindowCharacters,
    int MaximumDirectValueCharacters)
{
    public static TextDeliveryOptions Default { get; } = new(
        RestoreClipboardAfterPaste: true,
        ContextWindowCharacters: 256,
        MaximumDirectValueCharacters: 16_384);
}

public sealed record TextDeliveryRequest(
    ProcessedText Text,
    TargetWindowId Target,
    string? LanguageCode,
    TextDeliveryOptions Options);

public sealed record CaretContext(
    TargetWindowId Target,
    string FocusedElementId,
    TextTargetKind TargetKind,
    string Left,
    string Selection,
    string Right,
    bool LeftReachedDocumentStart,
    bool RightReachedDocumentEnd,
    bool HasTextContext,
    bool SupportsDirectValueWrite,
    bool DirectValueWriteAtEnd,
    bool IsScreenDerived = false,
    bool IsUrlBarField = false);

public sealed record TargetContextResult(
    TargetContextStatus Status,
    CaretContext? Context = null,
    TextDeliveryRefusalReason RefusalReason = TextDeliveryRefusalReason.None);

public sealed record TextCommitRequest(
    ProcessedText Text,
    ProcessedText LegacyText,
    TargetWindowId Target,
    CaretContext? ExpectedContext,
    TextTargetKind TargetKind,
    TextDeliveryOptions Options,
    TextDeliveryRefusalReason ForcedRefusalReason = TextDeliveryRefusalReason.None);

public sealed record TextCommitResult(
    TextDeliveryRoute Route,
    bool Delivered,
    bool ClipboardFallback,
    bool ClipboardRestored,
    TextDeliveryRefusalReason RefusalReason = TextDeliveryRefusalReason.None);

public sealed record DeliveryResult(
    DictationSessionId SessionId,
    bool Delivered,
    bool ClipboardFallback,
    TextDeliveryRoute Route = TextDeliveryRoute.None,
    TextDeliveryRefusalReason RefusalReason = TextDeliveryRefusalReason.None,
    CursorRepairDisposition RepairDisposition = CursorRepairDisposition.LegacyPayload,
    bool ClipboardRestored = false);

public interface ITextTargetAdapter
{
    Task<TargetContextResult> CaptureContextAsync(
        TargetWindowId target,
        TextDeliveryOptions options,
        CancellationToken cancellationToken = default);

    Task<TextCommitResult> CommitAsync(
        TextCommitRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITextDelivery
{
    Task<DeliveryResult> DeliverAsync(
        TextDeliveryRequest request,
        CancellationToken cancellationToken = default);
}
