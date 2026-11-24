using System.Net;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Feedback;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Feedback;

namespace EnviousWispr.Architecture.Tests;

public sealed class FeedbackTests : IDisposable
{
    private readonly string _tempDirectory;

    public FeedbackTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"enviouswispr-feedback-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    [Fact]
    public void FeedbackSubmissionEnforcesBoundsAndEmailValidation()
    {
        Assert.False(FeedbackSubmission.TryCreate(
            FeedbackKind.Bug,
            "   \r\n  ",
            null,
            includeDiagnostics: true,
            diagnosticsSnapshot: null,
            out _,
            out var emptyError));
        Assert.False(string.IsNullOrWhiteSpace(emptyError));

        var tooLongMessage = new string('a', FeedbackSubmission.MaxMessageLength + 1);
        Assert.False(FeedbackSubmission.TryCreate(
            FeedbackKind.Feature,
            tooLongMessage,
            null,
            includeDiagnostics: false,
            diagnosticsSnapshot: null,
            out _,
            out var tooLongError));
        Assert.Contains("4,000", tooLongError, StringComparison.Ordinal);

        var maxValidMessage = "  " + new string('b', FeedbackSubmission.MaxMessageLength) + "  ";
        Assert.True(FeedbackSubmission.TryCreate(
            FeedbackKind.General,
            maxValidMessage,
            "  alice@example.com  ",
            includeDiagnostics: false,
            diagnosticsSnapshot: CreateSampleSnapshot(),
            out var validSubmission,
            out var validError));
        Assert.Null(validError);
        Assert.NotNull(validSubmission);
        Assert.Equal(FeedbackSubmission.MaxMessageLength, validSubmission!.Message.Length);
        Assert.Equal("alice@example.com", validSubmission.ContactEmail);
        Assert.Null(validSubmission.DiagnosticsSnapshot);

        Assert.False(FeedbackSubmission.TryCreate(
            FeedbackKind.Bug,
            "Valid message",
            "not-an-email",
            includeDiagnostics: false,
            diagnosticsSnapshot: null,
            out _,
            out _));

        var overLengthEmail = new string('u', 245) + "@example.com";
        Assert.True(overLengthEmail.Length > FeedbackSubmission.MaxContactEmailLength);
        Assert.False(FeedbackSubmission.TryCreate(
            FeedbackKind.Bug,
            "Valid message",
            overLengthEmail,
            includeDiagnostics: false,
            diagnosticsSnapshot: null,
            out _,
            out _));
    }

