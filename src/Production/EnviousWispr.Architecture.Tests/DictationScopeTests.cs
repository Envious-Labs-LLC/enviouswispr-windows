using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Every line of one dictation says which dictation it was, in the file, after a prune.
/// </summary>
/// <remarks>
/// MEASURED THROUGH THE REAL LOGGER AND THE REAL FILE, because every way this could fail is a way
/// that compiles. A field can be dropped by a converter, stripped by the retention rewrite, or never
/// reach the writer at all - the first version of this change was dropped by a converter and would
/// have shipped joining nothing.
/// </remarks>
public sealed class DictationScopeTests
{
    [Fact]
    public async Task EveryLineWrittenInsideAScopeCarriesTheSameDictation()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(directory =>
        {
            var path = Path.Combine(directory, "app.jsonl");
            var logger = new JsonLineFileLogger(path, enabled: true);
            var dictation = Guid.NewGuid();

            using (DictationScope.Begin(dictation))
            {
                logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow, AppEventCode.DictationTranscriptionStarted));
                logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow, AppEventCode.TextDeliveryCompleted,
                    ElapsedMilliseconds: 12));
            }

            var lines = ReadLines(path);
            Assert.Equal(2, lines.Count);
            Assert.All(lines, line => Assert.Equal(dictation, line.DictationId));
            return Task.CompletedTask;
        });
    }

    /// <summary>A line written outside a dictation claims no dictation.</summary>
    /// <remarks>
    /// THE HALF THAT STOPS THE JOIN MEANING NOTHING. If everything carried an id, an id would say
    /// only that a line exists. Startup, settings and update lines are not part of any dictation and
    /// must say so by having none.
    /// </remarks>
    [Fact]
    public async Task ALineOutsideADictationCarriesNone()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(directory =>
        {
            var path = Path.Combine(directory, "app.jsonl");
            var logger = new JsonLineFileLogger(path, enabled: true);

            logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ApplicationStarting));

            Assert.Null(Assert.Single(ReadLines(path)).DictationId);
            return Task.CompletedTask;
        });
    }

    /// <summary>The scope puts back what it found, so a nested one cannot unjoin its parent.</summary>
    [Fact]
    public void ANestedScopeRestoresTheOneItInterrupted()
    {
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (DictationScope.Begin(outer))
        {
            Assert.Equal(outer, DictationScope.Current);
            using (DictationScope.Begin(inner))
            {
                Assert.Equal(inner, DictationScope.Current);
            }

            Assert.Equal(outer, DictationScope.Current);
        }

        Assert.Null(DictationScope.Current);
    }

    /// <summary>The scope follows the work across an await, not the thread that started it.</summary>
    /// <remarks>
    /// A `[ThreadStatic]` would join the first half of a dictation and lose the rest at its first
    /// await, which is where every stage of this pipeline happens.
    /// </remarks>
    [Fact]
    public async Task TheScopeSurvivesAnAwait()
    {
        var dictation = Guid.NewGuid();
        using (DictationScope.Begin(dictation))
        {
            await Task.Yield();
            await Task.Delay(1).ConfigureAwait(true);
            Assert.Equal(dictation, DictationScope.Current);
        }
    }

    /// <summary>A prune keeps the join it rewrites.</summary>
    /// <remarks>
    /// THE RETENTION PASS READS EVERY LINE AND WRITES THE SURVIVORS BACK. Parse as one shape and
    /// write another and the join disappears on the first prune - two weeks later, or the first time
    /// the log grew - with nothing saying it had happened.
    /// </remarks>
    [Fact]
    public async Task APruneKeepsTheJoin()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(directory =>
        {
            var path = Path.Combine(directory, "app.jsonl");
            var logger = new JsonLineFileLogger(path, enabled: true);
            var dictation = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            using (DictationScope.Begin(dictation))
            {
                logger.Write(new AppLogEntry(now, AppEventCode.DictationCompleted, ElapsedMilliseconds: 5));
            }

            // Configure runs the retention prune, which rewrites the file.
            logger.Configure(new ObservabilityPreferences(true, 14, false), now);

            Assert.Equal(dictation, Assert.Single(ReadLines(path)).DictationId);
            return Task.CompletedTask;
        });
    }

    private static List<LocalDiagnosticLine> ReadLines(string path) =>
        File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                Assert.True(JsonLineFileLogger.TryParseRecord(line, out var record), line);
                Assert.NotNull(record);
                return record!;
            })
            .ToList();
}
