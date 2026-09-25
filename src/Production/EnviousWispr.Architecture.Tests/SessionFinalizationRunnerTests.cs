using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The record-to-deliver path with no window: the real state machine, the real text pipeline, the real
/// persistence over fake stores, a fake speech engine and a fake delivery route, and a fake shell that
/// writes every effect down in order. What this proves is the ORDER and the COUNT - exactly one
/// delivery, the recovery copy written before the text goes anywhere - which the journeys on the real
/// app cannot assert.
/// </summary>
public sealed class SessionFinalizationRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACaptureBecomesDeliveredTextInTheOrderTheShellHad()
    {
        var world = await World.RecordingFinishedAsync("um hello world");

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Delivered, report.Outcome);
        Assert.Equal("hello world", report.Finalized?.Processed.Output.Text);
        Assert.Single(world.Delivery.Requests);
        Assert.Equal("hello world", world.Delivery.Requests[0].Text.Text);
        Assert.Equal(new TargetWindowId(101), world.Delivery.Requests[0].Target);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(
            [
                "ShowTranscribing", "RecordTranscriptionStarted", "ArchiveAudio",
                "RecordTranscriptionFinished", "CurrentOptions", "SaveRecovery:hello world", "ShowDelivering", "RecordDeliveryStarted",
                "Deliver:hello world", "RecordDelivery:delivered=True", "ClearRecovery", "HistoryChanged", "ReportDelivery", "RecordDictationCompleted",
            ],
            world.Trace);
        Assert.False(world.Persistence.HasPendingRecovery);
        var entry = Assert.Single(world.History.Added);
        Assert.True(entry.WasDelivered);
    }

    /// <summary>
    /// A MULTI-LINE SNIPPET GOES THROUGH THE ORDINARY DELIVERY ROUTE WITH ITS LINE BREAKS, marked so the
    /// route leaves its words alone, and the recovery copy and History hold the saved text - never the
    /// placeholder the text pass carried. macOS #2639: a snippet is delivered like any other text.
    /// </summary>
    [Fact]
    public async Task AMultiLineSnippetIsDeliveredWithItsLineBreaksAndMarkedForTheRoute()
    {
        var world = await World.RecordingFinishedAsync("backslash my sign off");
        world.OptionsOverride = () => new FinalizationOptions(
            [],
            new DeterministicTextOptions(true, true, true, true),
            new SnippetVocabulary([new SnippetEntry("my sign off", "Kind regards,\nSam Smith\nsam@example.com")], "backslash"),
            null);

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Delivered, report.Outcome);
        var request = Assert.Single(world.Delivery.Requests);
        Assert.Equal("Kind regards,\nSam Smith\nsam@example.com", request.Text.Text);
        Assert.True(request.SnippetExpanded);
        Assert.Equal(1, report.Finalized?.SnippetsExpanded);
        Assert.Contains("SaveRecovery:Kind regards,\nSam Smith\nsam@example.com", world.Trace);
        Assert.DoesNotContain(world.Trace, entry => entry.Contains("EWSNIP", StringComparison.Ordinal));
        Assert.Equal("Kind regards,\nSam Smith\nsam@example.com", Assert.Single(world.History.Added).Text);
    }

    /// <summary>The twin: an ordinary take is not marked, so the route's cursor-aware repair still runs for it.</summary>
    [Fact]
    public async Task ATakeWithNoSnippetIsNotMarkedForTheRoute()
    {
        var world = await World.RecordingFinishedAsync("my sign off");
        world.OptionsOverride = () => new FinalizationOptions(
            [],
            new DeterministicTextOptions(true, true, true, true),
            new SnippetVocabulary([new SnippetEntry("my sign off", "Kind regards")], "backslash"),
            null);

        await world.RunAsync();

        var request = Assert.Single(world.Delivery.Requests);
        Assert.False(request.SnippetExpanded);
        Assert.DoesNotContain("Kind regards", request.Text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRecoveryCopyIsWrittenBeforeAnythingIsDelivered()
    {
        var world = await World.RecordingFinishedAsync("hello world");

        await world.RunAsync();

        var saved = world.Trace.IndexOf("SaveRecovery:hello world");
        var delivered = world.Trace.IndexOf("Deliver:hello world");
        Assert.True(saved >= 0 && delivered > saved, $"recovery at {saved}, delivery at {delivered}");
    }

    [Fact]
    public async Task ASettingSavedWhileTheEngineWasWorkingReachesThisDictation()
    {
        // The shell always read its custom words and cleanup switches AFTER the speech engine
        // returned, so a word taught during a slow transcription corrected that very dictation. The
        // engine here flips the options source mid-call; the later values must be the ones used.
        var world = await World.RecordingFinishedAsync("envy wisper is here");
        var words = new List<CustomWordEntry>();
        world.OptionsOverride = () => new FinalizationOptions(words.ToArray(), new DeterministicTextOptions(true, true, true, true), SnippetVocabulary.Empty, null);
        world.Engine.BeforeReturning = () => words.Add(new CustomWordEntry("envy wisper", "EnviousWispr"));

        var report = await world.RunAsync();

        Assert.Equal("EnviousWispr is here", world.Delivery.Requests.Single().Text.Text);
        Assert.Equal(FinalizationOutcome.Delivered, report.Outcome);
    }

    [Fact]
    public async Task NoEngineAcknowledgesTheCaptureAndResetsWithoutTranscribing()
    {
        var world = await World.RecordingFinishedAsync("hello world");
        world.Effects.Engine = null;

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.EngineUnavailable, report.Outcome);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal(["RecordTranscriptionUnavailable", "ShowTranscriptionUnavailable"], world.Trace);
    }

    [Fact]
    public async Task AFailedEngineResetsTheSessionAndStillReportsTheWholeWait()
    {
        var world = await World.RecordingFinishedAsync("hello world");
        world.Engine.Failure = new AppError(AppErrorCode.TranscriptionFailed, AppErrorStage.FinalAsr, CanRetry: true);

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.TranscriptionFailed, report.Outcome);
        Assert.Equal(AppErrorCode.TranscriptionFailed, report.TranscriptionError?.Code);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Empty(world.Delivery.Requests);
        Assert.Empty(world.History.Added);
        Assert.Equal(
            ["ShowTranscribing", "RecordTranscriptionStarted", "ArchiveAudio", "RecordTranscriptionFailed:TranscriptionFailed", "ShowTranscriptionFailed", "RecordDictationCompleted"],
            world.Trace);
    }

    [Fact]
    public async Task AClipboardFallbackCountsAsDeliveredForRecoveryButNotForHistory()
    {
        var world = await World.RecordingFinishedAsync("hello world");
        world.Delivery.Answer = (id, _) => new DeliveryResult(id, Delivered: false, ClipboardFallback: true);

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Delivered, report.Outcome);
        Assert.False(world.Persistence.HasPendingRecovery, "the clipboard caught it, so nothing is pending");
        Assert.False(Assert.Single(world.History.Added).WasDelivered);
        Assert.Contains("ClearRecovery", world.Trace);
    }

    [Fact]
    public async Task ARefusedDeliveryHoldsTheTextAndOffersIt()
    {
        var world = await World.RecordingFinishedAsync("hello world");
        world.Delivery.Answer = (id, _) => new DeliveryResult(id, Delivered: false, ClipboardFallback: false, RefusalReason: TextDeliveryRefusalReason.TargetChanged);

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.DeliveryRefused, report.Outcome);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.Contains("ShowPendingRecovery:hello world", world.Trace);
        Assert.DoesNotContain("ClearRecovery", world.Trace);
        Assert.Single(world.Delivery.Requests);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task EscapeRecoveryNeverDelivers()
    {
        var world = await World.RecordingFinishedAsync("hello world");

        var report = await world.RunAsync(recoveryOnly: true);

        Assert.Equal(FinalizationOutcome.EscapeRecovery, report.Outcome);
        Assert.Empty(world.Delivery.Requests);
        Assert.True(world.Persistence.HasPendingRecovery);
        var entry = Assert.Single(world.History.Added);
        Assert.Equal(Now.AddHours(24), entry.ExpiresAt);
        Assert.False(entry.WasDelivered);
        Assert.Equal(
            [
                "ShowTranscribing", "RecordTranscriptionStarted", "ArchiveAudio",
                "RecordTranscriptionFinished", "CurrentOptions", "SaveRecovery:hello world", "HistoryChanged", "ShowPendingRecovery:hello world",
                "ShowEscapeRecoveryFinished", "ShowHeldStatus:EscapeRecovery", "RecordDictationCompleted",
            ],
            world.Trace);
    }

    [Fact]
    public async Task UntrustedDetectedLanguageIsNotUsedForInsertion()
    {
        // THE FINAL PARAKEET MODEL REPORTS A LANGUAGE IT DID NOT DETECT. The delivery route is told
        // nothing for its transcript; a Whisper transcript carries the language Whisper detected.
        var parakeet = await World.RecordingFinishedAsync("hallo welt");
        parakeet.Engine.EngineId = ParakeetModelIds.Final;
        parakeet.Engine.DetectedLanguage = "de";
        var whisper = await World.RecordingFinishedAsync("hallo welt");
        whisper.Engine.DetectedLanguage = "de";

        await parakeet.RunAsync();
        await whisper.RunAsync();

        Assert.Null(parakeet.Delivery.Requests.Single().LanguageCode);
        Assert.Equal("de", whisper.Delivery.Requests.Single().LanguageCode);
    }

    [Fact]
    public async Task NoSpeechIsHeldQuietlyWithNothingDeliveredAndNothingSaved()
    {
        var world = await World.RecordingFinishedAsync("um");

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Held, report.Outcome);
        Assert.Empty(world.Delivery.Requests);
        Assert.Empty(world.History.Added);
        Assert.False(world.Persistence.HasPendingRecovery);
        Assert.Contains("ShowHeldStatus:Held", world.Trace);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task WithNoDeliveryRouteTheTextIsHeld()
    {
        var world = await World.RecordingFinishedAsync("hello world");
        world.Effects.DeliveryRoute = null;

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Held, report.Outcome);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.False(Assert.Single(world.History.Added).WasDelivered);
    }

    [Fact]
    public async Task ARefusedPolishStillDeliversTheCleanedWords()
    {
        var world = await World.RecordingFinishedAsync("hello world");
        world.Polish = new PolishSetup(new FakePolishProvider(_ => string.Empty), UsesLocalRuntime: false, RuntimeResourceKind.Cpu);

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Delivered, report.Outcome);
        Assert.Equal("hello world", world.Delivery.Requests.Single().Text.Text);
        Assert.False(report.Finalized?.WasPolished);
        Assert.False(Assert.Single(world.History.Added).WasPolished);
    }

    /// <summary>Everything wired the way the shell wires it, minus the shell.</summary>
    private sealed class World
    {
        public required PushToTalkSessionController Controller { get; init; }

        public required SessionFinalizationRunner Runner { get; init; }

        public required SessionPersistence Persistence { get; init; }

        public required FakeEffects Effects { get; init; }

        public required FakeEngine Engine { get; init; }

        public required FakeDelivery Delivery { get; init; }

        public required FakeHistoryStore History { get; init; }

        public required CapturedAudio Audio { get; init; }

        public required DictationSessionId SessionId { get; init; }

        public PolishSetup? Polish { get; set; }

        public List<string> Trace => Effects.Trace;

        public static async Task<World> RecordingFinishedAsync(string spoken)
        {
            var capture = new FakeAudioCapture();
            var controller = new PushToTalkSessionController(capture, new FakeTargetProvider(101), minimumHoldDuration: TimeSpan.Zero);
            var started = await controller.PressAsync();
            var released = await controller.ReleaseAsync();
            Assert.Equal(SessionTransitionKind.FinalizeReady, released.Kind);

            var effects = new FakeEffects();
            var engine = new FakeEngine(spoken);
            var delivery = new FakeDelivery { Trace = effects.Trace };
            effects.Engine = engine;
            effects.DeliveryRoute = delivery;
            var history = new FakeHistoryStore();
            var persistence = new SessionPersistence(
                new FakeRecoveryStore(),
                history,
                new NullLogger(),
                new FrozenClock(Now),
                () => HistoryPreferences.Default,
                effects);
            effects.Persistence = persistence;
            var finalizer = new TranscriptFinalizer(
                PatientPipeline.Create(),
                new PolishExecutor(new FakeAdmission(), effects, () => []),
                effects,
                new SnippetExpansionStage(
                    new SnippetExpander(),
                    new FrozenClock(Now),
                    () => System.Globalization.CultureInfo.InvariantCulture,
                    _ => Task.FromResult<string?>(null),
                    TimeSpan.FromSeconds(30)));
            // A streaming owner that was never started: it has no head start, so the runner's
            // transcription is the whole take through the engine, which is what these tests are about.
            var streaming = new StreamingTranscriptionController(new NoStreaming(), new NullLogger(), new FrozenClock(Now));
            var runner = new SessionFinalizationRunner(controller, finalizer, persistence, streaming, effects, new FrozenClock(Now));
            return new World
            {
                Controller = controller,
                Runner = runner,
                Persistence = persistence,
                Effects = effects,
                Engine = engine,
                Delivery = delivery,
                History = history,
                Audio = released.Audio!,
                SessionId = started.Session!.Id,
            };
        }

        public Func<FinalizationOptions>? OptionsOverride { get; set; }

        public Task<FinalizationReport> RunAsync(bool recoveryOnly = false)
        {
            var polish = Polish;
            Effects.Options = OptionsOverride
                ?? (() => new FinalizationOptions([], new DeterministicTextOptions(true, true, true, true), SnippetVocabulary.Empty, polish));
            return Runner.RunAsync(SessionId, Audio, recoveryOnly, CancellationToken.None);
        }
    }

    private sealed class FakeEffects : ISessionFinalizationEffects, ITranscriptFinalizationEffects, ISessionPersistenceEffects
    {
        public List<string> Trace { get; } = [];

        public ITranscriptionEngine? Engine { get; set; }

        public ITextDelivery? DeliveryRoute { get; set; }

        public ITextDelivery? Delivery => DeliveryRoute;

        public Func<FinalizationOptions> Options { get; set; } =
            () => new FinalizationOptions([], new DeterministicTextOptions(true, true, true, true), SnippetVocabulary.Empty, null);

        public FinalizationOptions CurrentOptions()
        {
            Trace.Add("CurrentOptions");
            return Options();
        }

        public void ArchiveAudio(CapturedAudio audio) => Trace.Add("ArchiveAudio");

        public void RecordTranscriptionUnavailable() => Trace.Add("RecordTranscriptionUnavailable");

        public void ShowTranscriptionUnavailable() => Trace.Add("ShowTranscriptionUnavailable");

        public void ShowTranscribing() => Trace.Add("ShowTranscribing");

        public void RecordTranscriptionStarted() => Trace.Add("RecordTranscriptionStarted");

        public void RecordTranscriptionFinished(Transcript transcript, long elapsedMilliseconds) => Trace.Add("RecordTranscriptionFinished");

        public void RecordTranscriptionFailed(AppError? failure, long elapsedMilliseconds) => Trace.Add($"RecordTranscriptionFailed:{failure?.Code}");

        public void ShowTranscriptionFailed() => Trace.Add("ShowTranscriptionFailed");

        public void ShowDelivering() => Trace.Add("ShowDelivering");

        public void RecordDeliveryStarted() => Trace.Add("RecordDeliveryStarted");

        public void RecordDelivery(DeliveryResult delivery, long elapsedMilliseconds) => Trace.Add($"RecordDelivery:delivered={delivery.Delivered}");

        public void ReportDelivery(DeliveryResult delivery, string? language) => Trace.Add("ReportDelivery");

        public void ShowEscapeRecoveryFinished() => Trace.Add("ShowEscapeRecoveryFinished");

        public void ShowHeldStatus(FinalizationReport report) => Trace.Add($"ShowHeldStatus:{report.Outcome}");

        public void RecordDictationCompleted(long waitMilliseconds) => Trace.Add("RecordDictationCompleted");

        // Finalizer effects: only the recovery save is traced; the log lines are the finalizer's own tests' business.
        public void RecordDeterministicProcessingStarted()
        {
        }

        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly)
        {
        }

        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken)
        {
            Trace.Add($"SaveRecovery:{output.Text}");
            return Persistence!.SaveRecoveryTextAsync(output, cancellationToken);
        }

        public SessionPersistence? Persistence { get; set; }

        public void RecordPolishStarted(string providerId)
        {
        }

        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds)
        {
        }

        public void RecordPolishRefused() => Trace.Add("PolishRefused");

        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds)
        {
        }

        // Persistence effects.
        public void ShowPendingRecovery(RecoveryTextRecord record) => Trace.Add($"ShowPendingRecovery:{record.Text}");

        public void ClearRecoveredText() => Trace.Add("ClearRecovery");

        public void NotifyHistoryChanged() => Trace.Add("HistoryChanged");
    }

    private sealed class FakeEngine(string spoken) : ITranscriptionEngine
    {
        public string EngineId { get; set; } = "whisper";

        public string? DetectedLanguage { get; set; } = "en";

        public AppError? Failure { get; set; }

        public Action? BeforeReturning { get; set; }

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            if (Failure is { } failure)
            {
                throw new TranscriptionEngineException(failure);
            }

            BeforeReturning?.Invoke();

            return Task.FromResult(new Transcript(audio.SessionId, spoken, EngineId, DetectedLanguage: DetectedLanguage));
        }
    }

    private sealed class FakeDelivery : ITextDelivery
    {
        public List<TextDeliveryRequest> Requests { get; } = [];

        public List<string>? Trace { get; set; }

        public Func<DictationSessionId, TextDeliveryRequest, DeliveryResult> Answer { get; set; } =
            (id, _) => new DeliveryResult(id, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste);

        public Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Trace?.Add($"Deliver:{request.Text.Text}");
            return Task.FromResult(Answer(request.Text.SessionId, request));
        }
    }

    private sealed class FakePolishProvider(Func<string, string> answer) : IPolishProvider
    {
        public string ProviderId => "fake";

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolishResult(request.Input with { Text = answer(request.Input.Text) }, PolishAttemptStatus.Polished));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(RuntimeResourceKind resource, RuntimeWorkloadKind workload, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(Succeeded: true, new NoLease()));

        private sealed class NoLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRecoveryStore : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));

        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public List<DictationHistoryEntry> Added { get; } = [];

        public Task<HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryLoadResult(Added, HistoryLoadStatus.Loaded));

        public Task<HistoryOperationResult> AddAsync(DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Added.Add(entry);
            return Task.FromResult(new HistoryOperationResult(true));
        }

        public Task<HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));

        public Task<HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryOperationResult(true));
    }

    private sealed class NoStreaming : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;

        public ITranscriptionEngine? Engine => null;

        public IAudioSnapshotSource? Audio => null;
    }

    private sealed class NullLogger : IAppLogger
    {
        public void Write(AppLogEntry entry)
        {
        }
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeTargetProvider(nint window) : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new TargetWindowId(window);
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        private static readonly float[] OneSample = [0.2f];
        private DictationSessionId _sessionId;

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _sessionId = request.SessionId;
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
