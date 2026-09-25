using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;

namespace EnviousWispr.Pipeline;

public enum LastDictationAction
{
    Paste,
    Copy,
}

/// <summary>Where a reuse was asked for. The shortcut arrives with #206 part 3.</summary>
public enum LastDictationSource
{
    Menu,
    Shortcut,
}

/// <summary>How one reuse ended - a closed set, so the log can say it without saying the words.</summary>
public enum LastDictationOutcome
{
    /// <summary>The words were written into the target by one of the delivery's routes.</summary>
    Pasted,

    /// <summary>A requested copy: the words are on the clipboard.</summary>
    Copied,

    /// <summary>The target refused the paste and the clipboard caught the words, as a dictation's would be.</summary>
    KeptOnClipboard,

    /// <summary>No entry may be reused: history is empty or off, or the newest reusable one was deleted.</summary>
    NothingToReuse,

    /// <summary>A dictation is recording or being delivered; it owns the target and the clipboard next.</summary>
    DictationInProgress,

    /// <summary>Another reuse is still running. Two at once would race for one clipboard.</summary>
    Busy,

    /// <summary>Nothing was in front to paste into, or it has gone.</summary>
    NoTarget,

    /// <summary>The window in front is EnviousWispr's own; pasting a dictation into its own window is never meant.</summary>
    OwnWindow,

    /// <summary>The delivery wrote nothing and the clipboard did not catch the words either.</summary>
    Failed,
}

public sealed record LastDictationReuseResult(
    LastDictationAction Action,
    LastDictationSource Source,
    LastDictationOutcome Outcome,
    DeliveryResult? Delivery = null);

/// <summary>Everything the reuse touches, handed in so a test drives it without a desktop.</summary>
/// <param name="LoadHistory">The store's current entries, newest first.</param>
/// <param name="IsDictationActive">True while a take is recording, finalising or being delivered.</param>
/// <param name="IsOwnWindow">Whether a target is one of EnviousWispr's own windows.</param>
/// <param name="Delivery">
/// A delivery of its OWN over the shared adapter. `ContextAwareTextDelivery` keeps one recovery slot, and a
/// reuse sharing the dictation's instance would overwrite a take's recovery text with an old dictation.
/// </param>
/// <param name="DeliveryOptions">The dictation's delivery options, read at each use (the clipboard restore).</param>
/// <param name="Reacquire">
/// Brings back a target remembered without its focused field and names that field, or null. The delivery refuses a
/// target whose field it was not told - that id is how it notices the person moved - so a window-only target is
/// completed here rather than the check being loosened.
/// </param>
public sealed record LastDictationEnvironment(
    Func<CancellationToken, Task<IReadOnlyList<DictationHistoryEntry>>> LoadHistory,
    Func<DateTimeOffset> Now,
    Func<bool> IsDictationActive,
    Func<TargetWindowId, bool> IsOwnWindow,
    ITextDelivery Delivery,
    Func<TextDeliveryOptions> DeliveryOptions,
    Func<TargetWindowId, CancellationToken, Task<TargetWindowId?>> Reacquire);

/// <summary>Paste or copy the last dictation again: the one owner behind the tray items and, later, two shortcuts.</summary>
/// <remarks>
/// Ported from macOS `LastDictationAction` (#3106). Where Windows differs, and why:
/// <list type="bullet">
/// <item>PASTE GOES THROUGH THE DICTATION'S OWN DELIVERY - three routes, caret repair, the clipboard snapshot
/// and restore, and the rule that an unverified write is never followed by a paste - not a bare clipboard
/// and Ctrl+V. The macOS action posts Cmd+V itself because macOS has no such adapter to borrow; this one
/// would otherwise be the only text writer in the product that could insert twice.</item>
/// <item>THE TARGET IS NAMED BY THE CALLER, never read here. The tray takes the window the person was in
/// before they reached for the tray (clicking the notification area moves the foreground to the taskbar);
/// a shortcut takes the window in front at its press. A target sampled when the paste finally runs could
/// be anything, including this app.</item>
/// <item>COPY NEEDS NO TARGET and no activation: it puts the words as said on the clipboard.</item>
/// </list>
/// A dictation in flight owns the clipboard's next write, so both refuse while one runs, checked before and
/// again after the only wait (reading history). One reuse at a time: a second arriving while the first is
/// still delivering is refused rather than queued, since a queued paste lands after the person has moved on.
/// </remarks>
public sealed class LastDictationReuse
{
    private readonly LastDictationEnvironment _environment;
    /// <summary>1 while a reuse is running. A flag, not a lock: a second reuse is refused, never waited for.</summary>
    private int _running;

