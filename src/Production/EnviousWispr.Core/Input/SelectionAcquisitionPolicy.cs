namespace EnviousWispr.Core.Input;

/// <summary>How to get hold of the text a user has selected in another app.</summary>
public enum SelectionAcquisition
{
    /// <summary>The app told us what is selected. Use it.</summary>
    UsePublished,

    /// <summary>
    /// The app publishes nothing. Ask for it with a synthetic Copy, and put the clipboard back.
    /// </summary>
    SyntheticCopy,

    /// <summary>Do neither. Tell the user instead.</summary>
    Refuse,
}

/// <summary>
/// Decides whether it is safe to take the user's clipboard for a moment to read their selection.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. Quick Add teaches the app a word from whatever the user has selected. Many apps
/// publish their selection and it can simply be read. Many do not - most terminals, some editors,
/// anything drawing its own text - and there Quick Add currently does nothing but ask the user to
/// try again, which they have no way to act on because the problem is not something they did.
///
/// A synthetic Copy gets the selection out of any app that supports Ctrl+C, which is nearly all of
/// them. The cost is that it BORROWS THE USER'S CLIPBOARD, and borrowing it at the wrong moment is
/// worse than the feature is good: someone who copied a paragraph, dictated, and found their
/// paragraph gone would not connect it to a word they added.
///
/// SO THE REFUSALS ARE THE PRODUCT, and every one of them is a moment when the clipboard is not
/// ours to borrow:
///  - a dictation is running, so the keys we would synthesise are being watched by our own hook;
///  - a delivery is in flight, so the clipboard already holds text on its way into a document;
///  - there is no valid target, so the Copy would go somewhere we cannot name.
///
/// THE DICTATION REFUSAL IS A SECOND LINE RATHER THAN THE LIVE ONE, and that is worth stating so
/// nobody reads it as the working guard. The hotkey hook already refuses to raise a Quick Add
/// request while a recording is active, so from the only entry point that exists today this branch
/// cannot be reached - measured on the running app, where firing the shortcut mid-recording
/// produced no Quick Add event AT ALL rather than a refusal.
///
/// It is kept because it costs one boolean and because the hook's refusal is a different mechanism
/// in a different layer: a second entry point - a menu item, a tray command - would arrive here
/// without passing the hook at all. The delivery refusal beside it IS reachable today, because a
/// delivery runs after the recording has ended and the hook has stood down.
///
/// REFUSING IS ALWAYS RECOVERABLE and taking the clipboard wrongly is not, which is why anything
/// unclear resolves to Refuse rather than to an attempt.
/// </remarks>
public static class SelectionAcquisitionPolicy
{
    /// <summary>
    /// How to read the selection right now.
    /// </summary>
    /// <param name="hasValidTarget">Whether a real foreground window was captured.</param>
    /// <param name="publishedSelection">
    /// What the app said is selected, or null or blank when it says nothing.
    /// </param>
    /// <param name="isDictationRunning">True while a recording or its processing is in flight.</param>
    /// <param name="isDeliveryInFlight">True while text is on its way into a document.</param>
    public static SelectionAcquisition Decide(
        bool hasValidTarget,
        string? publishedSelection,
        bool isDictationRunning,
        bool isDeliveryInFlight)
    {
        if (!hasValidTarget)
        {
            return SelectionAcquisition.Refuse;
        }

        // A PUBLISHED SELECTION IS SAFE IN EVERY STATE, so it is answered before the state-based
        // refusals below and is deliberately NOT subject to them. Reading what an app already
        // offers borrows nothing, interrupts nothing, and cannot collide with a delivery - so
        // refusing it while a dictation runs would disable Quick Add for no reason.
        //
        // The refusals below guard one specific ACT: taking the clipboard. The target check above
        // is different again - without a target there is nothing to read by either route.
        //
        // Stated because the first version of this comment claimed the refusals came first, which
        // was true of neither the code nor the intent. A comment that argues for an order the code
        // does not follow is worse than none: it tells the next person the shape is deliberate.
        if (!string.IsNullOrWhiteSpace(publishedSelection))
        {
            return SelectionAcquisition.UsePublished;
        }

        if (isDictationRunning || isDeliveryInFlight)
        {
            return SelectionAcquisition.Refuse;
        }

        return SelectionAcquisition.SyntheticCopy;
    }
}