    [Fact]
    public void FeedbackDiagnosticsSnapshotIsPrivacySafeAndFormatsPreview()
    {
        var sampleUserPath = WinUserPath("alice", "secret.txt");
        var snapshot = new FeedbackDiagnosticsSnapshot(
            appVersion: "0.19.0",
            osVersion: "Windows 10.0.26100.0",
            finalAsrEngine: "Parakeet",
            polishProvider: "EgOne",
            livePreviewEnabled: true,
            livePreviewEngine: "Parakeet",
            recordingMode: "PushToTalk",
            autoStopEnabled: true,
            hardwareClass: "NvidiaCuda",
            recentEventCodes:
            [
                "ApplicationStarting",
                "DictationCompleted",
                sampleUserPath,
                "bad event with spaces",
            ]);

        Assert.Equal(["ApplicationStarting", "DictationCompleted"], snapshot.RecentEventCodes);

        var preview = snapshot.FormatPreviewText();
        Assert.Contains("App Version: 0.19.0", preview, StringComparison.Ordinal);
        Assert.Contains("OS Version: Windows 10.0.26100.0", preview, StringComparison.Ordinal);
        Assert.Contains("Final ASR Engine: Parakeet", preview, StringComparison.Ordinal);
        Assert.Contains("Polish Provider: EgOne", preview, StringComparison.Ordinal);
        Assert.Contains("Live Preview: On (Parakeet)", preview, StringComparison.Ordinal);
        Assert.Contains("Recording Mode: PushToTalk", preview, StringComparison.Ordinal);
        Assert.Contains("Auto-Stop: On", preview, StringComparison.Ordinal);
        Assert.Contains("Hardware Class: NvidiaCuda", preview, StringComparison.Ordinal);
        Assert.Contains("Recent Event Codes: ApplicationStarting, DictationCompleted", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", preview, StringComparison.OrdinalIgnoreCase);

        var contaminated = new FeedbackDiagnosticsSnapshot(
            appVersion: WinUserPath("alice", "app.exe"),
            osVersion: "alice@example.com",
            finalAsrEngine: FakeApiKey("sk-"),
            polishProvider: "None",
            livePreviewEnabled: false,
            livePreviewEngine: "Parakeet",
            recordingMode: "Toggle",
            autoStopEnabled: false,
            hardwareClass: "CpuOnly");
        Assert.Equal("[REDACTED]", contaminated.AppVersion);
        Assert.Equal("[REDACTED]", contaminated.OsVersion);
        Assert.Equal("[REDACTED]", contaminated.FinalAsrEngine);
    }

    [Fact]
    public void FeedbackDraftStoreRoundTripsAndClearsDraft()
    {
        var store = new FeedbackDraftStore(_tempDirectory);
        Assert.Null(store.Load());

        var draft = new FeedbackDraft(
            Kind: FeedbackKind.Feature,
            Message: "Please add dark mode contrast tuning.",
            ContactEmail: "founder@example.com",
            IncludeDiagnostics: false);

        store.Save(draft);
        Assert.True(File.Exists(store.FilePath));

        var loaded = store.Load();
        Assert.Equal(draft, loaded);

        store.Clear();
        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.Load());

        File.WriteAllText(store.FilePath, "{corrupt json");
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task FeedbackOutboxCapsAtTwentyItemsDeduplicatesAndPersistsAcrossInstances()
    {
        var firstId = Guid.NewGuid();
        var first = FeedbackSubmission.Create(
            FeedbackKind.Bug,
            "First report",
            submissionId: firstId);

        using (var outbox = new FeedbackOutbox(_tempDirectory))
        {
            Assert.True(await outbox.EnqueueAsync(first));
            // Duplicate SubmissionId should succeed without adding a second copy.
            Assert.True(await outbox.EnqueueAsync(first));
            Assert.Single(await outbox.PeekAllAsync());

            for (var i = 1; i < FeedbackOutbox.MaxItems; i++)
            {
                var next = FeedbackSubmission.Create(FeedbackKind.General, $"Report #{i}");
                Assert.True(await outbox.EnqueueAsync(next));
            }

            var queued = await outbox.PeekAllAsync();
            Assert.Equal(FeedbackOutbox.MaxItems, queued.Count);

            var overflow = FeedbackSubmission.Create(FeedbackKind.Feature, "Item 21");
            Assert.False(await outbox.EnqueueAsync(overflow));
            Assert.Equal(FeedbackOutbox.MaxItems, (await outbox.PeekAllAsync()).Count);
        }

        using (var reloadedOutbox = new FeedbackOutbox(_tempDirectory))
        {
            Assert.Equal(FeedbackOutbox.MaxItems, (await reloadedOutbox.PeekAllAsync()).Count);
            Assert.True(await reloadedOutbox.RemoveAsync(firstId));
            Assert.Equal(FeedbackOutbox.MaxItems - 1, (await reloadedOutbox.PeekAllAsync()).Count);
            var overflow = FeedbackSubmission.Create(FeedbackKind.Feature, "Item 21");
            Assert.True(await reloadedOutbox.EnqueueAsync(overflow));
            Assert.Equal(FeedbackOutbox.MaxItems, (await reloadedOutbox.PeekAllAsync()).Count);
        }
    }

    [Fact]
    public async Task FeedbackCoordinatorQueuesOfflineOn503AndFlushesOn200()
    {
        var draftStore = new FeedbackDraftStore(_tempDirectory);
        using var outbox = new FeedbackOutbox(_tempDirectory);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.True(SentryDsnDescriptor.TryParse("https://publickey123@o12345.ingest.us.sentry.io/67890", out var dsn));
        using var sender = new SentryFeedbackSender(dsn, CreateTestIdentity(), handler);
        using var coordinator = new FeedbackCoordinator(draftStore, outbox, sender, CreateTestIdentity());

        draftStore.Save(new FeedbackDraft(FeedbackKind.Bug, "Offline bug report", null, true));
        var submission = FeedbackSubmission.Create(
            FeedbackKind.Bug,
            "Offline bug report",
            includeDiagnostics: true,
            diagnosticsSnapshot: CreateSampleSnapshot());

        var submitResult = await coordinator.SubmitAsync(submission);
        Assert.Equal(FeedbackSendOutcome.QueuedOffline, submitResult.Outcome);
        Assert.Null(draftStore.Load());
        Assert.Single(await outbox.PeekAllAsync());

        // Offline 503 during flush preserves the queued item.
        var failedFlush = await coordinator.FlushOutboxAsync();
        Assert.Equal(1, failedFlush.Attempted);
        Assert.Equal(0, failedFlush.Sent);
        Assert.Equal(1, failedFlush.Remaining);
        Assert.Single(await outbox.PeekAllAsync());

        // Online 200 clears the outbox.
        handler.ResponseFactory = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        var onlineFlush = await coordinator.FlushOutboxAsync();
        Assert.Equal(1, onlineFlush.Attempted);
        Assert.Equal(1, onlineFlush.Sent);
        Assert.Equal(0, onlineFlush.Remaining);
        Assert.Empty(await outbox.PeekAllAsync());
    }

    [Fact]
    public async Task SentryFeedbackSenderMalformedOrMissingDsnReturnsUnavailableWithoutThrowing()
    {
        Assert.False(SentryDsnDescriptor.TryParse("http://insecure@example.com/123", out _));
        Assert.False(SentryDsnDescriptor.TryParse("not-a-valid-dsn", out _));

        using var sender = new SentryFeedbackSender(
            dsn: null,
            CreateTestIdentity(),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var outbox = new FeedbackOutbox(_tempDirectory);
        var draftStore = new FeedbackDraftStore(_tempDirectory);
        using var coordinator = new FeedbackCoordinator(draftStore, outbox, sender, CreateTestIdentity());

        draftStore.Save(new FeedbackDraft(FeedbackKind.General, "Keep my draft", null, true));
        var submission = FeedbackSubmission.Create(FeedbackKind.General, "Keep my draft");

        var result = await coordinator.SubmitAsync(submission);
        Assert.Equal(FeedbackSendOutcome.Unavailable, result.Outcome);
        // Draft stays preserved and nothing enters the outbox when DSN is unconfigured.
        Assert.NotNull(draftStore.Load());
        Assert.Empty(await outbox.PeekAllAsync());
    }

    [Fact]
    public async Task SentryFeedbackSenderHandlesNetworkExceptionAndBadRequestCleanly()
    {
        Assert.True(SentryDsnDescriptor.TryParse("https://pub123@o1.ingest.us.sentry.io/999", out var dsn));
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        using var sender = new SentryFeedbackSender(dsn, CreateTestIdentity(), handler);

        var submission = FeedbackSubmission.Create(FeedbackKind.Bug, "Test message");
        var networkResult = await sender.SendAsync(submission);
        Assert.Equal(FeedbackTransportStatus.TransientFailure, networkResult.Status);

        handler.ResponseFactory = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var rejectedResult = await sender.SendAsync(submission);
        Assert.Equal(FeedbackTransportStatus.Rejected, rejectedResult.Status);
        Assert.Equal(400, rejectedResult.HttpStatusCode);
    }

    [Fact]
    public async Task SentryFeedbackSenderBuildsSanitizedFeedbackEnvelopeAndPreservesExplicitContactEmail()
    {
        string? capturedBody = null;
        string? capturedAuthHeader = null;
        Uri? capturedUri = null;
        string? capturedContentType = null;

        var handler = StubHttpMessageHandler.FromAsync(async request =>
        {
            capturedUri = request.RequestUri;
            capturedAuthHeader = request.Headers.TryGetValues("X-Sentry-Auth", out var authValues)
                ? string.Join(",", authValues)
                : null;
            capturedContentType = request.Content?.Headers.ContentType?.MediaType;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.True(SentryDsnDescriptor.TryParse("https://pub999@o111.ingest.us.sentry.io/4242", out var dsn));
        var identity = CreateTestIdentity(environment: WinUserPath("bob", "env"));
        using var sender = new SentryFeedbackSender(dsn, identity, handler);

        var fakeKey = FakeApiKey("sk-");
        var submission = FeedbackSubmission.Create(
            FeedbackKind.Bug,
            $"Crash when opening {WinUserPath("bob", "Documents\\file.txt")}; leaked email other@example.com and key {fakeKey}",
            contactEmail: "reply-me@example.com",
            includeDiagnostics: true,
            diagnosticsSnapshot: CreateSampleSnapshot());

        var result = await sender.SendAsync(submission);
        Assert.Equal(FeedbackTransportStatus.Sent, result.Status);
        Assert.Equal(new Uri("https://o111.ingest.us.sentry.io/api/4242/envelope/"), capturedUri);
        Assert.Equal("application/x-sentry-envelope", capturedContentType);
        Assert.Contains("sentry_key=pub999", capturedAuthHeader, StringComparison.Ordinal);
        Assert.NotNull(capturedBody);

        var lines = capturedBody!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);

        using var envelopeHeader = JsonDocument.Parse(lines[0]);
        Assert.Equal(submission.SubmissionId.ToString("N"), envelopeHeader.RootElement.GetProperty("event_id").GetString());
        Assert.Equal(dsn!.RawDsn, envelopeHeader.RootElement.GetProperty("dsn").GetString());

        using var feedbackItemHeader = JsonDocument.Parse(lines[1]);
        Assert.Equal("feedback", feedbackItemHeader.RootElement.GetProperty("type").GetString());
        Assert.Equal("application/json", feedbackItemHeader.RootElement.GetProperty("content_type").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(lines[2]), feedbackItemHeader.RootElement.GetProperty("length").GetInt32());

        using var payload = JsonDocument.Parse(lines[2]);
        var root = payload.RootElement;
        Assert.Equal("feedback", root.GetProperty("type").GetString());
        Assert.Equal("never", root.GetProperty("sdk").GetProperty("settings").GetProperty("infer_ip").GetString());
        Assert.DoesNotContain("bob", root.GetProperty("environment").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(root.TryGetProperty("server_name", out _));
        Assert.False(root.TryGetProperty("user", out _));

        var feedbackContext = root.GetProperty("contexts").GetProperty("feedback");
        var scrubbedMessage = feedbackContext.GetProperty("message").GetString()!;
        Assert.DoesNotContain("bob", scrubbedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other@example.com", scrubbedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fakeKey, scrubbedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", scrubbedMessage, StringComparison.Ordinal);
        Assert.Equal("reply-me@example.com", feedbackContext.GetProperty("contact_email").GetString());

        Assert.Equal("bug", root.GetProperty("tags").GetProperty("feedback.kind").GetString());
        Assert.Equal("true", root.GetProperty("tags").GetProperty("diagnostics.included").GetString());

        using var attachmentHeader = JsonDocument.Parse(lines[3]);
        Assert.Equal("attachment", attachmentHeader.RootElement.GetProperty("type").GetString());
        Assert.Equal(SentryFeedbackSender.DiagnosticsAttachmentFilename, attachmentHeader.RootElement.GetProperty("filename").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(lines[4]), attachmentHeader.RootElement.GetProperty("length").GetInt32());

        // Without contact_email or diagnostics, contact_email, attachment, and operational metadata must be omitted.
        var minimal = FeedbackSubmission.Create(
            FeedbackKind.General,
            "Simple general note",
            contactEmail: null,
            includeDiagnostics: false);
        var minimalEnvelope = SentryFeedbackSender.BuildEnvelope(dsn, CreateTestIdentity(), minimal, DateTimeOffset.UtcNow);
        var minimalLines = minimalEnvelope.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, minimalLines.Length);
        using var minimalPayload = JsonDocument.Parse(minimalLines[2]);
        var minimalRoot = minimalPayload.RootElement;
        var minimalFeedback = minimalRoot.GetProperty("contexts").GetProperty("feedback");
        Assert.False(minimalFeedback.TryGetProperty("contact_email", out _));
        Assert.False(minimalRoot.GetProperty("contexts").TryGetProperty("diagnostics", out _));
        Assert.False(minimalRoot.GetProperty("contexts").TryGetProperty("app", out _));
        Assert.False(minimalRoot.GetProperty("contexts").TryGetProperty("os", out _));
        Assert.False(minimalRoot.TryGetProperty("release", out _));
        Assert.False(minimalRoot.TryGetProperty("environment", out _));
        var minimalTags = minimalRoot.GetProperty("tags");
        Assert.Equal("false", minimalTags.GetProperty("diagnostics.included").GetString());
        Assert.False(minimalTags.TryGetProperty("app.channel", out _));
        Assert.False(minimalTags.TryGetProperty("app.packaging", out _));
    }

    [Fact]
    public void FeedbackCoordinatorBuildsSnapshotFromSettingsHardwareAndRecentEvents()
    {
        var draftStore = new FeedbackDraftStore(_tempDirectory);
        using var outbox = new FeedbackOutbox(_tempDirectory);
        using var sender = new SentryFeedbackSender(dsn: null, CreateTestIdentity());
        using var coordinator = new FeedbackCoordinator(draftStore, outbox, sender, CreateTestIdentity());

        coordinator.ObserveEvent(AppEventCode.ApplicationStarting);
        coordinator.ObserveEvent(AppEventCode.DictationCompleted);

        var settings = AppSettings.Default with
        {
            Preferences = UserPreferences.Default with
            {
                LivePreviewEnabled = true,
                LivePreviewEngine = LivePreviewEngineChoice.Whisper,
                Polish = new PolishPreferences(PolishProvider.EgOne, null),
                Dictation = DictationPreferences.Default with
                {
                    FinalEngine = FinalAsrEngine.Whisper,
                    RecordingMode = DictationRecordingMode.Toggle,
                    AutoStopEnabled = true,
                },
            },
        };

        var cudaHardware = new HardwareSnapshot(
            Status: HardwareProbeStatus.Complete,
            Architecture: ProcessorArchitectureKind.X64,
            ProcessorVendor: ProcessorVendor.Amd,
            PhysicalCoreCount: 8,
            LogicalProcessorCount: 16,
            TotalPhysicalMemoryBytes: 32UL * 1024 * 1024 * 1024,
            GraphicsAdapters: [new GraphicsAdapterCapability(GraphicsVendor.Nvidia, IsActive: true, IsDirectMlCandidate: true)],
            IsDirectMlRuntimeAvailable: true,
            Cuda: new CudaDriverCapability(IsDriverAvailable: true, DeviceCount: 1, DriverVersion: 12040),
            IsOnnxRuntimeCudaDependencySetAvailable: true,
            IsWhisperCudaDependencySetAvailable: true);

        var snapshot = coordinator.BuildDiagnosticsSnapshot(settings, cudaHardware);
        Assert.Equal("0.19.0", snapshot.AppVersion);
        Assert.Equal("Whisper", snapshot.FinalAsrEngine);
        Assert.Equal("EgOne", snapshot.PolishProvider);
        Assert.True(snapshot.LivePreviewEnabled);
        Assert.Equal("Whisper", snapshot.LivePreviewEngine);
        Assert.Equal("Toggle", snapshot.RecordingMode);
        Assert.True(snapshot.AutoStopEnabled);
        Assert.Equal("NvidiaCuda", snapshot.HardwareClass);
        Assert.Equal(["ApplicationStarting", "DictationCompleted"], snapshot.RecentEventCodes);
    }

    [Fact]
    public async Task FeedbackOutboxPreservesCorruptFileAndThrowsOnPeekInsteadOfReportingEmpty()
    {
        using var outbox = new FeedbackOutbox(_tempDirectory);
        const string corruptPayload = "{ \"schemaVersion\": 2, \"entries\": [";
        await File.WriteAllTextAsync(outbox.FilePath, corruptPayload);

        await Assert.ThrowsAsync<InvalidDataException>(() => outbox.PeekAllAsync());
        var candidate = FeedbackSubmission.Create(FeedbackKind.Bug, "New report while corrupt");
        Assert.False(await outbox.EnqueueAsync(candidate));
        Assert.False(await outbox.RemoveAsync(candidate.SubmissionId));

        // Corrupt outbox bytes must remain untouched so existing reports are not discarded.
        Assert.Equal(corruptPayload, await File.ReadAllTextAsync(outbox.FilePath));

        using var sender = new SentryFeedbackSender(dsn: null, CreateTestIdentity());
        using var coordinator = new FeedbackCoordinator(
            new FeedbackDraftStore(_tempDirectory),
            outbox,
            sender,
            CreateTestIdentity());
        var flush = await coordinator.FlushOutboxAsync();
        Assert.True(flush.OutboxUnreadable);
        Assert.True(flush.StoppedDueToUnavailable);
    }

    [Fact]
    public async Task FeedbackCoordinatorPreservesRejectedQueuedReportsWithoutRetrying()
    {
        var draftStore = new FeedbackDraftStore(_tempDirectory);
        using var outbox = new FeedbackOutbox(_tempDirectory);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.True(SentryDsnDescriptor.TryParse("https://pub123@o1.ingest.us.sentry.io/999", out var dsn));
        using var sender = new SentryFeedbackSender(dsn, CreateTestIdentity(), handler);
        using var coordinator = new FeedbackCoordinator(
            draftStore,
            outbox,
            sender,
            CreateTestIdentity(),
            initialRetryDelay: TimeSpan.FromMinutes(10));

        var submission = FeedbackSubmission.Create(FeedbackKind.Bug, "Queued then rejected");
        var queued = await coordinator.SubmitAsync(submission);
        Assert.Equal(FeedbackSendOutcome.QueuedOffline, queued.Outcome);

        handler.ResponseFactory = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));
        var flush = await coordinator.FlushOutboxAsync();
        Assert.Equal(1, flush.Attempted);
        Assert.Equal(0, flush.Sent);
        Assert.Equal(0, flush.Remaining);

        // Pending list is empty (stops retrying), while the rejected entry stays preserved on disk.
        Assert.Empty(await outbox.PeekAllAsync());
        var entries = await outbox.PeekEntriesAsync();
        var preserved = Assert.Single(entries);
        Assert.Equal(submission.SubmissionId, preserved.Submission.SubmissionId);
        Assert.True(preserved.IsRejected);
        Assert.Equal(413, preserved.RejectedStatusCode);
    }

    [Fact]
    public async Task SentryFeedbackSenderAndCoordinatorHonorRateLimitHeadersOn429()
    {
        Assert.Equal(90, SentryFeedbackSender.ParseLongestApplicableRateLimitSeconds("90:feedback:key, 300:error:key"));
        Assert.Equal(45, SentryFeedbackSender.ParseLongestApplicableRateLimitSeconds("45::organization"));
        Assert.Null(SentryFeedbackSender.ParseLongestApplicableRateLimitSeconds("300:error:key"));

        var callCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            response.Headers.TryAddWithoutValidation("X-Sentry-Rate-Limits", "180:feedback:org");
            return response;
        });

