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

    /// <summary>The person asked for the clipboard rather than a paste.</summary>
    /// <remarks>
    /// A REASON RATHER THAN A FAILURE, and it needs its own name for that. Every other value here
    /// says something went wrong and the clipboard caught it; this one says the clipboard is where
    /// the text was going. Folding it into one of the others would report a refusal in the
    /// diagnostics every time somebody used the setting as intended.
    /// </remarks>
    CopyRequested,
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
    int MaximumDirectValueCharacters,
    bool CopyInsteadOfPaste = false)
{
    public static TextDeliveryOptions Default { get; } = new(
        RestoreClipboardAfterPaste: true,
        ContextWindowCharacters: 256,
        MaximumDirectValueCharacters: 16_384,
        CopyInsteadOfPaste: false);
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

    /// <summary>Puts the text on the clipboard and leaves every window alone.</summary>
    /// <remarks>
    /// A DELIVERY IN ITS OWN RIGHT, NOT A FALLBACK. The fallback path reaches the clipboard THROUGH
    /// the target: it validates the window, reads the caret context, repairs the spacing for where
    /// the text was going to land, and only then gives up. Somebody who asked for the clipboard is
    /// not going anywhere, so all of that is work done for a destination that does not exist - and
    /// it is not free. Capturing the context can bring the old window back to the front, it can fail
    /// and stop the copy that was the whole point, and the repaired text is not the text that was
    /// said.
    /// </remarks>
    Task<TextCommitResult> CopyOnlyAsync(
        ProcessedText text,
        CancellationToken cancellationToken = default);
}

public interface ITextDelivery
{
    Task<DeliveryResult> DeliverAsync(
        TextDeliveryRequest request,
        CancellationToken cancellationToken = default);
}
