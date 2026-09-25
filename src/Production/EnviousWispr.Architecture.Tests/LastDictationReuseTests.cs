using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Paste and Copy Last Dictation: which entry counts, and what the owner does with it. Ref: #206.</summary>
public sealed class LastDictationReuseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TargetWindowId Editor = new(0x1234, 42, null);
    private static readonly TargetWindowId OwnWindow = new(0x9999, 7, null);

    [Fact]
    public void TheNewestDeliveredDictationCounts()
    {
        var older = Entry("older", minutesAgo: 10);
        var newer = Entry("newer", minutesAgo: 1);

        Assert.Equal("newer", LastDictation.Pick([older, newer], Now)?.Text);
    }

    /// <summary>A paste the target refused is exactly what this is for.</summary>
    [Fact]
    public void AHeldDictationWhosePasteFailedCounts()
    {
        var held = Entry("held words", minutesAgo: 1, delivered: false);

        Assert.Equal("held words", LastDictation.Pick([held], Now)?.Text);
    }

    /// <summary>An Escape Recovery is a take the person threw away.</summary>
    [Fact]
    public void AnEscapeRecoveryDoesNotCountAndTheOneBeforeItDoes()
    {
        var before = Entry("kept on purpose", minutesAgo: 5);
        var cancelled = Entry("thrown away", minutesAgo: 1, delivered: false, expiresAt: Now.AddHours(23));

        Assert.Equal("kept on purpose", LastDictation.Pick([cancelled, before], Now)?.Text);
    }

    [Fact]
    public void WhitespaceAndExpiredEntriesDoNotCount()
    {
        var blank = Entry(" \n\t", minutesAgo: 1);
        var expired = Entry("gone", minutesAgo: 2, expiresAt: Now.AddMinutes(-1));

        Assert.Null(LastDictation.Pick([blank, expired], Now));
    }

    [Fact]
    public void ThePreviewIsTheFirstLineCutToThirtyCharacters()
    {
        Assert.Equal("Short one", LastDictation.Preview("  Short one\r\nsecond line"));
        var cut = LastDictation.Preview("This dictation is considerably longer than thirty characters");
        Assert.Equal(30, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasteDeliversTheWordsToTheNamedTargetAsAPaste()
    {
        var delivery = new RecordingDelivery(Delivered());
        var reuse = Reuse(delivery, Entry("hello there", minutesAgo: 1));

        var result = await reuse.PasteAsync(Editor, LastDictationSource.Menu);

        Assert.Equal(LastDictationOutcome.Pasted, result.Outcome);
        var request = Assert.Single(delivery.Requests);
        Assert.Equal("hello there", request.Text.Text);
        Assert.Equal(Editor with { FocusedElementId = "42.7" }, request.Target);
        Assert.False(request.Options.CopyInsteadOfPaste);
    }

    /// <summary>The person's copy-instead setting does not turn a paste they asked for into a copy.</summary>
    [Fact]
    public async Task PasteIsAPasteEvenWithCopyInsteadOfPasteOn()
    {
        var delivery = new RecordingDelivery(Delivered());
        var reuse = Reuse(delivery, [Entry("words", minutesAgo: 1)], copyInsteadOfPaste: true);

        await reuse.PasteAsync(Editor, LastDictationSource.Menu);

        Assert.False(Assert.Single(delivery.Requests).Options.CopyInsteadOfPaste);
    }

    [Fact]
    public async Task CopyAsksForACopyAndNeedsNoTarget()
    {
        var delivery = new RecordingDelivery(new DeliveryResult(default, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardOnly));
        var reuse = Reuse(delivery, Entry("copy me", minutesAgo: 1));

        var result = await reuse.CopyAsync(LastDictationSource.Menu);

        Assert.Equal(LastDictationOutcome.Copied, result.Outcome);
        var request = Assert.Single(delivery.Requests);
        Assert.True(request.Options.CopyInsteadOfPaste);
        Assert.Equal("copy me", request.Text.Text);
    }

    /// <summary>A window that will not come back with a field focused is not pasted into.</summary>
    [Fact]
    public async Task AWindowThatCannotBeReacquiredIsNotPastedInto()
    {
        var delivery = new RecordingDelivery(Delivered());
        var reuse = Reuse(
            delivery,
            [Entry("words", minutesAgo: 1)],
            reacquire: (_, _) => Task.FromResult<TargetWindowId?>(null));

        var result = await reuse.PasteAsync(Editor, LastDictationSource.Menu);

        Assert.Equal(LastDictationOutcome.NoTarget, result.Outcome);
        Assert.Empty(delivery.Requests);
    }

    /// <summary>A target that already names its field - a shortcut's, captured at the press - is used as it is.</summary>
    [Fact]
    public async Task ATargetThatNamesItsFieldIsNotReacquired()
    {
        var delivery = new RecordingDelivery(Delivered());
        var asked = 0;
        var reuse = Reuse(
            delivery,
            [Entry("words", minutesAgo: 1)],
            reacquire: (window, _) =>
            {
                asked++;
                return Task.FromResult<TargetWindowId?>(window);
            });
        var pressed = Editor with { FocusedElementId = "9.9" };

        await reuse.PasteAsync(pressed, LastDictationSource.Shortcut);

        Assert.Equal(0, asked);
        Assert.Equal(pressed, Assert.Single(delivery.Requests).Target);
    }

    /// <summary>The menu names one dictation; if that one is deleted before the click, nothing older is pasted instead.</summary>
    [Fact]
    public async Task TheDictationTheMenuNamedIsTheOnlyOneReused()
    {
        var older = Entry("older words", minutesAgo: 10);
        var shown = Entry("shown words", minutesAgo: 1);
        var present = new RecordingDelivery(Delivered());
        var deleted = new RecordingDelivery(Delivered());

        var used = await Reuse(present, [shown, older]).PasteAsync(Editor, LastDictationSource.Menu, shown.Id);
        var refused = await Reuse(deleted, [older]).PasteAsync(Editor, LastDictationSource.Menu, shown.Id);

        Assert.Equal(LastDictationOutcome.Pasted, used.Outcome);
        Assert.Equal("shown words", Assert.Single(present.Requests).Text.Text);
        Assert.Equal(LastDictationOutcome.NothingToReuse, refused.Outcome);
        Assert.Empty(deleted.Requests);
    }

    [Fact]
    public async Task ARefusedPasteThatTheClipboardCaughtSaysSo()
    {
        var delivery = new RecordingDelivery(new DeliveryResult(
            default,
            Delivered: false,
            ClipboardFallback: true,
            TextDeliveryRoute.ClipboardOnly,
            TextDeliveryRefusalReason.ElevatedTarget));
        var reuse = Reuse(delivery, Entry("words", minutesAgo: 1));

        var result = await reuse.PasteAsync(Editor, LastDictationSource.Menu);

        Assert.Equal(LastDictationOutcome.KeptOnClipboard, result.Outcome);
    }

    [Fact]
    public async Task NothingIsDeliveredWhenThereIsNothingToReuse()
    {
        var delivery = new RecordingDelivery(Delivered());
        var reuse = Reuse(delivery, Entry("thrown away", minutesAgo: 1, delivered: false, expiresAt: Now.AddHours(1)));

        Assert.Equal(LastDictationOutcome.NothingToReuse, (await reuse.PasteAsync(Editor, LastDictationSource.Menu)).Outcome);
        Assert.Equal(LastDictationOutcome.NothingToReuse, (await reuse.CopyAsync(LastDictationSource.Menu)).Outcome);
        Assert.Empty(delivery.Requests);
    }

    [Fact]
    public async Task PasteRefusesWithoutATargetAndIntoItsOwnWindow()
    {
        var delivery = new RecordingDelivery(Delivered());
        var reuse = Reuse(delivery, Entry("words", minutesAgo: 1));

        Assert.Equal(LastDictationOutcome.NoTarget, (await reuse.PasteAsync(null, LastDictationSource.Menu)).Outcome);
        Assert.Equal(LastDictationOutcome.OwnWindow, (await reuse.PasteAsync(OwnWindow, LastDictationSource.Menu)).Outcome);
        Assert.Empty(delivery.Requests);
    }

    [Fact]
    public async Task ADictationInFlightRefusesBothBeforeAndAfterTheHistoryRead()
    {
        var delivery = new RecordingDelivery(Delivered());
        var active = true;
        var reuse = Reuse(delivery, [Entry("words", minutesAgo: 1)], isActive: () => active);

        Assert.Equal(LastDictationOutcome.DictationInProgress, (await reuse.PasteAsync(Editor, LastDictationSource.Menu)).Outcome);
        Assert.Equal(LastDictationOutcome.DictationInProgress, (await reuse.CopyAsync(LastDictationSource.Menu)).Outcome);

        // A take that starts while history is being read.
        active = false;
        var startsDuringRead = new LastDictationReuse(new LastDictationEnvironment(
            _ =>
            {
                active = true;
                return Task.FromResult<IReadOnlyList<DictationHistoryEntry>>([Entry("words", minutesAgo: 1)]);
            },
            () => Now,
            () => active,
            target => target == OwnWindow,
            delivery,
            () => TextDeliveryOptions.Default,
            Reacquired));

        Assert.Equal(LastDictationOutcome.DictationInProgress, (await startsDuringRead.PasteAsync(Editor, LastDictationSource.Menu)).Outcome);
        Assert.Empty(delivery.Requests);
    }

    [Fact]
    public async Task ASecondReuseWhileTheFirstIsDeliveringIsRefusedNotQueued()
    {
        var release = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new RecordingDelivery(() => release.Task);
        var reuse = Reuse(delivery, Entry("words", minutesAgo: 1));

        var first = reuse.PasteAsync(Editor, LastDictationSource.Menu);
        await delivery.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await reuse.CopyAsync(LastDictationSource.Menu);
        release.SetResult(Delivered());

        Assert.Equal(LastDictationOutcome.Busy, second.Outcome);
        Assert.Equal(LastDictationOutcome.Pasted, (await first).Outcome);
        Assert.Single(delivery.Requests);
    }

    /// <summary>Every ending has its own line in the log, so no two can be confused by a reader.</summary>
    [Fact]
    public void EveryOutcomeHasItsOwnLogEvent()
    {
        var outcomes = Enum.GetValues<LastDictationOutcome>();
        var events = outcomes.Select(LastDictationDiagnostics.EventFor).ToArray();

        Assert.Equal(outcomes.Length, events.Distinct().Count());
        Assert.All(events, code => Assert.True(Enum.IsDefined(code)));
    }

    private static DeliveryResult Delivered() =>
        new(default, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.UiAutomationValue);

    private static DictationHistoryEntry Entry(
        string text,
        int minutesAgo,
        bool delivered = true,
        DateTimeOffset? expiresAt = null) =>
        new(Guid.NewGuid(), Now.AddMinutes(-minutesAgo), text, "engine", WasPolished: false, delivered, expiresAt);

    private static LastDictationReuse Reuse(RecordingDelivery delivery, DictationHistoryEntry entry) =>
        Reuse(delivery, [entry]);

    private static LastDictationReuse Reuse(
        RecordingDelivery delivery,
        IReadOnlyList<DictationHistoryEntry> entries,
        bool copyInsteadOfPaste = false,
        Func<bool>? isActive = null,
        Func<TargetWindowId, CancellationToken, Task<TargetWindowId?>>? reacquire = null) =>
        new(new LastDictationEnvironment(
            _ => Task.FromResult(entries),
            () => Now,
            isActive ?? (() => false),
            target => target == OwnWindow,
            delivery,
            () => TextDeliveryOptions.Default with { CopyInsteadOfPaste = copyInsteadOfPaste },
            reacquire ?? Reacquired));

    /// <summary>Brings the window back with the field Windows focused in it, as the production reacquire does.</summary>
    private static Task<TargetWindowId?> Reacquired(TargetWindowId window, CancellationToken cancellationToken) =>
        Task.FromResult<TargetWindowId?>(window with { FocusedElementId = "42.7" });

    private sealed class RecordingDelivery : ITextDelivery
    {
        private readonly Func<Task<DeliveryResult>> _answer;

        public RecordingDelivery(DeliveryResult answer)
            : this(() => Task.FromResult(answer))
        {
        }

        public RecordingDelivery(Func<Task<DeliveryResult>> answer) => _answer = answer;

        public List<TextDeliveryRequest> Requests { get; } = [];

        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            FirstRequest.TrySetResult();
            return _answer();
        }
    }
}
