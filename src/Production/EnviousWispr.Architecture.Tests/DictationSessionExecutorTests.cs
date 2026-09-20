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
        Assert.Equal(["EvaluateAdmission", "ShowMemoryCritical", "RecordDictationEdge"], effects.Trace);
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
        Assert.Equal(["ShowRecoveredTextWaiting", "RecordDictationEdge"], effects.Trace);
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
            ["EvaluateAdmission", "ShowDiskLow", "RecordTransition:Started", "RecordingSettings", "Background:Start", "ShowTransitionStatus:Started", "RecordDictationEdge"],
            effects.Trace);
        Assert.Equal(controller.CurrentSession?.Id, TracingBackgroundWork.StartedSession);
        Assert.Same(controller.CurrentSession, result.Session);
    }

    [Fact]
    public async Task AReleaseStopsTheWatchdogFirstAndFinalisesExactlyOnce()
    {
        var (executor, capture, effects, controller, finalization) = BuildWithFinalization();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(0, capture.CancelCount);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False", "RecordDictationEdge"],
            effects.Trace);
        Assert.Single(finalization.Finalized);
        Assert.Equal([0.2f], finalization.Finalized[0].Samples.ToArray());
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
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=True", "RecordDictationEdge"],
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
            ["Background:StopWatchdog", "RecordTransition:Cancelled", "Background:Stop", "ShowTransitionStatus:Cancelled", "RecordDictationEdge"],
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
            ["Background:StopWatchdog", "RecordTransition:Ignored", "ShowTransitionStatus:Ignored", "RecordDictationEdge"],
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
            ["EvaluateAdmission", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "RecordDictationEdge"],
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
            ["EvaluateAdmission", "RecoverFailedSession:SessionTimedOut:TimedOut", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task AFinalisationThatThrowsIsRecoveredBeforeItsDeadlineIsReleased()
    {
        var (executor, _, effects, controller, finalization) = BuildWithFinalization();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var recording = controller.CurrentSession;
        effects.Trace.Clear();
        finalization.Throws = true;

        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(recording?.Id, result.Session?.Id);
        // THE ORDER IS THE POINT: recovery runs while the deadline is still armed, then the deadline is
        // released, then the edge - exactly as the shell always did it. Lock/suspend and shutdown cancel
        // that deadline from their own callbacks and must still find it during the recovery.
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "RecordDictationEdge"],
            effects.Trace);
        Assert.True(effects.DeadlineArmedDuringRecovery, "recovery ran after the deadline had already been released");
    }

    [Fact]
    public async Task WindowsLockingMidRecordingReleasesAndKeepsTheAudioAsAKeyReleaseWould()
    {
        var (executor, capture, effects, controller, finalization) = BuildWithFinalization();
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
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "ShowInterruptionPreserving:SessionLocked", "Finalize:recoveryOnly=False", "RecordDictationEdge"],
            effects.Trace);
        Assert.Single(finalization.Finalized);
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
        Assert.Equal(["RecordDictationEdge"], effects.Trace);
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
        Assert.Equal(["RecoverFailedSession:Cancelled:Interrupted", "RecordDictationEdge"], effects.Trace);
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
            ["EvaluateAdmission", "RecordTransition:Started", "RecordingSettings", "Background:Start", "RecordSessionFailure", "RecoverFailedSession:InvalidTransition:Failed", "RecordDictationEdge"],
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
            ["EvaluateAdmission", "RecordSessionFailure", "RecordDictationEdge"],
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
    public async Task TheProcessingDeadlineIsArmedBeforeTheBackgroundStopAndCoversIt()
    {
        // THE DEADLINE COUNTS THE STOPS. A preview worker that takes long to stop eats into the three
        // minutes the finalisation has, as it always did: the deadline is armed and published before
        // the stops, and a finalisation asked for after the deadline passed is handed a token that
        // is already cancelled, and recovered as a timeout.
        var clock = new Deterministic.ManualClock();
        var (executor, _, effects, _, finalization) = BuildWithFinalization(processingDeadline: TimeSpan.FromMinutes(3), clock: clock);
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();
        TracingBackgroundWork.Instance!.HoldStop = true;

        var release = executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);
        await TracingBackgroundWork.Instance.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(executor.IsProcessing, "the deadline is armed before the background work is stopped");
        await clock.WhenRegistered(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromMinutes(3), clock.NextDue);
        clock.Advance(TimeSpan.FromMinutes(3));
        TracingBackgroundWork.Instance.AllowStopExit.SetResult();

        var result = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.True(finalization.Token!.Value.IsCancellationRequested, "the finalisation was handed the deadline that had already passed");
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False", "RecoverFailedSession:SessionTimedOut:TimedOut", "RecordDictationEdge"],
            effects.Trace);
        Assert.False(executor.IsProcessing, "the deadline is released after the recovery");
    }

    [Fact]
    public async Task CancellingProcessingReachesAFinalisationInFlightAndAStopStillRunning()
    {
        // WINDOWS LOCKING CANCELS THE FINALISATION IN FLIGHT AT ONCE - the coordinator asks the
        // executor before it queues the interruption - and a stop still running when it does finds
        // the deadline already armed, so the token the finalisation is then handed is cancelled.
        var (executor, _, effects, _, finalization) = BuildWithFinalization();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();
        TracingBackgroundWork.Instance!.HoldStop = true;

        var release = executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);
        await TracingBackgroundWork.Instance.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        executor.CancelProcessing();
        TracingBackgroundWork.Instance.AllowStopExit.SetResult();

        var result = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.True(finalization.Token!.Value.IsCancellationRequested);
        Assert.Contains("RecoverFailedSession:SessionTimedOut:TimedOut", effects.Trace);
    }

    [Fact]
    public async Task CancellingProcessingWithNothingInFlightDoesNothing()
    {
        var (executor, _, effects, _) = Build();

        executor.CancelProcessing();

        Assert.False(executor.IsProcessing);
        Assert.Empty(effects.Trace);
    }

    [Fact]
    public async Task AnInterruptionWhoseFinalisationThrowsIsRecoveredAsAnInterruptionFailure()
    {
        var (executor, _, effects, _, finalization) = BuildWithFinalization();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();
        finalization.Throws = true;

        var result = await executor.ExecuteAsync(Interruption(SystemLifecycleTransition.SessionLocked), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "ShowInterruptionPreserving:SessionLocked", "Finalize:recoveryOnly=False", "RecordInterruptionFailure", "RecoverFailedSession:InvalidTransition:InterruptionFailed", "RecordDictationEdge"],
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
        var (executor, capture, effects, controller, _) = BuildWithFinalization(admission);
        return (executor, capture, effects, controller);
    }

    private static (DictationSessionExecutor Executor, FakeAudioCapture Capture, FakeEffects Effects, PushToTalkSessionController Controller, FakeFinalization Finalization) BuildWithFinalization(
        DictationAdmissionResult? admission = null,
        TimeSpan? processingDeadline = null,
        TimeProvider? clock = null)
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
        var finalization = new FakeFinalization(effects.Trace);
        var executor = new DictationSessionExecutor(
            controller,
            new TracingBackgroundWork(effects.Trace),
            finalization,
            effects,
            processingDeadline,
            clock);
        effects.IsProcessing = () => executor.IsProcessing;
        return (executor, capture, effects, controller, finalization);
    }

    private sealed class FakeEffects : IDictationSessionEffects
    {
        public List<string> Trace { get; } = [];


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
            return new RecordingBackgroundSettings(TimeSpan.FromMinutes(5), () => DictationPreferences.Default);
        }

        public void ShowInterruptionPreserving(SystemLifecycleTransition transition) => Trace.Add($"ShowInterruptionPreserving:{transition}");

        public void ShowTransitionStatus(SessionTransitionResult result) => Trace.Add($"ShowTransitionStatus:{result.Kind}");

        public void RecordSessionFailure() => Trace.Add("RecordSessionFailure");

        /// <summary>Whether the executor still holds the command's processing deadline; read at recovery time.</summary>
        public Func<bool>? IsProcessing { get; set; }

        public Task RecoverFailedSessionAsync(AppError failure, SessionFailureKind kind)
        {
            Trace.Add($"RecoverFailedSession:{failure.Code}:{kind}");
            DeadlineArmedDuringRecovery = IsProcessing?.Invoke() == true;
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

    /// <summary>The executor's seam for the record-to-deliver path: traced, holdable, and made to fail on request.</summary>
    private sealed class FakeFinalization(List<string> trace) : ISessionFinalization
    {
        public List<CapturedAudio> Finalized { get; } = [];

        public bool Throws { get; set; }

        public bool Hold { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken? Token { get; private set; }

        public async Task<FinalizationReport> RunAsync(DictationSessionId sessionId, CapturedAudio audio, bool recoveryOnly, CancellationToken cancellationToken)
        {
            trace.Add($"Finalize:recoveryOnly={recoveryOnly}");
            Finalized.Add(audio);
            Token = cancellationToken;
            if (Throws)
            {
                throw new InvalidOperationException("synthetic finalisation failure");
            }

            if (Hold)
            {
                Entered.TrySetResult();
                await AllowExit.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new FinalizationReport(FinalizationOutcome.Held);
        }
    }

    /// <summary>The executor's seam for the four things beside a recording: traced, and made to fail on request.</summary>
    private sealed class TracingBackgroundWork : ISessionBackgroundWork
    {
        private readonly List<string> trace;

        public TracingBackgroundWork(List<string> trace)
        {
            this.trace = trace;
            Instance = this;
        }

        /// <summary>The one built last; the tests run one executor at a time.</summary>
        public static TracingBackgroundWork? Instance { get; private set; }

        public static DictationSessionId? StartedSession { get; private set; }

        public static Exception? StartThrows { get; set; }

        public static Exception? StopThrows { get; set; }

        public bool HoldStop { get; set; }

        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStopExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(DictationSessionId sessionId, RecordingBackgroundSettings settings)
        {
            trace.Add("Background:Start");
            StartedSession = sessionId;
            var failure = StartThrows;
            StartThrows = null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public async Task StopAsync()
        {
            trace.Add("Background:Stop");
            var failure = StopThrows;
            StopThrows = null;
            if (failure is not null)
            {
                throw failure;
            }

            if (HoldStop)
            {
                StopEntered.TrySetResult();
                await AllowStopExit.Task;
            }
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
