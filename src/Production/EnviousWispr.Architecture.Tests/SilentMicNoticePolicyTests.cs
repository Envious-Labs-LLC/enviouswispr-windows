using System.Text.Json;
using System.Text.Json.Nodes;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class SilentMicNoticePolicyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0, true, true, MicSilenceLevel.None)]
    [InlineData(79_999, true, true, MicSilenceLevel.None)]
    [InlineData(80_000, true, true, MicSilenceLevel.None)]
    [InlineData(95_999, true, true, MicSilenceLevel.None)]
    [InlineData(96_000, true, true, MicSilenceLevel.Calm)]
    [InlineData(239_999, true, true, MicSilenceLevel.Calm)]
    [InlineData(240_000, true, true, MicSilenceLevel.Strong)]
    [InlineData(79_999, false, true, MicSilenceLevel.None)]
    [InlineData(80_000, false, true, MicSilenceLevel.Calm)]
    [InlineData(239_999, false, true, MicSilenceLevel.Calm)]
    [InlineData(240_000, false, true, MicSilenceLevel.Strong)]
    [InlineData(250_000, true, false, MicSilenceLevel.None)]
    [InlineData(250_000, false, false, MicSilenceLevel.None)]
    public void EvaluateEnforcesStartVsMidTakeThresholdsAndEnableFlag(
        int sampleCount,
        bool sinceTakeStart,
        bool warningsEnabled,
        MicSilenceLevel expected)
    {
        var actual = SilentMicNoticePolicy.Evaluate(
            new ExactZeroRun(sampleCount, sinceTakeStart),
            warningsEnabled);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EvaluateReturnsNoneForNullOrNonPositiveRuns()
    {
        Assert.Equal(MicSilenceLevel.None, SilentMicNoticePolicy.Evaluate(null, warningsEnabled: true));
        Assert.Equal(MicSilenceLevel.None, SilentMicNoticePolicy.Evaluate(new ExactZeroRun(-1, true), warningsEnabled: true));
        Assert.Equal(MicSilenceLevel.None, SilentMicNoticePolicy.Evaluate(new ExactZeroRun(0, false), warningsEnabled: true));
    }

    [Fact]
    public void TrimTrailingExactZerosHandlesEmptyWholeZeroShortSuffixAndLongSuffix()
    {
        var emptyResult = SilentMicNoticePolicy.TrimTrailingExactZeros(ReadOnlyMemory<float>.Empty, out var emptyWholeZero);
        Assert.False(emptyWholeZero);
        Assert.True(emptyResult.IsEmpty);

        var wholeZero = new float[16_000];
        var wholeZeroResult = SilentMicNoticePolicy.TrimTrailingExactZeros(wholeZero, out var isWholeZero);
        Assert.True(isWholeZero);
        Assert.True(wholeZeroResult.IsEmpty);

        var shortSuffix = new float[1 + 15_999];
        shortSuffix[0] = 0.25f;
        var shortResult = SilentMicNoticePolicy.TrimTrailingExactZeros(shortSuffix, out var shortWholeZero);
        Assert.False(shortWholeZero);
        Assert.Equal(16_000, shortResult.Length);

        var longSuffix = new float[3 + 16_000];
        longSuffix[0] = 0f;
        longSuffix[1] = 0f;
        longSuffix[2] = -0.35f;
        var longResult = SilentMicNoticePolicy.TrimTrailingExactZeros(longSuffix, out var longWholeZero);
        Assert.False(longWholeZero);
        Assert.Equal(new[] { 0f, 0f, -0.35f }, longResult.ToArray());

        // Non-zero warm pre-roll followed by all-zero post-preroll samples must still classify as whole-zero.
        var preRollPlusZeros = new float[800 + 16_000];
        preRollPlusZeros[10] = 0.5f;
        var preRollWholeZeroResult = SilentMicNoticePolicy.TrimTrailingExactZeros(
            preRollPlusZeros,
            drainedPreRollSampleCount: 800,
            out var preRollIsWholeZero);
        Assert.True(preRollIsWholeZero);
        Assert.True(preRollWholeZeroResult.IsEmpty);
    }

    [Fact]
    public async Task SilentMicMonitorPostsOnTransitionsClearsOnSoundAndStopsQuietlyOnCaptureLoss()
    {
        var clock = new Deterministic.ManualClock();
        var source = new FakeSampleSource();
        var effects = new RecordingNoticeEffects();
        await using var monitor = new SilentMicMonitor(effects, () => source, clock);
        var sessionId = DictationSessionId.Create();

        monitor.Start(sessionId, warningsEnabled: true);
        Assert.True(monitor.IsRunning);

        // Before 6.0s since take start -> no notice.
        source.CurrentExactZeroRun = new ExactZeroRun(80_000, SinceTakeStart: true);
        await AdvancePollAsync(clock);
        Assert.Empty(effects.Events);

        // Reaches 6.0s since take start -> Calm posted once; repeated poll does not repost.
        source.CurrentExactZeroRun = new ExactZeroRun(96_000, SinceTakeStart: true);
        await AdvancePollAsync(clock);
        await AdvancePollAsync(clock);
        Assert.Equal(["show:Calm"], effects.Events);

        // Reaches 15.0s -> upgrades to Strong once.
        source.CurrentExactZeroRun = new ExactZeroRun(240_000, SinceTakeStart: true);
        await AdvancePollAsync(clock);
        await AdvancePollAsync(clock);
        Assert.Equal(["show:Calm", "show:Strong"], effects.Events);

        // Sound returns -> clears once.
        source.CurrentExactZeroRun = new ExactZeroRun(0, SinceTakeStart: false);
        await AdvancePollAsync(clock);
        await AdvancePollAsync(clock);
        Assert.Equal(["show:Calm", "show:Strong", "clear"], effects.Events);

        // Mid-take 5.0s silence -> Calm shown again.
        source.CurrentExactZeroRun = new ExactZeroRun(80_000, SinceTakeStart: false);
        await AdvancePollAsync(clock);
        Assert.Equal(["show:Calm", "show:Strong", "clear", "show:Calm"], effects.Events);

        // Capture lost (CurrentExactZeroRun == null) -> loop exits without clearing.
        source.CurrentExactZeroRun = null;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await monitor.Loop!.WaitAsync(Patience);
        Assert.Equal(["show:Calm", "show:Strong", "clear", "show:Calm"], effects.Events);

        Assert.Equal(StopOutcome.Completed, await monitor.StopAsync().WaitAsync(Patience));
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task SilentMicMonitorDoesNotStartWhenDisabledAndStopDoesNotClear()
    {
        var clock = new Deterministic.ManualClock();
        var source = new FakeSampleSource { CurrentExactZeroRun = new ExactZeroRun(250_000, true) };
        var effects = new RecordingNoticeEffects();
        await using var monitor = new SilentMicMonitor(effects, () => source, clock);

        monitor.Start(DictationSessionId.Create(), warningsEnabled: false);
        Assert.False(monitor.IsRunning);
        Assert.Null(clock.NextDue);

        monitor.Start(DictationSessionId.Create(), warningsEnabled: true);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => effects.Events.Count == 1);
        Assert.Equal(["show:Strong"], effects.Events);

        await monitor.StopAsync().WaitAsync(Patience);
        Assert.Equal(["show:Strong"], effects.Events);
    }

    [Fact]
    public async Task SessionBackgroundWorkStartsAndStopsSilentMicMonitorAcrossAllStopPaths()
    {
        var clock = new Deterministic.ManualClock();
        var source = new FakeSampleSource();
        var noticeEffects = new RecordingNoticeEffects();
        await using var silentMic = new SilentMicMonitor(noticeEffects, () => source, clock);
        await using var preview = new LivePreviewController(new StubPreviewEffects(), new NullLogger(), clock);
        await using var autoStop = new AutoStopMonitor(new StubTimerEffects(), new NullLogger(), clock);
        await using var watchdog = new RecordingWatchdog(new StubTimerEffects(), clock);
        var streaming = new StreamingTranscriptionController(new StubStreamingEffects(), new NullLogger(), clock);
        var work = new SessionBackgroundWork(watchdog, preview, autoStop, streaming, clock, silentMic: silentMic);

        var session1 = DictationSessionId.Create();
        await work.StartAsync(
            session1,
            new RecordingBackgroundSettings(
                TimeSpan.FromMinutes(3),
                () => DictationPreferences.Default with { SilentMicWarningsEnabled = true }));
        Assert.True(silentMic.IsRunning);
        await work.StopWatchdogAsync();
        Assert.True((await work.StopAsync().WaitAsync(Patience)).Completed);
        Assert.False(silentMic.IsRunning);

        var session2 = DictationSessionId.Create();
        await work.StartAsync(
            session2,
            new RecordingBackgroundSettings(
                TimeSpan.FromMinutes(3),
                () => DictationPreferences.Default with { SilentMicWarningsEnabled = true }));
        Assert.True(silentMic.IsRunning);
        await work.StopWatchdogAsync();
        Assert.True((await work.StopAsync(TimeSpan.FromSeconds(5)).WaitAsync(Patience)).Completed);
        Assert.False(silentMic.IsRunning);

        var session3 = DictationSessionId.Create();
        await work.StartAsync(
            session3,
            new RecordingBackgroundSettings(
                TimeSpan.FromMinutes(3),
                () => DictationPreferences.Default with { SilentMicWarningsEnabled = true }));
        Assert.True(silentMic.IsRunning);
        await work.StopWatchdogAsync();
        Assert.True((await work.CompleteOrdinaryAsync(() => TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(Patience)).Completed);
        Assert.False(silentMic.IsRunning);
    }

    [Fact]
    public async Task SessionFinalizationRunnerSkipsAsrAndRetiresWarmOnWholeZeroAudio()
    {
        var samples = new float[32_000];
        var world = await FinalizationHarness.CreateAsync(samples, engineSpoken: "should not run");

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Held, report.Outcome);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Empty(world.Delivery.Requests);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(["ArchiveAudio", "RetireWarmCapture", "ShowZeroSignalAdvisory"], world.Trace);
    }

    [Fact]
    public async Task SessionFinalizationRunnerTreatsNonZeroPreRollPlusZeroPostPreRollAsWholeZero()
    {
        var samples = new float[800 + 16_000];
        samples[5] = 0.45f;
        var world = await FinalizationHarness.CreateAsync(
            samples,
            engineSpoken: "should not transcribe pre-roll",
            drainedPreRollSampleCount: 800);

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Held, report.Outcome);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Empty(world.Delivery.Requests);
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(["ArchiveAudio", "RetireWarmCapture", "ShowZeroSignalAdvisory"], world.Trace);
    }

    [Fact]
    public async Task SessionFinalizationRunnerTrimsTrailingExactZerosBeforeAsr()
    {
        var samples = new float[4 + 16_000];
        samples[0] = 0.2f;
        samples[1] = -0.3f;
        samples[2] = 0.1f;
        samples[3] = 0.4f;
        var world = await FinalizationHarness.CreateAsync(samples, engineSpoken: "hello world");

        var report = await world.RunAsync();

        Assert.Equal(FinalizationOutcome.Delivered, report.Outcome);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(4, world.Engine.LastAudioLength);
        Assert.Equal("hello world", Assert.Single(world.Delivery.Requests).Text.Text);
    }

    [Fact]
    public async Task SessionFinalizationRunnerPreservesShortTrailingZerosAndEmptyAudioBehavior()
    {
        var shortSamples = new float[4 + 15_999];
        shortSamples[0] = 0.2f;
        shortSamples[1] = -0.3f;
        shortSamples[2] = 0.1f;
        shortSamples[3] = 0.4f;
        var shortWorld = await FinalizationHarness.CreateAsync(shortSamples, engineSpoken: "hello world");
        var shortReport = await shortWorld.RunAsync();
        Assert.Equal(FinalizationOutcome.Delivered, shortReport.Outcome);
        Assert.Equal(4 + 15_999, shortWorld.Engine.LastAudioLength);

        var emptyWorld = await FinalizationHarness.CreateAsync([], engineSpoken: "");
        var emptyReport = await emptyWorld.RunAsync();
        Assert.Equal(FinalizationOutcome.Held, emptyReport.Outcome);
        Assert.Equal(1, emptyWorld.Engine.Calls);
        Assert.Contains("ShowHeldStatus:Held", emptyWorld.Trace);
        Assert.DoesNotContain("ShowZeroSignalAdvisory", emptyWorld.Trace);
    }

    [Fact]
    public async Task SettingsValidatorRejectsInvalidWarmPolicyAndExistingSchema31LoadsDefaults()
    {
        var invalid = AppSettings.Default with
        {
            Preferences = AppSettings.Default.Preferences with
            {
                Dictation = AppSettings.Default.Preferences.Dictation with
                {
                    WarmMicrophonePolicy = (MicrophoneWarmPolicy)99,
                },
            },
        };
        Assert.NotNull(AppSettingsValidator.Validate(invalid, AppErrorStage.SettingsLoad));

        foreach (var policy in Enum.GetValues<MicrophoneWarmPolicy>())
        {
            var valid = AppSettings.Default with
            {
                Preferences = AppSettings.Default.Preferences with
                {
                    Dictation = AppSettings.Default.Preferences.Dictation with
                    {
                        WarmMicrophonePolicy = policy,
                    },
                },
            };
            Assert.Null(AppSettingsValidator.Validate(valid, AppErrorStage.SettingsSave));
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "EnviousWispr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            var root = JsonNode.Parse(JsonSerializer.Serialize(AppSettings.Default, JsonSettingsStore.SerializerOptions))!.AsObject();
            var dictation = root["preferences"]!["dictation"]!.AsObject();
            dictation.Remove("warmMicrophonePolicy");
            dictation.Remove("silentMicWarningsEnabled");
            await File.WriteAllTextAsync(settingsPath, root.ToJsonString(JsonSettingsStore.SerializerOptions));

            var loaded = await new JsonSettingsStore(settingsPath).LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(MicrophoneWarmPolicy.Seconds30, loaded.Settings.Preferences.Dictation.WarmMicrophonePolicy);
            Assert.True(loaded.Settings.Preferences.Dictation.SilentMicWarningsEnabled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task AdvancePollAsync(Deterministic.ManualClock clock)
    {
        var nextRegistration = clock.Registered + 1;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await clock.WhenRegistered(nextRegistration).WaitAsync(Patience);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for monitor callback.");
            await Task.Delay(5);
        }
    }

    private sealed class FakeSampleSource : ISilentMicSampleSource
    {
        public ExactZeroRun? CurrentExactZeroRun { get; set; }

        public bool LastCaptureWasWholeZero { get; set; }
    }

    private sealed class RecordingNoticeEffects : ISilentMicNoticeEffects
    {
        public List<string> Events { get; } = [];

        public void ShowSilentMicNotice(MicSilenceLevel level) => Events.Add($"show:{level}");

        public void ClearSilentMicNotice() => Events.Add("clear");
    }

    private sealed class StubTimerEffects : IRecordingTimerEffects
    {
        public IAudioSnapshotSource? Audio => null;
        public void Post(PushToTalkSignal signal, DictationSessionId forSession) { }
        public void RecordingTimedOut(DictationSessionId sessionId) { }
    }

    private sealed class StubPreviewEffects : ILivePreviewEffects
    {
        public bool Enabled => false;
        public EnviousWispr.Core.Preview.ILivePreviewEngine? Engine => null;
        public AppErrorCode? EngineUnavailableReason => null;
        public IAudioSnapshotSource? Audio => null;
        public DictationSessionId? RecordingSessionId => null;
        public void ShowPreview(LivePreviewFrame frame) { }
        public void ClearPreview() { }
    }

    private sealed class StubStreamingEffects : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;
        public ITranscriptionEngine? Engine => null;
        public IAudioSnapshotSource? Audio => null;
    }

    private sealed class NullLogger : EnviousWispr.Core.Diagnostics.IAppLogger
    {
        public void Write(EnviousWispr.Core.Diagnostics.AppLogEntry entry) { }
    }

    private sealed class FinalizationHarness
    {
        public required PushToTalkSessionController Controller { get; init; }
        public required SessionFinalizationRunner Runner { get; init; }
        public required StubEngine Engine { get; init; }
        public required StubDelivery Delivery { get; init; }
        public required CapturedAudio Audio { get; init; }
        public required DictationSessionId SessionId { get; init; }
        public required List<string> Trace { get; init; }

        public static async Task<FinalizationHarness> CreateAsync(
            float[] samples,
            string engineSpoken,
            int drainedPreRollSampleCount = 0)
        {
            var capture = new StaticCapture(samples, drainedPreRollSampleCount);
            var controller = new PushToTalkSessionController(capture, new StaticTargetProvider(), minimumHoldDuration: TimeSpan.Zero);
            var started = await controller.PressAsync();
            var released = await controller.ReleaseAsync();
            Assert.Equal(SessionTransitionKind.FinalizeReady, released.Kind);

            var trace = new List<string>();
            var engine = new StubEngine(engineSpoken);
            var delivery = new StubDelivery();
            var effects = new HarnessEffects(trace, engine, delivery);
            var persistence = new SessionPersistence(
                new NullRecoveryStore(),
                new NullHistoryStore(),
                new NullLogger(),
                TimeProvider.System,
                () => HistoryPreferences.Default,
                effects);
            var finalizer = new TranscriptFinalizer(
                PatientPipeline.Create(),
                new PolishExecutor(new AllowAdmission(), effects, () => []),
                effects,
                new SnippetExpansionStage(
                    new SnippetExpander(),
                    TimeProvider.System,
                    () => System.Globalization.CultureInfo.InvariantCulture,
                    _ => Task.FromResult<string?>(null),
                    TimeSpan.FromSeconds(30)));
            var streaming = new StreamingTranscriptionController(new StubStreamingEffects(), new NullLogger(), TimeProvider.System);
            var runner = new SessionFinalizationRunner(controller, finalizer, persistence, streaming, effects, TimeProvider.System);
            return new FinalizationHarness
            {
                Controller = controller,
                Runner = runner,
                Engine = engine,
                Delivery = delivery,
                Audio = released.Audio!,
                SessionId = started.Session!.Id,
                Trace = trace,
            };
        }

        public Task<FinalizationReport> RunAsync() =>
            Runner.RunAsync(SessionId, Audio, recoveryOnly: false, CancellationToken.None);
    }

    private sealed class StaticCapture(float[] samples, int drainedPreRollSampleCount = 0) : IAudioCapture
    {
        public event EventHandler<AudioLevel>? LevelChanged { add { } remove { } }
        public bool IsCapturing { get; private set; }
        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(true));
        }
        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(
                DictationSessionId.Create(),
                samples,
                16_000,
                1,
                DrainedPreRollSampleCount: drainedPreRollSampleCount));
        }
        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(true));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticTargetProvider : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => new(101);
    }

    private sealed class StubEngine(string spoken) : ITranscriptionEngine
    {
        public string EngineId => "stub";
        public int Calls { get; private set; }
        public int LastAudioLength { get; private set; }
        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAudioLength = audio.Samples.Length;
            return Task.FromResult(new Transcript(audio.SessionId, spoken, EngineId));
        }
    }

    private sealed class StubDelivery : ITextDelivery
    {
        public List<TextDeliveryRequest> Requests { get; } = [];
        public Task<DeliveryResult> DeliverAsync(TextDeliveryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DeliveryResult(request.Text.SessionId, Delivered: true, ClipboardFallback: false, TextDeliveryRoute.ClipboardPaste));
        }
    }

    private sealed class HarnessEffects(List<string> trace, ITranscriptionEngine engine, ITextDelivery delivery)
        : ISessionFinalizationEffects, ITranscriptFinalizationEffects, ISessionPersistenceEffects
    {
        public ITranscriptionEngine? Engine => engine;
        public ITextDelivery? Delivery => delivery;
        public FinalizationOptions CurrentOptions() => new([], new DeterministicTextOptions(true, true, true, true), SnippetVocabulary.Empty, null);
        public void ArchiveAudio(CapturedAudio audio) => trace.Add("ArchiveAudio");
        public void RecordTranscriptionUnavailable() => trace.Add("RecordTranscriptionUnavailable");
        public void ShowTranscriptionUnavailable() => trace.Add("ShowTranscriptionUnavailable");
        public void ShowTranscribing() { }
        public void RecordTranscriptionStarted() { }
        public void RecordTranscriptionFinished(Transcript transcript, long elapsedMilliseconds) { }
        public void RecordTranscriptionFailed(AppError? failure, long elapsedMilliseconds) { }
        public void ShowTranscriptionFailed() { }
        public void ShowDelivering() { }
        public void RecordDeliveryStarted() { }
        public void RecordDelivery(DeliveryResult deliveryResult, long elapsedMilliseconds) { }
        public void ReportDelivery(DeliveryResult deliveryResult, string? language) { }
        public void ShowEscapeRecoveryFinished(bool undoOffered) { }
        public void ShowHeldStatus(FinalizationReport report) => trace.Add($"ShowHeldStatus:{report.Outcome}");
        public void ShowZeroSignalAdvisory() => trace.Add("ShowZeroSignalAdvisory");
        public Task RetireWarmCaptureAsync()
        {
            trace.Add("RetireWarmCapture");
            return Task.CompletedTask;
        }
        public void RecordDictationCompleted(long waitMilliseconds) { }
        public void RecordDeterministicProcessingStarted() { }
        public void EmitStageReceipts(IReadOnlyList<DeterministicStageReceipt> receipts, bool emojiRestorationOnly) { }
        public Task SaveRecoveryTextAsync(ProcessedText output, CancellationToken cancellationToken) => Task.CompletedTask;
        public void RecordPolishStarted(string providerId) { }
        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) { }
        public void RecordPolishRefused() { }
        public void RecordDeterministicProcessingFinished(bool degraded, long elapsedMilliseconds, bool empty = false) { }
        public void ShowPendingRecovery(RecoveryTextRecord record, bool undoOffered) { }
        public void ClearRecoveredText() { }
        public void NotifyHistoryChanged() { }
    }

    private sealed class AllowAdmission : EnviousWispr.Core.Runtime.IRuntimeResourceAdmission
    {
        public Task<EnviousWispr.Core.Runtime.RuntimeResourceAcquireResult> AcquireAsync(
            EnviousWispr.Core.Runtime.RuntimeResourceKind resource,
            EnviousWispr.Core.Runtime.RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnviousWispr.Core.Runtime.RuntimeResourceAcquireResult(
                Succeeded: true,
                new NoLease()));

        private sealed class NoLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NullRecoveryStore : IRecoveryTextStore
    {
        public Task<RecoveryTextLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryTextLoadResult(RecoveryTextLoadStatus.Missing));
        public Task<bool> SaveAsync(RecoveryTextRecord record, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class NullHistoryStore : EnviousWispr.Core.History.IHistoryStore
    {
        public Task<EnviousWispr.Core.History.HistoryLoadResult> LoadAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnviousWispr.Core.History.HistoryLoadResult([], EnviousWispr.Core.History.HistoryLoadStatus.Loaded));
        public Task<EnviousWispr.Core.History.HistoryOperationResult> AddAsync(EnviousWispr.Core.History.DictationHistoryEntry entry, int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnviousWispr.Core.History.HistoryOperationResult(true));
        public Task<EnviousWispr.Core.History.HistoryOperationResult> KeepAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnviousWispr.Core.History.HistoryOperationResult(true));
        public Task<EnviousWispr.Core.History.HistoryOperationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnviousWispr.Core.History.HistoryOperationResult(true));
        public Task<EnviousWispr.Core.History.HistoryOperationResult> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnviousWispr.Core.History.HistoryOperationResult(true));
    }
}
