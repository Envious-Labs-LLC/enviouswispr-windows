using EnviousWispr.Core.Dictation;
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
        Assert.Equal("world ", adapter.LastCommit?.LegacyText.Text);
        Assert.Equal(CursorRepairDisposition.ContextApplied, result.RepairDisposition);
        Assert.True(result.Delivered);
        Assert.Null(delivery.RecoveryText);
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
        Assert.Equal(CursorRepairDisposition.LegacyPayload, result.RepairDisposition);
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
            TextDeliveryOptions.Default);

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

    [Fact]
    public async Task UnexpectedContextFailureRetainsTextWithoutEscaping()
    {
        var adapter = new FakeTargetAdapter(
            AvailableContext(left: "", right: ""),
            captureException: new InvalidOperationException("synthetic context failure"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("recover context"));

        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.Equal("recover context", delivery.RecoveryText?.Text);
    }

    [Fact]
    public async Task UnexpectedCommitFailureRetainsTextWithoutEscaping()
    {
        var adapter = new FakeTargetAdapter(
            AvailableContext(left: "", right: ""),
            commitException: new InvalidOperationException("synthetic commit failure"));
        var delivery = new ContextAwareTextDelivery(adapter);

        var result = await delivery.DeliverAsync(Request("recover commit"));

        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, result.RefusalReason);
        Assert.Equal("recover commit", delivery.RecoveryText?.Text);
    }

    private static TextDeliveryRequest Request(string text) => new(
        new ProcessedText(SessionId, text),
        Target,
        "en",
        TextDeliveryOptions.Default);

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
        Exception? commitException = null) : ITextTargetAdapter
    {
        public TextCommitRequest? LastCommit { get; private set; }

        public Task<TargetContextResult> CaptureContextAsync(
            TargetWindowId target,
            TextDeliveryOptions options,
            CancellationToken cancellationToken = default) => captureException is null
                ? Task.FromResult(context)
                : Task.FromException<TargetContextResult>(captureException);

        public Task<TextCommitResult> CommitAsync(
            TextCommitRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCommit = request;
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
