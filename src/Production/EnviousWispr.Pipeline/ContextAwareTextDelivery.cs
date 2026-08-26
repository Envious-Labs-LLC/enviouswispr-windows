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
