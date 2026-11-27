using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class PostHogSentryTelemetryTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string TranscriptSentinel =
        "Reminder to self, please email the whole team about the schedule change for next week and mention that " +
        "the secret phrase is PURPLE ELEPHANT SEVENTEEN before the call.";

    private static readonly Guid KeptBucket6Uuid = Guid.Parse("65C82E99-657B-2334-831D-0F310DA1A14B");
    private static readonly Guid DroppedBucket69Uuid = Guid.Parse("717C5468-9069-4DE5-3D25-BDA064959C35");
    private static readonly Guid EdgeExcludedBucket10Uuid = Guid.Parse("00000000-0000-0000-0000-000000000022");
    private static readonly Guid LastKeptBucket9Uuid = Guid.Parse("00000000-0000-0000-0000-000000000039");
    private static readonly string[] ExpectedSampleType = ["sampleByEvent"];
    private static readonly string[] ExpectedSampledPreviewEvents = ["live_preview.outcome"];
    private static readonly string[] ExpectedLifecycleBatchEvents =
    [
        "app.launched",
        "dictation.completed",
        "dictation.canceled",
        "llm.polish_completed",
        "llm.polish_failed",
        "pipeline.failed",
        "settings.changed",
        "runtime_worker.failed",
    ];

    [Fact]
    public void VolumePolicyBucketsMatchMacIndependentOracleAndTenPercentSampling()
    {
        Assert.Equal(6, TelemetryVolumePolicy.Bucket(KeptBucket6Uuid));
        Assert.Equal(69, TelemetryVolumePolicy.Bucket(DroppedBucket69Uuid));
        Assert.Equal(10, TelemetryVolumePolicy.Bucket(EdgeExcludedBucket10Uuid));
        Assert.Equal(9, TelemetryVolumePolicy.Bucket(LastKeptBucket9Uuid));
        Assert.Equal(1, TelemetryVolumePolicy.Bucket(Guid.Parse("9EB03C05-23F6-784C-01CF-13C075DE87E3")));

        // Sampled happy-path events: kept when bucket < 10, dropped when bucket >= 10.
        var startProps = new Dictionary<string, object?>(StringComparer.Ordinal) { ["press_action"] = "start" };
        Assert.Equal(
            TelemetryVolumeDecision.KeepSampled(10),
            TelemetryVolumePolicy.Decide("hotkey.pressed", startProps, KeptBucket6Uuid));
        Assert.Equal(
            TelemetryVolumeDecision.KeepSampled(10),
            TelemetryVolumePolicy.Decide("hotkey.pressed", startProps, LastKeptBucket9Uuid));
        Assert.Equal(
            TelemetryVolumeDecision.Drop,
            TelemetryVolumePolicy.Decide("hotkey.pressed", startProps, EdgeExcludedBucket10Uuid));
        Assert.Equal(
            TelemetryVolumeDecision.Drop,
            TelemetryVolumePolicy.Decide("hotkey.pressed", startProps, DroppedBucket69Uuid));

        // Signal actions and after_stop_timer are kept at 100% even in dropped buckets.
        var cancelProps = new Dictionary<string, object?>(StringComparer.Ordinal) { ["press_action"] = "cancel" };
        Assert.Equal(
            TelemetryVolumeDecision.Keep,
            TelemetryVolumePolicy.Decide("hotkey.pressed", cancelProps, DroppedBucket69Uuid));

        var afterStopProps = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["press_action"] = "start",
            ["window_timing"] = "after_stop_timer",
        };
        Assert.Equal(
            TelemetryVolumeDecision.Keep,
            TelemetryVolumePolicy.Decide("hotkey.pressed", afterStopProps, DroppedBucket69Uuid));

        // live_preview.outcome: "started" is sampled at 10%, failures are kept at 100%.
        Assert.Null(TelemetryVolumePolicy.Apply(
            "live_preview.outcome",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["outcome"] = "started" },
            DroppedBucket69Uuid));
        var keptSampledPreview = TelemetryVolumePolicy.Apply(
            "live_preview.outcome",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["outcome"] = "started" },
            KeptBucket6Uuid);
        Assert.NotNull(keptSampledPreview);
        Assert.Equal(6, keptSampledPreview!["telemetry_policy_version"]);
        Assert.Equal(0.1, keptSampledPreview["$sample_threshold"]);
        Assert.Equal(ExpectedSampleType, keptSampledPreview["$sample_type"]);
        Assert.Equal(ExpectedSampledPreviewEvents, keptSampledPreview["$sampled_events"]);

        var failedPreview = TelemetryVolumePolicy.Apply(
            "live_preview.outcome",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["outcome"] = "failed" },
            DroppedBucket69Uuid);
        Assert.NotNull(failedPreview);
        Assert.Equal(6, failedPreview!["telemetry_policy_version"]);
        Assert.False(failedPreview.ContainsKey("$sample_threshold"));
    }

    private static string WinUserPath(string user, string suffix, string drive = "C:", string slash = @"\") =>
        $"{drive}{slash}Users{slash}{user}{slash}{suffix}";

    private static string FakeApiKey(string prefix) =>
        prefix + "abcdefghijklmnopqrstuvwxyz123456";

    [Fact]
    public void SanitizerScrubsWindowsAndPosixUserPathsSecretsEmailsAndLongTextWhilePreservingContentFreeKeys()
    {
        Assert.True(TranscriptSentinel.Length > 100);

        // Windows & POSIX user paths (case-insensitive, idempotent).
        Assert.Equal(
            WinUserPath("[REDACTED]", @"AppData\Local\EnviousWispr\models\parakeet.onnx"),
            ObservabilitySanitizer.RedactUserPath(WinUserPath("alice", @"AppData\Local\EnviousWispr\models\parakeet.onnx")));
        Assert.Equal(
            WinUserPath("[REDACTED]", "Documents/notes.txt", drive: "d:", slash: "/"),
            ObservabilitySanitizer.RedactUserPath(WinUserPath("BobSmith", "Documents/notes.txt", drive: "d:", slash: "/")));
        Assert.Equal(
            "/Users/[REDACTED]/Developer/EnviousWispr/Sources/Foo.swift",
            ObservabilitySanitizer.RedactUserPath("/Users/saurabh/Developer/EnviousWispr/Sources/Foo.swift"));

        var once = ObservabilitySanitizer.RedactString("open failed: " + WinUserPath("alice", "model.bin"));
        Assert.Equal("open failed: " + WinUserPath("[REDACTED]", "model.bin"), once);
        Assert.Equal(once, ObservabilitySanitizer.RedactString(once));

        // Long transcript (>100 chars), emails, API keys, and 32+ contiguous hex.
        Assert.Equal("[REDACTED]", ObservabilitySanitizer.RedactString(TranscriptSentinel));
        Assert.Equal("[REDACTED]", ObservabilitySanitizer.RedactString("alice@example.com"));
        Assert.Equal("[REDACTED]", ObservabilitySanitizer.RedactString(FakeApiKey("sk-")));
        Assert.Equal("[REDACTED]", ObservabilitySanitizer.RedactString(FakeApiKey("phc_")));
        Assert.Equal("[REDACTED]", ObservabilitySanitizer.RedactString(FakeApiKey("sntrys_")));
        Assert.Equal("[REDACTED]", ObservabilitySanitizer.RedactString(FakeApiKey("key_")));
        Assert.Equal("[REDACTED]", ObservabilitySanitizer.RedactString("deadbeefdeadbeefdeadbeefdeadbeef0123"));

        // Long HTTPS URL without PII survives, while canonical hyphenated UUID (max 12 contiguous hex) survives.
        var longUrl = "https://models.enviouslabs.co/windows/releases/v1/speech-model-pack/manifest-" + new string('a', 30) + ".json";
        Assert.True(longUrl.Length > 100);
        Assert.Equal(longUrl, ObservabilitySanitizer.RedactString(longUrl));

        var canonicalUuid = "019f93ff-404b-7638-99e6-62cfd4da84f8";
        Assert.Equal(canonicalUuid, ObservabilitySanitizer.RedactString(canonicalUuid));

        // ContentFreeKeys allowlist vs unlisted keys.
        const string traceId = "53cd34f8daca430491da41d26b0b5149";
        const string revision = "aed02740059203c4a87495924f685de3722ae9ce";
        var kernelBanner = "Windows NT 10.0.26100.0 Build 26100 " + new string('x', 95);
        var sanitized = ObservabilitySanitizer.RedactDictionary(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["trace_id"] = traceId,
            ["revision"] = revision,
            ["kernel_version"] = kernelBanner,
            ["unlisted_hex"] = traceId,
            ["unlisted_long"] = kernelBanner,
            ["user_path"] = "Failed loading " + WinUserPath("alice", "secret.gguf"),
        });

        Assert.Equal(traceId, sanitized["trace_id"]);
        Assert.Equal(revision, sanitized["revision"]);
        Assert.Equal(kernelBanner, sanitized["kernel_version"]);
        Assert.Equal("[REDACTED]", sanitized["unlisted_hex"]);
        Assert.Equal("[REDACTED]", sanitized["unlisted_long"]);
        Assert.Equal("Failed loading " + WinUserPath("[REDACTED]", "secret.gguf"), sanitized["user_path"]);
    }

    [Fact]
    public async Task KeyResolverEnforcesPrecedenceSuppressesInUatAndNoOpsWhenMissing()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "posthog-api-key"), "phc_from_file_123456789012345");
            await File.WriteAllTextAsync(Path.Combine(directory, "sentry-dsn"), "https://filekey@o1.ingest.sentry.io/111");

            // Tier 3: file fallback when assembly metadata and env vars are empty.
            var fromFiles = ObservabilityKeyResolver.Resolve(
                assemblyMetadataOverride: new Dictionary<string, string?>(),
                getEnvironmentVariable: _ => null,
                allEnvironmentVariables: [],
                keysDirectory: directory);
            Assert.False(fromFiles.IsTelemetrySuppressed);
            Assert.Equal("phc_from_file_123456789012345", fromFiles.PostHogApiKey);
            Assert.Equal(new Uri("https://us.i.posthog.com"), fromFiles.PostHogHost);
            Assert.Equal("filekey", fromFiles.SentryDsn?.PublicKey);
            Assert.Equal("111", fromFiles.SentryDsn?.ProjectId);
            Assert.Equal(new Uri("https://o1.ingest.sentry.io/api/111/envelope/"), fromFiles.SentryDsn?.EnvelopeUri);

            // Tier 2: env vars override files.
            var envVars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENVIOUSWISPR_POSTHOG_API_KEY"] = "phc_from_env_123456789012345",
                ["ENVIOUSWISPR_POSTHOG_HOST"] = "https://eu.i.posthog.com",
                ["ENVIOUSWISPR_SENTRY_DSN"] = "https://envkey@o2.ingest.sentry.io/222",
            };
            var fromEnv = ObservabilityKeyResolver.Resolve(
                assemblyMetadataOverride: new Dictionary<string, string?>(),
                getEnvironmentVariable: key => envVars.GetValueOrDefault(key),
                allEnvironmentVariables: envVars,
                keysDirectory: directory);
            Assert.Equal("phc_from_env_123456789012345", fromEnv.PostHogApiKey);
            Assert.Equal(new Uri("https://eu.i.posthog.com"), fromEnv.PostHogHost);
            Assert.Equal("envkey", fromEnv.SentryDsn?.PublicKey);
            Assert.Equal("222", fromEnv.SentryDsn?.ProjectId);

            // Tier 1: build-time assembly metadata overrides env vars and files.
            var metadata = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EnviousWisprPostHogApiKey"] = "phc_from_msbuild_1234567890123",
                ["EnviousWisprSentryDsn"] = "https://msbuildkey@o3.ingest.sentry.io/333",
            };
            var fromMetadata = ObservabilityKeyResolver.Resolve(
                assemblyMetadataOverride: metadata,
                getEnvironmentVariable: key => envVars.GetValueOrDefault(key),
                allEnvironmentVariables: envVars,
                keysDirectory: directory);
            Assert.Equal("phc_from_msbuild_1234567890123", fromMetadata.PostHogApiKey);
            Assert.Equal("msbuildkey", fromMetadata.SentryDsn?.PublicKey);

            // Automatic suppression when ENVIOUSWISPR_DISABLE_TELEMETRY=1 or UAT env vars are present.
            var uatVars = new Dictionary<string, string?>(envVars, StringComparer.OrdinalIgnoreCase)
            {
                ["ENVIOUSWISPR_UAT_JOURNEY"] = "public-fixture-v1",
            };
            var suppressedByUat = ObservabilityKeyResolver.Resolve(
                assemblyMetadataOverride: metadata,
                getEnvironmentVariable: key => uatVars.GetValueOrDefault(key),
                allEnvironmentVariables: uatVars,
                keysDirectory: directory);
            Assert.True(suppressedByUat.IsTelemetrySuppressed);
            Assert.False(suppressedByUat.HasPostHog);
            Assert.False(suppressedByUat.HasSentry);
            Assert.Null(suppressedByUat.PostHogApiKey);
            Assert.Null(suppressedByUat.SentryDsn);

            // Legacy ENVIOUSWISPR_TELEMETRY_ENDPOINT transport also respects suppression and UAT flags.
            var legacySuppressedByUat = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENVIOUSWISPR_TELEMETRY_ENDPOINT"] = "https://telemetry.enviouswispr.example/v1/diagnostics",
                ["ENVIOUSWISPR_UAT_JOURNEY"] = "public-fixture-v1",
            };
            Assert.False(TelemetryEndpointPolicy.TryResolveConfiguredEndpoint(
                out _,
                getEnvironmentVariable: key => legacySuppressedByUat.GetValueOrDefault(key),
                allEnvironmentVariables: legacySuppressedByUat));

            var legacyDisabledExplicitly = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENVIOUSWISPR_TELEMETRY_ENDPOINT"] = "http://127.0.0.1:18080/diagnostics",
                ["ENVIOUSWISPR_UAT_ALLOW_LOOPBACK_TELEMETRY"] = "1",
                ["ENVIOUSWISPR_DISABLE_TELEMETRY"] = "1",
            };
            Assert.False(TelemetryEndpointPolicy.TryResolveConfiguredEndpoint(
                out _,
                getEnvironmentVariable: key => legacyDisabledExplicitly.GetValueOrDefault(key),
                allEnvironmentVariables: legacyDisabledExplicitly));

            var legacyLoopbackUatAllowed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENVIOUSWISPR_TELEMETRY_ENDPOINT"] = "http://127.0.0.1:18080/diagnostics",
                ["ENVIOUSWISPR_UAT_ALLOW_LOOPBACK_TELEMETRY"] = "1",
            };
            Assert.True(TelemetryEndpointPolicy.TryResolveConfiguredEndpoint(
                out var resolvedLoopbackUri,
                getEnvironmentVariable: key => legacyLoopbackUatAllowed.GetValueOrDefault(key),
                allEnvironmentVariables: legacyLoopbackUatAllowed));
            Assert.Equal(new Uri("http://127.0.0.1:18080/diagnostics"), resolvedLoopbackUri);

            // Missing keys cleanly no-op.
            var unconfigured = ObservabilityKeyResolver.Resolve(
                assemblyMetadataOverride: new Dictionary<string, string?>(),
                getEnvironmentVariable: _ => null,
                allEnvironmentVariables: [],
                keysDirectory: Path.Combine(directory, "nonexistent"));
            Assert.False(unconfigured.IsTelemetrySuppressed);
            Assert.False(unconfigured.HasPostHog);
            Assert.False(unconfigured.HasSentry);
        });
    }

    [Fact]
    public async Task PostHogClientBatchesStampsWindowsDifferentiationPersistsDistinctIdAndDropsQueueOnOptOut()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var handler = new RecordingHttpMessageHandler();
            var launchId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var identity = new WindowsObservabilityIdentity(
                AppVersion: "0.19.0",
                OsVersion: "10.0.26100.0",
                OsBuild: "26100",
                ReleaseChannel: "win-x64-stable",
                Packaging: "msix",
                Environment: "production",
                ProcessLaunchId: launchId);

            await using var client = new PostHogTelemetryClient(
                apiKey: "phc_test_key_for_windows_unit_test",
                host: new Uri("https://us.i.posthog.com"),
                dataDirectory: directory,
                identity: identity,
                usageMetricsEnabled: true,
                flushAt: 20,
                messageHandler: handler,
                startBackgroundFlushLoop: false);

            Assert.True(client.IsCapturing);
            var distinctId = client.DistinctId;
            Assert.NotNull(distinctId);
            Assert.Equal(distinctId, ObservabilitySanitizer.CanonicalAnonymousPostHogId(distinctId));
            Assert.True(File.Exists(client.TelemetryIdFilePath));

            // Capture an event containing Windows paths, email, and dictated text to verify dual-stage redaction + Windows stamps.
            var captured = client.Capture(
                "dictation.completed",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["result"] = "success",
                    ["asr_backend"] = "parakeet",
                    ["leaked_path"] = WinUserPath("alice", @"Documents\dictation.wav"),
                    ["leaked_email"] = "alice@example.com",
                    ["leaked_transcript"] = TranscriptSentinel,
                    ["app"] = "stale-app-name",
                },
                eventUuid: DroppedBucket69Uuid);

            Assert.True(captured);
            Assert.Equal(1, client.QueuedCount);

            var flushed = await client.FlushAsync();
            Assert.Equal(1, flushed);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("https://us.i.posthog.com/batch/"), request.Uri);
            Assert.DoesNotContain("alice", request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PURPLE ELEPHANT SEVENTEEN", request.Body, StringComparison.Ordinal);

            using var doc = JsonDocument.Parse(request.Body);
            Assert.Equal("phc_test_key_for_windows_unit_test", doc.RootElement.GetProperty("api_key").GetString());
            var batchItem = Assert.Single(doc.RootElement.GetProperty("batch").EnumerateArray());
            Assert.Equal("dictation.completed", batchItem.GetProperty("event").GetString());
            Assert.Equal(distinctId, batchItem.GetProperty("distinct_id").GetString());

            var props = batchItem.GetProperty("properties");
            Assert.Equal("enviouswispr-windows", props.GetProperty("app").GetString());
            Assert.Equal("windows", props.GetProperty("platform").GetString());
            Assert.Equal("Windows", props.GetProperty("$os").GetString());
            Assert.Equal("10.0.26100.0", props.GetProperty("os_version").GetString());
            Assert.Equal("0.19.0", props.GetProperty("app_version").GetString());
            Assert.Equal("win-x64-stable", props.GetProperty("release_channel").GetString());
            Assert.Equal("msix", props.GetProperty("packaging").GetString());
            Assert.Equal("production", props.GetProperty("environment").GetString());
            Assert.Equal(launchId.ToString("D"), props.GetProperty("process_launch_id").GetString());
            Assert.Equal(6, props.GetProperty("telemetry_policy_version").GetInt32());
            Assert.Equal(WinUserPath("[REDACTED]", @"Documents\dictation.wav"), props.GetProperty("leaked_path").GetString());
            Assert.Equal("[REDACTED]", props.GetProperty("leaked_email").GetString());
            Assert.Equal("[REDACTED]", props.GetProperty("leaked_transcript").GetString());

            // Live opt-out immediately drops queued events and prevents further sends.
            Assert.True(client.Capture("settings.changed", new Dictionary<string, object?> { ["final_engine"] = "whisper" }));
            Assert.Equal(1, client.QueuedCount);
            client.SetUsageMetricsEnabled(false);
            Assert.False(client.IsCapturing);
            Assert.Null(client.DistinctId);
            Assert.Equal(0, client.QueuedCount);
            Assert.Equal(0, await client.FlushAsync());
            Assert.Single(handler.Requests);
        });
    }

    [Fact]
    public async Task PostHogClientDoesNotCreateTelemetryIdFileWhenLaunchedWithUsageMetricsOff()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var handler = new RecordingHttpMessageHandler();
            var identity = WindowsObservabilityIdentity.CreateDefault(ReleaseIdentity.Stable, isPackagedMsix: false, appVersion: "0.19.0");
            await using var client = new PostHogTelemetryClient(
                apiKey: "phc_test_key_for_windows_unit_test",
                host: new Uri("https://us.i.posthog.com"),
                dataDirectory: directory,
                identity: identity,
                usageMetricsEnabled: false,
                messageHandler: handler,
                startBackgroundFlushLoop: false);

            Assert.False(client.IsCapturing);
            Assert.Null(client.DistinctId);
            Assert.False(File.Exists(client.TelemetryIdFilePath));
            Assert.False(client.Capture("app.launched"));
            Assert.Equal(0, await client.FlushAsync());
            Assert.Empty(handler.Requests);
        });
    }

    [Fact]
    public async Task SentryClientFormatsEnvelopeAuthAndWindowsReleaseTagsAndScrubsAllSurfaces()
    {
        Assert.True(SentryDsnDescriptor.TryParse("https://publickey123@o99.ingest.sentry.io/456789", out var dsn));
        var handler = new RecordingHttpMessageHandler();
        var launchId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var postHogDistinctId = "019f93ff-404b-7638-99e6-62cfd4da84f8";
        var identity = new WindowsObservabilityIdentity(
            AppVersion: "0.19.0",
            OsVersion: "10.0.26100.0",
            OsBuild: "26100",
            ReleaseChannel: "win-x64-founder",
            Packaging: "unpackaged",
            Environment: "development",
            ProcessLaunchId: launchId,
            ProcessRole: "main");

        using var sentry = new SentryCrashClient(
            dsn: dsn,
            identity: identity,
            sendCrashReportsAtLaunch: true,
            usageMetricsEnabled: true,
            postHogDistinctId: postHogDistinctId,
            messageHandler: handler);

        Assert.True(sentry.IsEnabled);
        Assert.False(sentry.NeedsRestart(storedSendCrashReports: true));
        Assert.True(sentry.NeedsRestart(storedSendCrashReports: false));

        sentry.UpdateRuntimeScope(asrEngine: "parakeet", acceleration: "cuda", gpuVendors: "nvidia");
        sentry.AddBreadcrumb(
            stage: "asr",
            message: "Loaded model from " + WinUserPath("alice", @"AppData\Local\model.onnx"),
            level: "info",
            data: new Dictionary<string, object?>
            {
                ["transcript_leak"] = TranscriptSentinel,
                ["revision"] = "aed02740059203c4a87495924f685de3722ae9ce",
            });

        var exception = new SentryExceptionItem(
            Type: "System.InvalidOperationException",
            Value: $"Failed reading {WinUserPath("alice", "secret.wav")} for alice@example.com with key {FakeApiKey("sk-")}",
            MechanismType: "unhandled_exception",
            Handled: false,
            Frames:
            [
                new SentryStackFrameItem(
                    Function: "EnviousWispr.ASR.ParakeetEngine.Transcribe",
                    Module: "EnviousWispr.ASR",
                    Filename: WinUserPath("alice", @"repo\src\ParakeetEngine.cs"),
                    AbsPath: WinUserPath("alice", @"repo\src\ParakeetEngine.cs"),
                    Lineno: 42,
                    InstructionAddr: "0x001a",
                    Package: WinUserPath("alice", @"repo\bin\EnviousWispr.ASR.dll")),
            ]);

        var eventId = await sentry.CaptureEventAsync(new SentryCaptureEvent(
            Level: "fatal",
            Message: TranscriptSentinel,
            Exceptions: [exception],
            Tags: new Dictionary<string, string>
            {
                ["custom.path"] = WinUserPath("alice", "file.txt"),
            },
            Extra: new Dictionary<string, object?>
            {
                ["dictated_text"] = TranscriptSentinel,
                ["api_key"] = FakeApiKey("phc_"),
            }));

        Assert.NotNull(eventId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://o99.ingest.sentry.io/api/456789/envelope/"), request.Uri);
        Assert.Equal("application/x-sentry-envelope", request.ContentType);
        Assert.Equal(
            "Sentry sentry_version=7, sentry_client=enviouswispr.windows.sentry/0.19.0, sentry_key=publickey123",
            request.SentryAuthHeader);

        // Zero PII, zero username, zero dictated text, zero keys anywhere in the envelope wire payload.
        Assert.DoesNotContain("alice", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PURPLE ELEPHANT SEVENTEEN", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeApiKey("sk-"), request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeApiKey("phc_"), request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("com.enviouswispr.app@", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("server_name", request.Body, StringComparison.Ordinal);

        var lines = request.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        using var envelopeHeader = JsonDocument.Parse(lines[0]);
        Assert.Equal(eventId.Value.ToString("N"), envelopeHeader.RootElement.GetProperty("event_id").GetString());

        using var itemHeader = JsonDocument.Parse(lines[1]);
        Assert.Equal("event", itemHeader.RootElement.GetProperty("type").GetString());
        Assert.Equal("application/json", itemHeader.RootElement.GetProperty("content_type").GetString());

        using var payloadDoc = JsonDocument.Parse(lines[2]);
        var root = payloadDoc.RootElement;
        Assert.Equal("EnviousLabsLLC.EnviousWispr@0.19.0", root.GetProperty("release").GetString());
        Assert.Equal("csharp", root.GetProperty("platform").GetString());
        Assert.Equal("development", root.GetProperty("environment").GetString());
        Assert.Equal("never", root.GetProperty("sdk").GetProperty("settings").GetProperty("infer_ip").GetString());

        var osCtx = root.GetProperty("contexts").GetProperty("os");
        Assert.Equal("Windows", osCtx.GetProperty("name").GetString());
        Assert.Equal("10.0.26100.0", osCtx.GetProperty("version").GetString());
        Assert.Equal("26100", osCtx.GetProperty("build").GetString());

        var tags = root.GetProperty("tags");
        Assert.Equal("windows", tags.GetProperty("app.platform").GetString());
        Assert.Equal("win-x64-founder", tags.GetProperty("app.channel").GetString());
        Assert.Equal("unpackaged", tags.GetProperty("app.packaging").GetString());
        Assert.Equal("main", tags.GetProperty("process.role").GetString());
        Assert.Equal("parakeet", tags.GetProperty("runtime.asr_engine").GetString());
        Assert.Equal("cuda", tags.GetProperty("runtime.acceleration").GetString());
        Assert.Equal("nvidia", tags.GetProperty("gpu.vendors").GetString());
        Assert.Equal("1", tags.GetProperty("crash.context_schema").GetString());
        Assert.Equal(launchId.ToString("D"), tags.GetProperty("process.launch_id").GetString());
        Assert.Equal(postHogDistinctId, tags.GetProperty("analytics.distinct_id").GetString());
        Assert.Equal(WinUserPath("[REDACTED]", "file.txt"), tags.GetProperty("custom.path").GetString());
        Assert.Equal("redacted", tags.GetProperty("exception.reason_status").GetString());
        Assert.Equal("addresses_present", tags.GetProperty("exception.stack_status").GetString());
        Assert.False(root.TryGetProperty("user", out _));

        // When usage metrics are toggled OFF while Sentry is running, analytics.distinct_id is removed and an ephemeral off-period user.id is used.
        sentry.UpdateUsageMetricsState(usageMetricsEnabled: false, postHogDistinctId: null);
        await sentry.CaptureEventAsync(new SentryCaptureEvent(Level: "warning", Message: "test_off_period"));
        Assert.Equal(2, handler.Requests.Count);
        using var offPayloadDoc = JsonDocument.Parse(handler.Requests[1].Body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]);
        var offRoot = offPayloadDoc.RootElement;
        Assert.False(offRoot.GetProperty("tags").TryGetProperty("analytics.distinct_id", out _));
        var offUserId = offRoot.GetProperty("user").GetProperty("id").GetString();
        Assert.NotNull(ObservabilitySanitizer.CanonicalAnonymousPostHogId(offUserId));
    }

    [Fact]
    public async Task SentryClientIsLaunchLockedAndNoOpsWhenSendCrashReportsIsFalseAtLaunch()
    {
        Assert.True(SentryDsnDescriptor.TryParse("https://publickey123@o99.ingest.sentry.io/456789", out var dsn));
        var handler = new RecordingHttpMessageHandler();
        var identity = WindowsObservabilityIdentity.CreateDefault(ReleaseIdentity.Stable, isPackagedMsix: true, appVersion: "0.19.0");

        using var sentry = new SentryCrashClient(
            dsn: dsn,
            identity: identity,
            sendCrashReportsAtLaunch: false,
            usageMetricsEnabled: true,
            postHogDistinctId: Guid.NewGuid().ToString("D"),
            messageHandler: handler);

        Assert.False(sentry.IsEnabled);
        Assert.False(sentry.LaunchedCrashReports);
        Assert.True(sentry.NeedsRestart(storedSendCrashReports: true));
        Assert.Null(await sentry.CaptureExceptionAsync(new InvalidOperationException("boom")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WindowsObservabilityServiceWiresLifecyclePipelineSettingsAndWorkerFailureEvents()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            Assert.True(SentryDsnDescriptor.TryParse("https://publickey123@o99.ingest.sentry.io/456789", out var dsn));
            var postHogHandler = new RecordingHttpMessageHandler();
            var sentryHandler = new RecordingHttpMessageHandler();
            var identity = WindowsObservabilityIdentity.CreateDefault(ReleaseIdentity.Stable, isPackagedMsix: true, appVersion: "0.19.0");

            var postHog = new PostHogTelemetryClient(
                apiKey: "phc_test_key_for_windows_unit_test",
                host: new Uri("https://us.i.posthog.com"),
                dataDirectory: directory,
                identity: identity,
                usageMetricsEnabled: true,
                messageHandler: postHogHandler,
                startBackgroundFlushLoop: false);
            var sentry = new SentryCrashClient(
                dsn: dsn,
                identity: identity,
                sendCrashReportsAtLaunch: true,
                usageMetricsEnabled: true,
                postHogDistinctId: postHog.DistinctId,
                messageHandler: sentryHandler);

            await using var service = new WindowsObservabilityService(postHog, sentry, identity);

            var hardware = new HardwareSnapshot(
                HardwareProbeStatus.Complete,
                ProcessorArchitectureKind.X64,
                ProcessorVendor.Amd,
                PhysicalCoreCount: 8,
                LogicalProcessorCount: 16,
                TotalPhysicalMemoryBytes: 32UL * 1024 * 1024 * 1024,
                GraphicsAdapters: [new GraphicsAdapterCapability(GraphicsVendor.Nvidia, IsActive: true, IsDirectMlCandidate: true)],
                IsDirectMlRuntimeAvailable: true,
                new CudaDriverCapability(IsDriverAvailable: true, DeviceCount: 1, DriverVersion: 12_040),
                IsOnnxRuntimeCudaDependencySetAvailable: true,
                IsWhisperCudaDependencySetAvailable: true);

            Assert.True(service.RecordAppLaunched(hardware, isFreshInstall: true, aiCapable: true, aiEnabled: false, speechModelsInstalled: "parakeet"));
            service.ObserveDiagnosticRecord(PrivacySafeDiagnosticRecord.From(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationCompleted,
                ElapsedMilliseconds: 420,
                Engine: DiagnosticEngineChoice.Parakeet,
                RecognitionLanguage: DiagnosticRecognitionLanguage.English)));
            service.ObserveDiagnosticRecord(PrivacySafeDiagnosticRecord.From(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.DictationCancelled,
                ElapsedMilliseconds: 110,
                Engine: DiagnosticEngineChoice.Parakeet)));
            service.ObserveDiagnosticRecord(PrivacySafeDiagnosticRecord.From(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.PolishCompleted,
                ElapsedMilliseconds: 180,
                Provider: DiagnosticProvider.Ollama)));
            service.ObserveDiagnosticRecord(PrivacySafeDiagnosticRecord.From(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.PolishDegraded,
                AppFailureCategory.LocalPolish,
                ElapsedMilliseconds: 95,
                Provider: DiagnosticProvider.Ollama,
                ErrorCode: AppErrorCode.PolishProviderUnavailable)));
            service.ObserveDiagnosticRecord(PrivacySafeDiagnosticRecord.From(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppEventCode.TextDeliveryFailed,
                AppFailureCategory.TextDelivery,
                ErrorCode: AppErrorCode.DeliveryFaulted)));

            var updatedSettings = AppSettings.Default with
            {
                Preferences = UserPreferences.Default with { LivePreviewEnabled = true },
            };
            service.ApplySettings(updatedSettings, emitSettingsChangedEvent: true);

            var workerSentryEventId = await service.RecordRuntimeWorkerFailedAsync("parakeet", exitCode: -1073741819, errorCode: AppErrorCode.RuntimeWorkerFailed);
            Assert.NotNull(workerSentryEventId);
            var interruptedEventId = await service.CaptureInterruptedDictationNoteAsync(previousRunWasDictating: true);
            Assert.NotNull(interruptedEventId);

            // Synchronous blocking crash capture completes before returning and never sends free-form exception.Message.
            var blockingException = new InvalidOperationException("short dictated secret phrase");
            var unhandledEventId = service.CaptureUnhandledExceptionBlocking(
                blockingException,
                mechanismType: "winui_unhandled_exception",
                handled: false,
                level: "fatal");
            Assert.NotNull(unhandledEventId);

            var flushedCount = await postHog.FlushAsync();
            Assert.Equal(8, flushedCount);
            Assert.Equal(3, sentryHandler.Requests.Count);
            Assert.DoesNotContain(
                "short dictated secret phrase",
                sentryHandler.Requests[2].Body,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                $"System.InvalidOperationException#0x{blockingException.HResult:X8}",
                sentryHandler.Requests[2].Body,
                StringComparison.Ordinal);

            using var postHogDoc = JsonDocument.Parse(postHogHandler.Requests[0].Body);
            var eventNames = postHogDoc.RootElement.GetProperty("batch")
                .EnumerateArray()
                .Select(item => item.GetProperty("event").GetString() ?? string.Empty)
                .ToArray();
            Assert.Equal(ExpectedLifecycleBatchEvents, eventNames);
        });
    }

    [Fact]
    public async Task SettingsStoreAndPortableProfileMigrateCleanlyAndPreserveExplicitTelemetryChoices()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(settingsPath);

            // Fresh install (missing file) defaults both ShareAnonymousTelemetry and SendCrashReports to true.
            var fresh = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Missing, fresh.Status);
            Assert.Equal(AppSettings.CurrentSchemaVersion, fresh.Settings.SchemaVersion);
            Assert.True(fresh.Settings.Observability?.ShareAnonymousTelemetry);
            Assert.True(fresh.Settings.Observability?.SendCrashReports);

            // Migrate v20 settings with explicit ShareAnonymousTelemetry = false (and no sendCrashReports property)
            // -> preserves the user's remote-sharing opt-out across both ShareAnonymousTelemetry and SendCrashReports.
            var v20OptedOut = JsonSettingsStoreTests.CreatePopulatedSettings() with
            {
                SchemaVersion = 20,
                Observability = new ObservabilityPreferences(
                    LocalDiagnosticsEnabled: true,
                    DiagnosticRetentionDays: 14,
                    ShareAnonymousTelemetry: false),
            };
            var root = JsonNode.Parse(JsonSerializer.Serialize(v20OptedOut, JsonSettingsStore.SerializerOptions))!.AsObject();
            root["observability"]!.AsObject().Remove("sendCrashReports");
            await File.WriteAllTextAsync(settingsPath, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var migrated = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Migrated, migrated.Status);
            Assert.Equal(20, migrated.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, migrated.Settings.SchemaVersion);
            Assert.False(migrated.Settings.Observability?.ShareAnonymousTelemetry);
            Assert.False(migrated.Settings.Observability?.SendCrashReports);

            // Migrate v20 settings with ShareAnonymousTelemetry = true -> both switches remain true.
            var v20OptedIn = v20OptedOut with
            {
                Observability = new ObservabilityPreferences(
                    LocalDiagnosticsEnabled: true,
                    DiagnosticRetentionDays: 14,
                    ShareAnonymousTelemetry: true),
            };
            var optedInRoot = JsonNode.Parse(JsonSerializer.Serialize(v20OptedIn, JsonSettingsStore.SerializerOptions))!.AsObject();
            optedInRoot["observability"]!.AsObject().Remove("sendCrashReports");
            await File.WriteAllTextAsync(settingsPath, optedInRoot.ToJsonString(JsonSettingsStore.SerializerOptions));

            var migratedOptedIn = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Migrated, migratedOptedIn.Status);
            Assert.True(migratedOptedIn.Settings.Observability?.ShareAnonymousTelemetry);
            Assert.True(migratedOptedIn.Settings.Observability?.SendCrashReports);

            // Migrate v14 PortableProfile -> current schema.
            var profilePath = Path.Combine(directory, "profile.enviouswispr.json");
            var v14Profile = JsonSettingsStoreTests.CreatePopulatedSettings().ToPortableProfile() with { SchemaVersion = 14 };
            await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(v14Profile, JsonSettingsStore.SerializerOptions));

            var profileResult = await new JsonPortableProfileService().ImportAsync(profilePath);
            Assert.Equal(PortableProfileImportStatus.Imported, profileResult.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, profileResult.Profile?.SchemaVersion);
        });
    }

    [Fact]
    public void DiagnosticsPageExposesUsageMetricsAndCrashReportsTogglesAndOnboardingHasZeroTelemetryToggles()
    {
        var root = FindRepositoryRoot();
        var document = AppXamlSources.LoadMainWindowAndPagesDocument(root);

        var diagnosticsSection = FindNamed(document, "DiagnosticsSection");
        var usageToggle = FindNamed(diagnosticsSection, "ShareUsageMetricsToggle");
        var usageLabel = FindNamed(diagnosticsSection, "ShareUsageMetricsToggleLabel");
        var crashToggle = FindNamed(diagnosticsSection, "SendCrashReportsToggle");
        var crashLabel = FindNamed(diagnosticsSection, "SendCrashReportsToggleLabel");

        Assert.Equal("Share usage metrics", (string?)usageLabel.Attribute("Text"));
        Assert.Equal("Send crash reports", (string?)crashLabel.Attribute("Text"));
        Assert.Contains(
            diagnosticsSection.Descendants().Where(e => e.Name.LocalName == "TextBlock"),
            textBlock => ((string?)textBlock.Attribute("Text"))?.Contains("Takes effect when EnviousWispr restarts", StringComparison.Ordinal) == true);

        // OnboardingView must NOT contain any telemetry or crash-report toggle,
        // and must accurately describe default-ON opt-out observability.
        var onboardingView = FindNamed(document, "OnboardingView");
        Assert.DoesNotContain(onboardingView.Descendants(), e => e.Name.LocalName == "ToggleSwitch");
        var onboardingText = string.Join(
            " ",
            onboardingView.Descendants()
                .Select(e => (string?)e.Attribute("Text") ?? (string?)e.Attribute("Content"))
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        Assert.Contains("Audio stays on this PC; dictated text stays local unless you turn on cloud polish.", onboardingText, StringComparison.Ordinal);
        Assert.Contains("on by default", onboardingText, StringComparison.Ordinal);
        Assert.Contains("can be turned off anytime in Diagnostics", onboardingText, StringComparison.Ordinal);
        Assert.DoesNotContain("PostHog", onboardingText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sentry", onboardingText, StringComparison.OrdinalIgnoreCase);
    }

    private static XElement FindNamed(XContainer container, string name) =>
        container.Descendants().Single(element =>
            string.Equals(
                (string?)element.Attribute(XName.Get("Name", XamlNamespace)),
                name,
                StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "EnviousWispr.Windows.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current.FullName;
    }

    private sealed record CapturedHttpRequest(
        HttpMethod Method,
        Uri? Uri,
        string? ContentType,
        string? SentryAuthHeader,
        string Body);

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Lock _lock = new();

        public List<CapturedHttpRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = request.Content?.Headers.ContentType?.MediaType;
            var sentryAuth = request.Headers.TryGetValues("X-Sentry-Auth", out var values)
                ? values.FirstOrDefault()
                : null;

            lock (_lock)
            {
                Requests.Add(new CapturedHttpRequest(
                    request.Method,
                    request.RequestUri,
                    contentType,
                    sentryAuth,
                    body));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
