using System.Buffers.Binary;
using System.Collections.Concurrent;
using EnviousWispr.App.Composition;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Reliability;
using EnviousWispr.Storage.Reliability;

namespace EnviousWispr.Architecture.Tests;

public sealed class Wave2RecoveryAndDeliveryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RecoverySpoolStoreStreamsEncryptedAudioAndRecoversPrefixAfterTornCrashTail()
    {
        using var temp = new TempFolder();
        var store = new RecoverySpoolStore(temp.Path);
        var sessionId = DictationSessionId.Create();
        var created = new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
        var header = new RecoverySpoolHeader(
            sessionId,
            SampleRate: 16_000,
            Channels: 1,
            LanguageCode: "fr",
            AutoLanguageDetection: false,
            CreatedAt: created);

        var writer = await store.OpenSessionAsync(header, canPersistRecovery: true);
        Assert.NotNull(writer);

        var packet1 = CreateSineSamples(16_000, 0.25f);
        var packet2 = CreateSineSamples(8_000, 0.5f);
        writer!.EnqueueSamples(packet1);
        await writer.FlushAsync().WaitAsync(TestTimeout);
        writer.EnqueueSamples(packet2);
        await writer.MarkFinalizingAsync().WaitAsync(TestTimeout);
        var filePath = writer.FilePath;
        await writer.DisposeAsync().AsTask().WaitAsync(TestTimeout);

        Assert.True(File.Exists(filePath));
        var rawBytes = await File.ReadAllBytesAsync(filePath);
        Assert.Equal("EWREC1"u8.ToArray(), rawBytes[..6]);
        var rawAscii = System.Text.Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain(sessionId.Value.ToString(), rawAscii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"fr\"", rawAscii, StringComparison.OrdinalIgnoreCase);

        // Simulate a partial torn frame written right before crash/power loss.
        await using (var append = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await append.WriteAsync(new byte[] { 0x45, 0x52, 0x4D, 0x46, 0x01, 0x99, 0xAA });
        }

        // Emulate a relaunched process with a fresh RecoverySpoolStore instance over the same directory.
        var relaunchStore = new RecoverySpoolStore(temp.Path);
        var orphans = relaunchStore.ListOrphanSpools();
        var orphan = Assert.Single(orphans);
        Assert.Equal(sessionId, orphan.SessionId);
        Assert.Equal("fr", orphan.LanguageCode);
        Assert.False(orphan.AutoLanguageDetection);
        Assert.Equal(24_000, orphan.SampleCount);

        var read = await relaunchStore.ReadAsync(orphan.FilePath);
        Assert.True(read.Succeeded);
        Assert.True(read.TruncatedTail);
        Assert.Equal(RecoverySpoolStatus.Finalizing, read.Status);
        Assert.NotNull(read.Header);
        Assert.Equal(24_000, read.Samples.Length);
        Assert.Equal(packet1.Concat(packet2), read.Samples);

        Assert.True(await relaunchStore.DeleteSessionAsync(sessionId));
        Assert.Empty(relaunchStore.ListOrphanSpools());
    }

    [Fact]
    public async Task RecoverySpoolStoreRefusesCreationWhenAdmissionOrDiskHeadroomDisallowsPersistence()
    {
        using var temp = new TempFolder();
        var store = new RecoverySpoolStore(
            temp.Path,
            availableDiskBytesProvider: _ => SystemResourceAdmissionPolicy.MinimumRecoveryDiskBytes - 1);

        var header = new RecoverySpoolHeader(
            DictationSessionId.Create(),
            16_000,
            1,
            LanguageCatalog.AutomaticCode,
            AutoLanguageDetection: true,
            DateTimeOffset.UtcNow);

        Assert.Null(await store.OpenSessionAsync(header, canPersistRecovery: true));
        Assert.Null(await new RecoverySpoolStore(temp.Path).OpenSessionAsync(header, canPersistRecovery: false));
        Assert.Empty(store.ListOrphanSpools());
    }

    [Fact]
    public async Task WasapiAudioCaptureAttachRecoverySpoolSinkCapturesPreRollAndLiveSamplesCleanly()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
        var recorder = new FakeRecorderSession(new AudioBufferFormat(16_000, 1, AudioSampleEncoding.Ieee754Single));
        await using var capture = new WasapiAudioCapture(
            new SingleRecorderFactory(recorder),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(1),
            clock,
            static _ => false);

        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);
        var warmPrimingId = DictationSessionId.Create();
        Assert.True((await capture.StartAsync(new AudioCaptureRequest(warmPrimingId, new AudioDeviceId("mic-1")))).Succeeded);
        recorder.EmitFloats([0.05f]);
        await capture.StopAsync().WaitAsync(TestTimeout);

        var preRoll = new float[] { 0.1f, 0.2f };
        recorder.EmitFloats(preRoll);

        var streamed = new List<float>();
        capture.AttachRecoverySpoolSink(chunk => streamed.AddRange(chunk.ToArray()));

        var sessionId = DictationSessionId.Create();
        var startResult = await capture.StartAsync(new AudioCaptureRequest(sessionId, new AudioDeviceId("mic-1")));
        Assert.True(startResult.Succeeded);

        var live = new float[] { 0.3f, 0.4f, 0.5f };
        recorder.EmitFloats(live);

        var captured = await capture.StopAsync().WaitAsync(TestTimeout);
        capture.AttachRecoverySpoolSink(null);

        Assert.Equal(AudioCaptureOutcome.Completed, captured.Outcome);
        Assert.Equal(preRoll.Concat(live), captured.Samples.ToArray());
        Assert.Equal(captured.Samples.ToArray(), streamed);
    }

    [Fact]
    public async Task JsonApplicationRunStateStoreRecordsAndClearsActiveRecoverySpoolMetadata()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "run-state.json");
        using var store = new JsonApplicationRunStateStore(path);
        var startedAt = new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
        var start = await store.BeginRunAsync(startedAt);
        var spoolId = Guid.NewGuid();

        Assert.True(await store.SetActiveRecoverySpoolAsync(start.RunId, spoolId, RecoverySpoolStatus.Recording, startedAt.AddSeconds(1)));
        var recordingJson = await File.ReadAllTextAsync(path);
        Assert.Contains(spoolId.ToString(), recordingJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recording", recordingJson, StringComparison.OrdinalIgnoreCase);

        Assert.True(await store.SetActiveRecoverySpoolAsync(start.RunId, spoolId, RecoverySpoolStatus.Finalizing, startedAt.AddSeconds(2)));
        var finalizingJson = await File.ReadAllTextAsync(path);
        Assert.Contains("Finalizing", finalizingJson, StringComparison.OrdinalIgnoreCase);

        Assert.True(await store.SetActiveRecoverySpoolAsync(start.RunId, null, null, startedAt.AddSeconds(3)));
        var clearedJson = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(spoolId.ToString(), clearedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionPersistenceDeletesSpoolImmediatelyOnDiscardAndOnlyAfterQueuedHistorySettles()
    {
        using var temp = new TempFolder();
        var spoolDir = Path.Combine(temp.Path, RecoverySpoolConstants.DirectoryName);
        var spoolStore = new RecoverySpoolStore(spoolDir);
        var historyStore = new GatedFakeHistoryStore();
        var runState = new FakeRunStateStore();
        var runId = Guid.NewGuid();
        var logger = new TestLogger();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
        Action<ReadOnlyMemory<float>>? attachedSink = null;

        var persistence = new SessionPersistence(
            new InMemoryRecoveryTextStore(),
            historyStore,
            logger,
            clock,
            () => HistoryPreferences.Default,
            new NullPersistenceEffects(),
            audioArchive: null,
            recoverySpoolStore: spoolStore,
            runStateStore: runState,
            runIdAccessor: () => runId,
            attachCaptureSpoolSink: () => sink => attachedSink = sink);

        // 1. Cancelled take deletes spool immediately.
        var cancelledId = DictationSessionId.Create();
        persistence.BeginAudioSpoolCapture();
        Assert.NotNull(attachedSink);
        attachedSink!(CreateSineSamples(4_000, 0.25f));
        await persistence.CommitOrOpenAudioSpoolAsync(cancelledId, DictationPreferences.Default, canPersistRecovery: true);
        Assert.Single(Directory.GetFiles(spoolDir, "*.ewrec"));
        await persistence.DiscardAudioSpoolAsync(cancelledId);
        Assert.Empty(Directory.GetFiles(spoolDir, "*.ewrec"));

        // 2. Delivered take deletes spool ONLY AFTER queued SaveHistoryAsync settles.
        var deliveredId = DictationSessionId.Create();
        persistence.BeginAudioSpoolCapture();
        await persistence.CommitOrOpenAudioSpoolAsync(deliveredId, DictationPreferences.Default, canPersistRecovery: true);
        attachedSink!(CreateSineSamples(16_000, 0.3f));
        await persistence.FlushAndFinalizeAudioSpoolAsync(deliveredId);
        Assert.Single(Directory.GetFiles(spoolDir, "*.ewrec"));

        historyStore.BlockNextAdd();
        var transcript = new Transcript(deliveredId, "hello recovery spool", "parakeet-v3", []);
        persistence.QueueHistorySaveAndSpoolCleanup(
            deliveredId,
            () => persistence.SaveHistoryAsync(
                transcript,
                "hello recovery spool",
                HistoryWriteIntent.Delivered(wasPolished: false, wasDelivered: true)));

        // Still on disk while SaveHistoryAsync is pending.
        await historyStore.AddEntered.Task.WaitAsync(TestTimeout);
        Assert.Single(Directory.GetFiles(spoolDir, "*.ewrec"));

        var pasteLastWait = persistence.WaitForPendingHistoryAsync(CancellationToken.None);
        Assert.False(pasteLastWait.IsCompleted);

        historyStore.ReleaseNextAdd();
        await pasteLastWait.WaitAsync(TestTimeout);
        Assert.Single(historyStore.Added);
        Assert.Empty(Directory.GetFiles(spoolDir, "*.ewrec"));
    }

    [Fact]
    public async Task StartupRecoveryReplayCoordinatorZeroCostNoOpWhenEmptyAndNeverAutoPastesOnReplay()
    {
        using var temp = new TempFolder();
        var spoolStore = new RecoverySpoolStore(temp.Path);
        var logger = new TestLogger();
        var statuses = new List<DictationStatus>();
        var engine = new CountingEngine("replayed phrase");
        var history = new GatedFakeHistoryStore();
        var persistence = CreatePersistence(history, logger, temp.Path, spoolStore);

        var coordinator = CreateReplayCoordinator(
            spoolStore,
            () => engine,
            persistence,
            logger,
            statuses.Add,
            minimumSpokenSamples: RecoverySpoolConstants.MinimumSpokenSamples);

        var emptySummary = await coordinator.ReplayPendingSpoolsAsync();
        Assert.Equal(0, emptySummary.FoundCount);
        Assert.Equal(0, engine.Calls);
        Assert.Empty(statuses);

        // Seed 1 too-short spool (< 8000 samples) and 1 valid spool (> 8000 samples).
        var shortId = await WriteOrphanSpoolAsync(spoolStore, sampleCount: 2_000, "en");
        var validId = await WriteOrphanSpoolAsync(spoolStore, sampleCount: 16_000, "fr");
        Assert.Equal(2, new RecoverySpoolStore(temp.Path).ListOrphanSpools().Count);

        var summary = await coordinator.ReplayPendingSpoolsAsync().WaitAsync(TestTimeout);
        Assert.Equal(2, summary.FoundCount);
        Assert.Equal(1, summary.ReplayedCount);
        Assert.Equal(1, summary.SavedCount);
        Assert.Equal(1, engine.Calls);

        var saved = Assert.Single(history.Added);
        Assert.False(saved.WasDelivered); // Never auto-pasted!
        Assert.Equal("replayed phrase", saved.Text);
        Assert.Contains(statuses, s => s.State == DictationOverlayState.Recovering && s.Action?.Kind == PillActionKind.DiscardRecovery);
        Assert.Contains(AppEventCode.ApplicationRunRecovered, logger.Codes);
        Assert.Contains(AppEventCode.DictationSessionRecovered, logger.Codes);
        Assert.Empty(new RecoverySpoolStore(temp.Path).ListOrphanSpools());
    }

    [Fact]
    public async Task StartupRecoveryReplayCoordinatorYieldsCleanlyBetweenItemsForLiveDictationAndSupportsDiscard()
    {
        using var temp = new TempFolder();
        var spoolStore = new RecoverySpoolStore(temp.Path);
        var first = await WriteOrphanSpoolAsync(spoolStore, sampleCount: 12_000, "en", TimeSpan.FromMinutes(-5));
        var second = await WriteOrphanSpoolAsync(spoolStore, sampleCount: 12_000, "en", TimeSpan.FromMinutes(-1));

        var logger = new TestLogger();
        var history = new GatedFakeHistoryStore();
        var persistence = CreatePersistence(history, logger, temp.Path, spoolStore);
        var statuses = new List<DictationStatus>();

        StartupRecoveryReplayCoordinator? coordinator = null;
        var callbackCalls = 0;
        var yieldingEngine = new CallbackEngine(async (_, ct) =>
        {
            callbackCalls++;
            if (callbackCalls == 1)
            {
                await Task.CompletedTask;
                return "first orphan saved";
            }

            // Trigger live-dictation yield while the 2nd orphan starts ASR: cancels ASR without deleting 2nd spool.
            coordinator!.RequestYieldBetweenItems();
            ct.ThrowIfCancellationRequested();
            return "never saved";
        });

        coordinator = CreateReplayCoordinator(
            spoolStore,
            () => yieldingEngine,
            persistence,
            logger,
            statuses.Add,
            minimumSpokenSamples: RecoverySpoolConstants.MinimumSpokenSamples);

        var yieldSummary = await coordinator.ReplayPendingSpoolsAsync().WaitAsync(TestTimeout);
        Assert.True(yieldSummary.YieldedToLiveDictation);
        Assert.Equal(1, yieldSummary.SavedCount);
        var remaining = Assert.Single(new RecoverySpoolStore(temp.Path).ListOrphanSpools());
        Assert.Equal(second, remaining.SessionId);

        // Now test Discard action on the remaining item while ASR holds.
        var heldEngine = new HeldEngine();
        var discardCoordinator = CreateReplayCoordinator(
            spoolStore,
            () => heldEngine,
            persistence,
            logger,
            statuses.Add,
            minimumSpokenSamples: RecoverySpoolConstants.MinimumSpokenSamples);

        var replayTask = discardCoordinator.ReplayPendingSpoolsAsync();
        await heldEngine.Entered.Task.WaitAsync(TestTimeout);
        var discarded = await discardCoordinator.DiscardActiveOrNextAsync().WaitAsync(TestTimeout);
        Assert.True(discarded);

        var discardSummary = await replayTask.WaitAsync(TestTimeout);
        Assert.Equal(1, discardSummary.DiscardedCount);
        Assert.Empty(new RecoverySpoolStore(temp.Path).ListOrphanSpools());
        Assert.Contains(AppEventCode.RecoveryTextCleared, logger.Codes);
    }

    [Fact]
    public async Task ExitDuringFinalizationCompletesInsideGraceWindowAndPreservesSpoolWhenBudgetExpires()
    {
        using var temp = new TempFolder();
        var spoolDir = Path.Combine(temp.Path, RecoverySpoolConstants.DirectoryName);
        var spoolStore = new RecoverySpoolStore(spoolDir);
        var logger = new TestLogger();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));

        // Part A: Mid-take exit completes within the bounded window without prematurely cancelling ASR.
        {
            var world = BuildHarness(spoolStore, clock, logger, new HeldEngine("finished inside grace"));
            await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
            world.Capture.EmitLiveAudio(CreateSineSamples(16_000, 0.4f));
            world.HoldEngine.Hold = true;
            var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
            await world.HoldEngine.Entered.Task.WaitAsync(TestTimeout);

            var lifetime = BuildExitLifetime(world, clock);
            var exitTask = lifetime.ExitAsync();

            // Advance 2 seconds (well inside 9s window) then release engine cleanly.
            clock.Advance(TimeSpan.FromSeconds(2));
            world.HoldEngine.Release();

            var report = await exitTask.WaitAsync(TestTimeout);
            await release.WaitAsync(TestTimeout);
            Assert.True(report.Clean);
            Assert.False(world.HoldEngine.WasCancelled);
            Assert.Single(world.History.Added);
            Assert.Empty(new RecoverySpoolStore(spoolDir).ListOrphanSpools());
        }

        // Part B: Finalization exceeding 9s window cancels ASR cleanly AND keeps encrypted spool on disk for next launch!
        {
            var spoolDir2 = Path.Combine(temp.Path, "partB-spools");
            var spoolStore2 = new RecoverySpoolStore(spoolDir2);
            var world = BuildHarness(spoolStore2, clock, logger, new HeldEngine("stuck in long asr"));
            await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
            world.Capture.EmitLiveAudio(CreateSineSamples(16_000, 0.4f));
            world.HoldEngine.Hold = true;
            var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
            await world.HoldEngine.Entered.Task.WaitAsync(TestTimeout);

            var lifetime = BuildExitLifetime(world, clock);
            var exitTask = lifetime.ExitAsync();
            await clock.WhenTimerRegisteredAsync().WaitAsync(TestTimeout);

            // Advance past 9s preservation grace so CancelProcessing runs and preserves the encrypted spool.
            clock.Advance(ApplicationLifetime.MaxDictationPreservationWindow + TimeSpan.FromMilliseconds(50));

            var report = await exitTask.WaitAsync(TestTimeout);
            Assert.True(report.Clean);
            Assert.True(world.HoldEngine.WasCancelled);
            Assert.Empty(world.History.Added);

            // Encrypted spool survived exit intact on disk and replays cleanly on next launch!
            var relaunchedSpools = new RecoverySpoolStore(spoolDir2);
            var orphan = Assert.Single(relaunchedSpools.ListOrphanSpools());
            Assert.Equal(16_000, orphan.SampleCount);

            var replayCoordinator = CreateReplayCoordinator(
                relaunchedSpools,
                () => new CountingEngine("salvaged on next launch"),
                CreatePersistence(world.History, logger, temp.Path, relaunchedSpools),
                logger,
                _ => { },
                RecoverySpoolConstants.MinimumSpokenSamples);
            var replayed = await replayCoordinator.ReplayPendingSpoolsAsync().WaitAsync(TestTimeout);
            Assert.Equal(1, replayed.SavedCount);
            Assert.Equal("salvaged on next launch", Assert.Single(world.History.Added).Text);
            Assert.Empty(relaunchedSpools.ListOrphanSpools());
        }
    }

    [Fact]
    public void PasteRevealPatienceMaxCapRaisedTo2500MsAndGrowsSlowlyDecays()
    {
        Assert.Equal(2_500, PasteRevealPatience.CapMilliseconds);
        var patience = new PasteRevealPatience();
        var floor = TimeSpan.FromMilliseconds(200);
        var token = patience.RecordDispatch("code");
        Assert.True(patience.RecordSighting(token, revealMilliseconds: 1_800, floor));
        var nextDelay = patience.GetSettleDelay("code", floor);
        Assert.Equal(2_500, nextDelay.TotalMilliseconds);
    }

    [Fact]
    public async Task CheckedMissRetainsUnmarkedClipboardAbortsGiveBackAndDrainsCleanlyOnSuccess()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));

        // 1. Checked-miss NotObserved on eligible target aborts GiveBack, rewrites unmarked, surfaces notice.
        {
            var ownership = new ClipboardBoardOwnership();
            var tracker = new ClipboardCleanupTracker(ownership, clock);
            var restoreCount = 0;
            var rewriteCount = 0;
            ClipboardCleanupOutcome? captured = null;
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            tracker.SchedulePostPaste(new PendingPostPasteCleanup
            {
                Request = DummyCommitRequest(TextTargetKind.StandardEdit, isScreenDerived: false),
                EligibleForCheckedMissRetention = true,
                SnapshotPresent = true,
                OwnedSequence = 42,
                FallbackText = "dictated words",
                SettleDelay = TimeSpan.FromMilliseconds(200),
                SettleFloor = TimeSpan.FromMilliseconds(200),
                ObserveArrivalAsync = _ => Task.FromResult((PasteArrivalStatus.NotObserved, 240)),
                IsSequenceStillOurs = () => true,
                RestoreClipboardAsync = () =>
                {
                    restoreCount++;
                    return Task.FromResult((true, false));
                },
                RewriteUnmarkedFallbackAsync = () =>
                {
                    rewriteCount++;
                    return Task.FromResult((true, true));
                },
                OnCompleted = outcome =>
                {
                    captured = outcome;
                    settled.TrySetResult();
                },
            });

            Assert.True(tracker.HasPendingCleanup);
            await clock.WhenTimerRegisteredAsync().WaitAsync(TestTimeout);
            clock.Advance(TimeSpan.FromMilliseconds(200));

            Assert.True(await tracker.DrainAsync(TestTimeout));
            await settled.Task.WaitAsync(TestTimeout);
            Assert.Equal(0, restoreCount);
            Assert.Equal(1, rewriteCount);
            Assert.NotNull(captured);
            Assert.True(captured!.RetainedOnClipboard);
            Assert.False(captured.ClipboardRestored);
        }

        // 2. Confirmed paste runs GiveBack AFTER ObserveArrivalAsync and drains cleanly on shutdown.
        {
            var ownership = new ClipboardBoardOwnership();
            var tracker = new ClipboardCleanupTracker(ownership, clock);
            var order = new List<string>();
            ClipboardCleanupOutcome? captured = null;

            tracker.SchedulePostPaste(new PendingPostPasteCleanup
            {
                Request = DummyCommitRequest(TextTargetKind.StandardEdit, isScreenDerived: false),
                EligibleForCheckedMissRetention = true,
                SnapshotPresent = true,
                OwnedSequence = 99,
                FallbackText = "dictated words",
                SettleDelay = TimeSpan.FromMilliseconds(200),
                SettleFloor = TimeSpan.FromMilliseconds(200),
                ObserveArrivalAsync = _ =>
                {
                    order.Add("observe");
                    return Task.FromResult((PasteArrivalStatus.Confirmed, 210));
                },
                IsSequenceStillOurs = () => true,
                RestoreClipboardAsync = () =>
                {
                    order.Add("giveback");
                    return Task.FromResult((true, false));
                },
                RewriteUnmarkedFallbackAsync = () =>
                {
                    order.Add("rewrite");
                    return Task.FromResult((false, false));
                },
                OnCompleted = outcome => captured = outcome,
            });

            await clock.WhenTimerRegisteredAsync().WaitAsync(TestTimeout);
            clock.Advance(TimeSpan.FromMilliseconds(200));
            Assert.True(await tracker.DrainAsync(TestTimeout));
            Assert.Equal(["observe", "giveback"], order);
            Assert.NotNull(captured);
            Assert.True(captured!.ClipboardRestored);
            Assert.False(captured.RetainedOnClipboard);
        }
    }

    [Fact]
    public async Task QuickAddBeginTakeoverPreemptsPendingCleanupRestoringSnapshotAndHoldsLease()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
        var ownership = new ClipboardBoardOwnership();
        var tracker = new ClipboardCleanupTracker(ownership, clock);
        var restored = 0;
        ClipboardCleanupOutcome? captured = null;

        tracker.SchedulePostPaste(new PendingPostPasteCleanup
        {
            Request = DummyCommitRequest(TextTargetKind.Browser, isScreenDerived: false),
            EligibleForCheckedMissRetention = true,
            SnapshotPresent = true,
            OwnedSequence = 55,
            FallbackText = "pasted text",
            SettleDelay = TimeSpan.FromMilliseconds(500),
            SettleFloor = TimeSpan.FromMilliseconds(350),
            ObserveArrivalAsync = ct => Task.Delay(TimeSpan.FromSeconds(1), clock, ct).ContinueWith(_ => (PasteArrivalStatus.NotObserved, 0), CancellationToken.None),
            IsSequenceStillOurs = () => true,
            RestoreClipboardAsync = () =>
            {
                Interlocked.Increment(ref restored);
                return Task.FromResult((true, false));
            },
            RewriteUnmarkedFallbackAsync = () => Task.FromResult((false, false)),
            OnCompleted = outcome => captured = outcome,
        });

        await clock.WhenTimerRegisteredAsync().WaitAsync(TestTimeout);

        // QuickAdd calls BeginTakeoverAsync without advancing the 500ms timer: must preempt immediately & restore snapshot!
        await using var lease = await ownership.BeginTakeoverAsync().WaitAsync(TestTimeout);
        Assert.Equal(1, Volatile.Read(ref restored));
        Assert.NotNull(captured);
        Assert.True(captured!.Preempted);
        Assert.True(captured.ClipboardRestored);
        Assert.False(tracker.HasPendingCleanup);

        // While QuickAdd holds the takeover lease across its 400ms copy window, stale cleanup restores are rejected.
        Assert.Null(await ownership.TryBeginRestoreAsync(0));
    }

    [Fact]
    public void DeliveryClipboardDiagnosticsSuppressesDeclinedLogWhileCleanupPending()
    {
        var deferred = new DeliveryResult(
            DictationSessionId.Create(),
            Delivered: true,
            ClipboardFallback: false,
            Route: TextDeliveryRoute.ClipboardPaste,
            ClipboardRestored: false,
            ClipboardCleanupPending: true);
        Assert.Null(DeliveryClipboardDiagnostics.EntryFor(deferred, DateTimeOffset.UtcNow));

        var declined = deferred with { ClipboardCleanupPending = false };
        var entry = DeliveryClipboardDiagnostics.EntryFor(declined, DateTimeOffset.UtcNow);
        Assert.NotNull(entry);
        Assert.Equal(AppEventCode.TextDeliveryClipboardRestoreDeclined, entry!.Event);
    }

    [Fact]
    public async Task StartupReplayPreservesExistingRecoveryTextAppliesLanguageAndPersistsSafely()
    {
        // 1. Recovering pill stays open (no fixed auto-dwell) while replay is active so Discard stays reachable.
        var recoveringDef = OverlayPillCatalog.Resolve(DictationStatus.Recovering());
        Assert.False(recoveringDef.AutoDwells);

        using var temp = new TempFolder();
        var spoolStore = new RecoverySpoolStore(temp.Path);
        Assert.False(spoolStore.HasCandidateFiles());

        var orphanId = await WriteOrphanSpoolAsync(spoolStore, sampleCount: 16_000, "fr");
        Assert.True(new RecoverySpoolStore(temp.Path).HasCandidateFiles());

        // 2. Successful History replay does NOT overwrite an unrelated pending Home recovery copy.
        var logger = new TestLogger();
        var existingRecord = new RecoveryTextRecord(DictationSessionId.Create(), DateTimeOffset.UtcNow, "existing home copy");
        var recoveryStore = new InMemoryRecoveryTextStore();
        await recoveryStore.SaveAsync(existingRecord);
        var history = new GatedFakeHistoryStore();
        var persistence = new SessionPersistence(
            recoveryStore,
            history,
            logger,
            TimeProvider.System,
            () => HistoryPreferences.Default,
            new NullPersistenceEffects(),
            audioArchive: null,
            recoverySpoolStore: spoolStore);
        persistence.AdoptStartupRecovery(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Found, existingRecord));

        var seenLanguages = new List<string?>();
        string? activeOverride = null;
        var engine = new CallbackEngine((_, _) =>
        {
            seenLanguages.Add(activeOverride);
            return Task.FromResult("replayed french");
        });
        var finalizer = new TranscriptFinalizer(
            new DeterministicTextPipeline(),
            new PolishExecutor(new NullAdmission(), new NullFinalizationEffects(), () => []),
            new NullFinalizationEffects(),
            new SnippetExpansionStage(new SnippetExpander(), TimeProvider.System, () => System.Globalization.CultureInfo.InvariantCulture, _ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(1)));
        var coordinator = new StartupRecoveryReplayCoordinator(
            new CrossProcessSimSpoolStore(spoolStore.DirectoryPath),
            () => engine,
            finalizer,
            persistence,
            () => new FinalizationOptions([], DeterministicTextOptions.From(DictationPreferences.Default), SnippetVocabulary.Empty, null),
            () => HistoryPreferences.Default,
            () => DictationPreferences.Default with { WhisperLanguage = "es" },
            logger,
            TimeProvider.System,
            RecoveryReplayStatusCallbacks.FromStatusSink(_ => { }),
            () => false,
            RecoverySpoolConstants.MinimumSpokenSamples,
            applyLanguageSnapshot: prefs => activeOverride = prefs?.WhisperLanguage);

        var summary = await coordinator.ReplayPendingSpoolsAsync().WaitAsync(TestTimeout);
        Assert.Equal(1, summary.SavedCount);
        Assert.Equal(["fr"], seenLanguages);
        Assert.Null(activeOverride);
        Assert.Equal("existing home copy", persistence.PendingRecord?.Text);
        Assert.Equal("existing home copy", (await recoveryStore.LoadAsync()).Record?.Text);
        Assert.Contains(orphanId, new[] { orphanId });

        // 3. When History is disabled and durable recovery-text persistence is refused, spool stays on disk!
        var secondId = await WriteOrphanSpoolAsync(spoolStore, sampleCount: 16_000, "de");
        var disabledHistoryPersistence = new SessionPersistence(
            recoveryStore,
            history,
            logger,
            TimeProvider.System,
            () => HistoryPreferences.Default with { IsEnabled = false },
            new NullPersistenceEffects(),
            audioArchive: null,
            recoverySpoolStore: spoolStore)
        {
            CanPersistRecovery = false,
        };
        var unpersistedCoordinator = new StartupRecoveryReplayCoordinator(
            new CrossProcessSimSpoolStore(spoolStore.DirectoryPath),
            () => engine,
            finalizer,
            disabledHistoryPersistence,
            () => new FinalizationOptions([], DeterministicTextOptions.From(DictationPreferences.Default), SnippetVocabulary.Empty, null),
            () => HistoryPreferences.Default with { IsEnabled = false },
            () => DictationPreferences.Default,
            logger,
            TimeProvider.System,
            RecoveryReplayStatusCallbacks.FromStatusSink(_ => { }),
            () => false,
            RecoverySpoolConstants.MinimumSpokenSamples);
        var unpersistedSummary = await unpersistedCoordinator.ReplayPendingSpoolsAsync().WaitAsync(TestTimeout);
        Assert.Equal(1, unpersistedSummary.FailedCount);
        Assert.Equal(0, unpersistedSummary.SavedCount);
        var kept = Assert.Single(new RecoverySpoolStore(temp.Path).ListOrphanSpools());
        Assert.Equal(secondId, kept.SessionId);
    }

    private static TextCommitRequest DummyCommitRequest(TextTargetKind targetKind, bool isScreenDerived) => new(
        new ProcessedText(DictationSessionId.Create(), "dictated words"),
        new ProcessedText(DictationSessionId.Create(), "dictated words"),
        new TargetWindowId(100, 200, "test"),
        new CaretContext(
            new TargetWindowId(100, 200, "test"),
            "el1",
            targetKind,
            "before ",
            "",
            "",
            LeftReachedDocumentStart: false,
            RightReachedDocumentEnd: true,
            IsScreenDerived: isScreenDerived),
        targetKind,
        TextDeliveryOptions.Default);

    private static float[] CreateSineSamples(int count, float amplitude)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * MathF.Sin(i * 0.1f);
        }

        return samples;
    }

    private static async Task<DictationSessionId> WriteOrphanSpoolAsync(
        RecoverySpoolStore store,
        int sampleCount,
        string languageCode,
        TimeSpan ageOffset = default)
    {
        var id = DictationSessionId.Create();
        var writer = await store.OpenSessionAsync(
            new RecoverySpoolHeader(
                id,
                16_000,
                1,
                languageCode,
                string.Equals(languageCode, LanguageCatalog.AutomaticCode, StringComparison.OrdinalIgnoreCase),
                DateTimeOffset.UtcNow.Add(ageOffset)),
            canPersistRecovery: true);
        Assert.NotNull(writer);
        writer!.EnqueueSamples(CreateSineSamples(sampleCount, 0.35f));
        await writer.MarkFinalizingAsync().WaitAsync(TestTimeout);
        await writer.DisposeAsync().AsTask().WaitAsync(TestTimeout);

        // Simulate another process owning nothing in-process by reading from a fresh store in assertions.
        // Force clearing in-process tracking only for test simulation by recreating store when listing!
        return id;
    }

    private static SessionPersistence CreatePersistence(
        GatedFakeHistoryStore history,
        TestLogger logger,
        string folder,
        IRecoverySpoolStore spoolStore) =>
        new(
            new InMemoryRecoveryTextStore(),
            history,
            logger,
            TimeProvider.System,
            () => HistoryPreferences.Default,
            new NullPersistenceEffects(),
            audioArchive: null,
            recoverySpoolStore: spoolStore);

    private static StartupRecoveryReplayCoordinator CreateReplayCoordinator(
        RecoverySpoolStore spoolStore,
        Func<ITranscriptionEngine?> engineAccessor,
        SessionPersistence persistence,
        TestLogger logger,
        Action<DictationStatus> showStatus,
        int minimumSpokenSamples)
    {
        var finalizer = new TranscriptFinalizer(
            new DeterministicTextPipeline(),
            new PolishExecutor(new NullAdmission(), new NullFinalizationEffects(), () => []),
            new NullFinalizationEffects(),
            new SnippetExpansionStage(new SnippetExpander(), TimeProvider.System, () => System.Globalization.CultureInfo.InvariantCulture, _ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(1)));

        return new StartupRecoveryReplayCoordinator(
            new CrossProcessSimSpoolStore(spoolStore.DirectoryPath),
            engineAccessor,
            finalizer,
            persistence,
            () => new FinalizationOptions([], DeterministicTextOptions.From(DictationPreferences.Default), SnippetVocabulary.Empty, null),
            () => HistoryPreferences.Default,
            () => DictationPreferences.Default,
            logger,
            TimeProvider.System,
            RecoveryReplayStatusCallbacks.FromStatusSink(showStatus),
            () => false,
            minimumSpokenSamples);
    }

    private static TestHarness BuildHarness(
        RecoverySpoolStore spoolStore,
        TimeProvider clock,
        TestLogger logger,
        HeldEngine engine)
    {
        var history = new GatedFakeHistoryStore();
        var runState = new FakeRunStateStore();
        var runId = Guid.NewGuid();
        var capture = new FakeSpoolAudioCapture();
        var view = new NullSessionView();

        var runtime = RuntimeComposition.Compose(new RuntimeCompositionParts(
            new InMemoryRecoveryTextStore(),
            history,
            new DeterministicTextPipeline(),
            new NullAdmission(),
            logger,
            new RuntimeShell(
                new NullRuntimeView(),
                () => false,
                () => HistoryPreferences.Default,
                () => [],
                () => null,
                () => engine,
                () => null,
                () => null,
                () => null,
                () => null,
                () => false,
                _ => Task.FromResult<string?>(null),
                () => DictationPreferences.Default),
            clock,
            AudioArchive: null,
            LearnedWordCheck: null,
            RecoverySpoolStore: spoolStore,
            RunStateStore: runState,
            RunId: () => runId,
            AttachCaptureSpoolSink: () => capture.AttachRecoverySpoolSink));

        var controller = new PushToTalkSessionController(
            capture,
            new StubTargetProvider(),
            clock,
            TimeSpan.Zero,
            () => TextDeliveryOptions.Default);

        var coordinator = SessionComposition.Compose(new SessionCompositionParts(
            controller,
            capture,
            runtime,
            new HealthyResourceProbe(),
            runState,
            logger,
            new SessionShell(
                view,
                () => controller.CurrentSession,
                () => DictationPreferences.Default,
                () => engine,
                () => null,
                () => new FinalizationOptions([], DeterministicTextOptions.From(DictationPreferences.Default), SnippetVocabulary.Empty, null),
                () => null,
                () => runId,
                _ => { },
                _ => { },
                () => { },
                () => { },
                () => { }),
            clock));

        return new TestHarness(coordinator, controller, runtime, capture, history, engine, logger, runState, spoolStore);
    }

    private static ApplicationLifetime BuildExitLifetime(TestHarness harness, TimeProvider clock)
    {
        var parts = new LifetimeParts(
            CloseAdmission: () =>
            {
                harness.Runtime.Persistence.SetExitPreservationMode(true);
                harness.Coordinator.CloseForExitPreservation();
            },
            DrainPresentation: () => Task.CompletedTask,
            ShellClosing: () => { },
            AbortPolishRuntime: () => { },
            CancelProcessing: () =>
            {
                harness.Coordinator.Close();
                harness.Coordinator.CancelProcessing();
            },
            ReleaseInputs: [],
            ShutDownSession: harness.Coordinator.ShutdownAsync,
            Quiesce:
            [
                new LifetimeStep("history", () => harness.Runtime.Persistence.DrainPendingHistoryAsync(TimeSpan.FromSeconds(5))),
            ],
            DisposeSessionDependencies: [],
            DisposeShell: [],
            CompleteRun: (_, _) => Task.FromResult(true),
            CloseRunState: () => { },
            DisposeLogger: () => Task.CompletedTask,
            PreserveInFlightDictation: window => harness.Coordinator.WaitForInFlightOnExitAsync(window));

        return new ApplicationLifetime(parts, harness.Logger, clock, new StubTerminator());
    }

    private sealed record TestHarness(
        DictationSessionCoordinator Coordinator,
        PushToTalkSessionController Controller,
        SessionRuntime Runtime,
        FakeSpoolAudioCapture Capture,
        GatedFakeHistoryStore History,
        HeldEngine HoldEngine,
        TestLogger Logger,
        FakeRunStateStore RunState,
        RecoverySpoolStore SpoolStore);

    private sealed class CrossProcessSimSpoolStore(string dir) : IRecoverySpoolStore
    {
        private readonly RecoverySpoolStore _inner = new(dir);
        public string DirectoryPath => _inner.DirectoryPath;
        public Task<IRecoverySpoolWriter?> OpenSessionAsync(RecoverySpoolHeader header, bool canPersistRecovery, CancellationToken cancellationToken = default) =>
            _inner.OpenSessionAsync(header, canPersistRecovery, cancellationToken);
        public IReadOnlyList<RecoverySpoolMetadata> ListOrphanSpools() => _inner.ListOrphanSpools();
        public Task<RecoverySpoolReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default) => _inner.ReadAsync(filePath, cancellationToken);
        public Task<bool> DeleteSessionAsync(DictationSessionId sessionId) => _inner.DeleteSessionAsync(sessionId);
        public Task<bool> DeleteFileAsync(string filePath) => _inner.DeleteFileAsync(filePath);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ew-wave2b-{Guid.NewGuid():N}");
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        private readonly List<TimerEntry> _timers = [];
        private readonly Queue<TaskCompletionSource> _waiters = new();
        private readonly object _sync = new();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override long GetTimestamp()
        {
            lock (_sync)
            {
                return _now.Ticks;
            }
        }

        public Task WhenTimerRegisteredAsync()
        {
            lock (_sync)
            {
                if (_timers.Count > 0)
                {
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(tcs);
                return tcs.Task;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<TimerEntry> due;
            lock (_sync)
            {
                _now = _now.Add(delta);
                due = _timers.Where(t => t.Due <= _now).ToList();
                foreach (var t in due)
                {
                    _timers.Remove(t);
                }
            }

            foreach (var t in due)
            {
                t.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var entry = new TimerEntry(this, callback, state, GetUtcNow().Add(dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.FromDays(365) : dueTime));
            lock (_sync)
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _timers.Add(entry);
                    while (_waiters.TryDequeue(out var w))
                    {
                        w.TrySetResult();
                    }
                }
            }

            return entry;
        }

        private sealed class TimerEntry(ManualClock owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = due;
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    Due = owner._now.Add(dueTime);
                    if (dueTime != Timeout.InfiniteTimeSpan && !owner._timers.Contains(this))
                    {
                        owner._timers.Add(this);
                    }
                }

                return true;
            }
            public void Dispose()
            {
                lock (owner._sync)
                {
                    owner._timers.Remove(this);
                }
            }
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeRecorderSession(AudioBufferFormat format) : IAudioRecorderSession
    {
        public AudioBufferFormat Format { get; } = format;
        public event EventHandler<AudioRecorderData>? DataAvailable;
        public event EventHandler<AudioRecorderStopped>? Stopped;
        public bool IsEndpointMuted() => false;
        public void Start() { }
        public void Stop() => Stopped?.Invoke(this, new AudioRecorderStopped(null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitFloats(float[] samples)
        {
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            DataAvailable?.Invoke(this, new AudioRecorderData(bytes, IsSilent: false));
        }
    }

    private sealed class SingleRecorderFactory(IAudioRecorderSession session) : IAudioRecorderFactory
    {
        public Task<IAudioRecorderSession> CreateAsync(AudioDeviceId? deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(session);
    }

    private sealed class FakeSpoolAudioCapture : IAudioCapture
    {
        private readonly List<float> _samples = [];
        private Action<ReadOnlyMemory<float>>? _sink;
        private DictationSessionId _sessionId;
        public event EventHandler<AudioLevel>? LevelChanged { add { } remove { } }
        public bool IsCapturing { get; private set; }

        public void AttachRecoverySpoolSink(Action<ReadOnlyMemory<float>>? sink) => _sink = sink;

        public void EmitLiveAudio(float[] samples)
        {
            _samples.AddRange(samples);
            _sink?.Invoke(samples);
        }

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            _samples.Clear();
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, _samples.ToArray(), 16_000, 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            _samples.Clear();
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedFakeHistoryStore : IHistoryStore
    {
        private TaskCompletionSource? _gate;
        public TaskCompletionSource AddEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<DictationHistoryEntry> Added { get; } = [];

        public void BlockNextAdd()
        {
            AddEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseNextAdd() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult(Added.ToArray(), HistoryLoadStatus.Loaded));

        public async Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var gate = Volatile.Read(ref _gate);
            if (gate is not null)
            {
                AddEntered.TrySetResult();
                await gate.Task.ConfigureAwait(false);
            }

            lock (Added)
            {
                Added.Add(entry);
            }

            return new HistoryOperationResult(true);
        }

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class CountingEngine(string text) : ITranscriptionEngine
    {
        public string EngineId => "test-engine";
        public int Calls { get; private set; }
        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new Transcript(audio.SessionId, text, EngineId, []));
        }
    }

    private sealed class CallbackEngine(Func<CapturedAudio, CancellationToken, Task<string>> handler) : ITranscriptionEngine
    {
        public string EngineId => "callback-engine";
        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default) =>
            new(audio.SessionId, await handler(audio, cancellationToken), EngineId, []);
    }

    private sealed class HeldEngine(string text = "held text") : ITranscriptionEngine
    {
        public string EngineId => "held-engine";
        public bool Hold { get; set; } = true;
        public bool WasCancelled { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (Hold)
            {
                using var reg = cancellationToken.Register(() =>
                {
                    WasCancelled = true;
                    _gate.TrySetCanceled(cancellationToken);
                });
                await _gate.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new Transcript(audio.SessionId, text, EngineId, []);
        }
    }

    private sealed class TestLogger : IAppLogger
    {
        public ConcurrentBag<AppLogEntry> Entries { get; } = [];
        public IEnumerable<AppEventCode> Codes => Entries.Select(e => e.Event);
        public void Write(AppLogEntry entry) => Entries.Add(entry);
    }

    private sealed class InMemoryRecoveryTextStore : IRecoveryTextStore
    {
        private RecoveryTextRecord? _record;
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_record is null
                ? new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing)
                : new RecoveryTextLoadResult(RecoveryTextLoadStatus.Found, _record));
        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default)
        {
            _record = record;
            return Task.FromResult(true);
        }
        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            _record = null;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRunStateStore : IApplicationRunStateStore
    {
        public Task<ApplicationRunStartResult> BeginRunAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationRunStartResult(Guid.NewGuid(), RunStateLoadStatus.Started, false));
        public Task<bool> HeartbeatAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SetDictationActiveAsync(Guid runId, bool active, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> NoteSystemEndingAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CompleteRunAsync(Guid runId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CompleteRunAsync(Guid runId, DateTimeOffset timestamp, PublicationFence fence, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class NullPersistenceEffects : ISessionPersistenceEffects
    {
        public void ShowPendingRecovery(RecoveryTextRecord record, bool undoOffered) { }
        public void ClearRecoveredText() { }
        public void NotifyHistoryChanged() { }
    }

    private sealed class NullFinalizationEffects : ITranscriptFinalizationEffects
    {
        public void RecordDeterministicProcessingStarted() { }
        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly) { }
        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken) => Task.CompletedTask;
        public void RecordPolishRefused() { }
        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds, bool empty = false) { }
        public void RecordPolishStarted(string providerId) { }
        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) { }
    }

    private sealed class NullAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(false));
    }

    private sealed class NullRuntimeView : IRuntimeView
    {
        public void ShowPreview(LivePreviewFrame? frame) { }
        public void ShowRecoveredText(RecoveryTextLoadResult result, bool undoOffered) { }
        public void ClearRecoveredText() { }
        public void NotifyHistoryChanged() { }
        public void ShowMainWindow() { }
        public void ShowSessionBusy(SessionHolder holder) { }
    }

    private sealed class NullSessionView : ISessionView
    {
        public void ShowStatus(DictationStatus status) { }
        public void ShowNotice(string title, string message, bool isError = false) { }
        public void ShowMainWindow() { }
        public void ReportDelivery(DeliveryReport delivered, string? detectedLanguage) { }
    }

    private sealed class StubTargetProvider : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(101, 200, "test");
    }

    private sealed class HealthyResourceProbe : ISystemResourceProbe
    {
        public SystemResourceSnapshot Probe() =>
            new(AvailableDiskBytes: 10L * 1024 * 1024 * 1024, AvailablePhysicalMemoryBytes: 16UL * 1024 * 1024 * 1024, MemoryLoadPercent: 25);
    }

    private sealed class StubTerminator : IHostTerminator
    {
        public void Terminate(ExitReport report) { }
    }
}