        Assert.True(SentryDsnDescriptor.TryParse("https://pub123@o1.ingest.us.sentry.io/999", out var dsn));
        using var sender = new SentryFeedbackSender(dsn, CreateTestIdentity(), handler);
        using var outbox = new FeedbackOutbox(_tempDirectory);
        using var coordinator = new FeedbackCoordinator(
            new FeedbackDraftStore(_tempDirectory),
            outbox,
            sender,
            CreateTestIdentity(),
            initialRetryDelay: TimeSpan.FromMinutes(10));

        var first = FeedbackSubmission.Create(FeedbackKind.Bug, "Rate limited report");
        var firstResult = await coordinator.SubmitAsync(first);
        Assert.Equal(FeedbackSendOutcome.QueuedOffline, firstResult.Outcome);
        Assert.Equal(1, callCount);

        // Immediate follow-up submission and flush must respect the 180s server hold without hitting HTTP.
        var second = FeedbackSubmission.Create(FeedbackKind.Feature, "Second report during hold");
        var secondResult = await coordinator.SubmitAsync(second);
        Assert.Equal(FeedbackSendOutcome.QueuedOffline, secondResult.Outcome);
        Assert.Equal(1, callCount);

        var flushDuringHold = await coordinator.FlushOutboxAsync();
        Assert.Equal(0, flushDuringHold.Attempted);
        Assert.Equal(2, flushDuringHold.Remaining);
        Assert.Equal(1, callCount);

