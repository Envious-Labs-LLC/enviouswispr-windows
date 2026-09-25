using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Home's one-shot Undo and History's Paste after an Escape Recovery: what each pastes, where, how often,
/// and what leaves the pending state afterwards. The real persistence owner over fake stores; a delivery
/// that writes down what it was asked.
/// </summary>
/// <remarks>
/// THE STATES, FROM THE macOS SOURCE (EscapeRecoveryPasteAction, TranscriptCoordinator, #2087): a pending
/// entry is pasted back by Undo (once) or by History's Paste (as often as asked); neither changes the entry,
/// whose 24 hours run on until Keep; an expired entry is refused by both. On Windows the copy on Home is what
/// leaves the pending state once the words are back.
/// </remarks>
public sealed class SavedDictationPasteTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The window and field the take was aimed at, frozen at the key press.</summary>
    private static readonly TargetWindowId Frozen = new(0x1234, 42, "42.7");

    /// <summary>The last window the person worked in before EnviousWispr's, as the foreground history keeps it.</summary>
    private static readonly TargetWindowId Previous = new(0x5678, 77, null);

    private const string Words = "kept on escape";

    [Fact]
    public async Task UndoPastesTheSavedWordsBackIntoTheFrozenFieldAndHomeStopsHoldingThem()
    {
        var world = await World.AfterEscapeAsync();

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.Pasted, result.Outcome);
        var request = Assert.Single(world.Delivery.Requests);
        Assert.Equal(Words, request.Text.Text);
        Assert.Equal(Frozen, request.Target);
        Assert.False(request.Options.CopyInsteadOfPaste);

        // HOME'S COPY LEAVES THE PENDING STATE: the next recording may start, and the card is cleared.
        Assert.False(world.Persistence.HasPendingRecovery);
        Assert.Null(world.Persistence.UndoOffer);
        Assert.True(world.Recovery.Cleared);
        Assert.Contains("ClearRecoveredText", world.Effects.Trace);

        // THE HISTORY ENTRY IS UNTOUCHED: still pending, its 24 hours still running, as macOS keeps a restored row.
        var entry = Assert.Single(world.History.Entries);
        Assert.Equal(Now.AddHours(24), entry.ExpiresAt);
        Assert.False(entry.WasDelivered);
    }

    [Fact]
    public async Task UndoIsOneShot()
    {
        var world = await World.AfterEscapeAsync();

        var first = await world.Paste.UndoAsync();
        var second = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.Pasted, first.Outcome);
        Assert.Equal(SavedDictationPasteOutcome.NotOffered, second.Outcome);
        Assert.Single(world.Delivery.Requests);
    }

    /// <summary>A double click, or two presses racing, restores once.</summary>
    [Fact]
    public async Task TwoUndosRacingPasteOnce()
    {
        for (var round = 0; round < 20; round++)
        {
            var world = await World.AfterEscapeAsync();
            world.Delivery.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => world.Paste.UndoAsync())).ToArray();
            await Task.Delay(20);
            world.Delivery.Gate.SetResult();
            var results = await Task.WhenAll(attempts);

            Assert.Single(world.Delivery.Requests);
            Assert.Equal(1, results.Count(result => result.Outcome == SavedDictationPasteOutcome.Pasted));
            Assert.All(
                results.Where(result => result.Outcome != SavedDictationPasteOutcome.Pasted),
                result => Assert.Contains(result.Outcome, new[] { SavedDictationPasteOutcome.Busy, SavedDictationPasteOutcome.NotOffered }));
        }
    }

    /// <summary>A take whose field is gone is not pasted into whatever is there now: the clipboard takes the words.</summary>
    [Fact]
    public async Task UndoIntoAFieldThatMovedGoesThroughTheDeliveryAndEndsOnTheClipboard()
    {
        var adapter = new FakeTargetAdapter { Capture = TargetContextStatus.TargetChanged };
        var world = await World.AfterEscapeAsync(delivery: new ContextAwareTextDelivery(adapter));

        var result = await world.Paste.UndoAsync();

        // THE DICTATION'S OWN DELIVERY decided: it asked the adapter about the frozen field, was told the
        // field changed, and handed the commit that refusal, which the adapter answers with the clipboard.
        Assert.Equal(SavedDictationPasteOutcome.KeptOnClipboard, result.Outcome);
        Assert.Equal(Frozen, Assert.Single(adapter.Captured));
        var commit = Assert.Single(adapter.Commits);
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, commit.ForcedRefusalReason);
        Assert.Equal(Frozen, commit.Target);
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, result.Delivery?.RefusalReason);
        Assert.False(world.Persistence.HasPendingRecovery, "the words reached the clipboard, so Home stops holding them");
    }

    /// <summary>The twin: the field is still there, and the same delivery writes the words into it.</summary>
    [Fact]
    public async Task UndoIntoTheSameFieldIsWrittenByTheDictationsDelivery()
    {
        var adapter = new FakeTargetAdapter { Capture = TargetContextStatus.Available };
        var world = await World.AfterEscapeAsync(delivery: new ContextAwareTextDelivery(adapter));

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.Pasted, result.Outcome);
        Assert.Equal(TextDeliveryRoute.ClipboardPaste, result.Delivery?.Route);
        var commit = Assert.Single(adapter.Commits);
        Assert.Equal(TextDeliveryRefusalReason.None, commit.ForcedRefusalReason);
        Assert.Equal(Words, commit.Text.Text.TrimEnd());
    }

    /// <summary>An entry that expired between the offer and the press is refused, and the offer is spent.</summary>
    [Fact]
    public async Task AnExpiredEntryIsNotPastedByUndo()
    {
        var world = await World.AfterEscapeAsync();
        world.Clock = Now.AddHours(24);

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.NoLongerAvailable, result.Outcome);
        Assert.Empty(world.Delivery.Requests);
        Assert.Null(world.Persistence.UndoOffer);
        Assert.True(world.Persistence.HasPendingRecovery, "Home still has the words to copy");
    }

    [Fact]
    public async Task ADeletedEntryIsNotPastedByUndo()
    {
        var world = await World.AfterEscapeAsync();
        world.History.Entries.Clear();

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.NoLongerAvailable, result.Outcome);
        Assert.Empty(world.Delivery.Requests);
    }

    /// <summary>Kept is a choice to keep the words, not to lose the way back to them.</summary>
    [Fact]
    public async Task AKeptEntryIsStillPastedByUndoAndStaysKept()
    {
        var world = await World.AfterEscapeAsync();
        await world.History.KeepAsync(world.EntryId);

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.Pasted, result.Outcome);
        Assert.Null(Assert.Single(world.History.Entries).ExpiresAt);
    }

    /// <summary>A dictation running when Undo is pressed refuses it and leaves the offer for later.</summary>
    [Fact]
    public async Task UndoIsRefusedWhileADictationRunsAndTheOfferStands()
    {
        var world = await World.AfterEscapeAsync();
        world.DictationActive = true;

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.DictationInProgress, result.Outcome);
        Assert.Empty(world.Delivery.Requests);
        Assert.NotNull(world.Persistence.UndoOffer);

        world.DictationActive = false;
        Assert.Equal(SavedDictationPasteOutcome.Pasted, (await world.Paste.UndoAsync()).Outcome);
    }

    /// <summary>A take that began with no window in front has nowhere to go back to, and is not guessed.</summary>
    [Fact]
    public async Task UndoWithNoFrozenTargetPastesNothing()
    {
        var world = await World.AfterEscapeAsync(target: new TargetWindowId(0));

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.NoTarget, result.Outcome);
        Assert.Empty(world.Delivery.Requests);
        Assert.True(world.Persistence.HasPendingRecovery);
    }

    /// <summary>A take aimed at a window with no field named is brought back and its field read, as the reuse does.</summary>
    [Fact]
    public async Task UndoToAWindowWithoutAFieldReacquiresIt()
    {
        var world = await World.AfterEscapeAsync(target: Frozen with { FocusedElementId = null });

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.Pasted, result.Outcome);
        Assert.Equal(Frozen with { FocusedElementId = "reacquired" }, Assert.Single(world.Delivery.Requests).Target);
    }

    [Fact]
    public async Task HistoryPastePastesAPendingEntryIntoThePreviousWindowAndLeavesTheEntryPending()
    {
        var world = await World.AfterEscapeAsync();

        var result = await world.Paste.PasteAsync(world.EntryId, Previous);

        Assert.Equal(SavedDictationPasteOutcome.Pasted, result.Outcome);
        var request = Assert.Single(world.Delivery.Requests);
        Assert.Equal(Words, request.Text.Text);
        Assert.Equal(Previous with { FocusedElementId = "reacquired" }, request.Target);
        Assert.Equal(Now.AddHours(24), Assert.Single(world.History.Entries).ExpiresAt);

        // THE SAME TAKE AS HOME'S COPY: Home stops holding it, and its Undo is spent.
        Assert.False(world.Persistence.HasPendingRecovery);
        Assert.Null(world.Persistence.UndoOffer);
        Assert.Equal(SavedDictationPasteOutcome.NotOffered, (await world.Paste.UndoAsync()).Outcome);
    }

    /// <summary>History's Paste is not one-shot: macOS lets a row be pasted as often as the person asks.</summary>
    [Fact]
    public async Task HistoryPasteCanBeRepeated()
    {
        var world = await World.AfterEscapeAsync();

        Assert.Equal(SavedDictationPasteOutcome.Pasted, (await world.Paste.PasteAsync(world.EntryId, Previous)).Outcome);
        Assert.Equal(SavedDictationPasteOutcome.Pasted, (await world.Paste.PasteAsync(world.EntryId, Previous)).Outcome);
        Assert.Equal(2, world.Delivery.Requests.Count);
    }

    [Fact]
    public async Task HistoryPasteOfAnotherEntryLeavesHomesCopyAndItsUndo()
    {
        var world = await World.AfterEscapeAsync();
        var other = DictationHistoryEntry.Create(Now.AddMinutes(-5), "an older dictation", "parakeet", false, true);
        world.History.Entries.Add(other);

        var result = await world.Paste.PasteAsync(other.Id, Previous);

        Assert.Equal(SavedDictationPasteOutcome.Pasted, result.Outcome);
        Assert.Equal("an older dictation", Assert.Single(world.Delivery.Requests).Text.Text);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.NotNull(world.Persistence.UndoOffer);
    }

    [Fact]
    public async Task HistoryPasteRefusesAnExpiredEntry()
    {
        var world = await World.AfterEscapeAsync();
        world.Clock = Now.AddHours(24).AddSeconds(1);

        var result = await world.Paste.PasteAsync(world.EntryId, Previous);

        Assert.Equal(SavedDictationPasteOutcome.NoLongerAvailable, result.Outcome);
        Assert.Empty(world.Delivery.Requests);
    }

    [Fact]
    public async Task HistoryPasteNeedsAWindowThatIsNotOurOwn()
    {
        var world = await World.AfterEscapeAsync();

        Assert.Equal(SavedDictationPasteOutcome.NoTarget, (await world.Paste.PasteAsync(world.EntryId, target: null)).Outcome);
        Assert.Equal(SavedDictationPasteOutcome.OwnWindow, (await world.Paste.PasteAsync(world.EntryId, World.OwnWindow)).Outcome);
        world.ReacquireFails = true;
        Assert.Equal(SavedDictationPasteOutcome.NoTarget, (await world.Paste.PasteAsync(world.EntryId, Previous)).Outcome);
        Assert.Empty(world.Delivery.Requests);
        Assert.True(world.Persistence.HasPendingRecovery);
    }

    [Fact]
    public async Task HistoryPasteIsRefusedWhileADictationRuns()
    {
        var world = await World.AfterEscapeAsync();
        world.DictationActive = true;

        var result = await world.Paste.PasteAsync(world.EntryId, Previous);

        Assert.Equal(SavedDictationPasteOutcome.DictationInProgress, result.Outcome);
        Assert.Empty(world.Delivery.Requests);
    }

    /// <summary>A refused paste - neither written nor caught by the clipboard - leaves Home's copy where it was.</summary>
    [Fact]
    public async Task AFailedPasteLeavesHomesCopy()
    {
        var world = await World.AfterEscapeAsync();
        world.Delivery.Answer = id => new DeliveryResult(id, Delivered: false, ClipboardFallback: false, RefusalReason: TextDeliveryRefusalReason.ClipboardUnavailable);

        var result = await world.Paste.UndoAsync();

        Assert.Equal(SavedDictationPasteOutcome.Failed, result.Outcome);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.Null(world.Persistence.UndoOffer);
    }

    /// <summary>The offer lives only beside its own copy: a newer take's copy takes it away.</summary>
    [Fact]
    public async Task ANewerRecoveryCopyWithdrawsTheUndo()
    {
        var world = await World.AfterEscapeAsync();

        await world.Persistence.SaveRecoveryTextAsync(new ProcessedText(DictationSessionId.Create(), "a later take"), CancellationToken.None);

        Assert.Null(world.Persistence.UndoOffer);
        Assert.Equal(SavedDictationPasteOutcome.NotOffered, (await world.Paste.UndoAsync()).Outcome);
    }

    [Fact]
    public async Task DeletingHomesCopyWithdrawsTheUndo()
    {
        var world = await World.AfterEscapeAsync();

        world.Persistence.ForgetPendingRecovery();

        Assert.Equal(SavedDictationPasteOutcome.NotOffered, (await world.Paste.UndoAsync()).Outcome);
        Assert.Empty(world.Delivery.Requests);
    }

    /// <summary>An offer for a take whose copy is not the one on Home is refused outright.</summary>
    [Fact]
    public async Task AnOfferForAnotherTakeIsRefused()
    {
        var world = await World.AfterEscapeAsync();

        Assert.False(world.Persistence.OfferUndo(new EscapeRecoveryUndoOffer(DictationSessionId.Create(), world.EntryId, Frozen)));
    }

    /// <summary>Every ending has its own log name, and the two that put words somewhere say which door.</summary>
    [Fact]
    public void EveryOutcomeHasItsOwnLogEvent()
    {
        var names = new HashSet<AppEventCode>();
        foreach (var action in Enum.GetValues<SavedDictationPasteAction>())
        {
            foreach (var outcome in Enum.GetValues<SavedDictationPasteOutcome>())
            {
                names.Add(SavedDictationPasteDiagnostics.EventFor(new SavedDictationPasteResult(action, outcome)));
            }
        }

        // Nine outcomes; Pasted and KeptOnClipboard are split by door.
        Assert.Equal(Enum.GetValues<SavedDictationPasteOutcome>().Length + 2, names.Count);
    }

    private sealed class World
    {
        public static readonly TargetWindowId OwnWindow = new(0x9999, (uint)Environment.ProcessId, null);

        private World(ITextDelivery? delivery)
        {
            Delivery = new RecordingDelivery();
            Persistence = new SessionPersistence(
                Recovery,
                History,
                new NullLogger(),
                new ClockOf(() => Clock),
                () => HistoryPreferences.Default,
                Effects);
            Paste = new SavedDictationPaste(new SavedDictationPasteEnvironment(
                cancellation => Task.FromResult<IReadOnlyList<DictationHistoryEntry>>([.. History.Entries]),
                () => Clock,
                () => DictationActive,
                target => target.ProcessId == OwnWindow.ProcessId,
                delivery ?? Delivery,
                () => TextDeliveryOptions.Default with { CopyInsteadOfPaste = true },
                (window, _) => Task.FromResult<TargetWindowId?>(
                    ReacquireFails ? null : window with { FocusedElementId = "reacquired" }),
                Persistence));
        }

        public DateTimeOffset Clock { get; set; } = Now;

        public bool DictationActive { get; set; }

        public bool ReacquireFails { get; set; }

        public RecordingDelivery Delivery { get; }

        public FakeRecoveryStore Recovery { get; } = new();

        public FakeHistoryStore History { get; } = new();

        public FakeEffects Effects { get; } = new();

        public SessionPersistence Persistence { get; }

        public SavedDictationPaste Paste { get; }

        public Guid EntryId { get; private set; }

        /// <summary>The state an Escape Recovery leaves: the copy on Home, a 24-hour History entry, the Undo beside them.</summary>
        public static async Task<World> AfterEscapeAsync(TargetWindowId? target = null, ITextDelivery? delivery = null)
        {
            var world = new World(delivery);
            var session = DictationSessionId.Create();
            await world.Persistence.SaveRecoveryTextAsync(new ProcessedText(session, Words), CancellationToken.None);
            var entryId = await world.Persistence.SaveHistoryAsync(
                new Transcript(session, Words, "parakeet", []),
                Words,
                HistoryWriteIntent.EscapeRecovery(wasPolished: false, Now.AddHours(24)));
            Assert.NotNull(entryId);
            world.EntryId = entryId.Value;
            Assert.True(world.Persistence.OfferUndo(new EscapeRecoveryUndoOffer(session, entryId.Value, target ?? Frozen)));
            Assert.True(world.Persistence.HasPendingRecovery);
            return world;
        }
    }

    private sealed class RecordingDelivery : ITextDelivery
    {
        private readonly object _lock = new();

        public List<TextDeliveryRequest> Requests { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public Func<DictationSessionId, DeliveryResult> Answer { get; set; } =
            id => new DeliveryResult(id, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste);

        public async Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Requests.Add(request);
            }

            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            return Answer(request.Text.SessionId);
        }
    }

    /// <summary>The adapter under the dictation's real delivery: reports the field as it is told to, and pastes or copies.</summary>
    private sealed class FakeTargetAdapter : ITextTargetAdapter
    {
        public TargetContextStatus Capture { get; set; }

        public List<TargetWindowId> Captured { get; } = [];

        public List<TextCommitRequest> Commits { get; } = [];

        public Task<TargetContextResult> CaptureContextAsync(TargetWindowId target, TextDeliveryOptions options, CancellationToken cancellationToken = default)
        {
            Captured.Add(target);
            return Task.FromResult(Capture == TargetContextStatus.Available
                ? new TargetContextResult(
                    TargetContextStatus.Available,
                    new CaretContext(target, target.FocusedElementId!, TextTargetKind.StandardEdit, "", "", "", true, true, true, false, false))
                : new TargetContextResult(Capture, RefusalReason: TextDeliveryRefusalReason.TargetChanged));
        }

        public Task<TextCommitResult> CommitAsync(TextCommitRequest request, CancellationToken cancellationToken = default)
        {
            Commits.Add(request);
            return Task.FromResult(request.ForcedRefusalReason == TextDeliveryRefusalReason.None
                ? new TextCommitResult(TextDeliveryRoute.ClipboardPaste, Delivered: true, ClipboardFallback: false, ClipboardRestored: true)
                : new TextCommitResult(TextDeliveryRoute.ClipboardOnly, Delivered: false, ClipboardFallback: true, ClipboardRestored: false, request.ForcedRefusalReason));
        }

        public Task<TextCommitResult> CopyOnlyAsync(ProcessedText text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A paste the person asked for is never a copy.");
    }

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public bool Cleared { get; private set; }

        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Entries { get; } = [];

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult([.. Entries], HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(Entries.RemoveAll(entry => entry.Id == id) > 0));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var index = Entries.FindIndex(entry => entry.Id == id);
            if (index < 0)
            {
                return Task.FromResult(new HistoryOperationResult(false));
            }

            Entries[index] = Entries[index] with { ExpiresAt = null };
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default)
        {
            Entries.Clear();
            return Task.FromResult(new HistoryOperationResult(true));
        }
    }

    private sealed class FakeEffects : ISessionPersistenceEffects
    {
        public List<string> Trace { get; } = [];

        public void ShowPendingRecovery(RecoveryTextRecord record, bool undoOffered) =>
            Trace.Add(undoOffered ? "ShowPendingRecovery:undo" : "ShowPendingRecovery");

        public void ClearRecoveredText() => Trace.Add("ClearRecoveredText");

        public void NotifyHistoryChanged() => Trace.Add("NotifyHistoryChanged");
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Write(AppLogEntry entry)
        {
        }
    }

    private sealed class ClockOf(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }
}
