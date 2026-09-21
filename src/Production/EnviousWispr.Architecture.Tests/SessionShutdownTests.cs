using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The shell's exit, seen from the session: the coordinator's shutdown protocol (plan-2 step 8)
/// against the production session - RuntimeComposition and SessionComposition over the real
/// coordinator, executor, controller, runner and persistence, fakes only at the leaves.
/// </summary>
/// <remarks>
/// PRODUCTION WIRING, NOT A SUBSTITUTE (plan-2 step 10). These proofs once ran against a hand-built
/// shell adapter and a hand-built background owner, which proved that the adapter did what the test
/// said it did. They run against <see cref="ComposedSessionWorld"/> now, so what is proved is what
/// the app's own composition does under a shutdown: which delivery is never issued, which words are
/// kept, when the microphone is closed, and what is left alone beside a command still running.
/// </remarks>
public sealed class SessionShutdownTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task QuittingDuringATranscriptionWaitsForItKeepsTheWordsAndThenClosesTheMicrophone()
    {
        // THE APP IS LEAVING, AND A DELIVERY NOT YET ISSUED IS NOT ISSUED. The transcription is
        // waited for - nothing is torn down under it - and its words go to the recovery copy rather
        // than into whatever is in front of an app that is shutting down; the teardown runs under
        // the session once the command is over.
        var world = ComposedSessionWorld.Create("hello world");
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        Assert.False(world.Capture.Disposed);
        Assert.Equal(0, world.Delivery.Deliveries);

        world.Engine.Release();
        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        var report = await shutdown.WaitAsync(Patience);

        Assert.True(report.Clean);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(["hello world"], world.RecoveryStore.Saved);
        Assert.True(world.Persistence.HasPendingRecovery, "the words wait on Home for the next launch");
        Assert.True(world.Capture.Disposed, "the teardown disposed the session controller, which disposed the capture");
        Assert.Equal(1, world.TearDowns);

        // Nothing after the teardown: a late timeout and a late lock are refused, and neither the
        // engine nor the delivery route is asked anything again.
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.TimeOutAsync(world.SessionId)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.InterruptAsync(SystemLifecycleTransition.SessionLocked)).Disposition);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(1, world.Engine.Calls);
    }

    [Fact]
    public async Task QuittingWhileTheMicrophoneIsOpeningRefusesTheQueuedReleaseAndClosesTheMicrophoneWithoutDelivering()
    {
        var world = ComposedSessionWorld.Create("hello world");
        world.Capture.HoldStart = true;
        var press = world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await world.Capture.StartEntered.Task.WaitAsync(Patience);
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);

        world.Capture.AllowStartExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);
        Assert.Equal(SessionCommandDisposition.Stopping, (await release.WaitAsync(Patience)).Disposition);
        Assert.True((await shutdown.WaitAsync(Patience)).Clean);

        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(0, world.Engine.Calls);
        Assert.True(world.Capture.Cancelled.Task.IsCompleted, "the recording that was open when the teardown came was cancelled, not delivered");
        Assert.True(world.Capture.Disposed);
        Assert.Equal(1, world.TearDowns);
    }

    [Fact]
    public async Task ATranscriptionThatFinishesInsideTheBudgetIsHeldForRecoveryAndTornDownUnderTheSession()
    {
        // ONE BUDGET. A transcription that finishes inside it ends with its words kept - delivery is
        // closed from the moment the shutdown began - and the teardown runs under the session once
        // it is over; the shutdown is clean. Crossed on the manual clock: the budget's timer is
        // registered, the engine let go before it is advanced.
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock);
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var budget = TimeSpan.FromSeconds(10);
        var registered = clock.Registered;
        var shutdown = world.Coordinator.ShutdownAsync(budget);
        await clock.WhenRegistered(registered + 1).WaitAsync(Patience);
        Assert.Equal(budget, clock.NextDue);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, world.TearDowns);
        world.Engine.Release();

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        var report = await shutdown.WaitAsync(Patience);
        Assert.True(report.Clean, "the transcription finished inside the budget");
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.True(world.Capture.Disposed);
        Assert.Equal(1, world.TearDowns);
    }

    [Fact]
    public async Task ATimeoutStillRunningWhenTheBudgetEndsIsReportedOutstandingAndNothingIsTornDown()
    {
        // THE WATCHDOG'S TIMEOUT IS STOPPING THE LOOPS when the budget runs out - the preview's
        // worker has not answered its stop. Nothing is torn down beside it: the microphone it is
        // closing is its to close, the shutdown says the command is outstanding, and when the worker
        // answers the timeout finishes on its own terms.
        var world = ComposedSessionWorld.Create("hello world");
        world.LivePreviewEnabled = true;
        world.PreviewEngine.HoldStop = true;
        await world.PressAsync();
        var timeout = world.Coordinator.TimeOutAsync(world.SessionId);
        await world.PreviewEngine.StopEntered.Task.WaitAsync(Patience);

        var report = await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience);

        Assert.Equal(ShutdownOutcome.Unclean, report.Outcome);
        Assert.True(report.CommandOutstanding);
        Assert.Null(report.Teardown);
        Assert.Equal(0, world.TearDowns);
        Assert.False(world.Capture.Disposed);

        world.PreviewEngine.AllowStopExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await timeout.WaitAsync(Patience)).Disposition);
        Assert.True(world.Capture.Cancelled.Task.IsCompleted, "the timeout closed the microphone itself");
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Equal(0, world.TearDowns);
    }

    [Fact]
    public async Task ATranscriptionThatOutlivesTheBudgetIsReportedOutstandingAndNotTornDownBeside()
    {
        // NEVER BESIDE A RESOURCE USER. A transcription that will not finish inside the budget is left
        // what it holds: the shutdown says the command is outstanding and runs no teardown. When the
        // engine answers at last the command ends on its own terms - its words kept, nothing
        // delivered into an app that is leaving - and nothing was disposed under it.
        var world = ComposedSessionWorld.Create("hello world");
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var report = await world.Coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Patience);

        Assert.Equal(ShutdownOutcome.Unclean, report.Outcome);
        Assert.True(report.CommandOutstanding);
        Assert.False(report.ExpiriesOutstanding);
        Assert.Equal(0, report.HoldsOutstanding);
        Assert.Null(report.Teardown);
        Assert.Equal(0, world.TearDowns);
        Assert.False(world.Capture.Disposed);
        Assert.Equal(0, world.Delivery.Deliveries);

        world.Engine.Release();
        var result = await release.WaitAsync(Patience);
        Assert.Equal(SessionCommandDisposition.Applied, result.Disposition);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(0, world.TearDowns);
    }

    [Fact]
    public async Task ADeliveryNotYetAdmittedWhenTheShutdownClosesIsNeverIssued()
    {
        // THE CLOSURE AND THE ADMISSION ARE ONE DECISION. The finalisation has read delivery as open
        // and moved the session to Delivering; it is held there, on the transition's own notification,
        // when the shutdown closes delivery. Resumed, it finds delivery closed at the admission that
        // counts - the one taken under the closure's lock, with the issue following at once - so
        // nothing is issued: the words go to the recovery copy and the teardown runs after. A check
        // made only before the transition would have let this delivery through.
        var world = ComposedSessionWorld.Create("hello world");
        await world.PressAsync();
        var delivering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Controller.SessionChanged += (_, snapshot) =>
        {
            if (snapshot.State != DictationSessionState.Delivering)
            {
                return;
            }

            delivering.TrySetResult();
            if (!resume.Task.Wait(Patience))
            {
                throw new TimeoutException("the finalisation held at Delivering was never resumed");
            }
        };
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await delivering.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        resume.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        var report = await shutdown.WaitAsync(Patience);
        Assert.True(report.Clean);
        Assert.Equal(0, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery, "the words not delivered are kept for recovery");
        Assert.Null(world.Controller.CurrentSession);
        Assert.Equal(1, world.TearDowns);
    }

    [Fact]
    public async Task IssuedDeliveryIsNeverRetriedDuringShutdown()
    {
        // A DELIVERY ALREADY ISSUED WHEN THE SHUTDOWN BEGINS IS LEFT TO SETTLE, and settles once.
        // The route is held mid-delivery; the shutdown waits; the route answers refused; nothing asks
        // it again, the words go to the recovery copy, and the teardown runs after.
        var world = ComposedSessionWorld.Create("hello world");
        await world.PressAsync();
        world.Delivery.Hold = true;
        world.Delivery.Refuse = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Delivery.Entered.Task.WaitAsync(Patience);

        var shutdown = world.Coordinator.ShutdownAsync(Patience);
        Assert.False(shutdown.IsCompleted);
        world.Delivery.AllowExit.SetResult();

        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        var report = await shutdown.WaitAsync(Patience);
        Assert.True(report.Clean);
        Assert.Equal(1, world.Delivery.Deliveries);
        Assert.True(world.Persistence.HasPendingRecovery, "the refused delivery left its words for recovery");
        Assert.Equal(1, world.TearDowns);
    }
}