        // Simulate app restart with a fresh coordinator reading the same outbox file.
        using var restartedOutbox = new FeedbackOutbox(_tempDirectory);
        using var restartedCoordinator = new FeedbackCoordinator(
            new FeedbackDraftStore(_tempDirectory),
            restartedOutbox,
            sender,
            CreateTestIdentity(),
            initialRetryDelay: TimeSpan.FromMinutes(10));
        var flushAfterRestart = await restartedCoordinator.FlushOutboxAsync();
        Assert.Equal(0, flushAfterRestart.Attempted);
        Assert.Equal(2, flushAfterRestart.Remaining);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task FeedbackOutboxBoundsRejectedRetentionAndDoesNotBlockPendingQueue()
    {
        using var outbox = new FeedbackOutbox(_tempDirectory);
        for (var i = 0; i < FeedbackOutbox.MaxRejectedItems + 5; i++)
        {
            var rejectedCandidate = FeedbackSubmission.Create(FeedbackKind.Bug, $"Rejected #{i}");
            Assert.True(await outbox.EnqueueAsync(rejectedCandidate));
            Assert.True(await outbox.MarkRejectedAsync(rejectedCandidate.SubmissionId, 413));
        }

        Assert.Empty(await outbox.PeekAllAsync());
        var entries = await outbox.PeekEntriesAsync();
        Assert.Equal(FeedbackOutbox.MaxRejectedItems, entries.Count);

        var nextPending = FeedbackSubmission.Create(FeedbackKind.Feature, "Pending after rejections");
        Assert.True(await outbox.EnqueueAsync(nextPending));
        var pending = await outbox.PeekAllAsync();
        Assert.Single(pending);
        Assert.Equal(nextPending.SubmissionId, pending[0].SubmissionId);
        Assert.True(await outbox.HasUndeliverableAsync());
    }

