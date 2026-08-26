using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

public sealed class PrivacySafeObservabilityTests
{
    private const string Sentinel = "PRIVATE dictated clipboard context api-key C:\\private\\model.gguf";

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public void SettingsRejectOutOfRangeDiagnosticRetention(int retentionDays)
    {
        var settings = AppSettings.Default with
        {
            Observability = new ObservabilityPreferences(true, retentionDays, false),
        };

        Assert.Equal(
            AppErrorCode.InvalidData,
            AppSettingsValidator.Validate(settings, AppErrorStage.SettingsSave)?.Code);
    }

    [Fact]
    public void InvalidEnumValuesCannotReachThePrivacySafeRecord()
    {
        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UtcNow,
            (AppEventCode)int.MaxValue,
            (AppFailureCategory)int.MaxValue,
            Provider: (DiagnosticProvider)int.MaxValue,
            ErrorCode: (AppErrorCode)int.MaxValue,
            Engine: (DiagnosticEngineChoice)int.MaxValue,
            HardwareClass: (DiagnosticHardwareClass)int.MaxValue));

        Assert.Equal(AppEventCode.UnhandledFailure, record.Event);
        Assert.Equal(AppFailureCategory.Unknown, record.Failure);
        Assert.Null(record.Provider);
        Assert.Null(record.ErrorCode);
        Assert.Null(record.Engine);
        Assert.Null(record.HardwareClass);
    }

    [Fact]
    public async Task ExportReparsesStrictRecordsAndDropsInjectedContent()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var source = Path.Combine(directory, "app.jsonl");
            var destination = Path.Combine(directory, "export.jsonl");
            var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
            var logger = new JsonLineFileLogger(source);
            logger.Write(new AppLogEntry(
                now,
                AppEventCode.RuntimeSelectionObserved,
                Engine: DiagnosticEngineChoice.Whisper,
                HardwareClass: DiagnosticHardwareClass.GpuPresent));
            await File.AppendAllTextAsync(
                source,
                $$"""
                {"timestamp":"{{now:O}}","event":"ShellShown","failure":"None","transcript":"{{Sentinel}}"}
                """ + Environment.NewLine);

            var result = await new JsonDiagnosticExportService(source).ExportAsync(
                destination,
                14,
                now);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.ExportedRecordCount);
            var exported = await File.ReadAllTextAsync(destination);
            Assert.DoesNotContain(Sentinel, exported, StringComparison.Ordinal);
            Assert.DoesNotContain("transcript", exported, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(exported);
            var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToHashSet();
            Assert.Subset(
                new HashSet<string>
                {
                    "timestamp", "event", "failure", "elapsedMilliseconds", "provider",
                    "errorCode", "engine", "hardwareClass",
                },
                names);
        });
    }

    [Fact]
    public async Task AnonymousTransportReceivesNothingBeforeConsentAndOnlyTypedRecordsAfterConsent()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var transport = new RecordingTransport();
            await using var logger = new PrivacySafeObservabilityLogger(
                new JsonLineFileLogger(Path.Combine(directory, "app.jsonl"), enabled: false),
                transport);
            var now = DateTimeOffset.UtcNow;
            logger.Configure(new ObservabilityPreferences(true, 14, false), now);
            logger.Write(new AppLogEntry(now, AppEventCode.ApplicationStarting));
            await Task.Delay(50);
            Assert.Empty(transport.Records);

            logger.Configure(new ObservabilityPreferences(true, 14, true), now);
            logger.Write(new AppLogEntry(
                now,
                AppEventCode.PolishCompleted,
                Provider: DiagnosticProvider.OpenAi,
                ElapsedMilliseconds: 25));
            await transport.Received.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var record = Assert.Single(transport.Records);
            Assert.Equal(DiagnosticProvider.OpenAi, record.Provider);
            Assert.Equal(25, record.ElapsedMilliseconds);
            var properties = record.GetType().GetProperties().Select(property => property.Name).ToArray();
            Assert.DoesNotContain(properties, name =>
                name.Contains("Text", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Clipboard", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Id", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData("https://telemetry.example.test/v1/events", false, true)]
    [InlineData("http://127.0.0.1:43210/events", true, true)]
    [InlineData("http://127.0.0.1:43210/events", false, false)]
    [InlineData("http://telemetry.example.test/events", true, false)]
    [InlineData("https://user:secret@telemetry.example.test/events", false, false)]
    [InlineData("https://telemetry.example.test/events?content=unsafe", false, false)]
    public void EndpointPolicyRequiresHttpsExceptExplicitLoopbackUat(
        string value,
        bool allowLoopback,
        bool expected)
    {
        Assert.Equal(expected, TelemetryEndpointPolicy.TryNormalize(value, allowLoopback, out _));
    }

    private sealed class RecordingTransport : IPrivacySafeTelemetryTransport
    {
        private readonly Lock _lock = new();

        public List<PrivacySafeDiagnosticRecord> Records { get; } = [];

        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(
            PrivacySafeDiagnosticRecord record,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Records.Add(record);
            }

            Received.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