    public LastDictationReuse(LastDictationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>The entry the menu should name right now, or null. Same rule as the action.</summary>
    public async Task<DictationHistoryEntry?> PeekAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _environment.LoadHistory(cancellationToken).ConfigureAwait(false);
        return LastDictation.Pick(entries, _environment.Now());
    }

    public Task<LastDictationReuseResult> PasteAsync(
        TargetWindowId? target,
        LastDictationSource source,
        CancellationToken cancellationToken = default) =>
        RunAsync(LastDictationAction.Paste, source, target, cancellationToken);

    public Task<LastDictationReuseResult> CopyAsync(
        LastDictationSource source,
        CancellationToken cancellationToken = default) =>
        RunAsync(LastDictationAction.Copy, source, target: null, cancellationToken);

    private async Task<LastDictationReuseResult> RunAsync(
        LastDictationAction action,
        LastDictationSource source,
        TargetWindowId? target,
        CancellationToken cancellationToken)
    {
        LastDictationReuseResult Ended(LastDictationOutcome outcome, DeliveryResult? delivery = null) =>
            new(action, source, outcome, delivery);

        if (_environment.IsDictationActive())
        {
            return Ended(LastDictationOutcome.DictationInProgress);
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Ended(LastDictationOutcome.Busy);
        }

        try
        {
            // The target before the history read, for the same reason macOS orders it: nothing about the
            // words can make our own window or a vanished one pasteable.
            if (action == LastDictationAction.Paste)
            {
                if (target is null)
                {
                    return Ended(LastDictationOutcome.NoTarget);
                }

                if (_environment.IsOwnWindow(target.Value))
                {
                    return Ended(LastDictationOutcome.OwnWindow);
                }
            }

            var entries = await _environment.LoadHistory(cancellationToken).ConfigureAwait(false);
            var entry = LastDictation.Pick(entries, _environment.Now());
            if (entry is null)
            {
                return Ended(LastDictationOutcome.NothingToReuse);
            }

            // Re-checked after the wait: a key pressed while history loaded has started a take that now
            // owns the clipboard and the target.
            if (_environment.IsDictationActive())
            {
                return Ended(LastDictationOutcome.DictationInProgress);
            }

            var aimed = target;
            if (action == LastDictationAction.Paste && aimed is { FocusedElementId: null } windowOnly)
            {
                aimed = await _environment.Reacquire(windowOnly, cancellationToken).ConfigureAwait(false);
                if (aimed is null)
                {
                    return Ended(LastDictationOutcome.NoTarget);
                }

                // And again after this wait, the last before the write.
                if (_environment.IsDictationActive())
                {
                    return Ended(LastDictationOutcome.DictationInProgress);
                }
            }

            var options = _environment.DeliveryOptions() with
            {
                // The person asked for exactly one of the two; the dictation's own copy-instead setting
                // does not turn a paste they chose into a copy, or the reverse.
                CopyInsteadOfPaste = action == LastDictationAction.Copy,
            };
            var result = await _environment.Delivery.DeliverAsync(
                new TextDeliveryRequest(
                    new ProcessedText(DictationSessionId.Create(), entry.Text),
                    aimed ?? default,
                    LanguageCode: null,
                    options),
                cancellationToken).ConfigureAwait(false);
            return Ended(Classify(action, result), result);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static LastDictationOutcome Classify(LastDictationAction action, DeliveryResult result) =>
        (action, result) switch
        {
            (LastDictationAction.Copy, { Delivered: true }) => LastDictationOutcome.Copied,
            (LastDictationAction.Paste, { ClipboardFallback: true }) => LastDictationOutcome.KeptOnClipboard,
            (LastDictationAction.Paste, { Delivered: true }) => LastDictationOutcome.Pasted,
            _ => LastDictationOutcome.Failed,
        };
}

/// <summary>The log's name for each ending, one to one, so no two endings share a line.</summary>
public static class LastDictationDiagnostics
{
    public static EnviousWispr.Core.Diagnostics.AppEventCode EventFor(LastDictationOutcome outcome) => outcome switch
    {
        LastDictationOutcome.Pasted => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationPasted,
        LastDictationOutcome.Copied => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationCopied,
        LastDictationOutcome.KeptOnClipboard => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationKeptOnClipboard,
        LastDictationOutcome.NothingToReuse => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationNothingToReuse,
        LastDictationOutcome.DictationInProgress => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationDeclinedDictationInProgress,
        LastDictationOutcome.Busy => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationDeclinedBusy,
        LastDictationOutcome.NoTarget => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationDeclinedNoTarget,
        LastDictationOutcome.OwnWindow => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationDeclinedOwnWindow,
        LastDictationOutcome.Failed => EnviousWispr.Core.Diagnostics.AppEventCode.LastDictationReuseFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

/// <summary>The log's error code for each way a delivery did not land, shared by a dictation and a reuse.</summary>
/// <remarks>
/// EACH WAY THE WORDS DID NOT LAND KEEPS ITS NAME IN THE LOG (plan-2 step 13): an accessibility failure
/// Windows reported, a direct write that could not be verified, the caller's cancellation, a disposal under
/// the delivery, a defect. They used to share "unsupported target", which is a policy refusal and none of
/// them. Lifted out of the dictation's effects so the reuse (#206) logs the same names for the same causes.
/// </remarks>
public static class DeliveryErrorCodes
{
    public static EnviousWispr.Core.Errors.AppErrorCode? For(TextDeliveryRefusalReason reason) => reason switch
    {
        TextDeliveryRefusalReason.None => null,
        TextDeliveryRefusalReason.TargetUnavailable or
            TextDeliveryRefusalReason.TargetChanged => EnviousWispr.Core.Errors.AppErrorCode.DeliveryTargetChanged,
        TextDeliveryRefusalReason.ProtectedField => EnviousWispr.Core.Errors.AppErrorCode.DeliveryProtectedField,
        TextDeliveryRefusalReason.ElevatedTarget => EnviousWispr.Core.Errors.AppErrorCode.DeliveryElevatedTarget,
        TextDeliveryRefusalReason.ClipboardUnavailable => EnviousWispr.Core.Errors.AppErrorCode.DeliveryClipboardUnavailable,
        TextDeliveryRefusalReason.InputStateUnsafe or
            TextDeliveryRefusalReason.InputBlocked => EnviousWispr.Core.Errors.AppErrorCode.DeliveryInputBlocked,
        TextDeliveryRefusalReason.UnsupportedTarget or
            TextDeliveryRefusalReason.UnsafeMultilineTarget => EnviousWispr.Core.Errors.AppErrorCode.DeliveryUnsupportedTarget,
        TextDeliveryRefusalReason.AccessibilityUnavailable => EnviousWispr.Core.Errors.AppErrorCode.DeliveryAccessibilityUnavailable,
        TextDeliveryRefusalReason.DirectWriteUnverified => EnviousWispr.Core.Errors.AppErrorCode.DeliveryUnverified,
        TextDeliveryRefusalReason.Cancelled => EnviousWispr.Core.Errors.AppErrorCode.DeliveryCancelled,
        TextDeliveryRefusalReason.DeliveryDisposed => EnviousWispr.Core.Errors.AppErrorCode.DeliveryDisposed,
        TextDeliveryRefusalReason.DeliveryFaulted => EnviousWispr.Core.Errors.AppErrorCode.DeliveryFaulted,
        _ => EnviousWispr.Core.Errors.AppErrorCode.DeliveryFaulted,
    };
}
