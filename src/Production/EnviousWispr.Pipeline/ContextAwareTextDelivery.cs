using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Pipeline;

/// <summary>Delivers a dictation to the window it was started in, through the target adapter, and keeps the words if it cannot.</summary>
/// <remarks>
/// A FAILURE KEEPS ITS NAME (plan-2 step 13). The adapter names the accessibility failures it
/// expects - a window that vanished, a pattern the control does not support, a COM call Windows
/// refused - and answers them as results, never exceptions. What still throws out of it is one of
/// three things, each answered by its own name: the caller's cancellation
/// (<see cref="TextDeliveryRefusalReason.Cancelled"/>), a disposal under the delivery because the
/// app is leaving (<see cref="TextDeliveryRefusalReason.DeliveryDisposed"/>), or a defect
/// (<see cref="TextDeliveryRefusalReason.DeliveryFaulted"/>, with the stage and the exception's type
/// in <see cref="DeliveryResult.Fault"/>). None of them is relabelled "accessibility unavailable"
/// any more; that name pointed a bug at Windows. In every case the words are kept in
/// <see cref="RecoveryText"/>, nothing is retried, and nothing further is written to the target:
/// a commit that threw may or may not have landed, and a second attempt could double it.
/// </remarks>
public sealed class RecentDeliverySeamTracker
{
    internal static readonly TimeSpan RecentSeamWindow = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private RecentSeamRecord? _last;

    internal bool ShouldPrependLeadingSpace(
        TargetWindowId target,
        string? effectiveFocusId,
        CaretContext? context,
        string insertionText,
        bool languageUsesWordSpacing,
        DateTimeOffset now)
    {
        if (!languageUsesWordSpacing ||
            string.IsNullOrEmpty(insertionText) ||
            char.IsWhiteSpace(insertionText[0]) ||
            IsLeadingSpaceSuppressor(insertionText[0]))
        {
            return false;
        }

        if (context is { HasTextContext: true } ||
            context?.IsScreenDerived == true ||
            context?.IsUrlBarField == true)
        {
            return false;
        }

        RecentSeamRecord? prior;
        lock (_gate)
        {
            prior = _last;
        }

        if (prior is null || !MatchesTarget(prior, target, effectiveFocusId))
        {
            return false;
        }

        var elapsed = now - prior.DeliveredAtUtc;
        if (elapsed < TimeSpan.Zero || elapsed > RecentSeamWindow)
        {
            return false;
        }

        if (context?.BaselineValue is { Length: > 0 } baselineValue)
        {
            return !char.IsWhiteSpace(baselineValue[^1]);
        }

        return prior.DeliveredText.Length > 0 && !char.IsWhiteSpace(prior.DeliveredText[^1]);
    }

    internal void RecordSuccess(
        TargetWindowId target,
        string? effectiveFocusId,
        string deliveredText,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            _last = new RecentSeamRecord(target, effectiveFocusId, deliveredText, now);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _last = null;
        }
    }

    private static bool MatchesTarget(RecentSeamRecord prior, TargetWindowId target, string? effectiveFocusId) =>
        prior.Target.Value == target.Value &&
        prior.Target.ProcessId == target.ProcessId &&
        string.Equals(prior.EffectiveFocusId, effectiveFocusId, StringComparison.Ordinal);

    private static bool IsLeadingSpaceSuppressor(char ch) =>
        ch is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '"' or '\'' or '\u2019' or '\u201D';

    private sealed record RecentSeamRecord(
        TargetWindowId Target,
        string? EffectiveFocusId,
        string DeliveredText,
        DateTimeOffset DeliveredAtUtc);
}

public sealed class ContextAwareTextDelivery : ITextDelivery
{
    private readonly ITextTargetAdapter _targetAdapter;
    private readonly RecentDeliverySeamTracker _seamTracker;
    private readonly TimeProvider _clock;

    public ContextAwareTextDelivery(ITextTargetAdapter targetAdapter)
        : this(targetAdapter, seamTracker: null, TimeProvider.System)
    {
    }

    public ContextAwareTextDelivery(
        ITextTargetAdapter targetAdapter,
        RecentDeliverySeamTracker? seamTracker)
        : this(targetAdapter, seamTracker, TimeProvider.System)
    {
    }

