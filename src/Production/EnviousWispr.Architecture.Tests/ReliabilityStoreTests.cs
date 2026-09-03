using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Services.Reliability;

namespace EnviousWispr.Architecture.Tests;

public sealed class ReliabilityStoreTests
{
    [Fact]
    public async Task RunStateDetectsInterruptedRunAndResetsAfterCleanShutdown()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        Guid interruptedRunId;
        using (var firstStore = new JsonApplicationRunStateStore(path))
        {
            var first = await firstStore.BeginRunAsync(Timestamp(0));
            Assert.Equal(RunStateLoadStatus.Started, first.Status);
            interruptedRunId = first.RunId;
            Assert.True(await firstStore.HeartbeatAsync(
                first.RunId,
                Timestamp(1)));
        }

        using (var recoveredStore = new JsonApplicationRunStateStore(path))
        {
            var recovered = await recoveredStore.BeginRunAsync(
                Timestamp(2));
            Assert.Equal(RunStateLoadStatus.PreviousRunInterrupted, recovered.Status);
            Assert.True(recovered.RecoveredInterruptedRun);
            // NOT DICTATING, because the interrupted run never said it was. That is the difference
            // between a warning about lost words and silence.
            Assert.False(recovered.PreviousRunWasDictating);
            Assert.NotEqual(interruptedRunId, recovered.RunId);
            Assert.False(await recoveredStore.CompleteRunAsync(
                interruptedRunId,
                Timestamp(3)));
            Assert.True(await recoveredStore.CompleteRunAsync(
                recovered.RunId,
                Timestamp(3)));
        }