    [Fact]
    public async Task FeedbackCoordinatorClearsOnlyMatchingSubmittedDraft()
    {
        var draftStore = new FeedbackDraftStore(_tempDirectory);
        using var outbox = new FeedbackOutbox(_tempDirectory);
        var submission = FeedbackSubmission.Create(FeedbackKind.Bug, "Original slow submission");
        var newerDraft = new FeedbackDraft(FeedbackKind.Feature, "Newer edited draft", null, true);

        var handler = StubHttpMessageHandler.FromAsync(_ =>
        {
            // Simulate user reopening Feedback and typing a new draft before slow send completes.
            draftStore.Save(newerDraft);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        Assert.True(SentryDsnDescriptor.TryParse("https://pub123@o1.ingest.us.sentry.io/999", out var dsn));
        using var sender = new SentryFeedbackSender(dsn, CreateTestIdentity(), handler);
        using var coordinator = new FeedbackCoordinator(draftStore, outbox, sender, CreateTestIdentity());

        draftStore.Save(new FeedbackDraft(FeedbackKind.Bug, "Original slow submission", null, true));
        var result = await coordinator.SubmitAsync(submission);
        Assert.Equal(FeedbackSendOutcome.Sent, result.Outcome);

        // Newer edited draft must NOT be wiped by the earlier submission completing.
        Assert.Equal(newerDraft, draftStore.Load());
    }

    [Fact]
    public async Task FeedbackCoordinatorAutomaticallyRetriesQueuedOfflineReportsWhenOnlineReturns()
    {
        var draftStore = new FeedbackDraftStore(_tempDirectory);
        using var outbox = new FeedbackOutbox(_tempDirectory);
        var returnOnline = false;
        using var sentSignal = new SemaphoreSlim(0, 1);

        var handler = new StubHttpMessageHandler(_ =>
        {
            if (!returnOnline)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            sentSignal.Release();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.True(SentryDsnDescriptor.TryParse("https://pub123@o1.ingest.us.sentry.io/999", out var dsn));
        using var sender = new SentryFeedbackSender(dsn, CreateTestIdentity(), handler);
        using var coordinator = new FeedbackCoordinator(
            draftStore,
            outbox,
            sender,
            CreateTestIdentity(),
            initialRetryDelay: TimeSpan.FromMilliseconds(40));

        var submission = FeedbackSubmission.Create(FeedbackKind.Bug, "Queued while offline");
        var result = await coordinator.SubmitAsync(submission);
        Assert.Equal(FeedbackSendOutcome.QueuedOffline, result.Outcome);
        Assert.Single(await outbox.PeekAllAsync());

        returnOnline = true;
        Assert.True(await sentSignal.WaitAsync(TimeSpan.FromSeconds(5)));

        // Allow the outbox removal after HTTP 200 to commit to disk.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && (await outbox.PeekAllAsync()).Count > 0)
        {
            await Task.Delay(20);
        }

        Assert.Empty(await outbox.PeekAllAsync());
    }

    private static string WinUserPath(string user, string suffix)
    {
        const string drive = "C:";
        const string slash = @"\";
        return $"{drive}{slash}Users{slash}{user}{slash}{suffix}";
    }

    private static string FakeApiKey(string prefix) =>
        prefix + "1234567890abcdefghijklmnop";

    private static FeedbackDiagnosticsSnapshot CreateSampleSnapshot() => new(
        appVersion: "0.19.0",
        osVersion: "Windows 10.0.26100.0 (Build 26100)",
        finalAsrEngine: "Automatic",
        polishProvider: "None",
        livePreviewEnabled: false,
        livePreviewEngine: "Parakeet",
        recordingMode: "PushToTalk",
        autoStopEnabled: false,
        hardwareClass: "CpuOnly",
        recentEventCodes: ["ApplicationStarting", "SettingsLoaded"]);

    private static WindowsObservabilityIdentity CreateTestIdentity(string environment = "production") => new(
        AppVersion: "0.19.0",
        OsVersion: "10.0.26100.0",
        OsBuild: "26100",
        ReleaseChannel: ReleaseIdentity.Stable.ChannelName,
        Packaging: "msix",
        Environment: environment,
        ProcessLaunchId: Guid.NewGuid());

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> syncFactory)
        {
            ResponseFactory = request => Task.FromResult(syncFactory(request));
        }

        private StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncFactory)
        {
            ResponseFactory = asyncFactory;
        }

        public static StubHttpMessageHandler FromAsync(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncFactory) =>
            new(asyncFactory);

        public Func<HttpRequestMessage, Task<HttpResponseMessage>> ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => ResponseFactory(request);
    }
}