    internal ContextAwareTextDelivery(
        ITextTargetAdapter targetAdapter,
        RecentDeliverySeamTracker? seamTracker,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(targetAdapter);
        ArgumentNullException.ThrowIfNull(clock);
        _targetAdapter = targetAdapter;
        _seamTracker = seamTracker ?? new RecentDeliverySeamTracker();
        _clock = clock;
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

        // A REQUESTED COPY NEEDS NO TARGET, so it is answered before the target is validated, its
        // caret read or the spacing repaired: none of that can refuse it, bring another window to the
        // front, or change the words - the clipboard gets what was said, "hello" and not "hello ".
        if (request.Options.CopyInsteadOfPaste)
        {
            TextCommitResult copied;
            try
            {
                copied = await _targetAdapter
                    .CopyOnlyAsync(request.Text, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNamed(exception))
            {
                return Stopped(request, DeliveryStage.Copy, exception, cancellationToken);
            }

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
        catch (Exception exception) when (IsNamed(exception))
        {
            return Stopped(request, DeliveryStage.ContextCapture, exception, cancellationToken);
        }

        // THE TARGET'S REFUSAL TRAVELS WITH THE COMMIT. An elevated, protected or changed target is
        // named here from the context capture and handed to the adapter, which then leaves the target
        // alone and puts the text on the clipboard - the same road every refused paste takes. (Copy-
        // only, the person's own choice, took its own early branch above; it does not come through here.)
        var forcedRefusal = RefusalFor(capture);
        // A TAKE THAT EXPANDED A SNIPPET IS NOT REPAIRED. Handing the repair no context is its whole
        // bypass - with none it returns the fallback payload - so it needs no branch of its own. The
        // capture still runs: its refusals (a protected field, an elevated or changed window) and the
        // target facts the commit routes on are about WHERE the words go, not what they say.
        var repair = CursorInsertionRepair.Apply(
            request.Text,
            capture.Status == TargetContextStatus.Available && !request.SnippetExpanded ? capture.Context : null,
            request.LanguageCode);
        var availableContext = capture.Status == TargetContextStatus.Available ? capture.Context : null;
        var effectiveFocusId = availableContext?.FocusedElementId ?? request.Target.FocusedElementId;
        var insertion = repair.Insertion;
        var languageUsesWordSpacing = repair.Fallback.Text.EndsWith(' ');
        if (!request.SnippetExpanded &&
            capture.Status == TargetContextStatus.Available &&
            _seamTracker.ShouldPrependLeadingSpace(
                request.Target,
                effectiveFocusId,
                availableContext,
                insertion.Text,
                languageUsesWordSpacing,
                _clock.GetUtcNow()))
        {
            insertion = insertion with { Text = " " + insertion.Text };
        }

        var targetKind = capture.Context?.TargetKind ?? TextTargetKind.Unknown;
        TextCommitResult commit;
        try
        {
            commit = await _targetAdapter.CommitAsync(
                new TextCommitRequest(
                    insertion,
                    repair.Fallback,
                    request.Target,
                    availableContext,
                    targetKind,
                    request.Options,
                    forcedRefusal),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNamed(exception))
        {
            _seamTracker.Clear();
            return Stopped(request, DeliveryStage.Commit, exception, cancellationToken, repair.Disposition);
        }

        if (commit.Delivered &&
            !commit.ClipboardFallback &&
            commit.Route is TextDeliveryRoute.ClipboardPaste or TextDeliveryRoute.UiAutomationValue)
        {
            _seamTracker.RecordSuccess(request.Target, effectiveFocusId, insertion.Text, _clock.GetUtcNow());
        }
        else if (!commit.Delivered)
        {
            _seamTracker.Clear();
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
            commit.ClipboardRestored,
            ClipboardUncertain: commit.ClipboardUncertain,
            ArrivalStatus: commit.ArrivalStatus,
            ArrivalElapsedMilliseconds: commit.ArrivalElapsedMilliseconds,
            DeliveredText: commit.Delivered ? insertion.Text : null);
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

    /// <summary>Whether an exception out of the adapter is one this delivery answers by name; the process-ending ones are left alone.</summary>
    private static bool IsNamed(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException);

    /// <summary>The result for a delivery an exception stopped: the words kept, the failure named, nothing retried.</summary>
    private static DeliveryResult Stopped(
        TextDeliveryRequest request,
        DeliveryStage stage,
        Exception exception,
        CancellationToken cancellationToken,
        CursorRepairDisposition disposition = CursorRepairDisposition.FallbackPayload)
    {
        // THE CALLER'S CANCELLATION IS CANCELLATION; ANY OTHER OperationCanceledException IS A DEFECT,
        // because nothing else was asked to stop. A disposal is the app leaving. Everything else is a
        // bug, named by its stage and type so it can be found without the words.
        var (reason, fault) = exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested =>
                (TextDeliveryRefusalReason.Cancelled, (DeliveryFault?)null),
            ObjectDisposedException => (TextDeliveryRefusalReason.DeliveryDisposed, DeliveryFault.Of(stage, exception)),
            _ => (TextDeliveryRefusalReason.DeliveryFaulted, DeliveryFault.Of(stage, exception)),
        };
        return new DeliveryResult(
            request.Text.SessionId,
            Delivered: false,
            ClipboardFallback: false,
            RefusalReason: reason,
            RepairDisposition: disposition,
            Fault: fault);
    }
}
