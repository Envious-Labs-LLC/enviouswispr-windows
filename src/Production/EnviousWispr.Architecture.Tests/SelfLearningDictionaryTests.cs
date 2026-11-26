using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing.Learning;
using EnviousWispr.Presentation;
using EnviousWispr.Presentation.Learning;
using EnviousWispr.Services.Settings;
using StoreLearnMemory = EnviousWispr.Services.Learning.JsonLearnMemoryStore;

namespace EnviousWispr.Architecture.Tests;

public sealed class SelfLearningDictionaryTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ew-learn-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    // ── 1. EditAlignment ─────────────────────────────────────────────────────

    [Fact]
    public void EditAlignmentIsolatesSubstitutionWithCoreLexemesAndTokens()
    {
        var result = EditAlignment.Align(
            "Let us deploy with coober netties soon.",
            "Let us deploy with Kubernetes soon.");

        Assert.False(result.LimitExceeded);
        var run = Assert.Single(result.Runs);
        Assert.Equal("coober netties", run.CoreOriginal);
        Assert.Equal("Kubernetes", run.CoreReplacement);
        Assert.Equal(4, run.OriginalStart);
    }

    [Fact]
    public void EditAlignmentStripsPunctuationAndDropsPureDeletions()
    {
        var result = EditAlignment.Align(
            "Please meet Saurab, and remove extra here.",
            "Please meet Saurabh, and here.");

        Assert.False(result.LimitExceeded);
        var run = Assert.Single(result.Runs);
        Assert.Equal("Saurab", run.CoreOriginal);
        Assert.Equal("Saurabh", run.CoreReplacement);
        Assert.Contains(result.DroppedRuns, d => d.Reason == EditAlignment.DropReason.InsertionOrDeletionOnly);
    }

    [Fact]
    public void EditAlignmentBailsOutAboveTokenCellLimit()
    {
        var words = Enumerable.Range(0, 550).Select(i => $"w{i}");
        var text = string.Join(' ', words);
        var result = EditAlignment.Align(text, text + " tail");
        Assert.True(result.LimitExceeded);
        Assert.Empty(result.Runs);
    }

    // ── 2. CorrectionCandidateFilter ─────────────────────────────────────────

    [Fact]
    public void CandidateFilterClassifiesStopwordsContractionsNumbersBlockedAndOwnedPairs()
    {
        var existingUserWord = new CustomWordEntry("oldalias", "Kubernetes");
        var ownedElsewhere = new CustomWordEntry("anthropic", "Anthropic");
        var runs = new[]
        {
            new EditAlignment.Run("the", "an", 0, 1, 0, 1, [EditAlignment.Label.Substitute]),
            new EditAlignment.Run("cant", "can't", 2, 3, 2, 3, [EditAlignment.Label.Substitute]),
            new EditAlignment.Run("twenty-first", "21st", 4, 5, 4, 5, [EditAlignment.Label.Substitute]),
            new EditAlignment.Run("anthropic", "AnthropicPB", 7, 8, 6, 7, [EditAlignment.Label.Substitute]),
            new EditAlignment.Run("kubernetees", "Kubernetes", 9, 10, 8, 9, [EditAlignment.Label.Substitute]),
            new EditAlignment.Run("envious wispr", "EnviousWispr", 11, 13, 10, 11, [EditAlignment.Label.Substitute]),
            new EditAlignment.Run("blockedsrc", "BlockedDst", 14, 15, 12, 13, [EditAlignment.Label.Substitute]),
        };

        var blockedKey = CorrectionPairKey.Make("blockedsrc", "BlockedDst");
        var packTerm = new CustomWordEntry("packsrc", "PackTarget");
        var packRun = new EditAlignment.Run("packsrc", "PackTarget", 16, 17, 14, 15, [EditAlignment.Label.Substitute]);

        var filtered = CorrectionCandidateFilter.Filter(
            [.. runs, packRun],
            new CorrectionCandidateFilter.Inputs(
                [existingUserWord, ownedElsewhere],
                [packTerm],
                new HashSet<string>(StringComparer.Ordinal) { blockedKey }));

        Assert.IsType<CorrectionCandidateFilter.Disposition.Ineligible>(filtered[0].Disposition);
        Assert.Equal(
            CorrectionCandidateFilter.IneligibleReason.StopwordPhrase,
            ((CorrectionCandidateFilter.Disposition.Ineligible)filtered[0].Disposition).Reason);

        Assert.Equal(
            CorrectionCandidateFilter.IneligibleReason.ContractionEnding,
            ((CorrectionCandidateFilter.Disposition.Ineligible)filtered[1].Disposition).Reason);

        Assert.Equal(
            CorrectionCandidateFilter.IneligibleReason.NumberFormatting,
            ((CorrectionCandidateFilter.Disposition.Ineligible)filtered[2].Disposition).Reason);

        Assert.Equal(
            CorrectionCandidateFilter.IneligibleReason.AliasOwnedElsewhere,
            ((CorrectionCandidateFilter.Disposition.Ineligible)filtered[3].Disposition).Reason);

        var targetExisting = Assert.IsType<CorrectionCandidateFilter.Disposition.Candidate>(filtered[4].Disposition);
        Assert.Equal(new LearnTargetState.ExistingWord(existingUserWord.Id), targetExisting.Target);

        var targetNew = Assert.IsType<CorrectionCandidateFilter.Disposition.Candidate>(filtered[5].Disposition);
        Assert.Equal(LearnTargetState.New, targetNew.Target);

        Assert.Equal(
            CorrectionCandidateFilter.IneligibleReason.BlockedByUser,
            ((CorrectionCandidateFilter.Disposition.Ineligible)filtered[6].Disposition).Reason);

        Assert.IsType<CorrectionCandidateFilter.Disposition.AlreadyCovered>(filtered[7].Disposition);
    }

    [Fact]
    public void CandidateFilterPrepareCapsAtFourCandidatesAndRecordsOverflow()
    {
        var runs = Enumerable.Range(1, 6)
            .Select(i => new EditAlignment.Run($"orig{i}", $"Custom{i}", i, i + 1, i, i + 1, [EditAlignment.Label.Substitute]))
            .ToArray();

        var filtered = CorrectionCandidateFilter.Filter(
            runs,
            new CorrectionCandidateFilter.Inputs([], [], new HashSet<string>(StringComparer.Ordinal)));
        var prepared = CorrectionCandidateFilter.Prepare(filtered);

        Assert.Equal(CorrectionJudgeRequest.MaxCandidates, prepared.Candidates.Count);
        Assert.Equal(2, prepared.Overflow.Count);
    }

    // ── 3. RepeatShapeGuard ──────────────────────────────────────────────────

    [Fact]
    public void RepeatShapeGuardRejectsPlainLowercaseAndSentenceStartCapitalButAllowsBrandsAndAcronyms()
    {
        Assert.False(RepeatShapeGuard.Passes("market", 2, "Go to marquee today."));
        Assert.False(RepeatShapeGuard.Passes("Thursday", 0, "thursday we ship."));
        Assert.True(RepeatShapeGuard.Passes("EnviousWispr", 0, "envious wispr ships."));
        Assert.True(RepeatShapeGuard.Passes("gRPC", 3, "Use grpc for calls."));
        Assert.True(RepeatShapeGuard.Passes("Saurabh", 2, "Ask saurab now."));
        Assert.True(RepeatShapeGuard.Passes("Q3", 2, "Check queue three."));
    }

    // ── 4. RulesCorrectionJudge ──────────────────────────────────────────────

    [Fact]
    public async Task RulesJudgeAcceptsCloseNamesAcronymsAndCompoundsAndRefusesGrammarAndRewrites()
    {
        var judge = new RulesCorrectionJudge();
        var caps = await judge.GetCapabilitiesAsync();
        Assert.True(caps.CanRunOnThisDevice);

        var req1 = new CorrectionJudgeRequest(
            [
                new CorrectionCandidate(1, "saurab", "Saurabh"),
                new CorrectionCandidate(2, "jay son", "JSON"),
                new CorrectionCandidate(3, "coober netties", "Kubernetes"),
            ],
            "context",
            "en");
        var req2 = new CorrectionJudgeRequest(
            [
                new CorrectionCandidate(1, "children", "child"),
                new CorrectionCandidate(2, "quickly", "tomorrow"),
                new CorrectionCandidate(3, "hello there", "see you later"),
            ],
            "context",
            "en");

        var outcome1 = await judge.JudgeAsync(req1);
        var outcome2 = await judge.JudgeAsync(req2);
        var v1 = Assert.IsType<CorrectionJudgeOutcome.Verdict>(outcome1);
        var v2 = Assert.IsType<CorrectionJudgeOutcome.Verdict>(outcome2);
        var d1 = v1.Decisions.ToDictionary(d => d.Id, d => d.Verdict);
        var d2 = v2.Decisions.ToDictionary(d => d.Id, d => d.Verdict);

        Assert.True(d1[1].IsVocabularyCorrection());
        Assert.True(d1[2].IsVocabularyCorrection());
        Assert.True(d1[3].IsVocabularyCorrection());
        Assert.False(d2[1].IsVocabularyCorrection());
        Assert.False(d2[2].IsVocabularyCorrection());
        Assert.False(d2[3].IsVocabularyCorrection());
    }

    // ── 5. JsonLearnMemoryStore ──────────────────────────────────────────────

    [Fact]
    public void LearnMemoryStorePersistsRefusalsTwoStrikeBlockForgetAllAndCorruptRecovery()
    {
        var dir = CreateTempDir();
        var path = Path.Combine(dir, StoreLearnMemory.FileName);
        var now = new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
        var store = new StoreLearnMemory(path, () => now);
        var key = CorrectionPairKey.Make("saurab", "Saurabh");

        Assert.False(store.HasStoredMemory);
        Assert.True(store.RecordJudgeRefusal(key).Committed);
        Assert.True(store.IsRepeat(key));
        Assert.False(store.IsBlocked(key));

        var strike1 = store.RecordUndo(key);
        Assert.True(strike1.Committed);
        Assert.Equal(LearnUserNoResult.Watching, strike1.Value);
        Assert.False(store.IsBlocked(key));

        // Reload in a second instance to prove disk persistence across processes/restarts.
        var reloaded = new StoreLearnMemory(path, () => now);
        Assert.True(reloaded.IsRepeat(key));
        var strike2 = reloaded.RecordUndo(key);
        Assert.True(strike2.Committed);
        Assert.Equal(LearnUserNoResult.Blocked, strike2.Value);
        Assert.True(reloaded.IsBlocked(key));
        Assert.Equal(1, reloaded.BlockedCount);
        Assert.Contains(key, reloaded.BlockedKeys);

        Assert.True(reloaded.ForgetAll());
        Assert.False(File.Exists(path));
        Assert.False(reloaded.HasStoredMemory);
        Assert.Equal(0, reloaded.BlockedCount);

        // Corrupt JSON is moved aside cleanly without crashing.
        File.WriteAllText(path, "{ invalid json !!!");
        Assert.False(reloaded.IsBlocked(key));
        Assert.True(reloaded.HasStoredMemory);
        Assert.True(reloaded.ForgetAll());
        Assert.False(reloaded.HasStoredMemory);
    }

    // ── 6. PastedRegionTracker ───────────────────────────────────────────────

    [Fact]
    public void PastedRegionTrackerHandlesWeakStrongSettleFocusGraceAndFinish()
    {
        const string initial = "Hello saurab today.";
        const string full = "Header: Hello saurab today. Footer.";

        Assert.True(PastedRegionTracker.TryCaptureTarget(
            full, initial, processId: 100, windowHandle: 200, processName: "notepad",
            out var target, out _));
        Assert.NotNull(target);

        var tracker = new PastedRegionTracker(target!, startedAtMs: 0);

        // Edit happens inside the pasted region.
        const string editedFull = "Header: Hello Saurabh today. Footer.";
        var changed = tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(
                Readable: true,
                FieldText: editedFull,
                CaretOffset: "Header: Hello Saurabh".Length, // cursor sits right at end of edited word
                SameApplicationFocus: true,
                SameElementFocus: true,
                ElementAlive: true),
            nowMs: 500);
        Assert.IsType<PastedRegionEvent.Changed>(Assert.Single(changed));

        // SettleMs (2500ms) reached while caret is still inside changed word -> deferred!
        var deferred = tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(
                Readable: true,
                FieldText: editedFull,
                CaretOffset: "Header: Hello Saurab".Length,
                SameApplicationFocus: true,
                SameElementFocus: true,
                ElementAlive: true),
            nowMs: 3100);
        Assert.Empty(deferred);

        // User moves caret away (e.g. end of sentence) -> settles with Strong evidence!
        var strongSettled = tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(
                Readable: true,
                FieldText: editedFull,
                CaretOffset: editedFull.Length,
                SameApplicationFocus: true,
                SameElementFocus: true,
                ElementAlive: true),
            nowMs: 3200);
        var settled = Assert.IsType<PastedRegionEvent.Settled>(Assert.Single(strongSettled));
        Assert.Equal("Hello Saurabh today.", settled.Region);
        Assert.Equal(SettleEvidence.Strong, settled.Evidence);

        var finished = tracker.Finish(PastedRegionEndReason.NextDictationStarted, 4000);
        var ended = Assert.IsType<PastedRegionEvent.Ended>(Assert.Single(finished));
        Assert.Equal(PastedRegionEndReason.NextDictationStarted, ended.Reason);
    }

    [Fact]
    public void PastedRegionTrackerRespectsFocusGraceAndFlushesWeakBeforeEnding()
    {
        Assert.True(PastedRegionTracker.TryCaptureTarget(
            "Hello saurab.", "Hello saurab.", 1, 2, "notepad",
            out var target, out _));

        var tracker = new PastedRegionTracker(target!, startedAtMs: 0);
        tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(true, "Hello Saurabh.", 5, true, true, true),
            nowMs: 200);

        // Focus moves briefly away within FocusGraceMs (1500ms) -> stays alive (not settled before SettleMs).
        Assert.Empty(tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(true, "Hello Saurabh.", 5, true, false, true),
            nowMs: 400));

        // App focus changes after >= FlushMinQuietMs (500ms) since edit at 200ms -> flushes Weak settle + Ended(FocusChanged)!
        var events = tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(true, "Hello Saurabh.", 5, false, false, true),
            nowMs: 800);
        Assert.Equal(2, events.Count);
        var settled = Assert.IsType<PastedRegionEvent.Settled>(events[0]);
        Assert.Equal(SettleEvidence.Weak, settled.Evidence);
        Assert.IsType<PastedRegionEvent.Ended>(events[1]);
    }

    // ── 7. Coordinator + Watcher Lifecycle (2-Strike Block + Undo + Toggle) ──

    [Fact]
    public async Task CoordinatorAndWatcherEnforceTwoStrikeUndoBlockManualRemovalsAndForget()
    {
        var dir = CreateTempDir();
        var memory = new StoreLearnMemory(Path.Combine(dir, StoreLearnMemory.FileName));
        var store = new InMemorySettingsStore();
        var settingsPresenter = new SettingsPresenter(store, AppSettings.Default);
        var vocabulary = new VocabularyPresenter(settingsPresenter);
        var events = new List<AppLogEntry>();
        var logger = new CapturingLogger(events);
        var committedCallbacks = 0;
        var coordinator = new LearnedCorrectionCoordinator(
            vocabulary,
            memory,
            () => [],
            logger,
            onSettingsCommitted: () => Interlocked.Increment(ref committedCallbacks));
        var presenter = new FakePresenter(coordinator);
        coordinator.AttachPresenter(presenter);

        var observer = new FakePastedRegionObserver();
        var learnEnabled = true;
        var currentWords = new List<CustomWordEntry>();
        var watcher = new ObservedCorrectionWatcher(
            () => learnEnabled,
            () => new RulesCorrectionJudge(),
            observer,
            () => currentWords,
            () => [],
            coordinator,
            memory,
            logger);

        // Strike 1: Paste -> edit "saurab" -> "Saurabh" -> save -> pill shown -> Undo
        var paste1 = new PasteCompletedEvent("Hello saurab.", new TargetWindowId(10, 1, "notepad"), "en", DateTimeOffset.UtcNow);
        watcher.OnPasteCompleted(paste1);
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observer.Emit(new PastedRegionEvent.Settled("Hello Saurabh.", SettleEvidence.Strong));

        var pill1 = await presenter.ShownQueue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref committedCallbacks));
        Assert.Equal("Saurabh", pill1.Canonical);
        Assert.Equal(LearnedCorrectionPillKind.Added, pill1.Kind);
        var savedAfter1 = Assert.Single(store.Current.UserData.CustomWords);
        Assert.Equal(["saurab"], savedAfter1.LearnedAliases);
        Assert.NotNull(savedAfter1.LearnedAt);
        currentWords = [.. store.Current.UserData.CustomWords];

        var claimed1 = coordinator.ClaimUndo(pill1.PillId);
        Assert.NotNull(claimed1);
        var undo1 = await coordinator.PerformUndoAsync(claimed1!, pill1.PillId);
        Assert.Equal(LearnedCorrectionUndoOutcome.Undone, undo1);
        Assert.Equal(2, Volatile.Read(ref committedCallbacks));
        Assert.Empty(store.Current.UserData.CustomWords);
        Assert.Equal(1, memory.GetEntry(CorrectionPairKey.Make("saurab", "Saurabh"))?.UserNos);
        Assert.False(memory.IsBlocked(CorrectionPairKey.Make("saurab", "Saurabh")));
        currentWords = [];

        // Strike 2: Next dictation starts -> ends previous watch -> paste again -> edit again -> Undo -> blocked!
        watcher.OnRecordingStarted();
        observer.ResetStart();
        presenter.ResetShown();
        watcher.OnPasteCompleted(paste1);
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observer.Emit(new PastedRegionEvent.Settled("Hello Saurabh.", SettleEvidence.Strong));

        var pill2 = await presenter.ShownQueue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var claimed2 = coordinator.ClaimUndo(pill2.PillId);
        Assert.NotNull(claimed2);
        Assert.Equal(LearnedCorrectionUndoOutcome.Undone, await coordinator.PerformUndoAsync(claimed2!, pill2.PillId));
        Assert.True(memory.IsBlocked(CorrectionPairKey.Make("saurab", "Saurabh")));
        Assert.Equal(1, memory.BlockedCount);

        // Strike 3: Blocked pair is suppressed without calling judge or adding the word.
        watcher.OnRecordingStarted();
        observer.ResetStart();
        watcher.OnPasteCompleted(paste1);
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observer.Emit(new PastedRegionEvent.Settled("Hello Saurabh.", SettleEvidence.Strong));
        await Task.Delay(150);
        Assert.Empty(store.Current.UserData.CustomWords);
        Assert.Contains(events, e => e.Event == AppEventCode.LearnBlockedSeen);

        // ForgetAll clears block and allows learning again.
        Assert.True(memory.ForgetAll());
        watcher.OnRecordingStarted();
        presenter.ResetShown();
        observer.ResetStart();
        watcher.OnPasteCompleted(paste1);
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observer.Emit(new PastedRegionEvent.Settled("Hello Saurabh.", SettleEvidence.Strong));
        var pill3 = await presenter.ShownQueue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Saurabh", pill3.Canonical);

        // Toggle off cancels live watch.
        watcher.OnLearnFromEditsChanged(false);
        Assert.True(observer.Cancelled);
    }

    // ── 8. Schema v26->v27, PortableProfile v19->v20, CustomWord Provenance ────

    [Fact]
    public async Task SettingsV26AndPortableProfileV19MigrateCleanlyAndCustomWordOmitsEmptyProvenance()
    {
        var dir = CreateTempDir();
        var settingsPath = Path.Combine(dir, "settings.json");
        var writer = new JsonSettingsStore(settingsPath);
        await writer.SaveAsync(AppSettings.Default);
        var currentJson = await File.ReadAllTextAsync(settingsPath);
        var v26Json = currentJson.Replace("\"schemaVersion\": 27", "\"schemaVersion\": 26", StringComparison.Ordinal);
        Assert.NotEqual(currentJson, v26Json);
        await File.WriteAllTextAsync(settingsPath, v26Json);

        var loaded = await new JsonSettingsStore(settingsPath).LoadAsync();
        Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
        Assert.Equal(26, loaded.SourceSchemaVersion);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
        Assert.True(loaded.Settings.Preferences.Dictation.LearnFromEditsEnabled);

        // CustomWordEntry omits learned fields when empty and preserves them when present.
        var manualEntry = new CustomWordEntry("manual", "Manual");
        var manualJson = JsonSerializer.Serialize(manualEntry);
        Assert.DoesNotContain("learnedAliases", manualJson, StringComparison.Ordinal);
        Assert.DoesNotContain("learnedAt", manualJson, StringComparison.Ordinal);
        var manualRoundTrip = JsonSerializer.Deserialize<CustomWordEntry>(manualJson);
        Assert.NotNull(manualRoundTrip);
        Assert.False(manualRoundTrip!.IsAutoLearned);

        var ts = new DateTimeOffset(2026, 11, 26, 8, 30, 0, TimeSpan.Zero);
        var learnedEntry = new CustomWordEntry(
            aliases: ["saurab", "sarab"],
            replacement: "Saurabh",
            learnedAliases: ["saurab"],
            learnedAt: ts);
        var learnedJson = JsonSerializer.Serialize(learnedEntry);
        Assert.Contains("learnedAliases", learnedJson, StringComparison.Ordinal);
        Assert.Contains("learnedAt", learnedJson, StringComparison.Ordinal);
        var learnedRoundTrip = JsonSerializer.Deserialize<CustomWordEntry>(learnedJson);
        Assert.NotNull(learnedRoundTrip);
        Assert.True(learnedRoundTrip!.IsAutoLearned);
        Assert.Equal(["saurab"], learnedRoundTrip.LearnedAliases);
        Assert.Equal(ts, learnedRoundTrip.LearnedAt);

        // Portable profile v19 -> v20 migration.
        var profilePath = Path.Combine(dir, "profile.json");
        var portableService = new JsonPortableProfileService();
        var exported = await portableService.ExportAsync(
            new PortableProfile(
                SchemaVersion: PortableProfile.CurrentSchemaVersion,
                Preferences: UserPreferences.Default,
                UserData: ReusableUserData.Empty),
            profilePath);
        Assert.True(exported.Succeeded);
        var exportedJson = await File.ReadAllTextAsync(profilePath);
        var v19Json = exportedJson.Replace("\"schemaVersion\": 20", "\"schemaVersion\": 19", StringComparison.Ordinal);
        Assert.NotEqual(exportedJson, v19Json);
        await File.WriteAllTextAsync(profilePath, v19Json);
        var imported = await portableService.ImportAsync(profilePath);
        Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
        Assert.Equal(PortableProfile.CurrentSchemaVersion, imported.Profile!.SchemaVersion);
        Assert.True(imported.Profile.Preferences.Dictation.LearnFromEditsEnabled);
        Assert.Null(imported.Profile.LearnMemory);
        Assert.DoesNotContain("learnMemory", exportedJson, StringComparison.Ordinal);

        // Portable profile round-trips LearnMemory blocks and merges cleanly on import.
        var memSource = new StoreLearnMemory(Path.Combine(dir, "mem-export", StoreLearnMemory.FileName));
        var blockKey = CorrectionPairKey.Make("saurab", "Saurabh");
        memSource.RecordUndo(blockKey);
        memSource.RecordUndo(blockKey);
        var memProfilePath = Path.Combine(dir, "profile-with-mem.json");
        var exportedWithMem = await portableService.ExportAsync(
            new PortableProfile(
                PortableProfile.CurrentSchemaVersion,
                UserPreferences.Default,
                ReusableUserData.Empty,
                memSource.ExportEntries()),
            memProfilePath);
        Assert.True(exportedWithMem.Succeeded);
        var importedWithMem = await portableService.ImportAsync(memProfilePath);
        Assert.Equal(PortableProfileImportStatus.Imported, importedWithMem.Status);
        Assert.NotNull(importedWithMem.Profile?.LearnMemory);

        var memTarget = new StoreLearnMemory(Path.Combine(dir, "mem-import", StoreLearnMemory.FileName));
        Assert.True(memTarget.ImportEntries(importedWithMem.Profile!.LearnMemory!));
        Assert.True(memTarget.IsBlocked(blockKey));
    }

    [Fact]
    public async Task EditingAndRemovalPreserveRemainingProvenanceAndReportOnlyRemovedEntries()
    {
        var store = new InMemorySettingsStore();
        var learnedAt = new DateTimeOffset(2026, 11, 26, 9, 15, 0, TimeSpan.Zero);
        var entry1 = new CustomWordEntry(
            aliases: ["saurab", "sarab", "sorab"],
            replacement: "Saurabh",
            learnedAliases: ["saurab", "sarab"],
            learnedAt: learnedAt);
        var entry2 = new CustomWordEntry("old", "KeepMe");
        await store.SaveAsync(AppSettings.Default with
        {
            UserData = new ReusableUserData([entry1, entry2], []),
        });

        var settingsPresenter = new SettingsPresenter(store, store.Current);
        var vocabulary = new VocabularyPresenter(settingsPresenter);
        var editSameCanonical = await vocabulary.UpdateMultiAliasWordAsync(
            entry1,
            ["sarab", "sorab", "saurav"],
            "Saurabh",
            MatchStrictness.Default,
            WordCategory.General);
        Assert.True(editSameCanonical.Saved);
        var edited1 = Assert.Single(store.Current.UserData.CustomWords, w => w.Id == entry1.Id);
        Assert.Equal(["sarab"], edited1.LearnedAliases);
        Assert.Equal(learnedAt, edited1.LearnedAt);
        Assert.True(edited1.IsAutoLearned);

        // Selecting stale entry1 (now replaced by edited1) beside live entry2 removes only entry2.
        var removal = await vocabulary.RemoveWordsAndReportAsync([entry1, entry2]);
        Assert.True(removal.Saved);
        var removed = Assert.Single(removal.Value);
        Assert.Same(entry2, removed);
        Assert.Single(store.Current.UserData.CustomWords);

        // Changing canonical clears learned provenance so unrelated replacements aren't misattributed.
        var editChangedCanonical = await vocabulary.UpdateMultiAliasWordAsync(
            edited1,
            ["sarab", "sorab"],
            "SomethingElse",
            MatchStrictness.Default,
            WordCategory.General);
        Assert.True(editChangedCanonical.Saved);
        var edited2 = Assert.Single(store.Current.UserData.CustomWords, w => w.Id == entry1.Id);
        Assert.Empty(edited2.LearnedAliases);
        Assert.Null(edited2.LearnedAt);
        Assert.False(edited2.IsAutoLearned);

        // PastedRegionTracker does not treat !SameElementFocus && !Readable (different control in same window) as read failure.
        Assert.True(PastedRegionTracker.TryCaptureTarget(
            "Hello saurab.", "Hello saurab.", 1, 2, "notepad", out var regionTarget, out _));
        var tracker = new PastedRegionTracker(regionTarget!, startedAtMs: 0);
        for (var t = 100; t <= 400; t += 100)
        {
            var tickEvents = tracker.Poll(
                new PastedRegionTracker.ReadSnapshot(false, null, null, SameApplicationFocus: true, SameElementFocus: false, ElementAlive: true),
                nowMs: t);
            Assert.Empty(tickEvents);
        }

        Assert.False(tracker.HasEnded);

        // Toggle off before/inside SaveLearnedWordAsync returns NoLongerWanted without saving.
        await settingsPresenter.SaveAsync(current => current with
        {
            Preferences = current.Preferences with
            {
                Dictation = current.Preferences.Dictation with { LearnFromEditsEnabled = false },
            },
        });
        var disabledSave = await vocabulary.SaveLearnedWordAsync(
            "saurab",
            "SaurabhNew",
            LearnTargetState.New,
            [],
            DateTimeOffset.UtcNow);
        Assert.True(disabledSave.Saved);
        Assert.Equal(LearnedSaveStatus.NoLongerWanted, disabledSave.Value.Status);
        Assert.DoesNotContain(store.Current.UserData.CustomWords, w => w.Replacement == "SaurabhNew");
    }

    [Fact]
    public async Task ContextAwareDeliveryExposesRepairedDeliveredTextSoTrackerAvoidsTrailingSpaceAnchorAmbiguity()
    {
        var targetWindow = new TargetWindowId(42, 7, "notepad");
        var context = new CaretContext(
            targetWindow,
            "edit-1",
            TextTargetKind.StandardEdit,
            Left: string.Empty,
            Selection: string.Empty,
            Right: string.Empty,
            LeftReachedDocumentStart: true,
            RightReachedDocumentEnd: true,
            HasTextContext: true,
            SupportsDirectValueWrite: true,
            DirectValueWriteAtEnd: true);
        var adapter = new RecordingTargetAdapter(context);
        var delivery = new ContextAwareTextDelivery(adapter);

        var sessionId = new DictationSessionId(Guid.NewGuid());
        var raw = new ProcessedText(sessionId, "Check with my partner please.");
        var result = await delivery.DeliverAsync(
            new TextDeliveryRequest(raw, targetWindow, "en", TextDeliveryOptions.Default, SnippetExpanded: false));

        Assert.True(result.Delivered);
        Assert.Equal("Check with my partner please. ", result.DeliveredText);

        // Using DeliveredText leaves empty anchors so spaces inside the sentence don't trip AnchorAmbiguous.
        Assert.True(PastedRegionTracker.TryCaptureTarget(
            result.DeliveredText!,
            result.DeliveredText!,
            42,
            7,
            "notepad",
            out var regionTarget,
            out _));
        Assert.Equal(string.Empty, regionTarget!.Anchors.Before);
        Assert.Equal(string.Empty, regionTarget.Anchors.After);

        var tracker = new PastedRegionTracker(regionTarget, startedAtMs: 0);
        var changed = tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(
                Readable: true,
                FieldText: "Check with my Partnero please. ",
                CaretOffset: 0,
                SameApplicationFocus: true,
                SameElementFocus: true,
                ElementAlive: true),
            nowMs: 750);
        Assert.IsType<PastedRegionEvent.Changed>(Assert.Single(changed));

        var settled = tracker.Poll(
            new PastedRegionTracker.ReadSnapshot(
                Readable: true,
                FieldText: "Check with my Partnero please. ",
                CaretOffset: 0,
                SameApplicationFocus: true,
                SameElementFocus: true,
                ElementAlive: true),
            nowMs: 2500);
        var settledEv = Assert.IsType<PastedRegionEvent.Settled>(Assert.Single(settled));
        Assert.Equal(SettleEvidence.Strong, settledEv.Evidence);
    }

    private sealed class RecordingTargetAdapter(CaretContext context) : ITextTargetAdapter
    {
        public Task<TargetContextResult> CaptureContextAsync(
            TargetWindowId target,
            TextDeliveryOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TargetContextResult(TargetContextStatus.Available, context));

        public Task<TextCommitResult> CommitAsync(
            TextCommitRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TextCommitResult(
                TextDeliveryRoute.UiAutomationValue,
                Delivered: true,
                ClipboardFallback: false,
                ClipboardRestored: false));

        public Task<TextCommitResult> CopyOnlyAsync(
            ProcessedText text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TextCommitResult(
                TextDeliveryRoute.ClipboardOnly,
                Delivered: true,
                ClipboardFallback: false,
                ClipboardRestored: false));
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public AppSettings Current { get; private set; } = AppSettings.Default;

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(Current, SettingsLoadStatus.Loaded));

        public Task<SettingsResetResult> ResetAsync(AppSettings replacement, CancellationToken cancellationToken = default)
        {
            Current = replacement;
            return Task.FromResult(new SettingsResetResult(false));
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger(List<AppLogEntry> entries) : IAppLogger
    {
        public void Write(AppLogEntry entry)
        {
            lock (entries)
            {
                entries.Add(entry);
            }
        }
    }

    private sealed class FakePresenter(LearnedCorrectionCoordinator coordinator) : ILearnedCorrectionPresenter
    {
        public TaskCompletionSource<LearnedCorrectionPillModel> ShownQueue { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetShown() =>
            ShownQueue = new TaskCompletionSource<LearnedCorrectionPillModel>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Show(LearnedCorrectionPillModel pill)
        {
            coordinator.PillAdmitted(pill.PillId);
            ShownQueue.TrySetResult(pill);
        }

        public void ShowUndone(Guid pillId)
        {
        }

        public void ShowUndoError(Guid pillId)
        {
        }

        public void ShowSaveError(string canonical)
        {
        }

        public void Close(Guid pillId)
        {
        }
    }

    private sealed class FakePastedRegionObserver : IPastedRegionObserver
    {
        private Action<PastedRegionEvent>? _onEvent;

        public bool IsObserving { get; private set; }
        public bool Cancelled { get; private set; }

        public TaskCompletionSource Started { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetStart()
        {
            Cancelled = false;
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task StartObservationAsync(
            PasteCompletedEvent paste,
            Action<PastedRegionEvent> onEvent,
            Action<string> onSkipped,
            CancellationToken cancellationToken = default)
        {
            _onEvent = onEvent;
            IsObserving = true;
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        public void Emit(PastedRegionEvent ev) => _onEvent?.Invoke(ev);

        public void Finish(PastedRegionEndReason reason)
        {
            IsObserving = false;
            _onEvent?.Invoke(new PastedRegionEvent.Ended(reason));
        }

        public void CancelObservation()
        {
            IsObserving = false;
            Cancelled = true;
        }
    }
}
