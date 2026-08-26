using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

public sealed class JsonLineFileLoggerTests
{
    [Fact]
    public void WriteEmitsOnlyTypedContentFreeFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EnviousWispr.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "app.jsonl");
        try
        {
            var logger = new JsonLineFileLogger(path);
            logger.Write(new AppLogEntry(
                DateTimeOffset.UnixEpoch,
                AppEventCode.ShellShown,
                AppFailureCategory.None,
                ElapsedMilliseconds: 12));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();

            Assert.Equal(["elapsedMilliseconds", "event", "failure", "timestamp"], names);
            Assert.False(document.RootElement.TryGetProperty("message", out _));
            Assert.False(document.RootElement.TryGetProperty("text", out _));
            Assert.False(document.RootElement.TryGetProperty("transcript", out _));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ProviderDiagnosticIsLowCardinalityAndContainsNoContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EnviousWispr.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "app.jsonl");
        try
        {
            var logger = new JsonLineFileLogger(path);
            logger.Write(new AppLogEntry(
                DateTimeOffset.UnixEpoch,
                AppEventCode.PolishDegraded,
                AppFailureCategory.CloudPolish,
                ElapsedMilliseconds: 20,
                Provider: DiagnosticProvider.OpenAi,
                ErrorCode: AppErrorCode.PolishRateLimited));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("OpenAi", document.RootElement.GetProperty("provider").GetString());
            Assert.Equal(
                "PolishRateLimited",
                document.RootElement.GetProperty("errorCode").GetString());
            Assert.False(document.RootElement.TryGetProperty("message", out _));
            Assert.False(document.RootElement.TryGetProperty("text", out _));
            Assert.False(document.RootElement.TryGetProperty("transcript", out _));
            Assert.False(document.RootElement.TryGetProperty("apiKey", out _));
            Assert.False(document.RootElement.TryGetProperty("requestBody", out _));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void InvalidDurationIsRemovedBeforeSerialization()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EnviousWispr.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "app.jsonl");
        try
        {
            var logger = new JsonLineFileLogger(path);
            logger.Write(new AppLogEntry(
                DateTimeOffset.UnixEpoch,
                AppEventCode.DictationTranscriptionCompleted,
                ElapsedMilliseconds: PrivacySafeDiagnosticRecord.MaximumElapsedMilliseconds + 1));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(document.RootElement.TryGetProperty("elapsedMilliseconds", out _));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LocalOptOutStopsWritesAndRetentionPrunesExpiredRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EnviousWispr.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "app.jsonl");
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var logger = new JsonLineFileLogger(path);
            logger.Write(new AppLogEntry(now.AddDays(-20), AppEventCode.ApplicationStarting));
            logger.Write(new AppLogEntry(now.AddDays(-1), AppEventCode.ShellShown));
            logger.Configure(new ObservabilityPreferences(true, 14, false), now);

            var retained = File.ReadAllLines(path);
            Assert.Single(retained);
            Assert.Contains("ShellShown", retained[0], StringComparison.Ordinal);

            logger.Configure(new ObservabilityPreferences(false, 14, false), now);
            logger.Write(new AppLogEntry(now, AppEventCode.ShellClosed));
            Assert.Single(File.ReadAllLines(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
