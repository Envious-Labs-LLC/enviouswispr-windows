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
    /// <summary>Nothing was refused.</summary>
    /// <remarks>
    /// A REQUESTED COPY ENDS HERE, AND THAT IS THE WHOLE POINT. It briefly had a name of its own,
    /// which was the wrong shape: every other value in this enum says something went wrong and the
    /// clipboard caught it, so a member for the ordinary case would have reported a refusal in the
    /// diagnostics every time somebody used the setting exactly as intended. Where the text went is
    /// carried by <see cref="TextDeliveryRoute"/>, which already has a value for the clipboard.
    /// </remarks>
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

    /// <summary>The adapter had been disposed under the delivery: the app was leaving. Not an accessibility failure.</summary>
    DeliveryDisposed,

    /// <summary>A defect inside the delivery - an exception nobody expected - stopped it; <see cref="DeliveryResult.Fault"/> says which, content-free.</summary>
    DeliveryFaulted,
}

/// <summary>Where in a delivery an unexpected exception was thrown.</summary>
public enum DeliveryStage
{
    /// <summary>The requested copy to the clipboard.</summary>
    Copy,

    /// <summary>Reading the target's caret context.</summary>
    ContextCapture,

    /// <summary>Committing the text to the target.</summary>
    Commit,
}

/// <summary>The family an unexpected exception belongs to: a fixed list, so it can be logged where a type name cannot.</summary>
/// <remarks>
/// LOW-CARDINALITY ON PURPOSE. The log's data dictionary allows enums and forbids free-form strings,
/// and a type name is a string; this is the type name's shape, coarse enough to be a category and
/// fine enough to say whether the fault was a null, a cast, a disposed object or Windows refusing.
/// </remarks>
public enum DeliveryFaultKind
{
    Other,
    InvalidOperation,
    ObjectDisposed,
    NullReference,
    InvalidCast,
    Argument,
    IndexOrKey,
    NotSupported,
    Com,
    Win32,
    UnauthorizedAccess,
    InputOutput,
    Timeout,
    Cancelled,
}

/// <summary>
/// A defect inside a delivery, described without the words: the stage it was thrown in, the family
/// of the exception and its type name. The transcript is never part of it.
/// </summary>
/// <remarks>
/// TYPED, AND CONTENT-FREE (plan-2 step 13). Every exception out of the adapter used to be relabelled
/// "accessibility unavailable", which named an environment for what was a bug - a disposed gate, a
/// null the adapter did not expect - so the diagnostics pointed at Windows and the defect went
/// unfound. The stage and the kind reach the log; the type name stays in the result for a test or a
/// debugger. None of it carries anything that was said.
/// </remarks>
public sealed record DeliveryFault(DeliveryStage Stage, DeliveryFaultKind Kind, string ExceptionType)
{
    /// <summary>Describes an exception by its stage and family; the message is never read.</summary>
    public static DeliveryFault Of(DeliveryStage stage, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new DeliveryFault(stage, KindOf(exception), exception.GetType().Name);
    }

    /// <summary>The family an exception belongs to. Subtypes are asked before their bases: a disposed object is not "invalid operation".</summary>
    public static DeliveryFaultKind KindOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            ObjectDisposedException => DeliveryFaultKind.ObjectDisposed,
            OperationCanceledException => DeliveryFaultKind.Cancelled,
            TimeoutException => DeliveryFaultKind.Timeout,
            InvalidOperationException => DeliveryFaultKind.InvalidOperation,
            NullReferenceException => DeliveryFaultKind.NullReference,
            InvalidCastException => DeliveryFaultKind.InvalidCast,
            IndexOutOfRangeException or KeyNotFoundException or ArgumentOutOfRangeException => DeliveryFaultKind.IndexOrKey,
            ArgumentException => DeliveryFaultKind.Argument,
            NotSupportedException or NotImplementedException => DeliveryFaultKind.NotSupported,
            UnauthorizedAccessException => DeliveryFaultKind.UnauthorizedAccess,
            System.ComponentModel.Win32Exception => DeliveryFaultKind.Win32,
            System.Runtime.InteropServices.COMException => DeliveryFaultKind.Com,
            IOException => DeliveryFaultKind.InputOutput,
            _ => DeliveryFaultKind.Other,
        };
    }
}

/// <summary>Which of the repair's two outputs a delivery wrote: the insertion adjusted to the caret's context, or the fallback payload.</summary>
/// <remarks>
/// NAMED FOR WHAT IT IS, NOT WHEN IT WAS WRITTEN (plan-2 step 14). The payload used when no context
/// is available - or when the target refuses and the words go to the clipboard - was called "legacy",
/// which said only that it came first. It is the fallback: the words as said, with the one trailing
/// space that lets a paste continue a sentence. The numeric values are unchanged (fallback 0, context
/// 1); nothing serialises this enum by name.
/// </remarks>
public enum CursorRepairDisposition
{
    /// <summary>The fallback payload was used: no caret context, a refused target, or a seam the repair does not touch.</summary>
    FallbackPayload,

    /// <summary>The insertion adjusted to the caret's context was used.</summary>
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

/// <param name="Text">The insertion adjusted to the caret's context: what a direct write or a paste puts at the caret.</param>
/// <param name="FallbackText">The fallback payload: what goes to the clipboard when the target refuses, or is pasted where no context could be read - the words as said, with a trailing space.</param>
public sealed record TextCommitRequest(
    ProcessedText Text,
    ProcessedText FallbackText,
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

/// <param name="Fault">The defect that stopped the delivery, for <see cref="TextDeliveryRefusalReason.DeliveryFaulted"/> and <see cref="TextDeliveryRefusalReason.DeliveryDisposed"/>; null otherwise.</param>
public sealed record DeliveryResult(
    DictationSessionId SessionId,
    bool Delivered,
    bool ClipboardFallback,
    TextDeliveryRoute Route = TextDeliveryRoute.None,
    TextDeliveryRefusalReason RefusalReason = TextDeliveryRefusalReason.None,
    CursorRepairDisposition RepairDisposition = CursorRepairDisposition.FallbackPayload,
    bool ClipboardRestored = false,
    DeliveryFault? Fault = null);

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
