using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// What is written for a dictation beyond its delivery, and what the next press must respect. Fake
/// stores that can be told to fail, a frozen clock, a logger that keeps its lines.
/// </summary>
public sealed class SessionPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASavedRecoveryCopyIsPendingAndOnDisk()
    {
        var (persistence, recovery, _, log, _) = Build();
        var text = Text("hello world");

        await persistence.SaveRecoveryTextAsync(text, CancellationToken.None);

        Assert.True(persistence.HasPendingRecovery);
        Assert.Equal("hello world", persistence.PendingRecord?.Text);
        Assert.Equal(text.SessionId, persistence.PendingRecord?.SessionId);
        Assert.Equal(Now, persistence.PendingRecord?.CreatedAt);
        Assert.Equal("hello world", recovery.Saved?.Text);
        Assert.Equal([AppEventCode.RecoveryTextSaved], log.Codes);
    }

    [Fact]
    public async Task AFailedWriteKeepsTheTextInMemoryAndSaysStorageWasUnavailable()
    {
        var (persistence, recovery, _, log, _) = Build();
        recovery.SaveSucceeds = false;

        await persistence.SaveRecoveryTextAsync(Text("hello world"), CancellationToken.None);

        Assert.True(persistence.HasPendingRecovery, "the words are still held for the person even when the disk refused them");
        Assert.Equal("hello world", persistence.PendingRecord?.Text);
        Assert.Equal([AppEventCode.RecoveryTextUnavailable], log.Codes);
        Assert.Equal(AppErrorCode.StorageUnavailable, log.Entries[0].ErrorCode);
        Assert.Equal(AppFailureCategory.Recovery, log.Entries[0].Failure);
    }

    [Fact]
    public async Task WhenAdmissionForbadePersistenceTheCopyIsHeldInMemoryOnly()
    {
        var (persistence, recovery, _, log, _) = Build();
        persistence.CanPersistRecovery = false;

        await persistence.SaveRecoveryTextAsync(Text("hello world"), CancellationToken.None);

        Assert.True(persistence.HasPendingRecovery);
        Assert.Null(recovery.Saved);
        Assert.Equal(0, recovery.SaveCalls);
        Assert.Equal([AppEventCode.RecoveryTextUnavailable], log.Codes);
        Assert.Equal(AppErrorCode.LowDiskSpace, log.Entries[0].ErrorCode);
        Assert.Equal(AppFailureCategory.ResourcePressure, log.Entries[0].Failure);
    }

    [Fact]
    public async Task BlankTextIsNeverARecoveryCopy()
    {
        var (persistence, recovery, _, log, _) = Build();

        await persistence.SaveRecoveryTextAsync(Text("   "), CancellationToken.None);

        Assert.False(persistence.HasPendingRecovery);
        Assert.Equal(0, recovery.SaveCalls);
        Assert.Empty(log.Codes);
    }

    [Fact]
    public async Task ClearingAfterDeliveryForgetsThePendingCopyAndTheScreen()
    {
        var (persistence, recovery, _, log, effects) = Build();
        await persistence.SaveRecoveryTextAsync(Text("hello world"), CancellationToken.None);

        await persistence.ClearRecoveryTextAsync();

        Assert.False(persistence.HasPendingRecovery);
        Assert.Null(persistence.PendingRecord);
        Assert.True(recovery.Cleared);
        Assert.Equal([AppEventCode.RecoveryTextSaved, AppEventCode.RecoveryTextCleared], log.Codes);
        Assert.Equal(["ClearRecoveredText"], effects.Trace);
    }

    [Fact]
    public async Task AFailedClearKeepsThePendingStateBecauseTheFileIsStillThere()
    {
        var (persistence, recovery, _, log, effects) = Build();
        await persistence.SaveRecoveryTextAsync(Text("hello world"), CancellationToken.None);
        recovery.ClearSucceeds = false;

        await persistence.ClearRecoveryTextAsync();

        Assert.True(persistence.HasPendingRecovery);
        Assert.NotNull(persistence.PendingRecord);
        Assert.Equal([AppEventCode.RecoveryTextSaved, AppEventCode.RecoveryTextUnavailable], log.Codes);
        Assert.Empty(effects.Trace);
    }

    [Fact]
    public async Task ShowingPendingRecoveryOffersTheRecordAndNothingWhenThereIsNone()
    {
        var (persistence, _, _, _, effects) = Build();

        persistence.ShowPendingRecovery();
        Assert.Empty(effects.Trace);

        await persistence.SaveRecoveryTextAsync(Text("hello world"), CancellationToken.None);
        persistence.ShowPendingRecovery();

        Assert.Equal(["ShowPendingRecovery:hello world"], effects.Trace);
    }

    [Fact]
    public void StartupRecoveryIsAdoptedAndCanBeForgotten()
    {
        var (persistence, _, _, _, _) = Build();
        var record = new RecoveryTextRecord(DictationSessionId.Create(), Now, "from last time");

        persistence.AdoptStartupRecovery(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Found, record));
        Assert.True(persistence.HasPendingRecovery);
        Assert.Same(record, persistence.PendingRecord);

        persistence.ForgetPendingRecovery();
        Assert.False(persistence.HasPendingRecovery);
        Assert.Null(persistence.PendingRecord);

        persistence.AdoptStartupRecovery(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));
        Assert.False(persistence.HasPendingRecovery);
    }

    [Fact]
    public async Task ADeliveredDictationIsWrittenToHistoryWithItsFlags()
    {
        var (persistence, _, history, _, effects) = Build();

        await persistence.SaveHistoryAsync(Transcript(), "hello world", HistoryWriteIntent.Delivered(wasPolished: true, wasDelivered: true));

        var entry = Assert.Single(history.Added);
        Assert.Equal("hello world", entry.Text);
        Assert.Equal("whisper", entry.EngineId);
        Assert.True(entry.WasPolished);
        Assert.True(entry.WasDelivered);
        Assert.Null(entry.ExpiresAt);
        Assert.Equal(Now, entry.CreatedAt);
        Assert.Equal(30, history.LastRetentionDays);
        Assert.Equal(["NotifyHistoryChanged"], effects.Trace);
    }

    [Fact]
    public async Task BlankTextIsNeverAHistoryEntryEvenWhenForced()
    {
        var (persistence, _, history, _, effects) = Build();

        await persistence.SaveHistoryAsync(Transcript(), "   ", HistoryWriteIntent.EscapeRecovery(wasPolished: false, Now.AddHours(24)));

        Assert.Empty(history.Added);
        Assert.Empty(effects.Trace);
    }

    [Fact]
    public async Task AHeldDictationRecordsItsFlagsAsFalseAndTheClockIsReadTwice()
    {
        var clock = new SteppingClock(Now);
        var (persistence, _, history, _, _) = Build(historyEnabled: () => true, clock: clock);

        await persistence.SaveHistoryAsync(Transcript(), "hello world", HistoryWriteIntent.Held(wasPolished: false));

        var entry = Assert.Single(history.Added);
        Assert.False(entry.WasPolished);
        Assert.False(entry.WasDelivered);
        Assert.Equal(Now, entry.CreatedAt);
        Assert.Equal(Now.AddSeconds(1), history.LastNow);
    }

    [Fact]
    public async Task TheRecoverySaveForwardsTheCallersToken()
    {
        var (persistence, recovery, _, _, _) = Build();
        using var cancellation = new CancellationTokenSource();

        await persistence.SaveRecoveryTextAsync(Text("hello world"), cancellation.Token);

        Assert.Equal(cancellation.Token, recovery.LastToken);
    }

    [Fact]
    public async Task ASecondSaveReplacesThePendingRecordEvenWhenTheDiskRefusesIt()
    {
        var (persistence, recovery, _, _, _) = Build();
        await persistence.SaveRecoveryTextAsync(Text("first"), CancellationToken.None);
        recovery.SaveSucceeds = false;

        await persistence.SaveRecoveryTextAsync(Text("second"), CancellationToken.None);

        Assert.Equal("second", persistence.PendingRecord?.Text);
        Assert.Equal("first", recovery.Saved?.Text);
    }

    [Fact]
    public async Task HistorySwitchedOffIsHonouredForAnOrdinaryDictation()
    {
        var (persistence, _, history, _, effects) = Build(historyEnabled: false);

        await persistence.SaveHistoryAsync(Transcript(), "hello world", HistoryWriteIntent.Held(wasPolished: false));

        Assert.Empty(history.Added);
        Assert.Empty(effects.Trace);
    }

    [Fact]
    public async Task AnEscapeRecoveryIsWrittenEvenWithHistoryOffAndExpiresWhenAsked()
    {
        var (persistence, _, history, _, _) = Build(historyEnabled: false);
        var expiresAt = Now.AddHours(24);

        await persistence.SaveHistoryAsync(Transcript(), "kept words", HistoryWriteIntent.EscapeRecovery(wasPolished: false, expiresAt));

        var entry = Assert.Single(history.Added);
        Assert.Equal(expiresAt, entry.ExpiresAt);
        Assert.False(entry.WasDelivered);
    }

    [Fact]
    public async Task AFailedHistoryWriteDoesNotAnnounceAChange()
    {
        var (persistence, _, history, _, effects) = Build();
        history.AddSucceeds = false;

        await persistence.SaveHistoryAsync(Transcript(), "hello world", HistoryWriteIntent.Held(wasPolished: false));

        Assert.Empty(effects.Trace);
    }

    [Fact]
    public async Task TheHistorySwitchIsReadAtTheWriteNotAtConstruction()
    {
        var enabled = false;
        var (persistence, _, history, _, _) = Build(historyEnabled: () => enabled);

        await persistence.SaveHistoryAsync(Transcript(), "first", HistoryWriteIntent.Held(wasPolished: false));
        enabled = true;
        await persistence.SaveHistoryAsync(Transcript(), "second", HistoryWriteIntent.Held(wasPolished: false));

        Assert.Equal(["second"], history.Added.Select(entry => entry.Text));
    }

    private static ProcessedText Text(string text) => new(DictationSessionId.Create(), text);

    private static Transcript Transcript() => new(DictationSessionId.Create(), "hello world", "whisper");

    private static (SessionPersistence Persistence, FakeRecoveryStore Recovery, FakeHistoryStore History, FakeLogger Log, FakeEffects Effects) Build(
        bool historyEnabled = true) => Build(() => historyEnabled);

    private static (SessionPersistence Persistence, FakeRecoveryStore Recovery, FakeHistoryStore History, FakeLogger Log, FakeEffects Effects) Build(
        Func<bool> historyEnabled,
        TimeProvider? clock = null)
    {
        var recovery = new FakeRecoveryStore();
        var history = new FakeHistoryStore();
        var log = new FakeLogger();
        var effects = new FakeEffects();
        var persistence = new SessionPersistence(
            recovery,
            history,
            log,
            clock ?? new FrozenClock(Now),
            () => new HistoryPreferences(historyEnabled(), RetentionDays: 30),
            effects);
        return (persistence, recovery, history, log, effects);
    }

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public RecoveryTextRecord? Saved { get; private set; }

        public int SaveCalls { get; private set; }

        public bool SaveSucceeds { get; set; } = true;

        public bool ClearSucceeds { get; set; } = true;

        public bool Cleared { get; private set; }

        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public CancellationToken LastToken { get; private set; }

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            LastToken = cancellationToken;
            if (SaveSucceeds)
            {
                Saved = record;
            }

            return Task.FromResult(SaveSucceeds);
        }

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared = ClearSucceeds;
            return Task.FromResult(ClearSucceeds);
        }
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Added { get; } = [];

        public int LastRetentionDays { get; private set; }

        public DateTimeOffset LastNow { get; private set; }

        public bool AddSucceeds { get; set; } = true;

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult(Added, HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            LastRetentionDays = retentionDays;
            LastNow = now;
            if (AddSucceeds)
            {
                Added.Add(entry);
            }

            return Task.FromResult(new HistoryOperationResult(AddSucceeds));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Each read is one second later than the last, so two reads are distinguishable.</summary>
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() => start.AddSeconds(_reads++);
    }

    private sealed class FakeLogger : IAppLogger
    {
        public List<AppLogEntry> Entries { get; } = [];

        public IEnumerable<AppEventCode> Codes => Entries.Select(entry => entry.Event);

        public void Write(AppLogEntry entry) => Entries.Add(entry);
    }

    private sealed class FakeEffects : ISessionPersistenceEffects
    {
        public List<string> Trace { get; } = [];

        public void ShowPendingRecovery(RecoveryTextRecord record) => Trace.Add($"ShowPendingRecovery:{record.Text}");

        public void ClearRecoveredText() => Trace.Add("ClearRecoveredText");

        public void NotifyHistoryChanged() => Trace.Add("NotifyHistoryChanged");
    }
}
