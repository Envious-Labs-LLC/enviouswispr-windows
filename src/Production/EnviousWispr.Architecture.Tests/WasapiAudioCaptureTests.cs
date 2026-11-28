using System.Buffers.Binary;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class WasapiAudioCaptureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(25);
    private static readonly float[] PreviewTail = [0.3f, 0.4f];
    private static readonly float[] CompletePreviewFixture = [0.1f, 0.2f, 0.3f, 0.4f];
    private static readonly float[] WarmFirstTakeFixture = [0.1f, 0.2f];
    private static readonly float[] WarmSecondDeviceFixture = [0.9f];

    [Fact]
    public async Task ClientStopReturnsCompletedBufferedAudio()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        var started = await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.25f, -0.5f);
        var result = await capture.StopAsync();

        Assert.True(started.Succeeded);
        Assert.False(capture.IsCapturing);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(AudioCaptureOutcome.Completed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(new[] { 0.25f, -0.5f }, result.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    /// <summary>An empty callback is not a packet, and it must not publish a level.</summary>
    /// <remarks>
    /// MEASURED ON A REAL MACHINE AND IT BLINDED EVERY METER IN THE APP. This recorder delivers a
    /// zero-length buffer on roughly half its callbacks, each within a millisecond of a real one, so
    /// every true level was immediately published over with a zero. Both meters read the latest
    /// level, so both read zero on virtually every look, and the recording pill sat at its floor
    /// through dictations that transcribed perfectly.
    /// </remarks>
    [Fact]
    public async Task EmptyCallbacksDoNotCountAsPacketsOrPublishSilence()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var levels = new List<AudioLevel>();
        capture.LevelChanged += (_, level) => levels.Add(level);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.5f, -0.5f);
        recorder.EmitBytes([]);
        var result = await capture.StopAsync();

        Assert.Equal(1, capture.LastPacketCount);
        Assert.Equal(0, capture.LastSilentPacketCount);
        // THE LEVEL FROM THE REAL BUFFER SURVIVES, which is the whole point. A second event here
        // would be the defect: a zero published a millisecond after the truth.
        Assert.Single(levels);
        Assert.Equal(0.5f, levels[0].Peak);
        Assert.Equal(0.5f, levels[0].RootMeanSquare, 5);
        Assert.Equal(0.5f, capture.LastPeak);
        Assert.Equal(0.5f, capture.LastRootMeanSquare, 5);
        Assert.Equal(new[] { 0.5f, -0.5f }, result.Samples.ToArray());
        Assert.Equal(AudioCaptureOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task PreviewSnapshotIsBoundedAndDoesNotConsumeFinalAudio()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.1f, 0.2f, 0.3f, 0.4f);
        var snapshot = capture.GetSnapshot(TimeSpan.FromSeconds(2d / 16_000));
        var final = await capture.StopAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(PreviewTail, snapshot.Samples.ToArray());
        Assert.Equal(CompletePreviewFixture, final.Samples.ToArray());
        Assert.Null(capture.GetSnapshot(TimeSpan.FromSeconds(1)));
    }

    /// <summary>Asking for the whole recording returns it instead of throwing.</summary>
    /// <remarks>
    /// THE STREAMING HEAD START ASKS FOR EXACTLY THIS AND HAS NEVER ONCE SURVIVED IT. Its loop wants
    /// the whole recording rather than a window - a commit is a range measured from the start, so a
    /// rolling window would make those indices mean something different on every poll - and it says
    /// so by passing `TimeSpan.MaxValue`. That is about 9.2e11 seconds; multiplied by the sample rate
    /// it is about 1.5e16, and the `checked` cast to `int` threw `OverflowException` on the FIRST
    /// poll of every recording ever made.
    ///
    /// THE EXCEPTION WAS INVISIBLE BECAUSE THE FEATURE IS DESIGNED TO GIVE UP QUIETLY. Any failure
    /// abandons the head start and the release transcribes the whole take, which is correct and is
    /// why nothing on screen was ever wrong. Measured 2026-09-03: `StreamingAbandoned` 511 ms after
    /// the recording started, matching the poll interval, with no error code because an
    /// `OverflowException` is not a transcription failure. Ref: #96.
    ///
    /// A CEILING THAT CANNOT BE REACHED IS NOT A LIMIT. `maximumDuration` only ever trims, so
    /// saturating it changes nothing for any duration a caller could mean and removes the one value
    /// that turned "give me everything" into a crash.
    /// </remarks>
    [Fact]
    public async Task AskingForTheWholeRecordingReturnsItRatherThanOverflowing()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.1f, 0.2f, 0.3f, 0.4f);
        var snapshot = capture.GetSnapshot(TimeSpan.MaxValue);

        Assert.NotNull(snapshot);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(CompletePreviewFixture, snapshot.Samples.ToArray());
    }

    /// <summary>Every duration a caller could mean is accepted, including the extremes.</summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue / 2)]
    [InlineData(TimeSpan.TicksPerDay * 365)]
    [InlineData(TimeSpan.TicksPerSecond * 20)]
    public async Task NoReachableDurationThrows(long ticks)
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.1f, 0.2f, 0.3f, 0.4f);

        Assert.NotNull(capture.GetSnapshot(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public async Task UnexpectedStopPreservesTakeAsRetryableInterruption()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.4f, -0.2f, 0.1f);
        recorder.Interrupt();
        var result = await capture.StopAsync();

        Assert.False(capture.IsCapturing);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
        Assert.True(result.Error?.CanRetry);
        Assert.Equal(new[] { 0.4f, -0.2f, 0.1f }, result.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task OverlappingStartIsRejectedWithoutReplacingActiveRecorder()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);

        var first = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        var second = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(AppErrorCode.CaptureAlreadyActive, second.Error?.Code);
        Assert.Equal(1, recorder.StartCount);
        await capture.CancelAsync();
    }

    [Fact]
    public async Task CancelClearsBufferedAudioAndEmitsSilentLevel()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var levels = new List<AudioLevel>();
        capture.LevelChanged += (_, level) => levels.Add(level);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.75f);
        var cancelled = await capture.CancelAsync();
        var afterCancel = await capture.StopAsync();

        Assert.True(cancelled.Succeeded);
        Assert.False(capture.IsCapturing);
        Assert.Equal(AudioLevel.Silent, levels[^1]);
        Assert.Equal(AppErrorCode.InvalidTransition, afterCancel.Error?.Code);
        Assert.Empty(afterCancel.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task StopTimeoutReturnsInterruptionAndStillDisposesRecorder()
    {
        var recorder = new FakeRecorderSession { CompleteWhenStopped = false };
        await using var capture = CreateCapture(recorder);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.6f);
        var result = await capture.StopAsync();

        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
        Assert.Single(result.Samples.ToArray());
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task UnavailableDeviceIsReturnedAsTypedStartFailure()
    {
        var factory = new FakeRecorderFactory(
            new InvalidOperationException("Synthetic unavailable endpoint."));
        await using var capture = new WasapiAudioCapture(factory, TestTimeout);

        var result = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));

        Assert.False(result.Succeeded);
        Assert.Equal(AppErrorCode.AudioDeviceUnavailable, result.Error?.Code);
        Assert.True(result.Error?.CanRetry);
    }

    [Fact]
    public async Task ImmediateBackendStopDuringStartLeavesCaptureInactiveAndRecoverable()
    {
        var recorder = new FakeRecorderSession { InterruptWhenStarted = true };
        await using var capture = CreateCapture(recorder);

        var started = await capture.StartAsync(
            new AudioCaptureRequest(DictationSessionId.Create()));
        var result = await capture.StopAsync();

        Assert.True(started.Succeeded);
        Assert.False(capture.IsCapturing);
        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
    }

    [Fact]
    public async Task MalformedCallbackInterruptsCaptureWithoutCorruptingPriorAudio()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.3f);
        recorder.EmitBytes(new byte[3]);
        var result = await capture.StopAsync();

        Assert.Equal(AudioCaptureOutcome.Interrupted, result.Outcome);
        Assert.Equal(AppErrorCode.AudioDeviceLost, result.Error?.Code);
        Assert.Equal(0.3f, Assert.Single(result.Samples.ToArray()), precision: 5);
    }

    [Fact]
    public async Task GetSnapshotFromReadsOnlySamplesAfterStartOffsetAndMatchesFinalStop()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = CreateCapture(recorder);
        var sessionId = DictationSessionId.Create();

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(0.1f, 0.2f, 0.3f);
        var first = capture.GetSnapshotFrom(0);
        Assert.NotNull(first);
        Assert.Equal(0, first.StartSample);
        Assert.Equal(3, first.TotalCapturedSamples);
        Assert.Equal([0.1f, 0.2f, 0.3f], first.Samples.ToArray());

        recorder.Emit(0.4f, 0.5f);
        var tail = capture.GetSnapshotFrom(3);
        Assert.NotNull(tail);
        Assert.Equal(3, tail.StartSample);
        Assert.Equal(5, tail.TotalCapturedSamples);
        Assert.Equal([0.4f, 0.5f], tail.Samples.ToArray());

        var empty = capture.GetSnapshotFrom(5);
        Assert.NotNull(empty);
        Assert.True(empty.Samples.IsEmpty);

        var final = await capture.StopAsync();
        Assert.Equal(AudioCaptureOutcome.Completed, final.Outcome);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f], final.Samples.ToArray());
    }

    [Fact]
    public async Task HardCaptureBackstopCapsAudioSignalsOnceAndReturnsCompletedOutcome()
    {
        var recorder = new FakeRecorderSession();
        // 10 ms at 16 kHz = 160 samples max.
        await using var capture = new WasapiAudioCapture(
            new FakeRecorderFactory(recorder),
            TestTimeout,
            hardCaptureBackstop: TimeSpan.FromMilliseconds(10));
        var sessionId = DictationSessionId.Create();
        var fired = new List<DictationSessionId>();
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.MaxDurationBackstopReached += (_, id) =>
        {
            lock (fired)
            {
                fired.Add(id);
            }

            signalled.TrySetResult();
        };

        await capture.StartAsync(new AudioCaptureRequest(sessionId));
        recorder.Emit(Enumerable.Repeat(0.25f, 100).ToArray());
        recorder.Emit(Enumerable.Repeat(0.5f, 150).ToArray());
        recorder.Emit(Enumerable.Repeat(0.75f, 50).ToArray());
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await capture.StopAsync();
        Assert.Equal(AudioCaptureOutcome.Completed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(160, result.Samples.Length);
        Assert.Equal([sessionId], fired);
    }

    [Fact]
    public async Task StandaloneConstructionDefaultsToWarmPolicyOff()
    {
        await using var capture = new WasapiAudioCapture();

        Assert.Equal(MicrophoneWarmPolicy.Off, capture.WarmPolicy);
        Assert.Null(capture.CurrentExactZeroRun);
        Assert.False(capture.LastCaptureWasWholeZero);
    }

    [Fact]
    public async Task WarmIdleReusesOpenRecorderDrainsNewest500MsPreRollAndZeroesStartTimings()
    {
        var recorders = new List<FakeRecorderSession>();
        var factory = new FakeRecorderFactory(deviceId =>
        {
            var session = new FakeRecorderSession();
            recorders.Add(session);
            return session;
        });
        var clock = new Deterministic.ManualClock();
        await using var capture = new WasapiAudioCapture(factory, TestTimeout, timeProvider: clock);
        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);

        var levels = new List<AudioLevel>();
        capture.LevelChanged += (_, level) => levels.Add(level);

        var deviceA = new AudioDeviceId("mic-a");
        Assert.True((await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create(), deviceA))).Succeeded);
        recorders[0].Emit(0.1f, 0.2f);
        var first = await capture.StopAsync();
        Assert.Equal(WarmFirstTakeFixture, first.Samples.ToArray());
        Assert.False(recorders[0].Disposed);
        Assert.Null(capture.CurrentExactZeroRun);

        // Emit 9,000 samples while WarmIdle (1,000 old + 8,000 newest = 500 ms pre-roll capacity).
        var levelCountAfterFirst = levels.Count;
        var packetsAfterFirst = capture.LastPacketCount;
        var staleLead = Enumerable.Repeat(0.05f, 1_000).ToArray();
        var retainedTail = Enumerable.Range(1, 8_000).Select(i => i * 0.0001f).ToArray();
        recorders[0].Emit(staleLead);
        recorders[0].Emit(retainedTail);
        recorders[0].EmitSilentFloats(160);
        // After silent packet, latest 8,000 samples are retainedTail[160..] + 160 zeros.
        Assert.Equal(levelCountAfterFirst, levels.Count);
        Assert.Equal(packetsAfterFirst, capture.LastPacketCount);
        Assert.Null(capture.CurrentExactZeroRun);

        // Second start on same device reuses open recorder with 0ms open/start timings and drained pre-roll.
        Assert.True((await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create(), deviceA))).Succeeded);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, recorders[0].StartCount);
        Assert.Equal(0L, capture.LastDeviceOpenMilliseconds);
        Assert.Equal(0L, capture.LastStreamStartMilliseconds);
        Assert.Equal(new ExactZeroRun(0, SinceTakeStart: true), capture.CurrentExactZeroRun);

        recorders[0].Emit(0.75f);
        var second = await capture.StopAsync();
        var expected = retainedTail.Skip(160).Concat(new float[160]).Append(0.75f).ToArray();
        Assert.Equal(expected.Length, second.Samples.Length);
        Assert.Equal(expected[0], second.Samples.Span[0], precision: 5);
        Assert.Equal(0.75f, second.Samples.Span[^1], precision: 5);
        Assert.Equal(1, capture.LastPacketCount);
        Assert.False(capture.LastCaptureWasWholeZero);
    }

    [Fact]
    public async Task WarmIdleDeviceMismatchTearsDownOldRecorderAndColdOpensRequestedDevice()
    {
        var recorders = new List<FakeRecorderSession>();
        var factory = new FakeRecorderFactory(_ =>
        {
            var session = new FakeRecorderSession();
            recorders.Add(session);
            return session;
        });
        var clock = new Deterministic.ManualClock();
        await using var capture = new WasapiAudioCapture(factory, TestTimeout, timeProvider: clock);
        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create(), new AudioDeviceId("mic-a")));
        recorders[0].Emit(0.2f);
        await capture.StopAsync();
        recorders[0].Emit(0.3f, 0.4f);

        var startedSecond = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create(), new AudioDeviceId("mic-b")));
        Assert.True(startedSecond.Succeeded);
        Assert.True(recorders[0].Disposed);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal([new AudioDeviceId("mic-a"), new AudioDeviceId("mic-b")], factory.RequestedDeviceIds);

        recorders[1].Emit(0.9f);
        var second = await capture.StopAsync();
        Assert.Equal(WarmSecondDeviceFixture, second.Samples.ToArray());
    }

    [Theory]
    [InlineData(MicrophoneWarmPolicy.Seconds10, 10)]
    [InlineData(MicrophoneWarmPolicy.Seconds30, 30)]
    [InlineData(MicrophoneWarmPolicy.Seconds60, 60)]
    public async Task WarmIdleTimersExpireAtConfiguredPolicyDuration(MicrophoneWarmPolicy policy, int seconds)
    {
        var recorder = new FakeRecorderSession();
        var clock = new Deterministic.ManualClock();
        await using var capture = new WasapiAudioCapture(new FakeRecorderFactory(recorder), TestTimeout, timeProvider: clock);
        await capture.SetWarmPolicyAsync(policy);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.2f);
        await capture.StopAsync();
        Assert.False(recorder.Disposed);

        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.False(recorder.Disposed);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(recorder.Disposed);
    }

    [Fact]
    public async Task WarmPolicyAlwaysKeepsRecorderOpenAcrossLongIdle()
    {
        var recorder = new FakeRecorderSession();
        var clock = new Deterministic.ManualClock();
        await using var capture = new WasapiAudioCapture(new FakeRecorderFactory(recorder), TestTimeout, timeProvider: clock);
        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Always);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.2f);
        await capture.StopAsync();

        Assert.Null(clock.NextDue);
        clock.Advance(TimeSpan.FromHours(4));
        Assert.False(recorder.Disposed);
    }

    [Fact]
    public async Task SetWarmPolicyAsyncReconcilesWarmIdleStateAcrossAllPolicies()
    {
        // Extending 30s -> 60s after 12s leaves 48s remaining.
        var recorder1 = new FakeRecorderSession();
        var clock1 = new Deterministic.ManualClock();
        await using (var capture1 = new WasapiAudioCapture(new FakeRecorderFactory(recorder1), TestTimeout, timeProvider: clock1))
        {
            await capture1.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);
            await capture1.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
            await capture1.StopAsync();
            clock1.Advance(TimeSpan.FromSeconds(12));
            await capture1.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds60);
            Assert.False(recorder1.Disposed);
            clock1.Advance(TimeSpan.FromSeconds(47));
            Assert.False(recorder1.Disposed);
            clock1.Advance(TimeSpan.FromSeconds(1));
            Assert.True(recorder1.Disposed);
        }

        // Shortening 30s -> 10s after 12s tears down immediately (elapsed >= 10s).
        var recorder2 = new FakeRecorderSession();
        var clock2 = new Deterministic.ManualClock();
        await using (var capture2 = new WasapiAudioCapture(new FakeRecorderFactory(recorder2), TestTimeout, timeProvider: clock2))
        {
            await capture2.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);
            await capture2.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
            await capture2.StopAsync();
            clock2.Advance(TimeSpan.FromSeconds(12));
            await capture2.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds10);
            Assert.True(recorder2.Disposed);
        }

        // Switching to Off tears down immediately.
        var recorder3 = new FakeRecorderSession();
        var clock3 = new Deterministic.ManualClock();
        await using (var capture3 = new WasapiAudioCapture(new FakeRecorderFactory(recorder3), TestTimeout, timeProvider: clock3))
        {
            await capture3.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);
            await capture3.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
            await capture3.StopAsync();
            await capture3.SetWarmPolicyAsync(MicrophoneWarmPolicy.Off);
            Assert.True(recorder3.Disposed);
        }

        // Switching to Always cancels pending timer.
        var recorder4 = new FakeRecorderSession();
        var clock4 = new Deterministic.ManualClock();
        await using (var capture4 = new WasapiAudioCapture(new FakeRecorderFactory(recorder4), TestTimeout, timeProvider: clock4))
        {
            await capture4.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);
            await capture4.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
            await capture4.StopAsync();
            await capture4.SetWarmPolicyAsync(MicrophoneWarmPolicy.Always);
            clock4.Advance(TimeSpan.FromMinutes(10));
            Assert.False(recorder4.Disposed);
        }
    }

    [Fact]
    public async Task TearDownWarmAsyncDisposesIdleRecorderWithoutDisturbingActiveCapture()
    {
        var recorders = new List<FakeRecorderSession>();
        var factory = new FakeRecorderFactory(_ =>
        {
            var session = new FakeRecorderSession();
            recorders.Add(session);
            return session;
        });
        await using var capture = new WasapiAudioCapture(factory, TestTimeout);
        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        await capture.TearDownWarmAsync();
        Assert.True(capture.IsCapturing);
        Assert.False(recorders[0].Disposed);

        await capture.StopAsync();
        Assert.False(recorders[0].Disposed);

        await capture.TearDownWarmAsync();
        Assert.True(recorders[0].Disposed);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task UnexpectedStopWhileWarmIdleIsNotReusedAndCancelOrDisposeTearsDownWarm()
    {
        var recorders = new List<FakeRecorderSession>();
        var factory = new FakeRecorderFactory(_ =>
        {
            var session = new FakeRecorderSession();
            recorders.Add(session);
            return session;
        });
        await using var capture = new WasapiAudioCapture(factory, TestTimeout);
        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        await capture.StopAsync();
        recorders[0].Interrupt();

        Assert.True((await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()))).Succeeded);
        Assert.Equal(2, factory.CreateCount);
        await capture.StopAsync();
        Assert.False(recorders[1].Disposed);

        await capture.CancelAsync();
        Assert.True(recorders[1].Disposed);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        await capture.StopAsync();
        Assert.False(recorders[2].Disposed);
        await capture.DisposeAsync();
        Assert.True(recorders[2].Disposed);
    }

    [Fact]
    public async Task ExactZeroRunExcludesPreRollTracksSinceTakeStartAndResetsOnSound()
    {
        var recorder = new FakeRecorderSession();
        var clock = new Deterministic.ManualClock();
        await using var capture = new WasapiAudioCapture(new FakeRecorderFactory(recorder), TestTimeout, timeProvider: clock);
        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);

        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.Emit(0.4f);
        await capture.StopAsync();

        // Fill 8,000 samples (500 ms) of pre-roll with zeros AND verify it does not shift live exact-zero count or SinceTakeStart.
        recorder.EmitSilentFloats(8_000);
        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        Assert.Equal(new ExactZeroRun(0, SinceTakeStart: true), capture.CurrentExactZeroRun);

        recorder.EmitSilentFloats(80_000);
        Assert.Equal(new ExactZeroRun(80_000, SinceTakeStart: true), capture.CurrentExactZeroRun);
        Assert.Equal(MicSilenceLevel.None, SilentMicNoticePolicy.Evaluate(capture.CurrentExactZeroRun, warningsEnabled: true));

        recorder.Emit(new float[16_000]);
        Assert.Equal(new ExactZeroRun(96_000, SinceTakeStart: true), capture.CurrentExactZeroRun);
        Assert.Equal(MicSilenceLevel.Calm, SilentMicNoticePolicy.Evaluate(capture.CurrentExactZeroRun, warningsEnabled: true));

        // Sound arrives mid-take -> resets count and SinceTakeStart becomes false.
        recorder.Emit(0f, 0.25f, 0f, 0f);
        Assert.Equal(new ExactZeroRun(2, SinceTakeStart: false), capture.CurrentExactZeroRun);

        recorder.Emit(new float[79_998]);
        Assert.Equal(new ExactZeroRun(80_000, SinceTakeStart: false), capture.CurrentExactZeroRun);
        Assert.Equal(MicSilenceLevel.Calm, SilentMicNoticePolicy.Evaluate(capture.CurrentExactZeroRun, warningsEnabled: true));

        recorder.Emit(new float[160_000]);
        Assert.Equal(new ExactZeroRun(240_000, SinceTakeStart: false), capture.CurrentExactZeroRun);
        Assert.Equal(MicSilenceLevel.Strong, SilentMicNoticePolicy.Evaluate(capture.CurrentExactZeroRun, warningsEnabled: true));

        await capture.StopAsync();
        Assert.False(capture.LastCaptureWasWholeZero);
        Assert.Null(capture.CurrentExactZeroRun);
    }

    [Fact]
    public async Task LastCaptureWasWholeZeroTracksPostPreRollSamplesOnly()
    {
        var recorder = new FakeRecorderSession();
        await using var capture = new WasapiAudioCapture(new FakeRecorderFactory(recorder), TestTimeout);
        await capture.SetWarmPolicyAsync(MicrophoneWarmPolicy.Seconds30);

        // 0 packets -> false.
        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        await capture.StopAsync();
        Assert.False(capture.LastCaptureWasWholeZero);

        // Pre-roll + 0 live packets -> false.
        recorder.Emit(Enumerable.Repeat(0.2f, 400).ToArray());
        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        await capture.StopAsync();
        Assert.False(capture.LastCaptureWasWholeZero);

        // Pre-roll + live exact zeros -> true.
        recorder.Emit(Enumerable.Repeat(0.2f, 400).ToArray());
        await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create()));
        recorder.EmitSilentFloats(1_600);
        await capture.StopAsync();
        Assert.True(capture.LastCaptureWasWholeZero);
    }

    private static WasapiAudioCapture CreateCapture(FakeRecorderSession recorder) =>
        new(new FakeRecorderFactory(recorder), TestTimeout);

    private sealed class FakeRecorderFactory : IAudioRecorderFactory
    {
        private readonly Func<AudioDeviceId?, IAudioRecorderSession>? _factory;
        private readonly Exception? _exception;

        public FakeRecorderFactory(IAudioRecorderSession recorder) => _factory = _ => recorder;

        public FakeRecorderFactory(Func<AudioDeviceId?, IAudioRecorderSession> factory) => _factory = factory;

        public FakeRecorderFactory(Exception exception) => _exception = exception;

        public int CreateCount { get; private set; }

        public List<AudioDeviceId?> RequestedDeviceIds { get; } = [];

        public Task<IAudioRecorderSession> CreateAsync(
            AudioDeviceId? deviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            RequestedDeviceIds.Add(deviceId);
            return _exception is null
                ? Task.FromResult(_factory!(deviceId))
                : Task.FromException<IAudioRecorderSession>(_exception);
        }
    }

    private sealed class FakeRecorderSession : IAudioRecorderSession
    {
        public event EventHandler<AudioRecorderData>? DataAvailable;

        public event EventHandler<AudioRecorderStopped>? Stopped;

        public bool CompleteWhenStopped { get; init; } = true;

        public bool InterruptWhenStarted { get; init; }

        public int StartCount { get; private set; }

        public bool Disposed { get; private set; }

        public void Start()
        {
            StartCount++;
            if (InterruptWhenStarted)
            {
                Interrupt();
            }
        }

        public void Stop()
        {
            if (CompleteWhenStopped)
            {
                Stopped?.Invoke(this, new AudioRecorderStopped(Exception: null));
            }
        }

        public void Emit(params float[] samples)
        {
            var bytes = new byte[samples.Length * sizeof(float)];
            for (var index = 0; index < samples.Length; index++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    bytes.AsSpan(index * sizeof(float), sizeof(float)),
                    BitConverter.SingleToInt32Bits(samples[index]));
            }

            EmitBytes(bytes);
        }

        public void EmitSilentFloats(int sampleCount) =>
            DataAvailable?.Invoke(this, new AudioRecorderData(new byte[sampleCount * sizeof(float)], IsSilent: true));

        public void EmitBytes(byte[] bytes) =>
            DataAvailable?.Invoke(this, new AudioRecorderData(bytes, IsSilent: false));

        public void Interrupt(Exception? exception = null) =>
            Stopped?.Invoke(this, new AudioRecorderStopped(exception));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
