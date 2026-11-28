using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using EnviousWispr.App.Composition;
using EnviousWispr.ASR;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Preview;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Runtime;

namespace EnviousWispr.Architecture.Tests;

public sealed class Wave2AsrStreamingHardeningTests
{
    private const int SampleRate = 16_000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------
    // Wave 1A Codex P2: min-hold measured from admitted PressAsync start
    // ------------------------------------------------------------------

    [Fact]
    public async Task MinHoldMeasuredFromAdmittedPressStartPreservesSlowColdStartTake()
    {
        var clock = new Deterministic.ManualClock();
        var capture = new DelayedStartAudioCapture(clock, startDelay: TimeSpan.FromMilliseconds(450));
        var controller = new PushToTalkSessionController(
            capture,
            new StubTargetProvider(new TargetWindowId(42)),
            clock,
            minimumHoldDuration: TimeSpan.FromMilliseconds(500));

        var pressed = await controller.PressAsync();
        Assert.Equal(SessionTransitionKind.Started, pressed.Kind);

        // User only holds 150ms AFTER cold-start finished (600ms total since PressAsync admitted).
        clock.Advance(TimeSpan.FromMilliseconds(150));
        var released = await controller.ReleaseAsync();

        Assert.Equal(SessionTransitionKind.FinalizeReady, released.Kind);
        Assert.NotNull(released.Audio);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task SubThresholdQuickTapFromAdmittedPressStartStillDiscards()
    {
        var clock = new Deterministic.ManualClock();
        var capture = new DelayedStartAudioCapture(clock, startDelay: TimeSpan.Zero);
        var controller = new PushToTalkSessionController(
            capture,
            new StubTargetProvider(new TargetWindowId(42)),
            clock,
            minimumHoldDuration: TimeSpan.FromMilliseconds(500));

        var pressed = await controller.PressAsync();
        Assert.Equal(SessionTransitionKind.Started, pressed.Kind);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        var released = await controller.ReleaseAsync();

        Assert.Equal(SessionTransitionKind.Discarded, released.Kind);
        await controller.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // F16 (Input): 1.0s OtherKeyDismissalWindow & NO Spacebar lock (FD4)
    // ------------------------------------------------------------------

    [Fact]
    public void BareModifierQuickBrushInsideOtherKeyDismissalWindowIsIgnoredWithoutHoldStarted()
    {
        const uint rightCtrl = 0xA3;
        const uint keyA = 0x41;
        var policy = new HotkeyGesturePolicy(rightCtrl, needsHoldThreshold: true, toggleMode: false);

        // Regular typing at t=1000ms..1050ms.
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(keyA, isKeyDown: true, TimeSpan.FromMilliseconds(1000)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(keyA, isKeyDown: false, TimeSpan.FromMilliseconds(1050)));

        // Quick 100ms modifier brush at t=1400ms..1500ms (inside 1.0s OtherKeyDismissalWindow, < 200ms hold threshold).
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(rightCtrl, isKeyDown: true, TimeSpan.FromMilliseconds(1400)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(TimeSpan.FromMilliseconds(1450)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(rightCtrl, isKeyDown: false, TimeSpan.FromMilliseconds(1500)));
        Assert.False(policy.IsHolding);
    }

    [Fact]
    public void ModifierPressedWhileOtherKeyPhysicallyHeldRefusesCaptureAsChord()
    {
        const uint rightCtrl = 0xA3;
        const uint keyC = 0x43;
        var policy = new HotkeyGesturePolicy(rightCtrl, needsHoldThreshold: true, toggleMode: false);

        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(keyC, isKeyDown: true, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(rightCtrl, isKeyDown: true, TimeSpan.FromMilliseconds(150)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(rightCtrl, isKeyDown: false, TimeSpan.FromMilliseconds(450)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(keyC, isKeyDown: false, TimeSpan.FromMilliseconds(460)));
        Assert.False(policy.IsHolding);
    }

    [Fact]
    public void DeliberateModifierHoldInsideDismissalWindowActivatesAfterThresholdAndSpacebarNeverLocks()
    {
        const uint rightCtrl = 0xA3;
        const uint keyA = 0x41;
        const uint spacebar = 0x20;
        var policy = new HotkeyGesturePolicy(rightCtrl, needsHoldThreshold: true, toggleMode: false);

        policy.Process(keyA, isKeyDown: true, TimeSpan.FromMilliseconds(100));
        policy.Process(keyA, isKeyDown: false, TimeSpan.FromMilliseconds(140));

        // Deliberate hold inside OtherKeyDismissalWindow promotes once 200ms threshold elapses.
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(rightCtrl, isKeyDown: true, TimeSpan.FromMilliseconds(400)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Elapsed(TimeSpan.FromMilliseconds(550)));
        Assert.Equal(HotkeyGestureOutcome.HoldStarted, policy.Elapsed(TimeSpan.FromMilliseconds(605)));

        // FD4: Pressing Spacebar while holding does not lock recording into hands-free mode.
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(spacebar, isKeyDown: true, TimeSpan.FromMilliseconds(800)));
        Assert.Equal(HotkeyGestureOutcome.Nothing, policy.Process(spacebar, isKeyDown: false, TimeSpan.FromMilliseconds(850)));
        Assert.Equal(HotkeyGestureOutcome.HoldEnded, policy.Process(rightCtrl, isKeyDown: false, TimeSpan.FromMilliseconds(950)));
        Assert.False(policy.IsRecordingHandsFree);
    }

    // ------------------------------------------------------------------
    // F4: ModelUnloadPolicy idle + memory pressure reap & lazy respawn
    // ------------------------------------------------------------------

    [Fact]
    public async Task IdleMonitorReapsWorkerUnderMemoryPressureAndLazilyRespawnsOnNextRequest()
    {
        var clock = new Deterministic.ManualClock();
        var logger = new RecordingLog();
        var probe = new StubResourceProbe(availableBytes: ModelUnloadPolicy.MemoryPressureBytes - (128UL * 1024 * 1024));
        var sessionActive = false;

        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--test-transcribe-stub"],
            maximumRestarts: 1);
        await using var directEngine = new RuntimeWorkerTranscriptionEngine(
            supervisor,
            "test:f4",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            engine: FinalAsrEngine.Parakeet,
            provider: RuntimeProviderKind.Cpu,
            resourceProbe: probe,
            isSessionActive: () => sessionActive,
            clock: clock,
            idleCheckInterval: TimeSpan.FromHours(1),
            logger: logger);

        var session = DictationSessionId.Create();
        var speech = new float[1600];
        Array.Fill(speech, 0.2f);
        await directEngine.TranscribeAsync(new CapturedAudio(session, speech, SampleRate, 1));
        var firstPid = supervisor.WorkerProcessId;
        Assert.NotNull(firstPid);

        // Active session refuses unload even past IdleBeforeUnloadUnderPressure.
        sessionActive = true;
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await directEngine.CheckIdleUnloadAsync());
        Assert.Equal(firstPid, supervisor.WorkerProcessId);

        // Once session becomes idle and remains idle for IdleBeforeUnloadUnderPressure (1m),
        // pressure check reaps cleanly and logs pressure event.
        sessionActive = false;
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await directEngine.CheckIdleUnloadAsync());
        Assert.Null(supervisor.WorkerProcessId);
        Assert.Contains(logger.Entries, entry => entry.Event == AppEventCode.ResourcePressureDetected);

        // Subsequent dictation lazily respawns a fresh worker process.
        await directEngine.TranscribeAsync(new CapturedAudio(DictationSessionId.Create(), speech, SampleRate, 1));
        var secondPid = supervisor.WorkerProcessId;
        Assert.NotNull(secondPid);
        Assert.NotEqual(firstPid, secondPid);
    }

    [Fact]
    public async Task PeriodicIdleMonitorTickReapsWorkerAutomaticallyPastIdleThreshold()
    {
        var clock = new Deterministic.ManualClock();
        var logger = new RecordingLog();
        var probe = new StubResourceProbe(availableBytes: 8UL * 1024 * 1024 * 1024);
        var supervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--test-transcribe-stub"],
            maximumRestarts: 1);
        await using var engine = new RuntimeWorkerTranscriptionEngine(
            supervisor,
            "test:f4-periodic",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            engine: FinalAsrEngine.Parakeet,
            provider: RuntimeProviderKind.Cpu,
            resourceProbe: probe,
            isSessionActive: static () => false,
            clock: clock,
            logger: logger);

        var speech = new float[1600];
        Array.Fill(speech, 0.2f);
        await engine.TranscribeAsync(new CapturedAudio(DictationSessionId.Create(), speech, SampleRate, 1));
        Assert.NotNull(supervisor.WorkerProcessId);

        // Wait until the 60s monitor timer is registered, then advance deterministically tick-by-tick past 10m idle.
        await clock.WhenRegistered(1).WaitAsync(Patience);
        for (var minute = 0; minute < 11; minute++)
        {
            var nextTimer = clock.Registered + 1;
            clock.Advance(TimeSpan.FromMinutes(1));
            await clock.WhenRegistered(nextTimer).WaitAsync(Patience);
        }

        var deadline = DateTime.UtcNow + Patience;
        while (supervisor.WorkerProcessId is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Null(supervisor.WorkerProcessId);
    }

    // ------------------------------------------------------------------
    // F6: Per-device channel selection + windowed-sinc anti-aliasing
    // ------------------------------------------------------------------

    [Fact]
    public void WindowedSincResamplerAttenuatesHighFrequencyAliasingAbove8KhzWhilePassing3KhzVoiceBand()
    {
        const int sourceRate = 48_000;
        const int frames = 48_000; // 1.0s

        var voiceRms = MeasureResampledRms(sourceRate, frames, frequencyHz: 3_000);
        var aliasRms = MeasureResampledRms(sourceRate, frames, frequencyHz: 14_000);

        Assert.True(voiceRms > 0.60f, $"3 kHz in-band tone should pass near 0.707 RMS, observed {voiceRms}");
        Assert.True(aliasRms < 0.035f, $"14 kHz out-of-band alias should be attenuated >=29 dB, observed {aliasRms}");
    }

    [Fact]
    public void PerDeviceChannelRegistryAndConverterExtractRequestedChannelSafely()
    {
        AudioInputChannelRegistry.Configure(new Dictionary<string, int>
        {
            ["mic-interface-a"] = 2,
            ["mic-bad-negative"] = -5,
        });
        try
        {
            Assert.Equal(2, AudioInputChannelRegistry.GetSelectedChannel(new AudioDeviceId("MIC-INTERFACE-A")));
            Assert.Equal(0, AudioInputChannelRegistry.GetSelectedChannel(new AudioDeviceId("mic-bad-negative")));
            Assert.Equal(0, AudioInputChannelRegistry.GetSelectedChannel(new AudioDeviceId("unlisted-mic")));

            // 4-channel 16 kHz float frame: [0.1, 0.2, 0.85, -0.4].
            var bytes = new byte[4 * 160 * sizeof(float)];
            for (var frame = 0; frame < 160; frame++)
            {
                var baseOffset = frame * 16;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(baseOffset, 4), BitConverter.SingleToInt32Bits(0.1f));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(baseOffset + 4, 4), BitConverter.SingleToInt32Bits(0.2f));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(baseOffset + 8, 4), BitConverter.SingleToInt32Bits(0.85f));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(baseOffset + 12, 4), BitConverter.SingleToInt32Bits(-0.4f));
            }

            var format = new AudioBufferFormat(16_000, 4, AudioSampleEncoding.Ieee754Single);
            var ch2 = AudioSampleConverter.ConvertToMono16Khz(bytes, format, selectedChannelIndex: 2);
            var clampedOutOfRange = AudioSampleConverter.ConvertToMono16Khz(bytes, format, selectedChannelIndex: 99);

            Assert.All(ch2.Samples, sample => Assert.Equal(0.85f, sample, precision: 5));
            Assert.All(clampedOutOfRange.Samples, sample => Assert.Equal(-0.4f, sample, precision: 5));
        }
        finally
        {
            AudioInputChannelRegistry.Configure(null);
        }
    }

    // ------------------------------------------------------------------
    // F7 + F16: Dynamic duration/backend IPC timeout & stage heartbeats
    // ------------------------------------------------------------------

    [Fact]
    public void ComputePieceTimeoutScalesByAudioDurationEngineAndProviderWithBounds()
    {
        // Short audio hits 120s minimum floor.
        Assert.Equal(
            TimeSpan.FromSeconds(120),
            RuntimeWorkerTranscriptionEngine.ComputePieceTimeout(
                TimeSpan.FromSeconds(10),
                FinalAsrEngine.Parakeet,
                RuntimeProviderKind.Cpu));

        // 60s CPU Whisper -> 60 + 60*4 = 300s.
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            RuntimeWorkerTranscriptionEngine.ComputePieceTimeout(
                TimeSpan.FromSeconds(60),
                FinalAsrEngine.Whisper,
                RuntimeProviderKind.Cpu));

        // Long 300s CUDA Whisper caps at 420s.
        Assert.Equal(
            TimeSpan.FromSeconds(420),
            RuntimeWorkerTranscriptionEngine.ComputePieceTimeout(
                TimeSpan.FromSeconds(300),
                FinalAsrEngine.Whisper,
                RuntimeProviderKind.Cuda));
    }

    [Fact]
    public async Task WorkerProgressHeartbeatsAllowSlowMultiStageStartupWhileWedgedStageStallsQuickly()
    {
        // 3 startup stages spaced by 200ms (>600ms total), base startup timeout = 350ms,
        // per-stage stall = 450ms -> progress heartbeats reset stall watchdog and succeed.
        await using var progressingSupervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--test-transcribe-stub", "--test-startup-progress-ms", "200"],
            maximumRestarts: 0,
            stageStallTimeout: TimeSpan.FromMilliseconds(450),
            maxProgressStartupTimeout: TimeSpan.FromSeconds(4));

        var started = await progressingSupervisor.StartAsync(TimeSpan.FromMilliseconds(350)).WaitAsync(Patience);
        Assert.True(started.Succeeded);
        await progressingSupervisor.StopAsync();

        // Conversely, silent health wedge with zero progress fails promptly at stageStallTimeout (~350ms).
        await using var wedgedSupervisor = new RuntimeWorkerSupervisor(
            WorkerPath(),
            ["--health-delay-ms", "5000"],
            maximumRestarts: 0,
            stageStallTimeout: TimeSpan.FromMilliseconds(350),
            maxProgressStartupTimeout: TimeSpan.FromSeconds(4));

        var sw = Stopwatch.StartNew();
        var failed = await wedgedSupervisor.StartAsync(TimeSpan.FromSeconds(10)).WaitAsync(Patience);
        sw.Stop();
        Assert.False(failed.Succeeded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected prompt stage stall timeout, took {sw.Elapsed}");
    }

    // ------------------------------------------------------------------
    // F11: Preserve committed head-start across legitimate silent blanks
    // ------------------------------------------------------------------

    [Fact]
    public async Task SilentBelowFloorBlankSegmentPreservesCommittedHeadStartAcrossTake()
    {
        var clock = new Deterministic.ManualClock();
        var logger = new RecordingLog();
        var audio = new IncrementalStubAudio
        {
            Session = DictationSessionId.Create(),
            Samples = Build((false, 200), (true, 3000), (false, 1200)),
        };
        var engine = new ScriptedEngine();
        var effects = new StreamingEffectsStub(audio, engine);
        var controller = new StreamingTranscriptionController(effects, logger, clock);

        var s2 = DictationSessionId.Create();
        audio.Session = s2;
        audio.Samples = Build((false, 200), (true, 2500), (false, 1200));
        engine.EnqueueText("opened cleanly.");
        controller.Start(s2);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await clock.WhenRegistered(2).WaitAsync(Patience);

        // Commit 2: below-floor slice past 60s hits forced quiet-valley cut and legitimately returns "" without error.
        var quietChunk = new float[(SampleRate * 61) + 500];
        Array.Fill(quietChunk, 0.0008f);
        var trailingSpeech = Build((true, 800));
        var fullAudio = audio.Samples.Concat(quietChunk).Concat(trailingSpeech).ToArray();
        audio.Samples = fullAudio;
        engine.EnqueueText("");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await clock.WhenRegistered(3).WaitAsync(Patience);
        await controller.StopAsync();

        engine.EnqueueText("tail preserved.");
        var result = await controller.TranscribeUsingAnyHeadStartAsync(
            engine,
            new CapturedAudio(s2, fullAudio, SampleRate, 1),
            CancellationToken.None);

        Assert.Equal("opened cleanly. tail preserved.", result.Text);
        Assert.Contains(AppEventCode.StreamingHeadStartUsed, logger.Codes);
    }

    [Fact]
    public async Task DegradedErrorBlankSegmentStillInvalidatesHeadStartAndFallsBackToWhole()
    {
        var clock = new Deterministic.ManualClock();
        var logger = new RecordingLog();
        var audio = new IncrementalStubAudio
        {
            Session = DictationSessionId.Create(),
            Samples = Build((false, 200), (true, 2500), (false, 1200)),
        };
        var engine = new DegradedBlankScriptedEngine();
        var effects = new StreamingEffectsStub(audio, engine);
        var controller = new StreamingTranscriptionController(effects, logger, clock);

        engine.Enqueue(new Transcript(audio.Session, "opened.", engine.EngineId));
        controller.Start(audio.Session);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await clock.WhenRegistered(2).WaitAsync(Patience);

        // Next chunk is quiet (<0.0015f), BUT returns empty with a DegradedError -> must invalidate head-start.
        var quietChunk = new float[(SampleRate * 61) + 500];
        Array.Fill(quietChunk, 0.0008f);
        var trailingSpeech = Build((true, 800));
        var fullAudio = audio.Samples.Concat(quietChunk).Concat(trailingSpeech).ToArray();
        audio.Samples = fullAudio;
        engine.Enqueue(new Transcript(
            audio.Session,
            string.Empty,
            engine.EngineId,
            UsedFallback: true,
            DegradedError: new AppError(AppErrorCode.TranscriptionFailed, AppErrorStage.FinalAsr, CanRetry: true)));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await clock.WhenRegistered(3).WaitAsync(Patience);
        await controller.StopAsync();

        engine.Enqueue(new Transcript(audio.Session, "full fallback result.", engine.EngineId));
        var result = await controller.TranscribeUsingAnyHeadStartAsync(
            engine,
            new CapturedAudio(audio.Session, fullAudio, SampleRate, 1),
            CancellationToken.None);

        Assert.Equal("full fallback result.", result.Text);
        Assert.DoesNotContain(AppEventCode.StreamingHeadStartUsed, logger.Codes);
    }

    // ------------------------------------------------------------------
    // F13: <60s quiet-floor gate, #950 tail preserve, #1434 salvage, #1920
    // ------------------------------------------------------------------

    [Fact]
    public async Task ShortBelowVoicedFloorTakeSkipsAsrWhileQuietVoicedSpeechStillDispatches()
    {
        var clock = new Deterministic.ManualClock();
        var engine = new ScriptedEngine();
        var controller = new StreamingTranscriptionController(
            new StreamingEffectsStub(new IncrementalStubAudio(), engine),
            new RecordingLog(),
            clock);

        var session = DictationSessionId.Create();
        var belowFloor = BuildSilent(amplitude: 0.0008f, milliseconds: 2000);
        var quietSkipped = await controller.TranscribeUsingAnyHeadStartAsync(
            engine,
            new CapturedAudio(session, belowFloor, SampleRate, 1),
            CancellationToken.None);
        Assert.Equal(string.Empty, quietSkipped.Text);
        Assert.Empty(engine.Requests);

        var quietVoiced = BuildSilent(amplitude: 0.003f, milliseconds: 1000);
        engine.EnqueueText("soft speech");
        var voicedTranscript = await controller.TranscribeUsingAnyHeadStartAsync(
            engine,
            new CapturedAudio(DictationSessionId.Create(), quietVoiced, SampleRate, 1),
            CancellationToken.None);
        Assert.Equal("soft speech", voicedTranscript.Text);
        Assert.Single(engine.Requests);
    }

    [Fact]
    public void TailPreserveDecisionAndMarginProtectSustainedSoftWordsAndRejectNoise()
    {
        // Sustained 1000ms tail at 0.003f -> 100% voiced frames -> Preserve (#950).
        var sustained = BuildSilent(amplitude: 0.003f, milliseconds: 1000);
        var preserve = AudioQuietCut.EvaluateTailPreserve(sustained);
        Assert.True(preserve.ShouldPreserve);

        // Mostly dead tail (200ms voiced out of 1000ms) -> Refuse.
        var sparse = Build((true, 200), (false, 800));
        var refuse = AudioQuietCut.EvaluateTailPreserve(sparse);
        Assert.False(refuse.ShouldPreserve);
        Assert.Equal("low_voiced_fraction", refuse.Reason);

        // Trailing consonant padding preserves 200ms past voicedEnd, clamped to audioLength.
        Assert.Equal(16_000 + 3_200, AudioQuietCut.ComputeTrailingConsonantPreserveEnd(32_000, 16_000));
        Assert.Equal(17_000, AudioQuietCut.ComputeTrailingConsonantPreserveEnd(17_000, 16_000));
    }

    [Fact]
    public async Task DegradedLeadSalvageRecoversWhenLeadExceeds400msAndSkipsShorterLeads()
    {
        var clock = new Deterministic.ManualClock();
        var engine = new ScriptedEngine();
        var controller = new StreamingTranscriptionController(
            new StreamingEffectsStub(new IncrementalStubAudio(), engine),
            new RecordingLog(),
            clock);

        // 800ms near-dead static (0.0002f) + 1200ms real speech (0.2f): initial blank triggers #1434 salvage.
        var degradedTake = BuildSilent(0.0002f, 800).Concat(Build((true, 1200))).ToArray();
        engine.EnqueueText("");
        engine.EnqueueText("salvaged sentence");

        var salvaged = await controller.TranscribeUsingAnyHeadStartAsync(
            engine,
            new CapturedAudio(DictationSessionId.Create(), degradedTake, SampleRate, 1),
            CancellationToken.None);
        Assert.Equal("salvaged sentence", salvaged.Text);
        Assert.Equal(2, engine.Requests.Count);
        Assert.True(engine.Requests[1].Length < degradedTake.Length);

        // 200ms (< 0.4s) lead yields no salvage candidates -> single whole call only.
        engine.Requests.Clear();
        var shortLeadTake = BuildSilent(0.0002f, 200).Concat(Build((true, 1200))).ToArray();
        engine.EnqueueText("");
        var unsalvaged = await controller.TranscribeUsingAnyHeadStartAsync(
            engine,
            new CapturedAudio(DictationSessionId.Create(), shortLeadTake, SampleRate, 1),
            CancellationToken.None);
        Assert.Equal(string.Empty, unsalvaged.Text);
        Assert.Single(engine.Requests);
    }

    [Fact]
    public void EmptyAsrOnWorkingUnmutedMicFinishesSilentlyWithoutNoisyNoSpeechAlert()
    {
        var logger = new RecordingLog();
        var view = new CapturingSessionView();
        var capture = new StubSilentSource(endpointMuted: false);
        var effects = CreateFinalizationEffects(logger, view, capture);
        var session = DictationSessionId.Create();
        var emptyTranscript = new Transcript(session, string.Empty, "whisper:cpu", [], RecognitionLanguage: "en");

        effects.RecordTranscriptionFinished(emptyTranscript, elapsedMilliseconds: 95);
        effects.ShowHeldStatus(new FinalizationReport(FinalizationOutcome.Held, Transcript: emptyTranscript));

        // Logged diagnostic observation once, never duplicated by ShowHeldStatus.
        Assert.Single(logger.Entries, entry => entry.Event == AppEventCode.DictationTranscriptionEmpty);
        var status = Assert.Single(view.Statuses);
        Assert.Equal(DictationOverlayState.Hidden, status.State);
        Assert.DoesNotContain("No speech", status.Text, StringComparison.OrdinalIgnoreCase);

        // Zero-signal & muted-endpoint advisories remain intact.
        var mutedEffects = CreateFinalizationEffects(logger, view, new StubSilentSource(endpointMuted: true));
        mutedEffects.ShowZeroSignalAdvisory();
        Assert.Equal(DictationOverlayState.Advisory, view.Statuses[^1].State);
        Assert.Contains(logger.Entries, entry => entry.Event == AppEventCode.AudioDeviceMuted);
    }

    // ------------------------------------------------------------------
    // F8: Coordinated preview + streaming, LocalAgreement-2, LID >= 0.85
    // ------------------------------------------------------------------

    [Fact]
    public void LocalAgreement2LocksAgreedWordsAgainstSubsequentAcousticRevisions()
    {
        var stabilizer = new LivePreviewAgreementStabilizer();

        // Pass 1 shows immediate hypothesis without blocking first-pass latency.
        var pass1 = stabilizer.Stabilize("one two three four");
        Assert.Equal("one two three four", pass1);
        Assert.Empty(stabilizer.AgreedTail);

        // Pass 2 agrees on prefix "one two three" -> promotes them into AgreedTail.
        var pass2 = stabilizer.Stabilize("one two three five");
        Assert.Equal("one two three five", pass2);
        Assert.Equal(["one", "two", "three"], stabilizer.AgreedTail.Select(u => u.Key).ToArray());

        // Pass 3 suffers a head acoustic revision ("won" instead of "one"):
        // locked AgreedTail remains anchored monotonically while trailing word advances.
        var pass3 = stabilizer.Stabilize("won two three five six");
        Assert.Equal("one two three five six", pass3);
    }

    [Fact]
    public async Task CoordinatedStreamingAndPreviewAnchorsCommittedChunksAndSlicesTrailingWindow()
    {
        var clock = new Deterministic.ManualClock();
        var logger = new RecordingLog();
        var audio = new IncrementalStubAudio
        {
            Session = DictationSessionId.Create(),
            Samples = Build((false, 200), (true, 3000), (false, 1200), (true, 1000)),
        };
        var streamEngine = new ScriptedEngine();
        streamEngine.EnqueueText("Alpha bravo.");
        var streamController = new StreamingTranscriptionController(
            new StreamingEffectsStub(audio, streamEngine) { LivePreviewEnabled = true },
            logger,
            clock);

        var previewEngine = new StubPreviewEngine();
        var previewEffects = new CoordinatedPreviewEffects(audio, previewEngine, streamController.SnapshotProgress);
        var previewController = new LivePreviewController(previewEffects, logger, clock);

        streamController.Start(audio.Session);
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await clock.WhenRegistered(2).WaitAsync(Patience);

        // Commit 1 finished at (200+3000+1200)ms = 70,400 samples; trailing 1000ms = 16,000 samples remain.
        var progress = streamController.SnapshotProgress();
        Assert.NotNull(progress);
        Assert.True(progress.CommittedThroughSample > 0);
        Assert.Equal("Alpha bravo.", progress.CommittedPrefix);

        previewEngine.NextText = "charlie delta";
        await previewController.StartAsync(audio.Session);
        await previewEffects.WhenShown(1).WaitAsync(Patience);

        Assert.Single(previewEngine.Snapshots);
        Assert.Equal(audio.Samples.Length - progress.CommittedThroughSample, previewEngine.Snapshots[0].Samples.Length);
        Assert.Equal("Alpha bravo. charlie delta", previewEffects.Previews[^1]);

        await previewController.StopAsync();
        await streamController.StopAsync();
    }

    [Fact]
    public void MultiWindowWhisperLidAggregationAndLanguageLockSuggesterEnforce085AndAntiFlap()
    {
        Assert.False(UserPreferences.Default.LivePreviewEnabled);

        // Consistent high-confidence multi-window French passes 0.85 confidence and 0.10 margin.
        var consistent = WhisperTranscriptionEngine.AggregateLanguageObservations([("fr", 0.91f), ("fr", 0.89f)]);
        Assert.Equal("fr", consistent.Language);
        Assert.True(consistent.Confidence >= 0.85);
        Assert.True(consistent.Margin >= 0.10);

        // Flapping across French and German suppresses confidence & margin cleanly.
        var flapping = WhisperTranscriptionEngine.AggregateLanguageObservations([("fr", 0.92f), ("de", 0.90f)]);
        Assert.True(flapping.Confidence < 0.85);
        Assert.True(flapping.Margin < 0.10);

        // LanguageLockSuggester enforces >= 0.85, margin >= 0.10, and resets streak on flap/low confidence.
        var suggester = new LanguageLockSuggester();
        Assert.Null(suggester.Observe("fr", "auto", FinalAsrEngine.Whisper, confidence: 0.91, margin: 0.82));
        Assert.Null(suggester.Observe("fr", "auto", FinalAsrEngine.Whisper, confidence: 0.90, margin: 0.80));
        // Low-confidence observation breaks streak so the next single 0.92 does NOT fire an offer yet.
        Assert.Null(suggester.Observe("fr", "auto", FinalAsrEngine.Whisper, confidence: 0.72, margin: 0.44));
        Assert.Null(suggester.Observe("fr", "auto", FinalAsrEngine.Whisper, confidence: 0.92, margin: 0.84));
        Assert.Null(suggester.Observe("fr", "auto", FinalAsrEngine.Whisper, confidence: 0.91, margin: 0.82));
        var offer = suggester.Observe("fr", "auto", FinalAsrEngine.Whisper, confidence: 0.90, margin: 0.80);
        Assert.NotNull(offer);
        Assert.Equal("fr", offer.LanguageCode);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static float MeasureResampledRms(int sourceRate, int frames, double frequencyHz)
    {
        var bytes = new byte[frames * sizeof(float)];
        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(0.99 * Math.Sin(2.0 * Math.PI * frequencyHz * i / sourceRate));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), BitConverter.SingleToInt32Bits(sample));
        }

        var result = AudioSampleConverter.ConvertToMono16Khz(
            bytes,
            new AudioBufferFormat(sourceRate, 1, AudioSampleEncoding.Ieee754Single));
        double sumSquares = 0;
        foreach (var s in result.Samples)
        {
            sumSquares += s * (double)s;
        }

        return (float)Math.Sqrt(sumSquares / result.Samples.Length);
    }

    private static float[] BuildSilent(float amplitude, int milliseconds)
    {
        var samples = new float[SampleRate * milliseconds / 1000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i % 2 == 0 ? amplitude : -amplitude;
        }

        return samples;
    }

    private static float[] Build(params (bool IsSpeech, int Milliseconds)[] parts)
    {
        var samples = new float[parts.Sum(part => SampleRate * part.Milliseconds / 1000)];
        var cursor = 0;
        foreach (var (isSpeech, milliseconds) in parts)
        {
            var length = SampleRate * milliseconds / 1000;
            var amplitude = isSpeech ? 0.2f : 0.0005f;
            for (var i = 0; i < length; i++)
            {
                samples[cursor + i] = i % 2 == 0 ? amplitude : -amplitude;
            }

            cursor += length;
        }

        return samples;
    }

    private static string WorkerPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.RuntimeWorker.exe");
        Assert.True(File.Exists(path), $"Worker apphost missing: {path}");
        return path;
    }

    private static SessionFinalizationEffects CreateFinalizationEffects(
        RecordingLog logger,
        CapturingSessionView view,
        ISilentMicSampleSource capture)
    {
        var shell = new SessionShell(
            view,
            AttachedSession: () => null,
            Dictation: () => DictationPreferences.Default,
            Engine: () => null,
            Delivery: () => null,
            Options: () => null!,
            CloudPolishProviderName: () => null,
            RunId: () => null,
            RecordingActive: _ => { },
            ArchiveAudio: _ => { },
            DetachCaptureObservers: () => { },
            ReleaseSession: () => { },
            DisposeDeliveryRoute: () => { });
        var parts = new SessionCompositionParts(
            null!,
            (IAudioCapture)capture,
            null!,
            null!,
            null!,
            logger,
            shell,
            TimeProvider.System);
        return new SessionFinalizationEffects(parts);
    }

    private sealed class DelayedStartAudioCapture(Deterministic.ManualClock clock, TimeSpan startDelay) : IAudioCapture
    {
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
            if (startDelay > TimeSpan.Zero)
            {
                clock.Advance(startDelay);
            }

            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, new float[1600], SampleRate, 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubTargetProvider(TargetWindowId target) : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => target;
    }

    private sealed class StubResourceProbe(ulong availableBytes) : ISystemResourceProbe
    {
        public SystemResourceSnapshot Probe() => new(
            AvailableDiskBytes: 10L * 1024 * 1024 * 1024,
            AvailablePhysicalMemoryBytes: availableBytes,
            MemoryLoadPercent: 90,
            IsAvailable: true);
    }

    private sealed class RecordingLog : IAppLogger
    {
        public ConcurrentQueue<AppLogEntry> Entries { get; } = new();
        public IEnumerable<AppEventCode> Codes => Entries.Select(e => e.Event);
        public void Write(AppLogEntry entry) => Entries.Enqueue(entry);
    }

    private sealed class StubSilentSource(bool endpointMuted) : IAudioCapture, ISilentMicSampleSource
    {
        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing => false;
        public ExactZeroRun? CurrentExactZeroRun => null;
        public bool LastCaptureWasWholeZero => false;
        public bool LastCaptureWasEndpointMuted => endpointMuted;

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudioOperationResult(true));

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapturedAudio(DictationSessionId.Create(), Array.Empty<float>(), SampleRate, 1));

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudioOperationResult(true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingSessionView : ISessionView
    {
        public List<DictationStatus> Statuses { get; } = [];
        public void ShowStatus(DictationStatus status) => Statuses.Add(status);
        public void ShowNotice(string title, string message, bool isError = false) { }
        public void ShowMainWindow() { }
        public void ReportDelivery(DeliveryReport delivered, string? detectedLanguage) { }
    }

    private sealed class ScriptedEngine : ITranscriptionEngine
    {
        private readonly Queue<string> _texts = new();
        public List<(int Length, float[] Samples)> Requests { get; } = [];
        public string EngineId => "scripted";
        public void EnqueueText(string text) => _texts.Enqueue(text);

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            Requests.Add((audio.Samples.Length, audio.Samples.ToArray()));
            var text = _texts.TryDequeue(out var next) ? next : string.Empty;
            return Task.FromResult(new Transcript(audio.SessionId, text, EngineId));
        }
    }

    private sealed class DegradedBlankScriptedEngine : ITranscriptionEngine
    {
        private readonly Queue<Transcript> _responses = new();
        public string EngineId => "degraded-scripted";
        public void Enqueue(Transcript transcript) => _responses.Enqueue(transcript);

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            var next = _responses.TryDequeue(out var t)
                ? t with { SessionId = audio.SessionId }
                : new Transcript(audio.SessionId, string.Empty, EngineId);
            return Task.FromResult(next);
        }
    }

    private sealed class IncrementalStubAudio : IAudioIncrementalSnapshotSource
    {
        public DictationSessionId Session { get; set; }
        public float[] Samples { get; set; } = [];

        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration)
        {
            if (Samples.Length == 0)
            {
                return null;
            }

            var maxSamples = maximumDuration == TimeSpan.MaxValue
                ? Samples.Length
                : (int)Math.Min(Samples.Length, (long)(maximumDuration.TotalSeconds * SampleRate));
            var start = Samples.Length - maxSamples;
            return new AudioSnapshot(Session, Samples.AsMemory(start, maxSamples), SampleRate, 1);
        }

        public IncrementalAudioSnapshot? GetSnapshotFrom(int fromSample)
        {
            if (fromSample >= Samples.Length)
            {
                return null;
            }

            return new IncrementalAudioSnapshot(
                Session,
                SampleRate,
                fromSample,
                Samples.Length,
                Samples.AsMemory(fromSample));
        }
    }

    private sealed class StreamingEffectsStub(IAudioSnapshotSource audio, ITranscriptionEngine engine) : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled { get; set; }
        public ITranscriptionEngine? Engine => engine;
        public IAudioSnapshotSource? Audio => audio;
    }

    private sealed class StubPreviewEngine : ILivePreviewEngine
    {
        public string EngineId => "stub-preview";
        public string NextText { get; set; } = string.Empty;
        public List<AudioSnapshot> Snapshots { get; } = [];

        public Task<RuntimeWorkerResult> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Ready));

        public Task<LivePreviewUpdate> PreviewAsync(AudioSnapshot snapshot, long sequence, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return Task.FromResult(new LivePreviewUpdate(snapshot.SessionId.Value, sequence, true, NextText));
        }

        public Task<RuntimeWorkerResult> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeWorkerResult(true, RuntimeWorkerState.Stopped));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CoordinatedPreviewEffects(
        IAudioSnapshotSource audio,
        ILivePreviewEngine engine,
        Func<StreamingProgressSnapshot?> streamingProgress) : ILivePreviewEffects
    {
        private readonly Deterministic.Milestone _shown = new();
        public List<string> Previews { get; } = [];
        public bool Enabled => true;
        public ILivePreviewEngine? Engine => engine;
        public AppErrorCode? EngineUnavailableReason => null;
        public IAudioSnapshotSource? Audio => audio;
        public DictationSessionId? RecordingSessionId => null;
        public StreamingProgressSnapshot? StreamingProgress => streamingProgress();
        public Task WhenShown(int count) => _shown.WhenAtLeast(count);

        public void ShowPreview(LivePreviewFrame frame)
        {
            Previews.Add(frame.Text);
            _shown.Increment();
        }

        public void ClearPreview() { }
    }
}
