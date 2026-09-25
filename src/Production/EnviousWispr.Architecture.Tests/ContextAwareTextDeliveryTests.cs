using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Input;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

public sealed class ContextAwareTextDeliveryTests
{
    private static readonly DictationSessionId SessionId = new(Guid.Parse(
        "557ec3af-65f9-4f87-894c-8a8204879a08"));
    private static readonly TargetWindowId Target = new(42, 7, "1.2.3");

    [Fact]
    public async Task AppliesRepairBeforeCommit()
    {
        var adapter = new FakeTargetAdapter(AvailableContext(left: "hello,", right: "again"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("world"));

        Assert.Equal(" world ", adapter.LastCommit?.Text.Text);
        Assert.Equal("world ", adapter.LastCommit?.FallbackText.Text);
        Assert.Equal(CursorRepairDisposition.ContextApplied, result.RepairDisposition);
        Assert.True(result.Delivered);
        Assert.Null(delivery.RecoveryText);
    }

    /// <summary>
    /// A TAKE THAT EXPANDED A SNIPPET IS NOT REPAIRED: the words are committed as the fallback payload,
    /// with no seam space added before them and nothing re-cased, while the target is still read and
    /// handed to the commit, which routes on it. The twin above, with the same caret, is repaired.
    /// </summary>
    [Fact]
    public async Task ASnippetTakeSkipsTheCursorRepairButTheTargetIsStillRead()
    {
        var adapter = new FakeTargetAdapter(AvailableContext(left: "hello,", right: "again"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("Sam.Smith@Example.com") with { SnippetExpanded = true });

        Assert.Equal("Sam.Smith@Example.com ", adapter.LastCommit?.Text.Text);
        Assert.Equal("Sam.Smith@Example.com ", adapter.LastCommit?.FallbackText.Text);
        Assert.Equal(CursorRepairDisposition.FallbackPayload, result.RepairDisposition);
        Assert.Equal(1, adapter.Captures);
        Assert.NotNull(adapter.LastCommit?.ExpectedContext);
        Assert.Equal(TextTargetKind.StandardEdit, adapter.LastCommit?.TargetKind);
        Assert.True(result.Delivered);
    }

    [Fact]
    public async Task ProtectedFieldForcesClipboardOnlyWithoutReadingContext()
    {
        var adapter = new FakeTargetAdapter(new TargetContextResult(
            TargetContextStatus.Protected,
            RefusalReason: TextDeliveryRefusalReason.ProtectedField));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("secret"));

        Assert.Equal(
            TextDeliveryRefusalReason.ProtectedField,
            adapter.LastCommit?.ForcedRefusalReason);
        Assert.Equal(TextDeliveryRoute.ClipboardOnly, result.Route);
        Assert.True(result.ClipboardFallback);
        Assert.False(result.Delivered);
    }

    [Fact]
    public async Task ChangedTargetForcesClipboardOnly()
    {
        var adapter = new FakeTargetAdapter(new TargetContextResult(
            TargetContextStatus.TargetChanged,
            RefusalReason: TextDeliveryRefusalReason.TargetChanged));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("private text"));

        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, result.RefusalReason);
        Assert.Equal(CursorRepairDisposition.FallbackPayload, result.RepairDisposition);
    }

    [Fact]
    public async Task InvalidFrozenTargetNeverCallsTheAdapter()
    {
        var adapter = new FakeTargetAdapter(AvailableContext(left: "", right: ""));
        var delivery = new ContextAwareTextDelivery(adapter);
        var request = new TextDeliveryRequest(
            new ProcessedText(SessionId, "hello"),
            default,
            "en",
            TextDeliveryOptions.Default,
            SnippetExpanded: false);

        var result = await delivery.DeliverAsync(request);

        Assert.Null(adapter.LastCommit);
        Assert.Equal(TextDeliveryRefusalReason.TargetUnavailable, result.RefusalReason);
        Assert.Equal("hello", delivery.RecoveryText?.Text);
    }

    [Fact]
    public async Task ClipboardFailureRetainsTheLastValidTextInMemory()
    {
        var adapter = new FakeTargetAdapter(
            AvailableContext(left: "", right: ""),
            commitResult: new TextCommitResult(
                TextDeliveryRoute.None,
                Delivered: false,
                ClipboardFallback: false,
                ClipboardRestored: false,
                TextDeliveryRefusalReason.ClipboardUnavailable));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("recover me"));

        Assert.False(result.Delivered);
        Assert.Equal("recover me", delivery.RecoveryText?.Text);
    }

    [Theory]
    [InlineData(DeliveryStage.Copy)]
    [InlineData(DeliveryStage.ContextCapture)]
    [InlineData(DeliveryStage.Commit)]
    public async Task UnexpectedDeliveryFailureRetainsRecoveryAndCategory(DeliveryStage stage)
    {
        // A DEFECT IS A DEFECT, WHEREVER IT IS THROWN. An InvalidOperationException out of the adapter
        // used to come back as "accessibility unavailable" - Windows blamed for a bug. It is named
        // now: DeliveryFaulted, with the stage and the exception's type and not a word of the text,
        // the words kept for recovery, nothing on the clipboard, and no second attempt at the target -
        // a commit that threw may have landed, and a retry could double it.
        var defect = new InvalidOperationException("synthetic defect - transcript must not travel");
        var adapter = new FakeTargetAdapter(
            AvailableContext(left: "", right: ""),
            captureException: stage == DeliveryStage.ContextCapture ? defect : null,
            commitException: stage == DeliveryStage.Commit ? defect : null,
            copyException: stage == DeliveryStage.Copy ? defect : null);
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(stage == DeliveryStage.Copy ? CopyRequest("recover me") : Request("recover me"));

        Assert.Equal(TextDeliveryRefusalReason.DeliveryFaulted, result.RefusalReason);
        Assert.Equal(new DeliveryFault(stage, DeliveryFaultKind.InvalidOperation, nameof(InvalidOperationException)), result.Fault);
        Assert.DoesNotContain("recover me", result.Fault!.ExceptionType, StringComparison.Ordinal);
        Assert.False(result.Delivered);
        Assert.False(result.ClipboardFallback);
        Assert.Equal("recover me", delivery.RecoveryText?.Text);
        Assert.Equal(stage == DeliveryStage.Commit ? 1 : 0, adapter.Commits);
        Assert.Equal("Text delivery failed unexpectedly. Text is held safely in memory", DeliveryStatusReport.For(result).Text);
    }

    [Fact]
    public async Task DisposedAdapterIsNotAccessibilityUnavailable()
    {
        // THE APP LEAVING IS NOT WINDOWS FAILING. A delivery that finds the adapter's gate disposed
        // under it is the exit's doing; it is named as such, the words are kept, and the log will not
        // send anyone to look at accessibility settings.
        var adapter = new FakeTargetAdapter(
            AvailableContext(left: "", right: ""),
            captureException: new ObjectDisposedException("automation gate"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("recover me"));

        Assert.Equal(TextDeliveryRefusalReason.DeliveryDisposed, result.RefusalReason);
        Assert.Equal(new DeliveryFault(DeliveryStage.ContextCapture, DeliveryFaultKind.ObjectDisposed, nameof(ObjectDisposedException)), result.Fault);
        Assert.NotEqual(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.Equal("recover me", delivery.RecoveryText?.Text);
        Assert.Equal(0, adapter.Commits);
    }

    [Fact]
    public async Task CancellationRemainsCancellation()
    {
        // THE CALLER'S CANCELLATION IS THE ONE THING THAT IS NOT A FAULT: it was asked for. An
        // OperationCanceledException nobody asked for is a defect like any other, because the only
        // token that can cancel a delivery is the caller's.
        using var cancellation = new CancellationTokenSource();
        var asked = new FakeTargetAdapter(
            AvailableContext(left: "", right: ""),
            commitException: new OperationCanceledException(cancellation.Token));
        var delivery = new ContextAwareTextDelivery(asked);
        cancellation.Cancel();

        var cancelled = await delivery.DeliverAsync(Request("recover me"), cancellation.Token);

        Assert.Equal(TextDeliveryRefusalReason.Cancelled, cancelled.RefusalReason);
        Assert.Null(cancelled.Fault);
        Assert.Equal("recover me", delivery.RecoveryText?.Text);

        var unasked = new FakeTargetAdapter(
            AvailableContext(left: "", right: ""),
            commitException: new TaskCanceledException("nobody asked"));
        var faulted = await new ContextAwareTextDelivery(unasked).DeliverAsync(Request("recover me"));

        Assert.Equal(TextDeliveryRefusalReason.DeliveryFaulted, faulted.RefusalReason);
        Assert.Equal(new DeliveryFault(DeliveryStage.Commit, DeliveryFaultKind.Cancelled, nameof(TaskCanceledException)), faulted.Fault);
    }

    [Fact]
    public async Task ExpectedAccessibilityFailuresKeepTheirName()
    {
        // WHAT THE ADAPTER NAMES AS THE ENVIRONMENT STAYS NAMED SO. The adapter answers an expected
        // accessibility failure as a result, not an exception; the delivery carries that name to the
        // commit as the forced refusal and back to the caller, with no fault attached.
        var adapter = new FakeTargetAdapter(new TargetContextResult(
            TargetContextStatus.AccessibilityUnavailable,
            RefusalReason: TextDeliveryRefusalReason.AccessibilityUnavailable));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("copied instead"));

        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, adapter.LastCommit?.ForcedRefusalReason);
        Assert.Null(result.Fault);
        Assert.True(result.ClipboardFallback);
    }

    [Fact]
    public async Task AskingToCopyNeverTouchesTheWindowTheTextIsNotGoingTo()
    {
        // Reading the caret of a window nothing will be typed into can bring that window back to the
        // front, and it can fail and stop the copy that was the whole point.
        var adapter = new FakeTargetAdapter(AvailableContext("before", "after"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(CopyRequest("hello"));

        Assert.Equal(0, adapter.Captures);
        Assert.Null(adapter.LastCommit);
        Assert.True(result.Delivered);
        Assert.Null(delivery.RecoveryText);
    }

    [Fact]
    public async Task RequestedCopyUsesOriginalText()
    {
        // The repair adds spacing for where the text was going to land - the insertion for the seam,
        // the fallback's trailing space for a paste. Nothing is landing anywhere, so "hello" must
        // arrive as "hello": neither payload, the words as said.
        var adapter = new FakeTargetAdapter(AvailableContext("before", "after"));
        var delivery = new ContextAwareTextDelivery(adapter);

        await delivery.DeliverAsync(CopyRequest("hello"));

        Assert.Equal("hello", adapter.LastCopied?.Text);
        Assert.Null(adapter.LastCommit);
    }

    [Fact]
    public async Task TheCommitCarriesBothPayloadsUnderTheirOwnNames()
    {
        // THE ADAPTER IS HANDED THE INSERTION AND THE FALLBACK, AND THEY DIFFER: the insertion
        // adjusted to the caret's seam, the fallback the words as said with a trailing space.
        var adapter = new FakeTargetAdapter(AvailableContext(left: "hello,", right: "again"));
        var delivery = new ContextAwareTextDelivery(adapter);

        await delivery.DeliverAsync(Request("world"));

        Assert.Equal(" world ", adapter.LastCommit?.Text.Text);
        Assert.Equal("world ", adapter.LastCommit?.FallbackText.Text);
    }

    [Fact]
    public async Task ACopyStillHappensWhenThereIsNoWindowToPasteInto()
    {
        // The target check refused this before the choice was ever read, so a copy that needs no
        // target was refused for the lack of one.
        var adapter = new FakeTargetAdapter(AvailableContext("before", "after"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(new TextDeliveryRequest(
            new ProcessedText(SessionId, "hello"),
            new TargetWindowId(0),
            "en",
            TextDeliveryOptions.Default with { CopyInsteadOfPaste = true },
            SnippetExpanded: false));

        Assert.True(result.Delivered);
        Assert.Equal("hello", adapter.LastCopied?.Text);
    }

    [Fact]
    public async Task AnIntendedCopyIsNotReportedAsAPasteThatFailed()
    {
        // Delivered false with a clipboard fallback is the shape every reader downstream treats as a
        // failure that was caught: a refusal in the log, an error code, a warning notice, and a
        // history entry reading "held safely".
        var adapter = new FakeTargetAdapter(AvailableContext("before", "after"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(CopyRequest("hello"));

        Assert.True(result.Delivered);
        Assert.False(result.ClipboardFallback);
        Assert.Equal(TextDeliveryRefusalReason.None, result.RefusalReason);
    }

    [Fact]
    public void TheSentenceForARequestedCopyNamesTheClipboardAndNotAPaste()
    {
        // The route is the only thing that says WHERE the text went, so a delivered copy that loses
        // it falls through to the paste sentences and tells the user it pasted into a window it
        // never touched.
        var copied = DeliveryStatusReport.For(new DeliveryResult(
            SessionId,
            Delivered: true,
            ClipboardFallback: false,
            TextDeliveryRoute.ClipboardOnly));

        Assert.Equal("Copied to your clipboard", copied.Text);
        Assert.Equal(DictationOverlayState.Success, copied.State);
    }

    [Fact]
    public async Task ARequestedCopyCarriesTheClipboardRouteBackToTheUser()
    {
        var adapter = new FakeTargetAdapter(AvailableContext("before", "after"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(CopyRequest("hello"));

        Assert.Equal(TextDeliveryRoute.ClipboardOnly, result.Route);
        Assert.Equal("Copied to your clipboard", DeliveryStatusReport.For(result).Text);
    }

    private static TextDeliveryRequest CopyRequest(string text) => new(
        new ProcessedText(SessionId, text),
        Target,
        "en",
        TextDeliveryOptions.Default with { CopyInsteadOfPaste = true },
        SnippetExpanded: false);

    private static TextDeliveryRequest Request(string text) => new(
        new ProcessedText(SessionId, text),
        Target,
        "en",
        TextDeliveryOptions.Default,
        SnippetExpanded: false);

    private static TargetContextResult AvailableContext(string left, string right) => new(
        TargetContextStatus.Available,
        new CaretContext(
            Target,
            Target.FocusedElementId!,
            TextTargetKind.StandardEdit,
            left,
            Selection: string.Empty,
            right,
            LeftReachedDocumentStart: true,
            RightReachedDocumentEnd: right.Length == 0,
            HasTextContext: true,
            SupportsDirectValueWrite: true,
            DirectValueWriteAtEnd: right.Length == 0));

    private sealed class FakeTargetAdapter(
        TargetContextResult context,
        TextCommitResult? commitResult = null,
        Exception? captureException = null,
        Exception? commitException = null,
        Exception? copyException = null) : ITextTargetAdapter
    {
        public TextCommitRequest? LastCommit { get; private set; }

        /// <summary>How many times the target was written to; a delivery that threw must not try again.</summary>
        public int Commits { get; private set; }

        /// <summary>How many times the target was asked for its caret context.</summary>
        /// <remarks>
        /// COUNTED SO A TEST CAN SAY "NOT AT ALL". A choice to copy must not read the caret of a
        /// window the text is not going to, and the only way to assert that is to notice the call
        /// that should never happen.
        /// </remarks>
        public int Captures { get; private set; }

        public ProcessedText? LastCopied { get; private set; }

        public Task<TextCommitResult> CopyOnlyAsync(
            ProcessedText text,
            CancellationToken cancellationToken = default)
        {
            if (copyException is not null)
            {
                return Task.FromException<TextCommitResult>(copyException);
            }

            LastCopied = text;
            return Task.FromResult(new TextCommitResult(
                TextDeliveryRoute.ClipboardOnly,
                Delivered: true,
                ClipboardFallback: false,
                ClipboardRestored: false,
                TextDeliveryRefusalReason.None));
        }

        public Task<TargetContextResult> CaptureContextAsync(
            TargetWindowId target,
            TextDeliveryOptions options,
            CancellationToken cancellationToken = default)
        {
            Captures++;
            return captureException is null
                ? Task.FromResult(context)
                : Task.FromException<TargetContextResult>(captureException);
        }

        public Task<TextCommitResult> CommitAsync(
            TextCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCommit = request;
            Commits++;
            if (commitException is not null)
            {
                return Task.FromException<TextCommitResult>(commitException);
            }

            if (commitResult is not null)
            {
                return Task.FromResult(commitResult);
            }

            var clipboardOnly =
                request.ForcedRefusalReason != TextDeliveryRefusalReason.None;
            return Task.FromResult(new TextCommitResult(
                clipboardOnly
                    ? TextDeliveryRoute.ClipboardOnly
                    : TextDeliveryRoute.ClipboardPaste,
                Delivered: !clipboardOnly,
                ClipboardFallback: clipboardOnly,
                ClipboardRestored: !clipboardOnly,
                request.ForcedRefusalReason));
        }
    }
}
