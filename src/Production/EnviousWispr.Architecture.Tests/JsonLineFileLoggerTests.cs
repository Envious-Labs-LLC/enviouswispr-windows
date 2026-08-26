using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
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
                Provider: "openai",
                ErrorCode: AppErrorCode.PolishRateLimited));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("openai", document.RootElement.GetProperty("provider").GetString());
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
}
