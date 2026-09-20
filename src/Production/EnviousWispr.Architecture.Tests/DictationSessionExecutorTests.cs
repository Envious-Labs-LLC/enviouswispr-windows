using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The executor decides; the shell acts. These tests drive the real state machine with a fake microphone
/// and a fake shell that writes down every effect in the order it was asked for, so the order of effects
/// the old handler had is asserted rather than assumed.
/// </summary>
public sealed class DictationSessionExecutorTests
{
    [Fact]
    public async Task ADeniedAdmissionNeverOpensTheMicrophone()
    {
        var (executor, capture, effects, _) = Build(admission: new DictationAdmissionResult(
            DictationAdmissionStatus.LowMemory, CanStart: false, CanPersistRecovery: false));

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(0, capture.StartCount);
        Assert.Equal(["EvaluateAdmission", "ShowMemoryCritical", "ReleaseProcessingDeadline", "RecordDictationEdge"], effects.Trace);
    }

    [Fact]
    public async Task PendingRecoveryBlocksAPressBeforeAdmissionIsEvenAsked()
    {
        var (executor, capture, effects, _) = Build();
        effects.HasPendingRecovery = true;

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(0, capture.StartCount);
        Assert.Equal(["ShowRecoveredTextWaiting", "ReleaseProcessingDeadline", "RecordDictationEdge"], effects.Trace);
    }

