using EnviousWispr.Core.Dictation;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Pipeline;

public sealed class ContextAwareTextDelivery : ITextDelivery
{
    private readonly ITextTargetAdapter _targetAdapter;

    public ContextAwareTextDelivery(ITextTargetAdapter targetAdapter)
    {
        ArgumentNullException.ThrowIfNull(targetAdapter);
        _targetAdapter = targetAdapter;
    }

    public ProcessedText? RecoveryText { get; private set; }

    public async Task<DeliveryResult> DeliverAsync(
        TextDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text.Text))
        {
            return new DeliveryResult(
                request.Text.SessionId,
                Delivered: true,
                ClipboardFallback: false);
        }

        RecoveryText = request.Text;

        // ANSWERED BEFORE ANYTHING IS TOUCHED. Putting this after the target check meant a choice to
        // copy still validated a window, still read its caret, and still repaired the spacing for a
        // place the text was never going - so an unavailable target could refuse a copy that needed
        // no target, the old window could be brought back to the front on the way, and what landed
        // on the clipboard was the repaired text rather than the words that were said. "hello"
        // arrived as "hello ".
        if (request.Options.CopyInsteadOfPaste)
        {
            var copied = await _targetAdapter
                .CopyOnlyAsync(request.Text, cancellationToken)
                .ConfigureAwait(false);
            if (copied.Delivered || copied.ClipboardFallback)
            {
                RecoveryText = null;
            }

            // THE ROUTE TRAVELS WITH THE RESULT, because the route is the only thing that says
            // WHERE the text went. Dropping it left a requested copy indistinguishable from an
            // ordinary paste at the one place that speaks to the user, so the notice read "Pasted
            // safely" over a delivery that pasted nothing.
            return new DeliveryResult(
                request.Text.SessionId,
                copied.Delivered,
                copied.ClipboardFallback,
                copied.Route,
                copied.RefusalReason);
        }

        if (!request.Target.IsValid)
        {
            return new DeliveryResult(
                request.Text.SessionId,
                Delivered: false,
                ClipboardFallback: false,
                RefusalReason: TextDeliveryRefusalReason.TargetUnavailable);
        }

        TargetContextResult capture;
        try
        {
            capture = await _targetAdapter.CaptureContextAsync(
                request.Target,
                request.Options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DeliveryResult(
                request.Text.SessionId,
                Delivered: false,
                ClipboardFallback: false,
                RefusalReason: TextDeliveryRefusalReason.Cancelled);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new DeliveryResult(
                request.Text.SessionId,
                Delivered: false,
                ClipboardFallback: false,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable);
        }

        // THE TARGET'S REFUSAL TRAVELS WITH THE COMMIT. An elevated, protected or changed target is
        // named here from the context capture and handed to the adapter, which then leaves the target
        // alone and puts the text on the clipboard - the same road every refused paste takes. (Copy-
        // only, the person's own choice, took its own early branch above; it does not come through here.)
        var forcedRefusal = RefusalFor(capture);
        var repair = CursorInsertionRepair.Apply(
            request.Text,
            capture.Status == TargetContextStatus.Available ? capture.Context : null,
            request.LanguageCode);
        var targetKind = capture.Context?.TargetKind ?? TextTargetKind.Unknown;
        TextCommitResult commit;
        try
        {
            commit = await _targetAdapter.CommitAsync(
                new TextCommitRequest(
                    repair.Output,
                    repair.LegacyOutput,
                    request.Target,
                    capture.Status == TargetContextStatus.Available ? capture.Context : null,
                    targetKind,
                    request.Options,
                    forcedRefusal),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DeliveryResult(
                request.Text.SessionId,
                Delivered: false,
                ClipboardFallback: false,
                RefusalReason: TextDeliveryRefusalReason.Cancelled,
                RepairDisposition: repair.Disposition);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new DeliveryResult(
                request.Text.SessionId,
                Delivered: false,
                ClipboardFallback: false,
                RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable,
                RepairDisposition: repair.Disposition);
        }

        if (commit.Delivered || commit.ClipboardFallback)
        {
            RecoveryText = null;
        }

        return new DeliveryResult(
            request.Text.SessionId,
            commit.Delivered,
            commit.ClipboardFallback,
            commit.Route,
            commit.RefusalReason,
            repair.Disposition,
            commit.ClipboardRestored);
    }

    private static TextDeliveryRefusalReason RefusalFor(TargetContextResult capture) =>
        capture.RefusalReason != TextDeliveryRefusalReason.None
            ? capture.RefusalReason
            : capture.Status switch
            {
                TargetContextStatus.Available => TextDeliveryRefusalReason.None,
                TargetContextStatus.TargetUnavailable => TextDeliveryRefusalReason.TargetUnavailable,
                TargetContextStatus.TargetChanged => TextDeliveryRefusalReason.TargetChanged,
                TargetContextStatus.Protected => TextDeliveryRefusalReason.ProtectedField,
                TargetContextStatus.Elevated => TextDeliveryRefusalReason.ElevatedTarget,
                _ => TextDeliveryRefusalReason.AccessibilityUnavailable,
            };

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException);
}
