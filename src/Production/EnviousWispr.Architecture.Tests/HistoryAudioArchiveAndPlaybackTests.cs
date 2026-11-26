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

        // Oldest file pruned by count bound 2 while third (newest) is preserved.
        Assert.Null(archive.ResolveExistingPath(first.FileName));
        Assert.NotNull(archive.ResolveExistingPath(third!.FileName));

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

        tracker.UpdateProgress(cardA, 1_500, 4_000);
        Assert.Equal(HistoryPlaybackToggleOutcome.Pause, tracker.Toggle(cardA, 4_000));
        Assert.False(tracker.IsPlaying);
        Assert.Equal(1_500, tracker.PositionMs);

        // Switching to cardB stops cardA and starts B from 0.
        Assert.Equal(HistoryPlaybackToggleOutcome.StartNew, tracker.Toggle(cardB, 8_000));
        Assert.Equal(cardB, tracker.ActiveEntryId);
        Assert.True(tracker.IsPlaying);
        Assert.Equal(0, tracker.PositionMs);

        Assert.Equal(4_000, tracker.Seek(cardB, 0.5, 8_000));
        Assert.Equal("0:04 / 0:08", HistoryPlaybackPresentation.FormatClockDisplay(tracker.PositionMs, tracker.DurationMs, isActive: true));

        tracker.MarkCompleted(cardB);
        Assert.False(tracker.IsPlaying);
        Assert.Equal(0, tracker.PositionMs);
        Assert.Equal("0:08", HistoryPlaybackPresentation.FormatClockDisplay(0, 8_000, isActive: false));
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
        foreach (var methodName in new[] { "CardCopyButton_Click", "CardCopyAlternateButton_Click", "CopyRecoveryButton_Click" })
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