    [Fact]
    public async Task LowDiskWarnsAndStillRecords()
    {
        var (executor, capture, effects, controller) = Build(admission: new DictationAdmissionResult(
            DictationAdmissionStatus.LowDisk, CanStart: true, CanPersistRecovery: false));
        effects.EscapeRecoveryEnabled = true;

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(1, capture.StartCount);
        Assert.Equal(DictationSessionState.Recording, controller.CurrentSession?.State);
        Assert.True(effects.EscapeRecoveryForSession, "the recording carries the setting it was started with");
        Assert.Equal(
            ["EvaluateAdmission", "ShowDiskLow", "RecordTransition:Started", "RecordingSettings", "Background:Start", "ShowTransitionStatus:Started", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.Equal(controller.CurrentSession?.Id, TracingBackgroundWork.StartedSession);
        Assert.Same(controller.CurrentSession, result.Session);
    }

    [Fact]
    public async Task AReleaseStopsTheWatchdogFirstAndFinalisesExactlyOnce()
    {
        var (executor, capture, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(0, capture.CancelCount);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.Single(effects.Finalized);
        Assert.Equal([0.2f], effects.Finalized[0].Samples.ToArray());
        Assert.Equal(DictationSessionState.Finalizing, result.Session?.State);
    }

    [Fact]
    public async Task EscapeWithRecoveryOnReleasesAndFinalisesForRecoveryOnly()
    {
        var (executor, capture, effects, _) = Build();
        effects.EscapeRecoveryEnabled = true;
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Cancelled), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(0, capture.CancelCount);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=True", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task EscapeWithRecoveryOffCancelsStopsBackgroundWorkAndResets()
    {
        var (executor, capture, effects, controller) = Build();
        effects.EscapeRecoveryEnabled = false;
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Cancelled), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(DictationSessionState.Cancelled, result.Session?.State);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(1, capture.CancelCount);
        Assert.Null(controller.CurrentSession);
        Assert.False(effects.EscapeRecoveryForSession);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:Cancelled", "Background:Stop", "ShowTransitionStatus:Cancelled", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task AReleaseWithNothingRecordingIsIgnoredAndStillShowsAStatus()
    {
        var (executor, _, effects, _) = Build();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:Ignored", "ShowTransitionStatus:Ignored", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task ATransitionThatThrowsIsRecordedAndRecoveredAsAFailure()
    {
        var (executor, capture, effects, _) = Build();
        capture.StartResultFactory = _ => throw new InvalidOperationException("synthetic microphone failure");

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["EvaluateAdmission", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task ATransitionThatTimesOutIsRecoveredAsATimeout()
    {
        var (executor, capture, effects, _) = Build();
        capture.StartResultFactory = _ => throw new OperationCanceledException("synthetic deadline");

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["EvaluateAdmission", "RecoverFailedSession:SessionTimedOut:TimedOut", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task AFinalisationThatThrowsIsRecoveredBeforeItsDeadlineIsReleased()
    {
        var (executor, _, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var recording = controller.CurrentSession;
        effects.Trace.Clear();
        effects.FinalizeThrows = true;

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(recording?.Id, result.Session?.Id);
        // THE ORDER IS THE POINT: recovery runs while the deadline is still armed, then the deadline is
        // released, then the edge - exactly as the shell always did it. Lock/suspend and shutdown cancel
        // that deadline from their own callbacks and must still find it during the recovery.
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.True(effects.DeadlineArmedDuringRecovery, "recovery ran after the deadline had already been released");
    }

    [Fact]
    public async Task WindowsLockingMidRecordingReleasesAndKeepsTheAudioAsAKeyReleaseWould()
    {
        var (executor, capture, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var recording = controller.CurrentSession;
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(Interruption(SystemLifecycleTransition.SessionLocked), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(recording?.Id, result.Session?.Id);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(0, capture.CancelCount);
        // The order the shell's lifecycle callback had: watchdog off, release, then the same
        // finalisation a key release gets, told which transition it is preserving for so the status is
        // shown after the loops have stopped and before transcription, where it always was.
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False:preserving=SessionLocked", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.Single(effects.Finalized);
    }

    [Fact]
    public async Task WindowsSuspendingWithNothingRecordingDoesNothingButRecordTheEdge()
    {
        // The shell's callback returned when there was no session; so does the command, and the
        // edge is still recorded in its finally, as it was.
        var (executor, capture, effects, _) = Build();

        var result = await executor.ExecuteAsync(Interruption(SystemLifecycleTransition.Suspending), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Ignored, result.Disposition);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(["ReleaseProcessingDeadline", "RecordDictationEdge"], effects.Trace);
    }

    [Fact]
    public async Task WindowsLockingMidFinalisationResetsSafelyAndSaysSo()
    {
        // A session that is finalising is not recording: the interruption recovers it as the shell
        // did - stops everything, aborts, resets, and says Windows interrupted it.
        var (executor, capture, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        await controller.ReleaseAsync();
        Assert.Equal(DictationSessionState.Finalizing, controller.CurrentSession?.State);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(Interruption(SystemLifecycleTransition.SessionLocked), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(["RecoverFailedSession:Cancelled:Interrupted", "ReleaseProcessingDeadline", "RecordDictationEdge"], effects.Trace);
    }

    [Fact]
    public async Task AnExpiredInterruptionSaysRecoveryIsPendingAndTouchesNothing()
    {
        // The coordinator decides that an interruption waited too long; the executor is told and
        // says so, outside the session, as the shell said it when its five-second gate wait failed.
        var (executor, capture, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        await executor.ExpireAsync(Interruption(SystemLifecycleTransition.SessionLocked));

        Assert.Equal(DictationSessionState.Recording, controller.CurrentSession?.State);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(["ShowInterruptionPending"], effects.Trace);
    }

    [Fact]
    public async Task ShutdownRunsTheShellsSessionTeardownThroughThePort()
    {
        var (executor, _, effects, _) = Build();

        await executor.ShutdownAsync();

        Assert.Equal(["TearDownSession"], effects.Trace);
    }

    [Fact]
    public async Task ADisposedDependencyWhileTheSessionIsLiveIsAnOrdinaryFailureAndIsRecovered()
    {
        // TORN DOWN IS A STATE, NOT AN EXCEPTION TYPE. A shell effect that throws ObjectDisposedException
        // while nothing has been torn down is a bug in the shell, and it gets the ordinary recovery.
        var (executor, _, effects, _) = Build();
        TracingBackgroundWork.StartThrows = new ObjectDisposedException("a status line");

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["EvaluateAdmission", "RecordTransition:Started", "RecordingSettings", "Background:Start", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task AfterTheTeardownACommandThatFailsEndsAsFailedWithoutRecovery()
    {
        // The session was torn down beside this command (the shutdown outlived both of its waits).
        // Whatever it then fails on, there is nothing to recover into: written, answered, no abort.
        var (executor, capture, effects, _) = Build();
        await executor.ShutdownAsync();
        effects.Trace.Clear();
        capture.StartResultFactory = _ => throw new InvalidOperationException("the capture is gone");

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["EvaluateAdmission", "RecordSessionFailure", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task AfterTheTeardownATimeoutThatFailsEndsAsFailedRatherThanFaultingItsTask()
    {
        var (executor, _, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var recording = controller.CurrentSession!.Id;
        await executor.ShutdownAsync();
        effects.Trace.Clear();
        TracingBackgroundWork.StopThrows = new ObjectDisposedException("the preview");

        var result = await executor.ExecuteAsync(Timeout(recording), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(["Background:Stop", "RecordDictationEdge", "RecordSessionFailure"], effects.Trace);
    }

    [Fact]
    public async Task AnInterruptionWhoseFinalisationThrowsIsRecoveredAsAnInterruptionFailure()
    {
        var (executor, _, effects, _) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();
        effects.FinalizeThrows = true;

        var result = await executor.ExecuteAsync(Interruption(SystemLifecycleTransition.SessionLocked), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False:preserving=SessionLocked", "RecordInterruptionFailure", "RecoverFailedSession:InvalidTransition:InterruptionFailed", "ReleaseProcessingDeadline", "RecordDictationEdge"],
            effects.Trace);
        Assert.True(effects.DeadlineArmedDuringRecovery, "recovery ran after the deadline had already been released");
    }

    [Fact]
    public async Task ATimeoutForTheRecordingStillInFlightAbortsResetsAndWarns()
    {
        var (executor, capture, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var recording = controller.CurrentSession!;
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(Timeout(recording.Id), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, capture.CancelCount);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(
            ["Background:Stop", "RecordRecordingTimedOut:SessionTimedOut", "ShowRecordingTimedOut", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task ATimeoutForARecordingThatHasMovedOnDoesNothing()
    {
        // The watchdog of an earlier recording fires after that recording ended and a new one began.
        var (executor, capture, effects, controller) = Build();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var first = controller.CurrentSession!.Id;
        await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Cancelled), CancellationToken.None);
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var second = controller.CurrentSession!.Id;
        effects.Trace.Clear();

        var stale = await executor.ExecuteAsync(Timeout(first), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Ignored, stale.Disposition);
        Assert.Equal(second, controller.CurrentSession?.Id);
        Assert.Equal(DictationSessionState.Recording, controller.CurrentSession?.State);
        Assert.Equal(1, capture.CancelCount);
        Assert.Equal(["RecordDictationEdge"], effects.Trace);
    }

    private static SessionCommand Interruption(SystemLifecycleTransition transition) => new(
        SessionCommandKind.Interruption,
        PushToTalkSignal.Cancelled,
        Transition: transition);

    private static SessionCommand Timeout(DictationSessionId sessionId) => new(
        SessionCommandKind.Timeout,
        PushToTalkSignal.Cancelled,
        TimedOutSession: sessionId);

    private static SessionCommand Press() => new(
        PushToTalkSignal.Pressed,
        new RecordingStartContext(new TargetWindowId(101), TextDeliveryOptions.Default));

    private static (DictationSessionExecutor Executor, FakeAudioCapture Capture, FakeEffects Effects, PushToTalkSessionController Controller) Build(
        DictationAdmissionResult? admission = null)
    {
        var capture = new FakeAudioCapture();
        var controller = new PushToTalkSessionController(
            capture,
            new FakeTargetProvider(101),
            minimumHoldDuration: TimeSpan.Zero);
        var effects = new FakeEffects
        {
            Admission = admission ?? new DictationAdmissionResult(
                DictationAdmissionStatus.Ready, CanStart: true, CanPersistRecovery: true),
        };
        return (new DictationSessionExecutor(controller, new TracingBackgroundWork(effects.Trace), effects), capture, effects, controller);
    }

    private sealed class FakeEffects : IDictationSessionEffects
    {
        public List<string> Trace { get; } = [];

        public List<CapturedAudio> Finalized { get; } = [];


        public required DictationAdmissionResult Admission { get; init; }

        public bool FinalizeThrows { get; set; }


        public bool DeadlineArmed { get; private set; }

        public bool DeadlineArmedDuringRecovery { get; private set; }

        public bool HasPendingRecovery { get; set; }

        public bool EscapeRecoveryEnabled { get; set; }

        public bool EscapeRecoveryForSession { get; set; }

        public DictationAdmissionResult EvaluateAdmission()
        {
            Trace.Add("EvaluateAdmission");
            return Admission;
        }

        public void ShowRecoveredTextWaiting() => Trace.Add("ShowRecoveredTextWaiting");

        public void ShowMemoryCritical() => Trace.Add("ShowMemoryCritical");

        public void ShowDiskLow() => Trace.Add("ShowDiskLow");

        public void RecordTransition(SessionTransitionResult result) => Trace.Add($"RecordTransition:{result.Kind}");

        public RecordingBackgroundSettings RecordingSettings()
        {
            Trace.Add("RecordingSettings");
            return new RecordingBackgroundSettings(TimeSpan.FromMinutes(5), DictationPreferences.Default);
        }

        public Task FinalizeAsync(DictationSessionId sessionId, CapturedAudio audio, bool recoveryOnly, SystemLifecycleTransition? preserving = null)
        {
            Trace.Add(preserving is { } transition ? $"Finalize:recoveryOnly={recoveryOnly}:preserving={transition}" : $"Finalize:recoveryOnly={recoveryOnly}");
            DeadlineArmed = true;
            Finalized.Add(audio);
            if (FinalizeThrows)
            {
                throw new InvalidOperationException("synthetic finalisation failure");
            }

            return Task.CompletedTask;
        }

        public void ShowTransitionStatus(SessionTransitionResult result) => Trace.Add($"ShowTransitionStatus:{result.Kind}");

        public void RecordSessionFailure() => Trace.Add("RecordSessionFailure");

        public void ReleaseProcessingDeadline()
        {
            Trace.Add("ReleaseProcessingDeadline");
            DeadlineArmed = false;
        }

        public Task RecoverFailedSessionAsync(AppError failure, SessionFailureKind kind)
        {
            Trace.Add($"RecoverFailedSession:{failure.Code}:{kind}");
            DeadlineArmedDuringRecovery = DeadlineArmed;
            return Task.CompletedTask;
        }

        public Task RecordDictationEdgeAsync()
        {
            Trace.Add("RecordDictationEdge");
            return Task.CompletedTask;
        }

        public void RecordInterruptionFailure() => Trace.Add("RecordInterruptionFailure");

        public void ShowInterruptionPending() => Trace.Add("ShowInterruptionPending");

        public Task TearDownSessionAsync()
        {
            Trace.Add("TearDownSession");
            return Task.CompletedTask;
        }

        public void RecordRecordingTimedOut(AppError failure) => Trace.Add($"RecordRecordingTimedOut:{failure.Code}");

        public void ShowRecordingTimedOut() => Trace.Add("ShowRecordingTimedOut");
    }

    /// <summary>The executor's seam for the four things beside a recording: traced, and made to fail on request.</summary>
    private sealed class TracingBackgroundWork(List<string> trace) : ISessionBackgroundWork
    {
        public static DictationSessionId? StartedSession { get; private set; }

        public static Exception? StartThrows { get; set; }

        public static Exception? StopThrows { get; set; }

        public Task StartAsync(DictationSessionId sessionId, RecordingBackgroundSettings settings)
        {
            trace.Add("Background:Start");
            StartedSession = sessionId;
            var failure = StartThrows;
            StartThrows = null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task StopAsync()
        {
            trace.Add("Background:Stop");
            var failure = StopThrows;
            StopThrows = null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task StopWatchdogAsync()
        {
            trace.Add("Background:StopWatchdog");
            return Task.CompletedTask;
        }
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

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int CancelCount { get; private set; }

        public Func<AudioCaptureRequest, AudioOperationResult>? StartResultFactory { get; set; }

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            StartCount++;
            _sessionId = request.SessionId;
            var result = StartResultFactory?.Invoke(request) ?? new AudioOperationResult(Succeeded: true);
            IsCapturing = result.Succeeded;
            return Task.FromResult(result);
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCount++;
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
