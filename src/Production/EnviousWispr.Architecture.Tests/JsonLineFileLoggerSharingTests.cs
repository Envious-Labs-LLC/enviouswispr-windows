using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

/// <summary>The diagnostic log takes an append while somebody else is reading it.</summary>
/// <remarks>
/// THE LINE THAT WENT MISSING WAS A REQUIRED STAGE. The journey harness polls the log while the app
/// writes it; a reader that shares the file for reading only makes the app's append fail with a
/// sharing violation, and since diagnostics are best-effort the line is dropped without a word.
/// Once in about four runs it was a stage the journey requires, and a passing take was reported as
/// having skipped it. Windows decides sharing by what the FIRST opener allowed, so the reader is
/// the one that has to share writes - the harness now does - and the writer shares reads and writes
/// so a reader arriving while a line is being written is not refused either.
/// </remarks>
public sealed class JsonLineFileLoggerSharingTests
{
    [Fact]
    public async Task AnAppendLandsWhileAReaderThatSharesWritesHoldsTheFile()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(directory =>
        {
            var path = Path.Combine(directory, "app.jsonl");
            var logger = new JsonLineFileLogger(path, enabled: true);
            logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.ApplicationStarting));

            // The reader the harness now is: open for reading, sharing reads and writes.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.DeterministicProcessingStarted));
            }

            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Contains("DeterministicProcessingStarted", lines[1]);
            return Task.CompletedTask;
        });
    }
}
