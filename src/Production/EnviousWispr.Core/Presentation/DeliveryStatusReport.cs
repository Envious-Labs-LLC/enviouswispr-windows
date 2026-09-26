using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Core.Presentation;

/// <summary>The one sentence the user reads about where their words went.</summary>
/// <remarks>
/// MOVED OUT OF THE APP SO A TEST CAN READ IT. Every sentence here is user-facing copy, and while it
/// lived beside the window it was reachable only by a gate that read the source as text. That is the
/// weaker instrument: it can see that an arm was written and cannot see what the arm ANSWERS. The
/// defect that caused the move is exactly the kind text cannot catch - a copy the user asked for
/// fell through to "Pasted safely", which named a paste that never happened, and every arm involved
/// was present and correctly spelled.
///
/// ORDER IS THE WHOLE BEHAVIOUR. A requested copy IS delivered, so it reaches the delivered arms and
/// has to be answered before the two that talk about pasting.
/// </remarks>
public static class DeliveryStatusReport
{
    /// <summary>Turns a delivery into the sentence shown on the pill.</summary>
    public static DictationStatus For(DeliveryResult result) => result switch
    {
        // A CLIPBOARD THAT COULD NOT BE GIVEN BACK IS SAID FIRST (#242): the paste borrowed it, the restore
        // failed, and it may now hold these words instead of what the person had copied. Every other arm
        // would read as a clean finish. A newer write left alone is not this - that clipboard is theirs.
        { Delivered: true, ClipboardUncertain: true } =>
            DictationStatus.Warning("Pasted, but your clipboard could not be restored"),
        { ClipboardUncertain: true } =>
            DictationStatus.Warning("Clipboard unavailable and could not be restored. Text is held safely in memory"),
        { Delivered: true, Route: TextDeliveryRoute.UiAutomationValue } =>
            DictationStatus.Success("Inserted safely in the app you started in"),

        // BEFORE THE TWO PASTE SENTENCES, because a requested copy is delivered and would otherwise
        // fall into them and tell the user it pasted somewhere it never went.
        { Delivered: true, Route: TextDeliveryRoute.ClipboardOnly } =>
            DictationStatus.Success("Copied to your clipboard"),
        { Delivered: true, ClipboardRestored: true } =>
            DictationStatus.Success("Pasted safely and restored your clipboard"),
        { Delivered: true } => DictationStatus.Success("Pasted safely"),
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.ProtectedField } =>
            DictationStatus.Warning("Protected field: copied only. Paste manually if intended"),
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.ElevatedTarget } =>
            DictationStatus.Warning("Windows blocked the elevated app, so the text was copied only"),
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.TargetChanged } =>
            DictationStatus.Warning("The target changed, so the text was copied only to protect it"),
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.UnsafeMultilineTarget } =>
            DictationStatus.Warning("Terminal line break refused, so the text was copied only"),
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.UnsupportedTarget } =>
            DictationStatus.Warning("Automatic paste is unsafe here, so the text was copied only"),
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.InputStateUnsafe } =>
            DictationStatus.Warning("A key was held, so the text was copied only. Paste manually"),
        { ClipboardFallback: true, RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable } =>
            DictationStatus.Warning("Windows accessibility did not answer, so the text was copied only. Press Ctrl+V"),
        { ClipboardFallback: true } => DictationStatus.Warning("Copied. Press Ctrl+V"),
        { RefusalReason: TextDeliveryRefusalReason.ClipboardUnavailable } =>
            DictationStatus.Warning("Clipboard unavailable. Text is held safely in memory"),
        { RefusalReason: TextDeliveryRefusalReason.DirectWriteUnverified } =>
            DictationStatus.Warning("Insertion could not be verified. Text is held safely in memory"),
        // EACH WAY THE WORDS DID NOT LAND HAS ITS OWN SENTENCE (plan-2 step 13), and every one ends
        // the same way: the text is held. Accessibility that did not answer, a delivery cancelled, a
        // delivery whose Windows link was already closed, a fault - the person reads which, and the
        // log carries the same name.
        { RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable } =>
            DictationStatus.Warning("Windows accessibility did not answer. Text is held safely in memory"),
        { RefusalReason: TextDeliveryRefusalReason.Cancelled } =>
            DictationStatus.Warning("Text delivery was cancelled. Text is held safely in memory"),
        { RefusalReason: TextDeliveryRefusalReason.DeliveryDisposed } =>
            DictationStatus.Warning("Text delivery was no longer available. Text is held safely in memory"),
        { RefusalReason: TextDeliveryRefusalReason.DeliveryFaulted } =>
            DictationStatus.Error("Text delivery failed unexpectedly. Text is held safely in memory"),
        _ => DictationStatus.Error("Text delivery stopped safely"),
    };
}
