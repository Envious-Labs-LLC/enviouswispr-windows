using System.Reflection;
using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

public sealed class PrivacySafeObservabilityTests
{
    [Theory]
    [InlineData("eg-one", DiagnosticProvider.EgOne)]
    [InlineData("eg-1", DiagnosticProvider.EgOne)]
    [InlineData("ollama", DiagnosticProvider.Ollama)]
    [InlineData("openai", DiagnosticProvider.OpenAi)]
    [InlineData("anthropic", DiagnosticProvider.Anthropic)]
    [InlineData("gemini", DiagnosticProvider.Gemini)]
    public void ProviderIdsMapToLowCardinalityDiagnostics(
        string providerId,
        DiagnosticProvider expected)
    {
        Assert.Equal(expected, DiagnosticProviderIds.FromProviderId(providerId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void UnknownProviderIdsAreNotInvented(string? providerId)
    {
        Assert.Null(DiagnosticProviderIds.FromProviderId(providerId));
    }

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

    /// <summary>
    /// Every field that crosses the network is named in the documents that promise what crosses it.
    /// </summary>
    /// <remarks>
    /// THE DOCUMENTS ARE A HAND-MAINTAINED LIST AND THEY HAVE ALREADY DRIFTED TWICE. Three fields
    /// were added to the telemetry record; the first pass missed the data dictionary, the in-app
    /// disclosure and the UAT allowlist, and the pass that fixed those missed `PRIVACY.md`, whose own
    /// closing paragraph requires it to be updated before any telemetry-schema change. Nothing was
    /// comparing the record to the promise, so each omission was found by a reviewer rather than by
    /// the build.
    ///
    /// FAILS RATHER THAN SKIPS WHEN THE REPOSITORY IS NOT FOUND, because a privacy guard that
    /// quietly opts out on an unfamiliar layout is the same as not having one.
    /// </remarks>
    [Fact]
    public void EveryTelemetryFieldIsNamedInThePrivacyDocuments()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EnviousWispr.Windows.slnx")))
        {
            root = root.Parent;
        }

        Assert.True(
            root is not null,
            "Could not find the repository root from the test output directory, so the privacy " +
            "documents could not be checked. Fix the lookup rather than removing this test.");

        // THE TABLE ROWS ARE PARSED, NOT SEARCHED FOR. "does `stage` appear anywhere in this file"
        // is satisfied by a passing mention in a paragraph, and it says nothing at all about a field
        // REMOVED from the record and left standing in the dictionary. Comparing the row set against
        // reflection in both directions is what actually binds the two.
        var dictionary = Path.Combine(root!.FullName, "docs", "privacy", "observability.md");
        Assert.True(File.Exists(dictionary), $"Missing the approved data dictionary: {dictionary}");

        var documented = DocumentedTelemetryFields(File.ReadAllLines(dictionary));
        var actual = TelemetryFieldNames();

        Assert.False(
            documented.Count == 0,
            "No rows were parsed out of the allowed data dictionary table. The table shape changed " +
            "and this gate stopped checking anything; fix the parser rather than deleting the test.");

        var undisclosed = actual.Except(documented).Order().ToArray();
        Assert.True(
            undisclosed.Length == 0,
            $"These fields cross the network with no row in docs/privacy/observability.md: " +
            $"{string.Join(", ", undisclosed)}. A field has to be disclosed before it ships.");

        var phantom = documented.Except(actual).Order().ToArray();
        Assert.True(
            phantom.Length == 0,
            $"The data dictionary promises fields the record no longer has: " +
            $"{string.Join(", ", phantom)}. A stale row makes the disclosure wrong in the other " +
            "direction, so remove it in the same change that removes the field.");
    }

    /// <summary>The camelCase names of every field on the record that crosses the network.</summary>
    private static HashSet<string> TelemetryFieldNames() =>
        typeof(PrivacySafeDiagnosticRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The field named by the first cell of each row of the allowed-data table.</summary>
    /// <remarks>
    /// Rows look like <c>| `stage` | optional enum | why it exists |</c>. The header and its
    /// separator have no backticked first cell, so they fall out without being special-cased, and a
    /// table that stops looking like this produces zero rows rather than a quiet pass.
    /// </remarks>
    private static HashSet<string> DocumentedTelemetryFields(IEnumerable<string> lines) => lines
        .Select(line => line.TrimStart())
        .Where(line => line.StartsWith('|'))
        .Select(line => line.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim())
        .Where(cell => cell is not null && cell.StartsWith('`') && cell.EndsWith('`'))
        .Select(cell => cell!.Trim('`'))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every nullable enum on the local line is rejected when it arrives out of range.
    /// </summary>
    /// <remarks>
    /// REFLECTIVE, BECAUSE THE CHECK IT GUARDS IS A HAND-WRITTEN LIST. `TryParseRecord` names each
    /// enum explicitly, so a field added without a matching clause is admitted silently -
    /// `JsonStringEnumConverter` accepts integers, and an unchecked line then survives a prune and
    /// reaches an export. Adding an enum without validating it fails here rather than shipping.
    /// </remarks>
    [Fact]
    public void EveryNullableEnumOnTheLocalLineIsRejectedWhenOutOfRange()
    {
        var enums = typeof(LocalDiagnosticLine)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => Nullable.GetUnderlyingType(property.PropertyType)?.IsEnum == true)
            .ToArray();
        Assert.NotEmpty(enums);

        foreach (var property in enums)
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var line =
                $$"""
                {"timestamp":"2026-08-30T22:00:00+00:00","event":"DictationCompleted","failure":"None","{{name}}":97}
                """;
            Assert.False(
                JsonLineFileLogger.TryParseRecord(line, out _),
                $"A line carrying an undefined '{name}' was accepted. Add it to TryParseRecord.");
        }
    }

    /// <summary>
    /// The local log line copies the telemetry record's fields by hand, so a forgotten line there
    /// drops a field with nothing to show for it. This is the check that turns that into a red test.
    /// </summary>
    /// <remarks>
    /// MEASURED, NOT IMAGINED. Stage, status and changed were added to the telemetry record, the call
    /// site passed all three, the build was clean and 1055 tests passed, and five identical lines
    /// reached the disk carrying none of them. Nothing failed because nothing compared the two shapes.
    /// </remarks>
    [Fact]
    public void LocalDiagnosticLineCarriesEveryTelemetryField()
    {
        var expected = typeof(PrivacySafeDiagnosticRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .ToArray();
        var actual = typeof(LocalDiagnosticLine)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, property => property.PropertyType);

        foreach (var property in expected)
        {
            Assert.True(
                actual.TryGetValue(property.Name, out var type),
                $"LocalDiagnosticLine is missing '{property.Name}'. A field added to the telemetry " +
                "record has to be copied onto the local line too, or it never reaches the disk.");
            Assert.Equal(property.PropertyType, type);
        }
    }

    /// <summary>Every field the record carries is copied, not merely declared.</summary>
    /// <remarks>
    /// The count guard is deliberate. A new field would satisfy the shape check above while
    /// <see cref="LocalDiagnosticLine.From"/> quietly failed to copy it, so adding one has to fail
    /// here until somebody sets it below and confirms it survives.
    /// </remarks>
    [Fact]
    public void EveryPopulatedTelemetryFieldSurvivesOntoTheLocalLine()
    {
        var record = new PrivacySafeDiagnosticRecord(
            DateTimeOffset.UtcNow,
            AppEventCode.DeterministicStageObserved,
            AppFailureCategory.PostProcessing,
            ElapsedMilliseconds: 7,
            Provider: DiagnosticProvider.EgOne,
            ErrorCode: AppErrorCode.ModelPackUnavailable,
            Engine: DiagnosticEngineChoice.Whisper,
            HardwareClass: DiagnosticHardwareClass.NvidiaCuda,
            Stage: DeterministicTextStage.CustomWords,
            StageStatus: DeterministicStageStatus.Completed,
            Changed: true);

        var populated = typeof(PrivacySafeDiagnosticRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .ToArray();
        Assert.Equal(11, populated.Length);

        var line = LocalDiagnosticLine.From(record, Guid.NewGuid());
        foreach (var property in populated)
        {
            var carried = typeof(LocalDiagnosticLine).GetProperty(property.Name);
            Assert.NotNull(carried);
            Assert.Equal(
                property.GetValue(record),
                carried.GetValue(line));
        }
    }

    [Theory]
    [InlineData(DeterministicTextStage.CustomWords, DeterministicStageStatus.Completed, true)]
    [InlineData(DeterministicTextStage.CustomWords, DeterministicStageStatus.Skipped, false)]
    [InlineData(DeterministicTextStage.InverseTextNormalization, DeterministicStageStatus.TimedOut, false)]
    [InlineData(DeterministicTextStage.EmojiRestoration, DeterministicStageStatus.Failed, false)]
    public void DeterministicStageDetailSurvivesIntoTheRecord(
        DeterministicTextStage stage,
        DeterministicStageStatus status,
        bool changed)
    {
        // A SKIPPED STAGE IS THE CASE THIS EXISTS FOR. Custom-word correction disappears when the
        // word list is empty, and a record that dropped the detail could not tell that apart from a
        // correction that ran and found nothing to change.
        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DeterministicStageObserved,
            ElapsedMilliseconds: 4,
            Stage: stage,
            StageStatus: status,
            Changed: changed));

        Assert.Equal(stage, record.Stage);
        Assert.Equal(status, record.StageStatus);
        Assert.Equal(changed, record.Changed);
    }

    [Fact]
    public void StageDetailIsAbsentUnlessTheEntryCarriedIt()
    {
        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DictationCompleted));

        Assert.Null(record.Stage);
        Assert.Null(record.StageStatus);
        Assert.Null(record.Changed);
    }

    [Fact]
    public void UndefinedStageValuesAreDroppedRatherThanWritten()
    {
        // The same admission rule every other enum on this record obeys. A value outside the enum
        // reaches the log as an absence, never as a number nobody can read back.
        var record = PrivacySafeDiagnosticRecord.From(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DeterministicStageObserved,
            Stage: (DeterministicTextStage)97,
            StageStatus: (DeterministicStageStatus)97));

        Assert.Null(record.Stage);
        Assert.Null(record.StageStatus);
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
