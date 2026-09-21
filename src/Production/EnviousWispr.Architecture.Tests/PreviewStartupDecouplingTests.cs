using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A press whose preview worker is still starting must not hold the recording hostage: the capture
/// must stop the moment a release, a cancel, a lock or a timeout arrives, without waiting for the
/// worker to answer, and the final transcription must not begin until the preview has been torn
/// down - including while its engine is still stopping.
/// </summary>
/// <remarks>
/// PRODUCTION WIRING, NOT A SUBSTITUTE (plan-2 step 10). These proofs once ran against a hand-built
/// finalisation that asserted the order it was asked in, and a traced background owner; what they
/// proved was that trace. They run against <see cref="ComposedSessionWorld"/> now - the real
/// executor, runner, controller and background owners - and the order is read from what the engine
/// found when it was reached (<see cref="ComposedSessionWorld.EngineSaw"/>) and from what the
/// production path did after: the words delivered, kept, or never transcribed.
/// </remarks>
public sealed class PreviewStartupDecouplingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReleaseDuringPreviewStartupCompletesRealFinalization()
    {
        // THE RELEASE DOES NOT WAIT FOR THE WORKER. The capture has stopped while the worker's start
        // is still held; the engine has not been asked. The worker is let out of its start and held
        // in its stop; the engine is still not asked. Only once the preview is torn down does the
        // real finalisation run: the engine is reached with the capture stopped and the preview gone,
        // and the words go through the production runner to the delivery route and the history.
        var world = await ComposedSessionWorld.StartRecordingWithPreviewStartupHeldAsync("hello world");

        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Capture.Stopped.Task.WaitAsync(Patience);

        Assert.False(world.Capture.IsCapturing);
        Assert.False(release.IsCompleted);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Equal(0, world.PreviewEngine.Stops);

        await world.PreviewEngine.StartCancellationObserved.Task.WaitAsync(Patience);
        world.PreviewEngine.HoldStop = true;
        world.PreviewEngine.AllowStartExit.SetResult();
        await world.PreviewEngine.StopEntered.Task.WaitAsync(Patience);
        Assert.False(release.IsCompleted);
        Assert.Equal(0, world.Engine.Calls);

        world.PreviewEngine.AllowStopExit.SetResult();
        var result = await release.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal([(false, false, false)], world.EngineSaw);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(1, world.PreviewEngine.Stops);
        Assert.Equal(0, world.PreviewEngine.Passes);
        Assert.Contains(AppEventCode.LivePreviewStartupCancelled, world.Log.Events);
        Assert.Equal("hello world", world.Delivery.Requests.Single().Text.Text);
        Assert.Equal("hello world", world.HistoryStore.Added.Single().Text);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task ACancelDuringPreviewStartupDropsTheCaptureBeforeTheWorkerAnswersAndNeverTranscribes()
    {
        var world = await ComposedSessionWorld.StartRecordingWithPreviewStartupHeldAsync();

        var cancel = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        await world.Capture.Cancelled.Task.WaitAsync(Patience);

        Assert.False(world.Capture.IsCapturing);
        await world.PreviewEngine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(cancel.IsCompleted);
        Assert.Equal(0, world.PreviewEngine.Stops);

        world.PreviewEngine.AllowStartExit.SetResult();
        var result = await cancel.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Empty(world.EngineSaw);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Equal(1, world.PreviewEngine.Stops);
        Assert.Equal(0, world.PreviewEngine.Passes);
        Assert.Contains(AppEventCode.LivePreviewStartupCancelled, world.Log.Events);
        Assert.Empty(world.Delivery.Requests);
        Assert.Null(world.Controller.CurrentSession);
    }

    [Fact]
    public async Task AnEscapeRecoveryCancelDuringPreviewStartupStopsTheCaptureAndTranscribesForRecoveryOnly()
    {
        var world = await ComposedSessionWorld.StartRecordingWithPreviewStartupHeldAsync("hello world", escapeRecovery: true);

        var cancel = world.Coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        await world.Capture.Stopped.Task.WaitAsync(Patience);

        Assert.False(world.Capture.IsCapturing);
        await world.PreviewEngine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(cancel.IsCompleted);
        Assert.Equal(0, world.Engine.Calls);

        world.PreviewEngine.AllowStartExit.SetResult();
        var result = await cancel.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal([(false, false, false)], world.EngineSaw);
        Assert.Equal(1, world.PreviewEngine.Stops);
        Assert.Contains(AppEventCode.LivePreviewStartupCancelled, world.Log.Events);
        // RECOVERY ONLY: the words are kept, on Home, and never delivered.
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.True(world.Persistence.HasPendingRecovery);
    }

    [Fact]
    public async Task WindowsLockingDuringPreviewStartupStopsTheCaptureKeepsTheAudioAndFinalisesOnce()
    {
        // THE LOCK IS A COMMAND ON THE SAME QUEUE AS A KEY. It reaches the capture while the preview
        // worker is still starting, the audio is kept and finalised exactly as a release would
        // finalise it, and a release queued behind the lock finds nothing to end.
        var world = await ComposedSessionWorld.StartRecordingWithPreviewStartupHeldAsync("hello world");

        var lockCommand = world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked);
        var queuedRelease = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Capture.Stopped.Task.WaitAsync(Patience);

        Assert.False(world.Capture.IsCapturing);
        await world.PreviewEngine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(lockCommand.IsCompleted);
        Assert.Equal(0, world.Engine.Calls);

        world.PreviewEngine.AllowStartExit.SetResult();
        var locked = await lockCommand.WaitAsync(Patience);
        var released = await queuedRelease.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, locked.Disposition);
        Assert.Equal([(false, false, false)], world.EngineSaw);
        Assert.Contains(world.View.Statuses, status => status.Text.Contains("Windows locked", StringComparison.Ordinal));
        Assert.Equal(1, world.Engine.Calls);
        // The release ran after the lock and found the session finalising: the controller answers
        // Ignored and nothing is finalised twice.
        Assert.Equal(SessionCommandDisposition.Applied, released.Disposition);
        Assert.True(released.WasQueued);
        Assert.Equal(1, world.PreviewEngine.Stops);
        Assert.Contains(AppEventCode.LivePreviewStartupCancelled, world.Log.Events);
        Assert.Equal("hello world", world.Delivery.Requests.Single().Text.Text);
    }

    [Fact]
    public async Task ShutdownDuringPreviewStartupRefusesNewCommandsAndTearsDownUnderTheSessionOnceTheWorkerAnswers()
    {
        // THE SHUTDOWN, THROUGH THE PRODUCTION PROTOCOL, WITH THE PREVIEW'S WORKER STILL STARTING. The
        // press has finished, so nothing is running; admission closes and a release, a lock and a
        // suspend are refused. The executor's teardown runs under the session: the preview's stop
        // cancels the worker's start and waits for it to answer; only then are the microphone and the
        // controller disposed, and the shutdown reports clean - nothing was torn down under the
        // worker, and nothing was transcribed.
        var world = await ComposedSessionWorld.StartRecordingWithPreviewStartupHeldAsync();

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.SubmitAsync(PushToTalkSignal.Released)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.InterruptAsync(SystemLifecycleTransition.Suspending)).Disposition);
        await world.PreviewEngine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(shutdown.IsCompleted, "the teardown waits for the worker to answer its cancelled start");
        Assert.False(world.Capture.Disposed, "nothing is disposed while the worker is still inside its start");
        Assert.Equal(0, world.TearDowns);

        world.PreviewEngine.AllowStartExit.SetResult();
        var report = await shutdown.WaitAsync(Patience);

        Assert.True(report.Clean);
        Assert.True(report.Teardown!.Completed);
        Assert.Equal(1, world.TearDowns);
        Assert.True(world.Capture.Disposed, "the teardown disposed the controller, which disposed the capture, after the worker answered");
        Assert.False(world.Runtime.Preview.IsRunning);
        Assert.Equal(1, world.PreviewEngine.Stops);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Contains(AppEventCode.LivePreviewStartupCancelled, world.Log.Events);
    }

    [Fact]
    public async Task ATimeoutDuringPreviewStartupCancelsTheCaptureAndTheStartup()
    {
        // THE WATCHDOG'S TIMEOUT IS A COMMAND. If the recording is still the one that was armed, the
        // loops are stopped first - the preview's startup is cancelled - and then it is aborted:
        // capture cancelled, not stopped. The order the watchdog always had.
        var world = await ComposedSessionWorld.StartRecordingWithPreviewStartupHeldAsync();

        var timeout = world.Coordinator.TimeOutAsync(world.SessionId);
        await world.PreviewEngine.StartCancellationObserved.Task.WaitAsync(Patience);
        Assert.False(timeout.IsCompleted);
        Assert.True(world.Capture.IsCapturing, "the abort follows the stops, as it always did");

        world.PreviewEngine.AllowStartExit.SetResult();
        await world.Capture.Cancelled.Task.WaitAsync(Patience);
        var result = await timeout.WaitAsync(Patience);

        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Null(world.Controller.CurrentSession);
        Assert.False(world.Capture.Stopped.Task.IsCompleted, "the timeout cancels the take; nothing is handed on");
        Assert.Contains(world.View.Statuses, status => status.Text.Contains("timed out", StringComparison.Ordinal));
        Assert.Equal(0, world.Engine.Calls);
        Assert.Equal(1, world.PreviewEngine.Stops);
    }

    [Fact]
    public async Task ShutdownWhileTheMicrophoneIsStillOpeningRefusesTheQueuedReleaseAndTearsDownAfterThePress()
    {
        // QUIT WHILE A PRESS IS STILL OPENING THE MICROPHONE with a release already queued behind it.
        // The release is refused when its turn comes; the press finishes on its own terms; the
        // teardown runs after it, once, and nothing is transcribed.
        var world = ComposedSessionWorld.Create("hello world");
        world.LivePreviewEnabled = true;
        world.Capture.HoldStart = true;
        var press = world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Capture.StartEntered.Task.WaitAsync(Patience);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, world.TearDowns);

        world.Capture.AllowStartExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await release.WaitAsync(Patience)).Disposition);
        Assert.True((await shutdown.WaitAsync(Patience)).Clean);

        Assert.Equal(1, world.TearDowns);
        Assert.Equal(0, world.Engine.Calls);
        Assert.True(world.Capture.Disposed);
    }

    [Fact]
    public async Task ShutdownDuringFinalisationWaitsForItAndTranscribesExactlyOnce()
    {
        // QUIT WHILE A RELEASE IS TRANSCRIBING. The teardown waits for the transcription, which is
        // transcribed once and kept (delivery closed with admission); nothing is torn down under it
        // and nothing runs after. A lock arriving meanwhile is refused - and does not cut the
        // transcription short: its cancel is made only for an interruption that will be admitted.
        var world = await ComposedSessionWorld.StartRecordingWithPreviewStartupHeldAsync("hello world");
        world.PreviewEngine.AllowStartExit.SetResult();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, world.TearDowns);
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked)).Disposition);

        Assert.False(world.Engine.Token!.Value.IsCancellationRequested, "the refused lock did not cancel the transcription the shutdown waits for");
        world.Engine.Release();
        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        Assert.True((await shutdown.WaitAsync(Patience)).Clean);

        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(1, world.TearDowns);
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.True(world.Capture.Disposed, "the teardown came after the transcription, not under it");
    }
}
