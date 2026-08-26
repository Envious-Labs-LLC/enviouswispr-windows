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
            Assert.Equal(1, recovered.ConsecutiveInterruptedRuns);
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
        Assert.Equal(0, clean.ConsecutiveInterruptedRuns);
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

    private static DateTimeOffset Timestamp(int minute) => new(
        year: 2026,
        month: 8,
        day: 26,
        hour: 10,
        minute: minute,
        second: 0,
        offset: TimeSpan.Zero);
}