        using var cleanStore = new JsonApplicationRunStateStore(path);
        var clean = await cleanStore.BeginRunAsync(Timestamp(4));
        Assert.Equal(RunStateLoadStatus.Started, clean.Status);
        Assert.False(clean.PreviousRunWasDictating);
    }

    /// <summary>A run stopped mid-dictation says so to the next one.</summary>
    /// <remarks>
    /// THE ONE ROW THAT EARNS A WARNING. Recovery text is written only after transcription finishes,
    /// so a stop during a dictation leaves nothing to restore and is indistinguishable from an idle
    /// restart unless the flag survives. This is what makes it survive.
    ///
    /// AND THE HEARTBEAT MUST NOT CLEAR IT, which is the half a careless implementation gets wrong.
    /// The heartbeat runs once a minute and knows nothing about dictations, so it is checked here
    /// between the mark and the crash.
    /// </remarks>
    [Fact]
    public async Task ARunStoppedWhileDictatingTellsTheNextRunSo()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        using (var dictatingStore = new JsonApplicationRunStateStore(path))
        {
            var run = await dictatingStore.BeginRunAsync(Timestamp(0));
            Assert.True(await dictatingStore.SetDictationActiveAsync(run.RunId, true, Timestamp(1)));
            Assert.True(await dictatingStore.HeartbeatAsync(run.RunId, Timestamp(2)));
        }

        using (var afterCrash = new JsonApplicationRunStateStore(path))
        {
            var recovered = await afterCrash.BeginRunAsync(Timestamp(3));
            Assert.Equal(RunStateLoadStatus.PreviousRunInterrupted, recovered.Status);
            Assert.True(recovered.PreviousRunWasDictating);
            Assert.True(await afterCrash.SetDictationActiveAsync(recovered.RunId, true, Timestamp(4)));
            Assert.True(await afterCrash.SetDictationActiveAsync(recovered.RunId, false, Timestamp(5)));
        }

        using var afterFinishedDictation = new JsonApplicationRunStateStore(path);
        var next = await afterFinishedDictation.BeginRunAsync(Timestamp(6));
        Assert.Equal(RunStateLoadStatus.PreviousRunInterrupted, next.Status);
        // THE DICTATION FINISHED, so this interruption costs nobody their words and says nothing.
        Assert.False(next.PreviousRunWasDictating);
    }

    [Fact]
    public async Task InvalidRunStateIsPreservedBeforeRecoveryMarkerIsWritten()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        const string invalid = "{not valid json";
        await File.WriteAllTextAsync(path, invalid);

        using var store = new JsonApplicationRunStateStore(path);
        var result = await store.BeginRunAsync(Timestamp(0));

        Assert.Equal(RunStateLoadStatus.InvalidStateRecovered, result.Status);
        Assert.Equal(invalid, await File.ReadAllTextAsync(path + ".previous"));
        Assert.DoesNotContain(invalid, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryTextIsEncryptedAtRestAndRoundTripsForCurrentWindowsUser()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "recovery.json");
        const string privateText = "Synthetic private recovery text that must not appear on disk.";
        var record = new RecoveryTextRecord(
            DictationSessionId.Create(),
            Timestamp(0),
            privateText);
        using var store = new WindowsRecoveryTextStore(path);

        Assert.True(await store.SaveAsync(record));
        var envelope = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(privateText, envelope, StringComparison.Ordinal);

        var loaded = await store.LoadAsync();
        Assert.Equal(RecoveryTextLoadStatus.Found, loaded.Status);
        Assert.Equal(record.SessionId, loaded.Record?.SessionId);
        Assert.Equal(privateText, loaded.Record?.Text);

        Assert.True(await store.ClearAsync());
        Assert.Equal(RecoveryTextLoadStatus.Missing, (await store.LoadAsync()).Status);
    }

    [Fact]
    public async Task InvalidRecoveryEnvelopeIsPreservedBeforeReplacement()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "recovery.json");
        const string invalid = "{not valid json";
        await File.WriteAllTextAsync(path, invalid);
        using var store = new WindowsRecoveryTextStore(path);

        var loaded = await store.LoadAsync();
        Assert.Equal(RecoveryTextLoadStatus.Invalid, loaded.Status);

        var replacement = new RecoveryTextRecord(
            DictationSessionId.Create(),
            Timestamp(0),
            "Synthetic replacement");
        Assert.True(await store.SaveAsync(replacement));
        Assert.Equal(invalid, await File.ReadAllTextAsync(path + ".previous"));
        Assert.Equal(RecoveryTextLoadStatus.Found, (await store.LoadAsync()).Status);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"EnviousWispr-reliability-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    /// <summary>A Windows shutdown reads as a known ending, not an unexplained one.</summary>
    /// <remarks>
    /// THE TWO USED TO BE ONE RECORD. A run only writes a clean exit when the app completes one
    /// itself, and Windows shutting the machine down kills the process first - so a deliberate
    /// Restart was stored with exactly the trace a crash leaves. Ref: #93.
    /// </remarks>
    [Fact]
    public async Task AWindowsEndingIsRecordedAsKnownRatherThanInterrupted()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        using (var store = new JsonApplicationRunStateStore(path))
        {
            var run = await store.BeginRunAsync(Timestamp(0));
            Assert.True(await store.NoteSystemEndingAsync(run.RunId, Timestamp(1)));
        }

        using (var next = new JsonApplicationRunStateStore(path))
        {
            var recovered = await next.BeginRunAsync(Timestamp(2));

            Assert.Equal(RunStateLoadStatus.PreviousRunEndedByWindows, recovered.Status);
            // NOT AN INTERRUPTION, WHICH IS THE WHOLE POINT: nothing downstream should treat a
            // restart as a fault, and no error should travel with it.
            Assert.False(recovered.RecoveredInterruptedRun);
            Assert.Null(recovered.Error);
        }
    }

    /// <summary>A shutdown does not claim the app tidied up after itself.</summary>
    /// <remarks>
    /// WINDOWS ENDING THE SESSION MEANS THE ENDING WAS EXPECTED, not that teardown ran. The process
    /// can still be killed part-way through. If this wrote a clean shutdown, a restart would be
    /// indistinguishable from a proper exit and the next run would report `Started` - erasing the
    /// distinction in the other direction.
    /// </remarks>
    [Fact]
    public async Task AWindowsEndingIsNotRecordedAsACleanShutdown()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        using (var store = new JsonApplicationRunStateStore(path))
        {
            var run = await store.BeginRunAsync(Timestamp(0));
            Assert.True(await store.NoteSystemEndingAsync(run.RunId, Timestamp(1)));
        }

        using (var next = new JsonApplicationRunStateStore(path))
        {
            Assert.NotEqual(RunStateLoadStatus.Started, (await next.BeginRunAsync(Timestamp(2))).Status);
        }
    }

    /// <summary>A shutdown during a dictation still says a dictation was in flight.</summary>
    /// <remarks>
    /// THE ONE THING A USER GENUINELY NEEDS TELLING SURVIVES. Recovery text is written only after
    /// transcription completes, so an ending mid-dictation loses it whether Windows caused the
    /// ending or not.
    /// </remarks>
    [Fact]
    public async Task AWindowsEndingDuringADictationStillReportsTheDictation()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        using (var store = new JsonApplicationRunStateStore(path))
        {
            var run = await store.BeginRunAsync(Timestamp(0));
            Assert.True(await store.SetDictationActiveAsync(run.RunId, true, Timestamp(1)));
            Assert.True(await store.NoteSystemEndingAsync(run.RunId, Timestamp(2)));
        }

        using (var next = new JsonApplicationRunStateStore(path))
        {
            var recovered = await next.BeginRunAsync(Timestamp(3));

            Assert.Equal(RunStateLoadStatus.PreviousRunEndedByWindows, recovered.Status);
            Assert.True(recovered.PreviousRunWasDictating);
        }
    }

    /// <summary>A heartbeat arriving after the notice does not erase it.</summary>
    /// <remarks>
    /// WINDOWS ANNOUNCING THE END OF THE SESSION DOES NOT STOP BEING TRUE. The heartbeat runs on a
    /// timer and knows nothing about it; if it cleared the flag, the very case this records would be
    /// lost whenever the process lived a moment longer than the notice.
    /// </remarks>
    [Fact]
    public async Task AHeartbeatAfterTheNoticeDoesNotEraseIt()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        using (var store = new JsonApplicationRunStateStore(path))
        {
            var run = await store.BeginRunAsync(Timestamp(0));
            Assert.True(await store.NoteSystemEndingAsync(run.RunId, Timestamp(1)));
            Assert.True(await store.HeartbeatAsync(run.RunId, Timestamp(2)));
        }

        using (var next = new JsonApplicationRunStateStore(path))
        {
            Assert.Equal(
                RunStateLoadStatus.PreviousRunEndedByWindows,
                (await next.BeginRunAsync(Timestamp(3))).Status);
        }
    }

    /// <summary>An ending nobody announced is still an interruption.</summary>
    /// <remarks>
    /// THE CONTROL. Task Manager, a forced kill and a power cut genuinely cannot be told from a
    /// crash by the process they end, and must stay exactly as they were.
    /// </remarks>
    [Fact]
    public async Task AnUnannouncedEndingIsStillAnInterruption()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        using (var store = new JsonApplicationRunStateStore(path))
        {
            var run = await store.BeginRunAsync(Timestamp(0));
            Assert.True(await store.HeartbeatAsync(run.RunId, Timestamp(1)));
        }

        using (var next = new JsonApplicationRunStateStore(path))
        {
            var recovered = await next.BeginRunAsync(Timestamp(2));

            Assert.Equal(RunStateLoadStatus.PreviousRunInterrupted, recovered.Status);
            Assert.True(recovered.RecoveredInterruptedRun);
        }
    }

    private static DateTimeOffset Timestamp(int minute) => new(
        year: 2026,
        month: 8,
        day: 26,
        hour: 10,
        minute: minute,
        second: 0,
        offset: TimeSpan.Zero);
}
