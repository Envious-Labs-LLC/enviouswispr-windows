using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.History;
using EnviousWispr.Services.Reliability;
using EnviousWispr.Services.Settings;
using EnviousWispr.Services.UserData;

namespace EnviousWispr.Architecture.Tests;

public sealed partial class HistoryAudioArchiveAndPlaybackTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HistoryAudioMetadataRoundTripsAndDeletesCleanlyOnDeleteClearAndPrune()
    {
        using var temp = new TempFolder();
        var historyPath = Path.Combine(temp.Path, "history.json");
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        Directory.CreateDirectory(recordingsDir);

        using var store = new JsonHistoryStore(historyPath, recordingsDir);

        var entryA = DictationHistoryEntry.Create(
            Now.AddMinutes(-10),
            "First dictation.",
            "parakeet",
            wasPolished: false,
            wasDelivered: true,
            audioFileName: "a.wav",
            audioDurationMs: 1850);
        var entryB = DictationHistoryEntry.Create(
            Now.AddMinutes(-5),
            "Second dictation.",
            "parakeet",
            wasPolished: false,
            wasDelivered: true,
            audioFileName: "b.wav",
            audioDurationMs: 2400);
        var entryExpired = DictationHistoryEntry.Create(
            Now.AddMinutes(-2),
            "Expiring recovery.",
            "parakeet",
            wasPolished: false,
            wasDelivered: false,
            expiresAt: Now.AddHours(1),
            audioFileName: "expired.wav",
            audioDurationMs: 900);

        File.WriteAllBytes(Path.Combine(recordingsDir, "a.wav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(recordingsDir, "b.wav"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(recordingsDir, "expired.wav"), [7, 8, 9]);

        Assert.True((await store.AddAsync(entryA, retentionDays: 30, Now)).Succeeded);
        Assert.True((await store.AddAsync(entryB, retentionDays: 30, Now)).Succeeded);
        Assert.True((await store.AddAsync(entryExpired, retentionDays: 30, Now)).Succeeded);

        // Once the recovery entry passes its expiry, LoadAsync prunes both the entry and its WAV file.
        var afterExpiry = await store.LoadAsync(retentionDays: 30, Now.AddHours(2));
        Assert.Equal(HistoryLoadStatus.Loaded, afterExpiry.Status);
        Assert.Equal(2, afterExpiry.Entries.Count);
        Assert.False(File.Exists(Path.Combine(recordingsDir, "expired.wav")));
        Assert.True(File.Exists(Path.Combine(recordingsDir, "a.wav")));
        Assert.True(File.Exists(Path.Combine(recordingsDir, "b.wav")));

        var loaded = await store.LoadAsync(retentionDays: 30, Now);
        Assert.Equal(HistoryLoadStatus.Loaded, loaded.Status);
        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal("b.wav", loaded.Entries[0].AudioFileName);
        Assert.Equal(2400, loaded.Entries[0].AudioDurationMs);

        // Re-polishing preserves audio metadata.
        var repolished = await store.UpdateRepolishedAsync(
            entryB.Id,
            entryB.Text,
            "Second dictation, polished.",
            wasPolished: true,
            providerId: "eg-1",
            modelId: "eg-1");
        Assert.True(repolished.Succeeded);
        Assert.Equal("b.wav", repolished.UpdatedEntry!.AudioFileName);
        Assert.Equal(2400, repolished.UpdatedEntry.AudioDurationMs);

        // Individual delete deletes a.wav and leaves b.wav intact.
        Assert.True((await store.DeleteAsync(entryA.Id)).Succeeded);
        Assert.False(File.Exists(Path.Combine(recordingsDir, "a.wav")));
        Assert.True(File.Exists(Path.Combine(recordingsDir, "b.wav")));

        // Clear all deletes remaining audio files including stray ones.
        File.WriteAllBytes(Path.Combine(recordingsDir, "stray.wav"), [0]);
        Assert.True((await store.ClearAsync()).Succeeded);
        Assert.Empty(Directory.GetFiles(recordingsDir, "*.wav"));
    }

    [Fact]
    public async Task JsonHistoryStoreDeleteManyRemovesOnlySelectedAudioFilesAndRejectsPathTraversal()
    {
        using var temp = new TempFolder();
        var historyPath = Path.Combine(temp.Path, "history.json");
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        Directory.CreateDirectory(recordingsDir);

        using var store = new JsonHistoryStore(historyPath, recordingsDir);

        var bad = DictationHistoryEntry.Create(
            Now,
            "Bad path",
            "parakeet",
            wasPolished: false,
            wasDelivered: true,
            audioFileName: "../escape.wav",
            audioDurationMs: 100);
        Assert.False(bad.IsValid);
        Assert.False((await store.AddAsync(bad, 30, Now)).Succeeded);

        var e1 = DictationHistoryEntry.Create(Now.AddMinutes(-3), "One", "parakeet", false, true, audioFileName: "1.wav", audioDurationMs: 500);
        var e2 = DictationHistoryEntry.Create(Now.AddMinutes(-2), "Two", "parakeet", false, true, audioFileName: "2.wav", audioDurationMs: 500);
        var e3 = DictationHistoryEntry.Create(Now.AddMinutes(-1), "Three", "parakeet", false, true, audioFileName: "3.wav", audioDurationMs: 500);
        File.WriteAllBytes(Path.Combine(recordingsDir, "1.wav"), [1]);
        File.WriteAllBytes(Path.Combine(recordingsDir, "2.wav"), [2]);
        File.WriteAllBytes(Path.Combine(recordingsDir, "3.wav"), [3]);

        await store.AddAsync(e1, 30, Now);
        await store.AddAsync(e2, 30, Now);
        await store.AddAsync(e3, 30, Now);

        Assert.True((await store.DeleteManyAsync([e1.Id, e3.Id])).Succeeded);
        Assert.False(File.Exists(Path.Combine(recordingsDir, "1.wav")));
        Assert.True(File.Exists(Path.Combine(recordingsDir, "2.wav")));
        Assert.False(File.Exists(Path.Combine(recordingsDir, "3.wav")));
    }

    [Fact]
    public void LocalDictationAudioArchiveWritesValidWavEnforcesBoundedRetentionAndExtractsPeaks()
    {
        using var temp = new TempFolder();
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        var archive = new LocalDictationAudioArchive(recordingsDir, maxRecordings: 2, maxTotalBytes: 1_000_000);

        var samples = new float[16_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i < 8_000 ? 0.25f : 0.75f;
        }

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var first = archive.SaveForEntry(id1, new CapturedAudio(DictationSessionId.Create(), samples, 16_000, 1));
        Assert.NotNull(first);
        Assert.Equal($"{id1:D}.wav", first!.FileName);
        Assert.Equal(1000, first.DurationMs);

        var second = archive.SaveForEntry(id2, new CapturedAudio(DictationSessionId.Create(), samples, 16_000, 1));
        Assert.NotNull(second);
        File.SetLastWriteTimeUtc(archive.ResolveExistingPath(first.FileName)!, DateTime.UtcNow.AddMinutes(-5));

        var third = archive.SaveForEntry(id3, new CapturedAudio(DictationSessionId.Create(), samples, 16_000, 1));
        Assert.NotNull(third);

        // SaveForEntry alone defers pruning until explicit post-commit PruneRetention runs.
        Assert.NotNull(archive.ResolveExistingPath(first.FileName));
        archive.PruneRetention(third!.FileName);

        // Oldest file pruned by count bound 2 while third (newest) is preserved.
        Assert.Null(archive.ResolveExistingPath(first.FileName));
        Assert.NotNull(archive.ResolveExistingPath(third.FileName));

        var resolved = archive.ResolveExistingPath(third.FileName)!;
        Assert.True(WaveFile.TryAnalyzeFile(resolved, HistoryPlaybackPresentation.DefaultWaveformBarCount, out var peaks, out var durationMs));
        Assert.Equal(1000, durationMs);
        Assert.Equal(HistoryPlaybackPresentation.DefaultWaveformBarCount, peaks.Length);
        Assert.True(peaks[^1] > peaks[0]);
    }

    [Fact]
    public async Task SessionPersistenceSavesAudioOnlyWhenPersistingEntryAndCleansUpIfStoreFails()
    {
        using var temp = new TempFolder();
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        var archive = new LocalDictationAudioArchive(recordingsDir);
        var fakeStore = new StubHistoryStore();
        var recovery = new InMemoryRecoveryStore();
        var effects = new NoOpPersistenceEffects();
        var historyEnabled = true;

        var persistence = new SessionPersistence(
            recovery,
            fakeStore,
            new NoOpLogger(),
            TimeProvider.System,
            () => new HistoryPreferences(historyEnabled, 30),
            effects,
            archive);

        var session = DictationSessionId.Create();
        var audio = new CapturedAudio(session, new float[8_000], 16_000, 1);
        var transcript = new Transcript(session, "hello", "parakeet");

        // 1. Saved entry attaches audio file & duration and keeps WAV on disk.
        var savedId = await persistence.SaveHistoryAsync(
            transcript,
            "hello",
            HistoryWriteIntent.Delivered(wasPolished: false, wasDelivered: true),
            audio: audio);
        Assert.NotNull(savedId);
        var savedEntry = Assert.Single(fakeStore.Added);
        Assert.Equal($"{savedId:D}.wav", savedEntry.AudioFileName);
        Assert.Equal(500, savedEntry.AudioDurationMs);
        Assert.NotNull(archive.ResolveExistingPath(savedEntry.AudioFileName));

        // 2. When History is off and not forced, no WAV is saved.
        historyEnabled = false;
        var skippedId = await persistence.SaveHistoryAsync(
            transcript,
            "ignored",
            HistoryWriteIntent.Delivered(wasPolished: false, wasDelivered: true),
            audio: audio);
        Assert.Null(skippedId);
        Assert.Single(Directory.GetFiles(recordingsDir, "*.wav"));

        // 3. When store AddAsync fails, staged WAV is rolled back cleanly.
        historyEnabled = true;
        fakeStore.FailAdds = true;
        var failedId = await persistence.SaveHistoryAsync(
            transcript,
            "rollback",
            HistoryWriteIntent.Delivered(wasPolished: false, wasDelivered: true),
            audio: audio);
        Assert.Null(failedId);
        Assert.Single(Directory.GetFiles(recordingsDir, "*.wav"));
    }

    [Fact]
    public async Task UserDataExportNeverIncludesSavedAudioWavFiles()
    {
        using var temp = new TempFolder();
        var dataDir = Path.Combine(temp.Path, "data");
        var recordingsDir = Path.Combine(dataDir, "recordings", "dictations");
        Directory.CreateDirectory(recordingsDir);

        using var store = new JsonHistoryStore(Path.Combine(dataDir, "history.json"), recordingsDir);
        var archive = new LocalDictationAudioArchive(recordingsDir);
        var entry = DictationHistoryEntry.Create(Now, "Private text", "parakeet", false, true);
        var saved = archive.SaveForEntry(entry.Id, new CapturedAudio(DictationSessionId.Create(), new float[1600], 16_000, 1));
        await store.AddAsync(entry with { AudioFileName = saved!.FileName, AudioDurationMs = saved.DurationMs }, 30, Now);

        var destZip = Path.Combine(temp.Path, "export.zip");
        var result = await UserDataExportService.ExportAsync(
            JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile(),
            store,
            30,
            new WindowsRecoveryTextStore(Path.Combine(dataDir, "recovery.json")),
            destZip,
            Now);

        Assert.True(result.Succeeded);
        using var zip = System.IO.Compression.ZipFile.OpenRead(destZip);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SingleActivePlaybackTrackerEnforcesOneActiveCardSeekingAndCompletionReset()
    {
        var tracker = new SingleActivePlaybackTracker();
        var cardA = Guid.NewGuid();
        var cardB = Guid.NewGuid();

        Assert.Equal(HistoryPlaybackToggleOutcome.StartNew, tracker.Toggle(cardA, 4_000));
        Assert.Equal(cardA, tracker.ActiveEntryId);
        Assert.True(tracker.IsPlaying);

        tracker.CompleteLoad(cardA, 4_000);
        tracker.UpdateProgress(cardA, 1_500, 4_000);
        Assert.Equal(HistoryPlaybackToggleOutcome.Pause, tracker.Toggle(cardA, 4_000));
        Assert.False(tracker.IsPlaying);
        Assert.Equal(1_500, tracker.PositionMs);

        // Switching to cardB stops cardA and starts B from 0.
        Assert.Equal(HistoryPlaybackToggleOutcome.StartNew, tracker.Toggle(cardB, 8_000));
        Assert.Equal(cardB, tracker.ActiveEntryId);
        Assert.True(tracker.IsPlaying);
        Assert.Equal(0, tracker.PositionMs);

        tracker.CompleteLoad(cardB, 8_000);
        var plan = tracker.Seek(cardB, 0.5, 8_000, hasLoadedSource: true);
        Assert.False(plan.StartNewLoad);
        Assert.True(plan.ShouldPlay);
        Assert.Equal(4_000, plan.TargetPositionMs);
        Assert.Equal("0:04 / 0:08", HistoryPlaybackPresentation.FormatClockDisplay(tracker.PositionMs, tracker.DurationMs, isActive: true));

        tracker.MarkCompleted(cardB);
        Assert.False(tracker.IsPlaying);
        Assert.Equal(0, tracker.PositionMs);
        Assert.Equal("0:08", HistoryPlaybackPresentation.FormatClockDisplay(0, 8_000, isActive: false));
    }

    [Fact]
    public void ScrubSeekDuringPendingAsyncLoadPreservesPlayStateAndLatestPosition()
    {
        var tracker = new SingleActivePlaybackTracker();
        var cardA = Guid.NewGuid();
        var cardB = Guid.NewGuid();

        // Scrubbing an unselected entry starts one async load at the requested offset with play active.
        var initialSeek = tracker.Seek(cardA, 0.25, 8_000, hasLoadedSource: false);
        Assert.True(initialSeek.StartNewLoad);
        Assert.True(initialSeek.ShouldPlay);
        Assert.Equal(2_000, initialSeek.TargetPositionMs);
        Assert.Equal(cardA, tracker.LoadingEntryId);

        // Repeated drag events while load is still in flight must NOT pause playback or restart loading.
        var drag1 = tracker.Seek(cardA, 0.5, 8_000, hasLoadedSource: false);
        Assert.False(drag1.StartNewLoad);
        Assert.True(drag1.ShouldPlay);
        Assert.True(tracker.IsPlaying);
        Assert.Equal(4_000, drag1.TargetPositionMs);

        var drag2 = tracker.Seek(cardA, 0.75, 8_000, hasLoadedSource: false);
        Assert.False(drag2.StartNewLoad);
        Assert.True(drag2.ShouldPlay);
        Assert.True(tracker.IsPlaying);
        Assert.Equal(6_000, drag2.TargetPositionMs);

        // Completing async load lands at the latest scrubbed offset and preserves playing intent.
        Assert.Equal(6_000, tracker.CompleteLoad(cardA, 8_000));
        Assert.Null(tracker.LoadingEntryId);
        Assert.True(tracker.IsPlaying);

        // Scrubbing while paused on a loaded card preserves paused state.
        Assert.Equal(HistoryPlaybackToggleOutcome.Pause, tracker.Toggle(cardA, 8_000));
        var pausedSeek = tracker.Seek(cardA, 0.25, 8_000, hasLoadedSource: true);
        Assert.False(pausedSeek.StartNewLoad);
        Assert.False(pausedSeek.ShouldPlay);
        Assert.False(tracker.IsPlaying);
        Assert.Equal(2_000, pausedSeek.TargetPositionMs);

        // Toggle-pause during pending load of cardB leaves IsPlaying false when CompleteLoad runs.
        Assert.Equal(HistoryPlaybackToggleOutcome.StartNew, tracker.Toggle(cardB, 4_000));
        Assert.Equal(HistoryPlaybackToggleOutcome.Pause, tracker.Toggle(cardB, 4_000));
        _ = tracker.Seek(cardB, 0.5, 4_000, hasLoadedSource: false);
        Assert.False(tracker.IsPlaying);
        Assert.Equal(2_000, tracker.CompleteLoad(cardB, 4_000));
        Assert.False(tracker.IsPlaying);
    }

    [Fact]
    public async Task RetentionPruneIsSkippedWhenHistoryWriteFailsAndRunsOnlyAfterCommit()
    {
        using var temp = new TempFolder();
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        var archive = new LocalDictationAudioArchive(recordingsDir, maxRecordings: 2, maxTotalBytes: 1_000_000);
        var fakeStore = new StubHistoryStore();
        var persistence = new SessionPersistence(
            new InMemoryRecoveryStore(),
            fakeStore,
            new NoOpLogger(),
            TimeProvider.System,
            () => new HistoryPreferences(true, 30),
            new NoOpPersistenceEffects(),
            archive);

        var session1 = DictationSessionId.Create();
        var session2 = DictationSessionId.Create();
        var session3 = DictationSessionId.Create();
        var audio = new CapturedAudio(session1, new float[1600], 16_000, 1);

        var id1 = await persistence.SaveHistoryAsync(
            new Transcript(session1, "one", "parakeet"),
            "one",
            HistoryWriteIntent.Delivered(false, true),
            audio: audio);
        var id2 = await persistence.SaveHistoryAsync(
            new Transcript(session2, "two", "parakeet"),
            "two",
            HistoryWriteIntent.Delivered(false, true),
            audio: audio);
        Assert.NotNull(id1);
        Assert.NotNull(id2);
        File.SetLastWriteTimeUtc(Path.Combine(recordingsDir, $"{id1:D}.wav"), DateTime.UtcNow.AddMinutes(-5));

        // Simulate History store failure: new WAV is rolled back and neither older WAV is pruned.
        fakeStore.FailAdds = true;
        var failedId = await persistence.SaveHistoryAsync(
            new Transcript(session3, "three-failed", "parakeet"),
            "three-failed",
            HistoryWriteIntent.Delivered(false, true),
            audio: audio);
        Assert.Null(failedId);
        Assert.True(File.Exists(Path.Combine(recordingsDir, $"{id1:D}.wav")));
        Assert.True(File.Exists(Path.Combine(recordingsDir, $"{id2:D}.wav")));
        Assert.Equal(2, Directory.GetFiles(recordingsDir, "*.wav").Length);

        // Once commit succeeds, retention prunes the oldest WAV (id1) leaving id2 and the new entry.
        fakeStore.FailAdds = false;
        var id3 = await persistence.SaveHistoryAsync(
            new Transcript(session3, "three-ok", "parakeet"),
            "three-ok",
            HistoryWriteIntent.Delivered(false, true),
            audio: audio);
        Assert.NotNull(id3);
        Assert.False(File.Exists(Path.Combine(recordingsDir, $"{id1:D}.wav")));
        Assert.True(File.Exists(Path.Combine(recordingsDir, $"{id2:D}.wav")));
        Assert.True(File.Exists(Path.Combine(recordingsDir, $"{id3:D}.wav")));
    }

    [Fact]
    public void AsyncCloudAndOllamaNoticesRefreshInlineProviderDisplayWithoutInfoBar()
    {
        var baseLocalBadge = EnginePolishPresentation.DescribeActivePolishProvider(
            PolishProvider.Ollama,
            selectedEgOneState: null,
            egOneServerExists: false,
            isOllamaHosted: true);
        Assert.Equal("Runs on Ollama's servers", baseLocalBadge.BadgeText);

        var updatedBadge = HistoryPlaybackPresentation.ApplyPolishNoticeToBadge(
            baseLocalBadge,
            PolishProvider.Ollama,
            isOllamaHosted: true,
            cloudPolishNotice: "Ollama polish uses http://127.0.0.1:11434 with hosted model qwen3:cloud on Ollama's servers.");
        Assert.Contains("qwen3:cloud", updatedBadge.StatusText, StringComparison.Ordinal);

        // Local Ollama or EG-1 must ignore cloud disclosure overrides.
        var localUntouched = HistoryPlaybackPresentation.ApplyPolishNoticeToBadge(
            EnginePolishPresentation.DescribeActivePolishProvider(PolishProvider.Ollama, null, false, isOllamaHosted: false),
            PolishProvider.Ollama,
            isOllamaHosted: false,
            cloudPolishNotice: "stale notice");
        Assert.DoesNotContain("stale notice", localUntouched.StatusText, StringComparison.Ordinal);

        var root = FindRepositoryRoot();
        var polishCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.AiPolish.cs"));
        foreach (var methodName in new[] { "SetCloudPolishNotice", "SetOllamaPolishNotice" })
        {
            var body = ExtractMethodBody(polishCs, methodName);
            Assert.DoesNotContain("ShowMessage(", body, StringComparison.Ordinal);
            Assert.Contains("UpdateActivePolishProviderDisplay();", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HistoryPolishReadinessAlwaysMatchesStatusAndNeverAllowsRepolishWhenOffOrUnconfigured()
    {
        var off = HistoryPlaybackPresentation.DescribePolishReadiness(
            PolishProvider.None, null, isEgOneReady: true, isOllamaEndpointValid: true, hasCloudCredential: true, isCloudKeyRejected: false);
        Assert.Equal("Off", off.StatusText);
        Assert.False(off.IsEnabled);
        Assert.False(off.CanRepolishEntry(isGlobalBusy: false, isTemporary: false));

        var egNotReady = HistoryPlaybackPresentation.DescribePolishReadiness(
            PolishProvider.EgOne, "eg-1", isEgOneReady: false, isOllamaEndpointValid: false, hasCloudCredential: false, isCloudKeyRejected: false);
        Assert.Contains("Setup needed", egNotReady.StatusText, StringComparison.Ordinal);
        Assert.True(egNotReady.IsEnabled);
        Assert.False(egNotReady.CanRepolishEntry(isGlobalBusy: false, isTemporary: false));

        var openAiMissingKey = HistoryPlaybackPresentation.DescribePolishReadiness(
            PolishProvider.OpenAI, "gpt-5-nano", isEgOneReady: false, isOllamaEndpointValid: false, hasCloudCredential: false, isCloudKeyRejected: false);
        Assert.Contains("No key", openAiMissingKey.StatusText, StringComparison.Ordinal);
        Assert.False(openAiMissingKey.CanRepolishEntry(isGlobalBusy: false, isTemporary: false));

        var ready = HistoryPlaybackPresentation.DescribePolishReadiness(
            PolishProvider.Anthropic, "claude-haiku-4-5", isEgOneReady: false, isOllamaEndpointValid: false, hasCloudCredential: true, isCloudKeyRejected: false);
        Assert.Equal("Anthropic", ready.StatusText);
        Assert.True(ready.CanRepolishEntry(isGlobalBusy: false, isTemporary: false));
        Assert.False(ready.CanRepolishEntry(isGlobalBusy: true, isTemporary: false));
        Assert.False(ready.CanRepolishEntry(isGlobalBusy: false, isTemporary: true));
    }

    [Fact]
    public void CopyPolishNoticesAudioArchivalAndSelectionModeRespectGotchas()
    {
        var root = FindRepositoryRoot();
        var historyCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.History.cs"));
        var polishCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.AiPolish.cs"));
        var runtimeCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "App.DictationRuntime.cs"));

        // Routine copy & polish notice paths must not pop OperationInfoBar via ShowMessage,
        // and Copy click handlers must never swap Button.Style across ControlTemplates.
        foreach (var methodName in new[] { "CardCopyButton_Click", "CardCopyOriginalMenuItem_Click", "CopyRecoveryButton_Click" })
        {
            var body = ExtractMethodBody(historyCs, methodName);
            Assert.DoesNotContain("ShowMessage(", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".Style =", body, StringComparison.Ordinal);
        }

        foreach (var methodName in new[] { "SetCloudPolishNotice", "SetOllamaPolishNotice" })
        {
            var body = ExtractMethodBody(polishCs, methodName);
            Assert.DoesNotContain("ShowMessage(", body, StringComparison.Ordinal);
        }

        // HistoryList stays SelectionMode="None" and never touches SelectedItems/SelectedItem in code-behind.
        Assert.DoesNotContain("HistoryList.SelectedItems", historyCs, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryList.SelectedItem", historyCs, StringComparison.Ordinal);

        // Release audio archival must not carry Conditional("DEBUG").
        var archiveDecl = Regex.Match(runtimeCs, @"\[.*?\][\r\n\s]*private void ArchiveDictationAudio\(");
        Assert.False(archiveDecl.Success && archiveDecl.Value.Contains("Conditional(\"DEBUG\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PruneRetentionIsNonFatalWhenDirectoryEnumerationOrDeleteFails()
    {
        using var temp = new TempFolder();
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        Directory.CreateDirectory(recordingsDir);

        var archive = new LocalDictationAudioArchive(recordingsDir, maxRecordings: 1, maxTotalBytes: 100_000);
        var audio = new CapturedAudio(DictationSessionId.Create(), new float[1600], 16_000, 1);

        var id1 = Guid.NewGuid();
        var saved1 = archive.SaveForEntry(id1, audio);
        Assert.NotNull(saved1);

        var id2 = Guid.NewGuid();
        var saved2 = archive.SaveForEntry(id2, audio);
        Assert.NotNull(saved2);

        // Part 1: Holding a stale WAV open with FileShare.None triggers IOException in Delete; PruneRetention must not throw.
        var lockedPath = Path.Combine(recordingsDir, saved1!.FileName);
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            archive.PruneRetention(saved2!.FileName);
        }

        // Part 2: Denying directory listing makes Directory.EnumerateFiles throw UnauthorizedAccessException;
        // PruneRetention and SaveHistoryAsync must remain non-fatal.
        var user = WindowsIdentity.GetCurrent().User!;
        var denyRule = new FileSystemAccessRule(
            user,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var dirInfo = new DirectoryInfo(recordingsDir);
        var restricted = dirInfo.GetAccessControl();
        restricted.AddAccessRule(denyRule);
        dirInfo.SetAccessControl(restricted);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.EnumerateFiles(recordingsDir, "*.wav").ToArray());
            archive.PruneRetention(saved2.FileName);

            var persistence = new SessionPersistence(
                new InMemoryRecoveryStore(),
                new StubHistoryStore(),
                new NoOpLogger(),
                TimeProvider.System,
                () => new HistoryPreferences(true, 30),
                new NoOpPersistenceEffects(),
                archive);
            var savedId = await persistence.SaveHistoryAsync(
                new Transcript(DictationSessionId.Create(), "still-saves", "parakeet"),
                "still-saves",
                HistoryWriteIntent.Delivered(false, true),
                audio: audio);
            Assert.NotNull(savedId);
        }
        finally
        {
            var restored = dirInfo.GetAccessControl();
            restored.RemoveAccessRuleSpecific(denyRule);
            dirInfo.SetAccessControl(restored);
        }
    }

    [Fact]
    public async Task ArchiveCleanupRemovesOrphanedTempFilesOnPruneAndClearAll()
    {
        using var temp = new TempFolder();
        var historyPath = Path.Combine(temp.Path, "history.json");
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        Directory.CreateDirectory(recordingsDir);

        var staleInternalTmp = Path.Combine(recordingsDir, $".stale.wav.{Guid.NewGuid():N}.tmp");
        var staleWavTmp = Path.Combine(recordingsDir, "interrupted.wav.tmp");
        var freshActiveTmp = Path.Combine(recordingsDir, $".inflight.wav.{Guid.NewGuid():N}.tmp");

        File.WriteAllBytes(staleInternalTmp, [1, 2, 3]);
        File.WriteAllBytes(staleWavTmp, [4, 5, 6]);
        File.WriteAllBytes(freshActiveTmp, [7, 8, 9]);

        var staleTimestamp = DateTime.UtcNow - LocalDictationAudioArchive.DefaultStaleTempFileAge - TimeSpan.FromMinutes(5);
        File.SetLastWriteTimeUtc(staleInternalTmp, staleTimestamp);
        File.SetLastWriteTimeUtc(staleWavTmp, staleTimestamp);
        File.SetLastWriteTimeUtc(freshActiveTmp, DateTime.UtcNow);

        var archive = new LocalDictationAudioArchive(recordingsDir, maxRecordings: 10, maxTotalBytes: 1_000_000);
        archive.PruneRetention();

        Assert.False(File.Exists(staleInternalTmp));
        Assert.False(File.Exists(staleWavTmp));
        Assert.True(File.Exists(freshActiveTmp));

        // JsonHistoryStore.ClearAsync and archive.ClearAll must remove both *.wav and any remaining *.tmp files.
        using var store = new JsonHistoryStore(historyPath, recordingsDir);
        var entry = DictationHistoryEntry.Create(Now, "Clear test", "parakeet", false, true);
        var saved = archive.SaveForEntry(entry.Id, new CapturedAudio(DictationSessionId.Create(), new float[1600], 16_000, 1));
        await store.AddAsync(entry with { AudioFileName = saved!.FileName, AudioDurationMs = saved.DurationMs }, 30, Now);

        var cleared = await store.ClearAsync();
        Assert.True(cleared.Succeeded);
        Assert.Empty(Directory.GetFiles(recordingsDir));

        var extraWavTmp = Path.Combine(recordingsDir, "leftover.wav.tmp");
        var extraWav = Path.Combine(recordingsDir, $"{Guid.NewGuid():D}.wav");
        File.WriteAllBytes(extraWavTmp, [1]);
        File.WriteAllBytes(extraWav, [2]);
        archive.ClearAll();
        Assert.Empty(Directory.GetFiles(recordingsDir));
    }

    [Fact]
    public void CanTogglePlaybackAllowsPauseWhileBusyOnlyWhenCurrentlyPlaying()
    {
        Assert.True(HistoryPlaybackPresentation.CanTogglePlayback(hasAudioRecording: true, isGlobalBusy: false, isPlaying: false));
        Assert.True(HistoryPlaybackPresentation.CanTogglePlayback(hasAudioRecording: true, isGlobalBusy: false, isPlaying: true));
        Assert.True(HistoryPlaybackPresentation.CanTogglePlayback(hasAudioRecording: true, isGlobalBusy: true, isPlaying: true));
        Assert.False(HistoryPlaybackPresentation.CanTogglePlayback(hasAudioRecording: true, isGlobalBusy: true, isPlaying: false));
        Assert.False(HistoryPlaybackPresentation.CanTogglePlayback(hasAudioRecording: false, isGlobalBusy: false, isPlaying: false));
        Assert.False(HistoryPlaybackPresentation.CanTogglePlayback(hasAudioRecording: false, isGlobalBusy: true, isPlaying: true));

        var root = FindRepositoryRoot();
        var historyCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.History.cs"));
        Assert.Contains("HistoryPlaybackPresentation.CanTogglePlayback(_hasAudioRecording, _isGlobalBusy, _isPlaying)", historyCs, StringComparison.Ordinal);

        var setSnapshotStart = historyCs.IndexOf("public void SetPlaybackSnapshot(", StringComparison.Ordinal);
        Assert.True(setSnapshotStart >= 0, "Method 'public void SetPlaybackSnapshot(' not found.");
        var setSnapshotEnd = historyCs.IndexOf("private void ApplySnapshotFields(", setSnapshotStart, StringComparison.Ordinal);
        Assert.True(setSnapshotEnd > setSnapshotStart, "End of 'SetPlaybackSnapshot' not found.");
        var setSnapshotBody = historyCs[setSnapshotStart..setSnapshotEnd];
        Assert.Contains("nameof(CanTogglePlayback)", setSnapshotBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscapeRecoverySkipsAudioArchivalEvenWhenHistoryAndAudioAreEnabled()
    {
        using var temp = new TempFolder();
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        Directory.CreateDirectory(recordingsDir);

        var archive = new LocalDictationAudioArchive(recordingsDir);
        var fakeStore = new StubHistoryStore();
        var persistence = new SessionPersistence(
            new InMemoryRecoveryStore(),
            fakeStore,
            new NoOpLogger(),
            TimeProvider.System,
            () => new HistoryPreferences(true, 30),
            new NoOpPersistenceEffects(),
            archive);
        var audio = new CapturedAudio(DictationSessionId.Create(), new float[1600], 16_000, 1);

        var recoveryIntent = HistoryWriteIntent.EscapeRecovery(wasPolished: false, Now.AddHours(24));
        Assert.False(recoveryIntent.ArchiveAudio);
        Assert.True(HistoryWriteIntent.Held(wasPolished: false).ArchiveAudio);
        Assert.True(HistoryWriteIntent.Delivered(wasPolished: false, wasDelivered: true).ArchiveAudio);

        var savedId = await persistence.SaveHistoryAsync(
            new Transcript(DictationSessionId.Create(), "escape recovery text", "parakeet"),
            "escape recovery text",
            recoveryIntent,
            audio: audio);

        Assert.NotNull(savedId);
        var entry = Assert.Single(fakeStore.Added);
        Assert.Null(entry.AudioFileName);
        Assert.Null(entry.AudioDurationMs);
        Assert.Empty(Directory.GetFiles(recordingsDir, "*.wav"));
    }

    [Fact]
    public void SavedDictationPasteNoticeTargetsCardInlineWhenRenderedAndFallsBackToStatusBannerWhenOffScreen()
    {
        var cardId = Guid.NewGuid();

        var inlinePlan = HistoryPlaybackPresentation.PlanSavedDictationPasteNotice(
            isUndoAction: false,
            isRecoveryCardVisible: false,
            lastPasteRequestedEntryId: cardId,
            isTargetCardRendered: true,
            title: "Paste could not be confirmed",
            sentence: "Could not confirm the paste.",
            tone: HistoryPasteNoticeTone.Warning);
        Assert.Equal(HistoryPasteNoticeTarget.CardInline, inlinePlan.Target);
        Assert.Equal(cardId, inlinePlan.TargetEntryId);

        foreach (var tone in new[] { HistoryPasteNoticeTone.Warning, HistoryPasteNoticeTone.Error, HistoryPasteNoticeTone.Informational })
        {
            var bannerPlan = HistoryPlaybackPresentation.PlanSavedDictationPasteNotice(
                isUndoAction: false,
                isRecoveryCardVisible: false,
                lastPasteRequestedEntryId: cardId,
                isTargetCardRendered: false,
                title: "Notice title",
                sentence: "Notice sentence",
                tone: tone);
            Assert.Equal(HistoryPasteNoticeTarget.StatusBanner, bannerPlan.Target);
            Assert.Equal(tone, bannerPlan.Tone);
            Assert.Equal("Notice title", bannerPlan.Title);
            Assert.Equal("Notice sentence", bannerPlan.Sentence);
        }

        var undoCardPlan = HistoryPlaybackPresentation.PlanSavedDictationPasteNotice(
            isUndoAction: true,
            isRecoveryCardVisible: true,
            lastPasteRequestedEntryId: cardId,
            isTargetCardRendered: true,
            title: "Dictation placed on clipboard",
            sentence: "Words are on the clipboard.",
            tone: HistoryPasteNoticeTone.Warning);
        Assert.Equal(HistoryPasteNoticeTarget.RecoveryCard, undoCardPlan.Target);

        var undoCollapsedPlan = HistoryPlaybackPresentation.PlanSavedDictationPasteNotice(
            isUndoAction: true,
            isRecoveryCardVisible: false,
            lastPasteRequestedEntryId: cardId,
            isTargetCardRendered: true,
            title: "Dictation placed on clipboard",
            sentence: "Words are on the clipboard.",
            tone: HistoryPasteNoticeTone.Warning);
        Assert.Equal(HistoryPasteNoticeTarget.StatusBanner, undoCollapsedPlan.Target);

        var root = FindRepositoryRoot();
        var historyCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.History.cs"));
        var surfaceBody = ExtractMethodBody(historyCs, "private void SurfacePasteNotice");
        Assert.Contains("HistoryPlaybackPresentation.PlanSavedDictationPasteNotice(", surfaceBody, StringComparison.Ordinal);
        Assert.Contains("ShowMessage(plan.Title, plan.Sentence, severity);", surfaceBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitDeleteManyAndClearFailWithoutErasingHistoryWhenAudioCannotBeRemoved()
    {
        using var temp = new TempFolder();
        var historyPath = Path.Combine(temp.Path, "history.json");
        var recordingsDir = Path.Combine(temp.Path, "recordings", "dictations");
        Directory.CreateDirectory(recordingsDir);

        using var store = new JsonHistoryStore(historyPath, recordingsDir);
        var archive = new LocalDictationAudioArchive(recordingsDir);
        var audio = new CapturedAudio(DictationSessionId.Create(), new float[1600], 16_000, 1);

        var entry1 = DictationHistoryEntry.Create(Now, "Entry one", "parakeet", false, true);
        var saved1 = archive.SaveForEntry(entry1.Id, audio)!;
        await store.AddAsync(entry1 with { AudioFileName = saved1.FileName, AudioDurationMs = saved1.DurationMs }, 30, Now);

        var entry2 = DictationHistoryEntry.Create(Now.AddMinutes(1), "Entry two", "parakeet", false, true);
        var saved2 = archive.SaveForEntry(entry2.Id, audio)!;
        await store.AddAsync(entry2 with { AudioFileName = saved2.FileName, AudioDurationMs = saved2.DurationMs }, 30, Now.AddMinutes(1));

        var wav1Path = Path.Combine(recordingsDir, saved1.FileName);
        var wav2Path = Path.Combine(recordingsDir, saved2.FileName);

        // Locking entry1's WAV must fail explicit DeleteAsync and keep entry1 in history.json.
        using (new FileStream(wav1Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var deleteResult = await store.DeleteAsync(entry1.Id);
            Assert.False(deleteResult.Succeeded);
            Assert.Equal(AppErrorCode.StorageUnavailable, deleteResult.Error?.Code);

            var loaded = await store.LoadAsync(30, Now.AddMinutes(2));
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Contains(loaded.Entries, e => e.Id == entry1.Id);
            Assert.True(File.Exists(wav1Path));
        }

        // Locking entry2's WAV must fail explicit DeleteManyAsync for [entry1, entry2] AND roll back entry1's staged WAV.
        using (new FileStream(wav2Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var deleteManyResult = await store.DeleteManyAsync([entry1.Id, entry2.Id]);
            Assert.False(deleteManyResult.Succeeded);
            Assert.Equal(AppErrorCode.StorageUnavailable, deleteManyResult.Error?.Code);

            var loaded = await store.LoadAsync(30, Now.AddMinutes(2));
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Contains(loaded.Entries, e => e.Id == entry1.Id);
            Assert.Contains(loaded.Entries, e => e.Id == entry2.Id);
            Assert.True(File.Exists(wav1Path));
            Assert.True(File.Exists(wav2Path));

            // ClearAsync must likewise fail when any WAV under recordingsDir remains locked, preserving both WAVs.
            var clearLockedResult = await store.ClearAsync();
            Assert.False(clearLockedResult.Succeeded);
            Assert.Equal(AppErrorCode.StorageUnavailable, clearLockedResult.Error?.Code);
            Assert.Equal(2, (await store.LoadAsync(30, Now.AddMinutes(2))).Entries.Count);
            Assert.True(File.Exists(wav1Path));
            Assert.True(File.Exists(wav2Path));
        }

        // Releasing all locks allows ClearAsync to succeed and remove both entries and WAVs.
        var clearResult = await store.ClearAsync();
        Assert.True(clearResult.Succeeded);
        Assert.Empty((await store.LoadAsync(30, Now.AddMinutes(2))).Entries);
        Assert.Empty(Directory.GetFiles(recordingsDir));

        // Passive LoadAsync retention pruning must stay non-fatal if locked, and retry deletion on later loads once unlocked.
        var expired = DictationHistoryEntry.Create(Now.AddDays(-10), "Expired entry", "parakeet", false, true);
        var savedExpired = archive.SaveForEntry(expired.Id, audio)!;
        await store.AddAsync(expired with { AudioFileName = savedExpired.FileName, AudioDurationMs = savedExpired.DurationMs }, 30, Now);
        var expiredWavPath = Path.Combine(recordingsDir, savedExpired.FileName);
        using (new FileStream(expiredWavPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var passiveLoaded = await store.LoadAsync(1, Now);
            Assert.Equal(HistoryLoadStatus.Loaded, passiveLoaded.Status);
            Assert.Empty(passiveLoaded.Entries);
            Assert.True(File.Exists(expiredWavPath));
        }

        var retriedLoad = await store.LoadAsync(1, Now.AddMinutes(5));
        Assert.Equal(HistoryLoadStatus.Loaded, retriedLoad.Status);
        Assert.False(File.Exists(expiredWavPath));

        // Simulate an interrupted deletion where .deltmp files remain while one entry is still in history.json and another is not.
        var surviving = DictationHistoryEntry.Create(Now, "Surviving take", "parakeet", false, true);
        var savedSurviving = archive.SaveForEntry(surviving.Id, audio)!;
        await store.AddAsync(surviving with { AudioFileName = savedSurviving.FileName, AudioDurationMs = savedSurviving.DurationMs }, 30, Now);
        var survivingWavPath = Path.Combine(recordingsDir, savedSurviving.FileName);
        var interruptedStagedPath = $"{survivingWavPath}.{Guid.NewGuid():N}.deltmp";
        File.Move(survivingWavPath, interruptedStagedPath);
        var staleOrphanStagedPath = Path.Combine(recordingsDir, $"{Guid.NewGuid():D}.wav.{Guid.NewGuid():N}.deltmp");
        File.WriteAllBytes(staleOrphanStagedPath, [1, 2, 3]);

        // Even PruneRetention on an unrelated save must never destroy interrupted .deltmp files before JsonHistoryStore recovers them.
        archive.PruneRetention();
        Assert.True(File.Exists(interruptedStagedPath));

        var recoveredLoad = await store.LoadAsync(30, Now.AddMinutes(6));
        Assert.Equal(HistoryLoadStatus.Loaded, recoveredLoad.Status);
        Assert.True(File.Exists(survivingWavPath));
        Assert.False(File.Exists(interruptedStagedPath));
        Assert.False(File.Exists(staleOrphanStagedPath));
    }

    [Fact]
    public void DictationStartStopsHistoryPlaybackAndBlocksCardPlaybackWhileBusy()
    {
        var root = FindRepositoryRoot();
        var onboardingCs = AppXamlSources.ReadMainWindowOnboardingCodeText(root);
        var historyCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.History.cs"));

        var setDictationBody = ExtractMethodBody(onboardingCs, "public void SetDictationActive");
        Assert.Contains("_audioPlayback.Stop();", setDictationBody, StringComparison.Ordinal);

        foreach (var handler in new[] { "private void CardPlayPauseButton_Click", "private void CardWaveform_SeekRequested" })
        {
            var body = ExtractMethodBody(historyCs, handler);
            Assert.Contains("_dictationActive", body, StringComparison.Ordinal);
            Assert.Contains("IsSpeechEngineBusy?.Invoke() == true", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MediaPlaybackCallbackGuardRejectsStaleDisposedOrUnloadedCallbacks()
    {
        var activeId = Guid.NewGuid();
        Assert.True(HistoryPlaybackPresentation.ShouldAcceptMediaCallback(
            disposed: false,
            callbackGeneration: 3,
            currentGeneration: 3,
            hasLoadedSource: true,
            activeEntryId: activeId));

        Assert.False(HistoryPlaybackPresentation.ShouldAcceptMediaCallback(false, 2, 3, true, activeId));
        Assert.False(HistoryPlaybackPresentation.ShouldAcceptMediaCallback(false, 0, 0, true, activeId));
        Assert.False(HistoryPlaybackPresentation.ShouldAcceptMediaCallback(true, 3, 3, true, activeId));
        Assert.False(HistoryPlaybackPresentation.ShouldAcceptMediaCallback(false, 3, 3, false, activeId));
        Assert.False(HistoryPlaybackPresentation.ShouldAcceptMediaCallback(false, 3, 3, true, null));
        Assert.False(HistoryPlaybackPresentation.ShouldAcceptMediaCallback(false, 3, 3, true, Guid.Empty));

        var root = FindRepositoryRoot();
        var playbackCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "Services", "WindowsAudioPlaybackService.cs"));
        Assert.Contains("Interlocked.Increment(ref _mediaGeneration);", playbackCs, StringComparison.Ordinal);
        Assert.Contains("AttachMediaHandlers(player, sourceGeneration);", playbackCs, StringComparison.Ordinal);
        Assert.Contains("_mediaEndedHandler = (_, _) => OnMediaEnded(sourceGeneration);", playbackCs, StringComparison.Ordinal);
        Assert.Contains("_mediaFailedHandler = (_, _) => OnMediaFailed(sourceGeneration);", playbackCs, StringComparison.Ordinal);

        foreach (var callbackName in new[] { "private void OnMediaEnded", "private void OnMediaFailed" })
        {
            var body = ExtractMethodBody(playbackCs, callbackName);
            Assert.Contains("HistoryPlaybackPresentation.ShouldAcceptMediaCallback(", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SetOllamaPolishNoticeClearsStaleNoticeAndPreservesActiveCatalogFallback()
    {
        Assert.Equal("Model warmup failed", HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay("Model warmup failed", null));
        Assert.Equal("Model warmup failed", HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay("Model warmup failed", "Ollama warning"));
        Assert.Equal("Ollama warning", HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay(null, "Ollama warning"));
        Assert.Equal("Ollama warning", HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay("   ", "Ollama warning"));
        Assert.Null(HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay(null, null));
        Assert.Null(HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay("  ", " "));

        var root = FindRepositoryRoot();
        var polishCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.AiPolish.cs"));
        var setOllamaBody = ExtractMethodBody(polishCs, "SetOllamaPolishNotice");
        Assert.Contains("HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay(", setOllamaBody, StringComparison.Ordinal);
        Assert.Contains("effectiveNotice is null ? Visibility.Collapsed : Visibility.Visible", setOllamaBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowMessage(", setOllamaBody, StringComparison.Ordinal);

        var ollamaCs = File.ReadAllText(Path.Combine(root, "src", "Production", "EnviousWispr.App", "MainWindow.OllamaModels.cs"));
        Assert.Contains("HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay(", ollamaCs, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var start = source.IndexOf(methodName + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{methodName}' not found.");
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.IndexOf("\r\n    }\r\n", start, StringComparison.Ordinal);
        }

        Assert.True(end > start, $"Closing brace for '{methodName}' not found.");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ew-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class StubHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Added { get; } = [];
        public bool FailAdds { get; set; }

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult(Added.ToArray(), HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            if (FailAdds)
            {
                return Task.FromResult(new HistoryOperationResult(false, new AppError(AppErrorCode.StorageUnavailable, AppErrorStage.HistorySave, CanRetry: true)));
            }

            Added.Add(entry);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class InMemoryRecoveryStore : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class NoOpPersistenceEffects : ISessionPersistenceEffects
    {
        public void ShowPendingRecovery(RecoveryTextRecord record, bool undoOffered) { }
        public void ClearRecoveredText() { }
        public void NotifyHistoryChanged() { }
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Write(AppLogEntry entry) { }
    }
}
