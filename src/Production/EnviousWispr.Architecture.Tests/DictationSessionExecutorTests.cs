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
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CriticalMemoryNeverOpensCapture()
    {
        var (executor, capture, effects, _, world) = BuildWithFinalization(resources: LowMemory);

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(0, capture.StartCount);
        Assert.Equal(["RecordResourcePressure:LowMemory", "ShowMemoryCritical", "RecordDictationEdge"], effects.Trace);
        Assert.False(world.Persistence.CanPersistRecovery, "low memory with low disk: the copy is refused too");
    }

    [Fact]
    public async Task PendingRecoveryBlocksAPressBeforeAdmissionIsEvenAsked()
    {
        var (executor, capture, effects, _, world) = BuildWithFinalization();
        world.Persistence.HasPendingRecovery = true;

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(result.Session);
        Assert.Equal(0, capture.StartCount);
        Assert.True(world.Resources.Probes == 0, "the machine is not even asked");
        Assert.Equal(["ShowRecoveredTextWaiting", "RecordDictationEdge"], effects.Trace);
    }

    [Fact]
    public async Task LowDiskDisablesRecoveryPersistenceOnly()
    {
        var (executor, capture, effects, controller, world) = BuildWithFinalization(resources: LowDisk);
        effects.EscapeRecoveryEnabled = true;
        bool? persistenceAllowedAsCaptureOpened = null;
        capture.StartResultFactory = _ =>
        {
            persistenceAllowedAsCaptureOpened = world.Persistence.CanPersistRecovery;
            return new AudioOperationResult(Succeeded: true);
        };

        var result = await executor.ExecuteAsync(Press(), CancellationToken.None);

        Assert.Equal(1, capture.StartCount);
        Assert.Equal(DictationSessionState.Recording, controller.CurrentSession?.State);
        Assert.True(executor.EscapeRecoveryForSession, "the recording carries the setting it was started with");
        Assert.False(persistenceAllowedAsCaptureOpened, "the persistence owner is told before the microphone opens");
        Assert.False(world.Persistence.CanPersistRecovery);
        Assert.Equal(
            ["RecordResourcePressure:LowDiskSpace", "ShowDiskLow", "RecordTransition:Started", "RecordingSettings", "Background:Start", "ShowTransitionStatus:Started", "RecordDictationEdge"],
            effects.Trace);
        Assert.Equal(controller.CurrentSession?.Id, TracingBackgroundWork.StartedSession);
        Assert.Same(controller.CurrentSession, result.Session);
    }

    [Fact]
    public async Task AReleaseStopsTheWatchdogFirstAndFinalisesExactlyOnce()
    {
        var (executor, capture, effects, controller, world) = BuildWithFinalization();
        var finalization = world.Finalization;
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
        Assert.False(executor.EscapeRecoveryForSession);
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
            ["RecordSessionFailure", "Background:StopWatchdog", "Background:Stop", "RecordSessionRecovered:InvalidTransition", "ShowPendingRecovery", "ShowSessionRecovered:Failed", "RecordDictationEdge"],
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
            ["Background:StopWatchdog", "Background:Stop", "RecordSessionRecovered:SessionTimedOut", "ShowPendingRecovery", "ShowSessionRecovered:TimedOut", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task RecoveryStopsBackgroundThenAbortsAndResetsOnce()
    {
        // A FINALISATION THAT FAILS LEAVES A SESSION IN FINALIZING. The recovery stops the background
        // work first, then aborts and resets the controller exactly once, then records and shows the
        // recovery; nothing is left for a second pass to find.
        var (executor, capture, effects, controller, world) = BuildWithFinalization();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        var recording = controller.CurrentSession;
        effects.Trace.Clear();
        world.Finalization.Throws = true;
        var states = new List<DictationSessionState>();
        controller.SessionChanged += (_, snapshot) => states.Add(snapshot.State);
        // The first stop is the finalisation's own; the recovery's is the second, and it is held so
        // that what the controller looks like while the background work is still stopping is visible.
        TracingBackgroundWork.Instance!.HoldStop = true;
        TracingBackgroundWork.Instance.HoldFromCall = 2;

        var release = executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);
        await TracingBackgroundWork.Instance.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DictationSessionState.Finalizing, controller.CurrentSession?.State);
        Assert.Equal([DictationSessionState.Finalizing], states);
        Assert.DoesNotContain(effects.Trace, entry => entry.StartsWith("RecordSessionRecovered:", StringComparison.Ordinal));

        TracingBackgroundWork.Instance.AllowStopExit.SetResult();
        var result = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(recording?.Id, result.Session?.Id);
        // Finalizing on the release, Failed on the abort, then the reset - one abort, one reset, both
        // after the held stop was let go.
        Assert.Equal([DictationSessionState.Finalizing, DictationSessionState.Failed], states);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(0, capture.CancelCount);
        var recoveryStopped = effects.Trace.LastIndexOf("Background:Stop");
        var recovered = effects.Trace.IndexOf("RecordSessionRecovered:InvalidTransition");
        Assert.True(recoveryStopped > effects.Trace.IndexOf("Finalize:recoveryOnly=False"), "the recovery's stop follows the failed finalisation");
        Assert.True(recovered > recoveryStopped, "the recovery is recorded only after the background work is stopped");
        Assert.Equal(1, effects.Trace.Count(entry => entry.StartsWith("RecordSessionRecovered:", StringComparison.Ordinal)));
        Assert.Equal(1, effects.Trace.Count(entry => entry == "ShowPendingRecovery"));
        Assert.Equal(["RecordSessionRecovered:InvalidTransition", "ShowPendingRecovery", "ShowSessionRecovered:Failed", "RecordDictationEdge"], effects.Trace[recovered..]);
    }

    [Fact]
    public async Task AFinalisationThatThrowsIsRecoveredBeforeItsDeadlineIsReleased()
    {
        var (executor, _, effects, controller, world) = BuildWithFinalization();
        var finalization = world.Finalization;
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
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False", "RecordSessionFailure", "Background:StopWatchdog", "Background:Stop", "RecordSessionRecovered:InvalidTransition", "ShowPendingRecovery", "ShowSessionRecovered:Failed", "RecordDictationEdge"],
            effects.Trace);
        Assert.True(effects.DeadlineArmedDuringRecovery, "recovery ran after the deadline had already been released");
    }

    [Fact]
    public async Task WindowsLockingMidRecordingReleasesAndKeepsTheAudioAsAKeyReleaseWould()
    {
        var (executor, capture, effects, controller, world) = BuildWithFinalization();
        var finalization = world.Finalization;
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
    public async Task AnInterruptionWhoseReleaseFailsLetsGoOfTheEscapeSettingBeforeRecovering()
    {
        // ESCAPE RECOVERY WAS ON WHEN THE RECORDING STARTED. Windows locks, the microphone hands back an
        // error and no audio, so the release fails and nothing is finalised. The shell cleared the
        // setting at the transition; so must the executor, or the next recording inherits it.
        var (executor, capture, effects, controller) = Build();
        effects.EscapeRecoveryEnabled = true;
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        Assert.True(executor.EscapeRecoveryForSession);
        effects.Trace.Clear();
        capture.StopAudioFactory = id => new CapturedAudio(
            id,
            ReadOnlyMemory<float>.Empty,
            SampleRate: 16_000,
            Channels: 1,
            AudioCaptureOutcome.Interrupted,
            new AppError(AppErrorCode.AudioDeviceLost, AppErrorStage.AudioCapture, CanRetry: true));
        bool? escapeAsRecoveryRan = null;
        effects.OnSessionRecovered = () => escapeAsRecoveryRan = executor.EscapeRecoveryForSession;

        var result = await executor.ExecuteAsync(Interruption(SystemLifecycleTransition.SessionLocked), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(controller.CurrentSession);
        Assert.False(executor.EscapeRecoveryForSession);
        Assert.False(escapeAsRecoveryRan, "the setting was still held when the recovery ran");
        Assert.Equal("RecordTransition:Failed", effects.Trace[1]);
        Assert.Contains("RecordSessionRecovered:Cancelled", effects.Trace);
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
        Assert.Equal(["Background:StopWatchdog", "Background:Stop", "RecordSessionRecovered:Cancelled", "ShowPendingRecovery", "ShowSessionRecovered:Interrupted", "RecordDictationEdge"], effects.Trace);
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
    public async Task TheTeardownHandsOneDeadlineDownAsWhatIsLeftOfItAndJoinsTheShellsTeardownUnderTheRest()
    {
        // ONE DEADLINE, NOT ONE PER OWNER. The watchdog's stop takes a second, the background's stop
        // takes another; each was handed what was left when it began, and the shell's teardown after
        // them is joined under the remainder. The report says every owner and the shell finished.
        var clock = new Deterministic.ManualClock();
        var (executor, _, effects, _, _) = BuildWithFinalization(clock: clock);
        var background = TracingBackgroundWork.Instance!;
        background.SpendOnStop = () => clock.Advance(TimeSpan.FromSeconds(1));

        var report = await executor.TearDownAsync(TimeSpan.FromSeconds(4)).WaitAsync(Patience);

        Assert.Equal(
            ["Background:StopWatchdog:4s", "Background:StopWatchdog", "Background:Stop:3s", "Background:Stop", "TearDownSession"],
            effects.Trace);
        Assert.Equal(StopOutcome.Completed, report.Watchdog);
        Assert.True(report.Background.Completed);
        Assert.Equal(StopOutcome.Completed, report.Shell);
        Assert.True(report.Completed);
    }

    [Fact]
    public async Task TheShellsTeardownIsNotRunBehindAnOwnerThatHadNotFinished()
    {
        // AN OWNER STILL RUNNING STILL USES WHAT THE SHELL WOULD DISPOSE. The preview outlived its
        // deadline: the capture and the controller are its, the shell's teardown is not run, and the
        // report says which owner and that the shell did not run - the teardown is not complete.
        var (executor, _, effects, _) = Build();
        TracingBackgroundWork.Instance!.BoundedStopReport = new BackgroundStopReport(
            StopOutcome.Completed, StopOutcome.Completed, StopOutcome.StillRunning);

        var report = await executor.TearDownAsync(TimeSpan.FromSeconds(4)).WaitAsync(Patience);

        Assert.DoesNotContain("TearDownSession", effects.Trace);
        Assert.Equal(StopOutcome.StillRunning, report.Background.Preview);
        Assert.Null(report.Shell);
        Assert.False(report.Completed);
    }

    [Fact]
    public async Task ATeardownGivenNothingLeftStillStopsAndObservesEveryOwnerAndWaitsForNone()
    {
        // ZERO IS A DEADLINE, NOT A REFUSAL. The budget the shutdown had is spent; the teardown is
        // still asked for, so every owner is cancelled and looked at - a finished one reports
        // finished - and the shell's teardown is issued and observed at once: a held one is reported
        // still running rather than waited for, on a clock nobody advances.
        var clock = new Deterministic.ManualClock();
        var (executor, _, effects, _, _) = BuildWithFinalization(clock: clock);
        effects.HoldTearDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var report = await executor.TearDownAsync(TimeSpan.Zero).WaitAsync(Patience);

        Assert.Equal(
            ["Background:StopWatchdog:0s", "Background:StopWatchdog", "Background:Stop:0s", "Background:Stop", "TearDownSession"],
            effects.Trace);
        Assert.Equal(StopOutcome.Completed, report.Watchdog);
        Assert.True(report.Background.Completed);
        Assert.Equal(StopOutcome.StillRunning, report.Shell);
        Assert.False(report.Completed);
        effects.HoldTearDown.SetResult();
    }

    [Fact]
    public async Task AShellTeardownThatOutlivesWhatIsLeftIsReportedNotWaitedFor()
    {
        // THE SHELL'S TEARDOWN IS UNDER THE SAME DEADLINE. Two seconds of four are left when it is
        // issued; it is still held when they pass; the report says so and the teardown returns.
        var clock = new Deterministic.ManualClock();
        var (executor, _, effects, _, _) = BuildWithFinalization(clock: clock);
        TracingBackgroundWork.Instance!.SpendOnStop = () => clock.Advance(TimeSpan.FromSeconds(1));
        effects.HoldTearDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var teardown = executor.TearDownAsync(TimeSpan.FromSeconds(4));
        await clock.WhenRegistered(1).WaitAsync(Patience);
        Assert.False(teardown.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));

        var report = await teardown.WaitAsync(Patience);
        Assert.Equal(StopOutcome.StillRunning, report.Shell);
        Assert.False(report.Completed);
        Assert.Contains("TearDownSession", effects.Trace);
        effects.HoldTearDown.SetResult();
    }

    [Fact]
    public async Task AStaleGenerationCancelsNothingAndTheCurrentOneCancelsTheFinalisation()
    {
        // THE GENERATION IS THE FINALISATION'S OWN SOURCE. A cancel naming a generation that is no
        // longer in flight does nothing to the one that is; a cancel naming the current one cancels
        // it - what the coordinator's interruption relies on to cut only what it was queued behind.
        var (executor, _, _, _, world) = BuildWithFinalization();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        Assert.Null(executor.ProcessingGeneration);
        world.Finalization.Hold = true;
        var release = executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);
        await world.Finalization.Entered.Task.WaitAsync(Patience);
        var current = executor.ProcessingGeneration;
        Assert.NotNull(current);

        executor.CancelProcessing(new object());
        Assert.False(world.Finalization.Token!.Value.IsCancellationRequested, "a stale generation cancelled the finalisation in flight");

        executor.CancelProcessing(current);
        Assert.True(world.Finalization.Token.Value.IsCancellationRequested);
        world.Finalization.AllowExit.SetResult();
        await release.WaitAsync(Patience);
    }

    [Fact]
    public async Task AfterAdmissionClosedAFinalisationKeepsItsWordsAndDeliversNothing()
    {
        // THE APP IS LEAVING. A finalisation that starts after admission closed runs for recovery only -
        // the words are kept, nothing is pasted into whatever is in front - and the runner is told
        // that a delivery not yet issued is not to be issued.
        var (executor, _, effects, _, world) = BuildWithFinalization();
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();

        executor.Close();
        var result = await executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.True(world.Finalization.DeliveryClosed, "the runner was told delivery is closed");
        Assert.Contains("Finalize:recoveryOnly=True", effects.Trace);
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
            ["RecordTransition:Started", "RecordingSettings", "Background:Start", "RecordSessionFailure", "Background:StopWatchdog", "Background:Stop", "RecordSessionRecovered:InvalidTransition", "ShowPendingRecovery", "ShowSessionRecovered:Failed", "RecordDictationEdge"],
            effects.Trace);
    }

    [Fact]
    public async Task TheProcessingDeadlineIsArmedBeforeTheBackgroundStopAndCoversIt()
    {
        // THE DEADLINE COUNTS THE STOPS. A preview worker that takes long to stop eats into the three
        // minutes the finalisation has, as it always did: the deadline is armed and published before
        // the stops, and a finalisation asked for after the deadline passed is handed a token that
        // is already cancelled, and recovered as a timeout.
        var clock = new Deterministic.ManualClock();
        var (executor, _, effects, _, world) = BuildWithFinalization(processingDeadline: TimeSpan.FromMinutes(3), clock: clock);
        var finalization = world.Finalization;
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
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "Finalize:recoveryOnly=False", "Background:StopWatchdog", "Background:Stop", "RecordSessionRecovered:SessionTimedOut", "ShowPendingRecovery", "ShowSessionRecovered:TimedOut", "RecordDictationEdge"],
            effects.Trace);
        Assert.False(executor.IsProcessing, "the deadline is released after the recovery");
    }

    [Fact]
    public async Task CancellingProcessingReachesAFinalisationInFlightAndAStopStillRunning()
    {
        // WINDOWS LOCKING CANCELS THE FINALISATION IN FLIGHT AT ONCE - the coordinator asks the
        // executor before it queues the interruption - and a stop still running when it does finds
        // the deadline already armed, so the token the finalisation is then handed is cancelled.
        var (executor, _, effects, _, world) = BuildWithFinalization();
        var finalization = world.Finalization;
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
        Assert.Contains("RecordSessionRecovered:SessionTimedOut", effects.Trace);
        Assert.Contains("ShowSessionRecovered:TimedOut", effects.Trace);
    }

    [Fact]
    public async Task TheDeadlineIsHeldThroughARecoveryStillRunningAndReleasedWhenItEnds()
    {
        // A FINALISATION THAT FAILED IS BEING RECOVERED, and the recovery is itself waiting on the
        // background work. A lock arriving now still finds the deadline to cancel; the deadline is
        // held for the whole of the recovery and let go only when the command ends.
        var (executor, _, effects, _, world) = BuildWithFinalization();
        var finalization = world.Finalization;
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();
        finalization.Throws = true;
        // The first stop is the finalisation's own; the second is the recovery's, and that is the
        // one held.
        TracingBackgroundWork.Instance!.HoldStop = true;
        TracingBackgroundWork.Instance.HoldFromCall = 2;
        var release = executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);
        await finalization.Failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await TracingBackgroundWork.Instance.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(executor.IsProcessing, "the deadline is still held while the recovery runs");
        executor.CancelProcessing();
        Assert.True(finalization.Token!.Value.IsCancellationRequested);
        Assert.True(executor.IsProcessing, "cancelling does not release; the command's end does");

        TracingBackgroundWork.Instance.AllowStopExit.SetResult();
        var result = await release.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.False(executor.IsProcessing);
        Assert.Contains("RecordSessionRecovered:InvalidTransition", effects.Trace);
        Assert.Contains("ShowSessionRecovered:Failed", effects.Trace);
    }

    [Fact]
    public async Task ACancelRacingTheFinalisationsCompletionEndsCleanlyEitherWay()
    {
        // THE TWO ENDINGS RACE AND NEITHER MAY THROW OUT: the finalisation is held; it is released
        // and cancelled in the same breath. Whichever wins, the command ends with the deadline
        // released and no disposal exception escaping the executor.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var (executor, _, effects, _, world) = BuildWithFinalization();
        var finalization = world.Finalization;
            await executor.ExecuteAsync(Press(), CancellationToken.None);
            finalization.Hold = true;
            var release = executor.ExecuteAsync(new SessionCommand(PushToTalkSignal.Released), CancellationToken.None);
            await finalization.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var cancel = Task.Run(executor.CancelProcessing);
            finalization.AllowExit.SetResult();
            await cancel;
            var result = await release.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains(result.Disposition, new[] { SessionCommandDisposition.Applied, SessionCommandDisposition.Failed });
            Assert.False(executor.IsProcessing);
            executor.CancelProcessing();
            Assert.Contains("RecordDictationEdge", effects.Trace);
        }
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
        var (executor, _, effects, _, world) = BuildWithFinalization();
        var finalization = world.Finalization;
        await executor.ExecuteAsync(Press(), CancellationToken.None);
        effects.Trace.Clear();
        finalization.Throws = true;

        var result = await executor.ExecuteAsync(Interruption(SystemLifecycleTransition.SessionLocked), CancellationToken.None);

        Assert.Equal(SessionCommandDisposition.Failed, result.Disposition);
        Assert.Equal(
            ["Background:StopWatchdog", "RecordTransition:FinalizeReady", "Background:Stop", "ShowInterruptionPreserving:SessionLocked", "Finalize:recoveryOnly=False", "RecordInterruptionFailure", "Background:StopWatchdog", "Background:Stop", "RecordSessionRecovered:InvalidTransition", "ShowPendingRecovery", "ShowSessionRecovered:InterruptionFailed", "RecordDictationEdge"],
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

    private static readonly SystemResourceSnapshot Healthy = new(
        AvailableDiskBytes: 10L * 1024 * 1024 * 1024,
        AvailablePhysicalMemoryBytes: 8UL * 1024 * 1024 * 1024,
        MemoryLoadPercent: 40);

    private static readonly SystemResourceSnapshot LowMemory = Healthy with
    {
        AvailablePhysicalMemoryBytes = SystemResourceAdmissionPolicy.MinimumDictationMemoryBytes - 1,
        AvailableDiskBytes = SystemResourceAdmissionPolicy.MinimumRecoveryDiskBytes - 1,
    };

    private static readonly SystemResourceSnapshot LowDisk = Healthy with
    {
        AvailableDiskBytes = SystemResourceAdmissionPolicy.MinimumRecoveryDiskBytes - 1,
    };

    private static (DictationSessionExecutor Executor, FakeAudioCapture Capture, FakeEffects Effects, PushToTalkSessionController Controller) Build()
    {
        var (executor, capture, effects, controller, _) = BuildWithFinalization();
        return (executor, capture, effects, controller);
    }

    private static (DictationSessionExecutor Executor, FakeAudioCapture Capture, FakeEffects Effects, PushToTalkSessionController Controller, ExecutorWorld World) BuildWithFinalization(
        SystemResourceSnapshot? resources = null,
        TimeSpan? processingDeadline = null,
        TimeProvider? clock = null)
    {
        var capture = new FakeAudioCapture();
        var controller = new PushToTalkSessionController(
            capture,
            new FakeTargetProvider(101),
            minimumHoldDuration: TimeSpan.Zero);
        var effects = new FakeEffects();
        var finalization = new FakeFinalization(effects.Trace);
        var persistence = new FakeRecoveryState(effects.Trace);
        var probe = new FakeResources(resources ?? Healthy);
        var executor = new DictationSessionExecutor(
            controller,
            new TracingBackgroundWork(effects.Trace),
            finalization,
            persistence,
            probe,
            effects,
            processingDeadline,
            clock);
        effects.IsProcessing = () => executor.IsProcessing;
        return (executor, capture, effects, controller, new ExecutorWorld(finalization, persistence, probe));
    }

    /// <summary>The executor's other collaborators, for the tests that read or hold them.</summary>
    private sealed record ExecutorWorld(FakeFinalization Finalization, FakeRecoveryState Persistence, FakeResources Resources)
    {
        // The finalization is also reachable as the fifth tuple item for the older tests' shape.
        public static implicit operator FakeFinalization(ExecutorWorld world) => world.Finalization;
    }

    private sealed class FakeRecoveryState(List<string> trace) : ISessionRecoveryState
    {
        public bool HasPendingRecovery { get; set; }

        public bool CanPersistRecovery { get; set; } = true;

        public void ShowPendingRecovery() => trace.Add("ShowPendingRecovery");
    }

    private sealed class FakeResources(SystemResourceSnapshot snapshot) : ISystemResourceProbe
    {
        public int Probes { get; private set; }

        public SystemResourceSnapshot Probe()
        {
            Probes++;
            return snapshot;
        }
    }

    private sealed class FakeEffects : IDictationSessionEffects
    {
        public List<string> Trace { get; } = [];


        public bool FinalizeThrows { get; set; }


        public bool DeadlineArmed { get; private set; }

        public bool DeadlineArmedDuringRecovery { get; private set; }

        public bool EscapeRecoveryEnabled { get; set; }

        public void RecordResourcePressure(AppError? failure) => Trace.Add($"RecordResourcePressure:{failure?.Code}");

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

        /// <summary>Runs as the recovery is recorded; what the executor holds at that moment is visible to it.</summary>
        public Action? OnSessionRecovered { get; set; }

        public void RecordSessionRecovered(AppError failure)
        {
            Trace.Add($"RecordSessionRecovered:{failure.Code}");
            DeadlineArmedDuringRecovery = IsProcessing?.Invoke() == true;
            OnSessionRecovered?.Invoke();
        }

        public void ShowSessionRecovered(SessionFailureKind kind) => Trace.Add($"ShowSessionRecovered:{kind}");

        public Task RecordDictationEdgeAsync()
        {
            Trace.Add("RecordDictationEdge");
            return Task.CompletedTask;
        }

        public void RecordInterruptionFailure() => Trace.Add("RecordInterruptionFailure");

        public void ShowInterruptionPending() => Trace.Add("ShowInterruptionPending");

        /// <summary>When set, the shell's teardown does not return until it is completed.</summary>
        public TaskCompletionSource? HoldTearDown { get; set; }

        public Task TearDownSessionAsync()
        {
            Trace.Add("TearDownSession");
            return HoldTearDown?.Task ?? Task.CompletedTask;
        }

        public void RecordRecordingTimedOut(AppError failure) => Trace.Add($"RecordRecordingTimedOut:{failure.Code}");

        public void ShowRecordingTimedOut() => Trace.Add("ShowRecordingTimedOut");
    }

    /// <summary>The executor's seam for the record-to-deliver path: traced, holdable, and made to fail on request.</summary>
    private sealed class FakeFinalization(List<string> trace) : ISessionFinalization
    {
        public List<CapturedAudio> Finalized { get; } = [];

        public bool DeliveryClosed { get; private set; }

        public void CloseDelivery() => DeliveryClosed = true;

        public bool Throws { get; set; }

        public bool Hold { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken? Token { get; private set; }

        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FinalizationReport> RunAsync(DictationSessionId sessionId, CapturedAudio audio, bool recoveryOnly, CancellationToken cancellationToken)
        {
            trace.Add($"Finalize:recoveryOnly={recoveryOnly}");
            Finalized.Add(audio);
            Token = cancellationToken;
            if (Throws)
            {
                Failed.TrySetResult();
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

        /// <summary>Which stop, counting from one, the hold applies to; the earlier ones pass through.</summary>
        public int HoldFromCall { get; set; } = 1;

        private int _stops;

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

            if (HoldStop && ++_stops >= HoldFromCall)
            {
                StopEntered.TrySetResult();
                await AllowStopExit.Task;
            }
        }

        public BackgroundStopReport BoundedStopReport { get; set; } = BackgroundStopReport.AllCompleted;

        public StopOutcome BoundedWatchdogOutcome { get; set; } = StopOutcome.Completed;

        /// <summary>What each bounded stop does with the clock before it answers: the time it takes.</summary>
        public Action? SpendOnStop { get; set; }

        public async Task<BackgroundStopReport> StopAsync(TimeSpan deadline)
        {
            trace.Add($"Background:Stop:{deadline.TotalSeconds}s");
            await StopAsync();
            SpendOnStop?.Invoke();
            return BoundedStopReport;
        }

        public Task StopWatchdogAsync()
        {
            trace.Add("Background:StopWatchdog");
            return Task.CompletedTask;
        }

        public async Task<StopOutcome> StopWatchdogAsync(TimeSpan deadline)
        {
            trace.Add($"Background:StopWatchdog:{deadline.TotalSeconds}s");
            await StopWatchdogAsync();
            SpendOnStop?.Invoke();
            return BoundedWatchdogOutcome;
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

        public Func<DictationSessionId, CapturedAudio>? StopAudioFactory { get; set; }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsCapturing = false;
            return Task.FromResult(
                StopAudioFactory?.Invoke(_sessionId) ??
                new CapturedAudio(_sessionId, OneSample, SampleRate: 16_000, Channels: 1));
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
