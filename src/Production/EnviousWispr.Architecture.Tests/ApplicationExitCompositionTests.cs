using EnviousWispr.App.Composition;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The whole exit, as one composition: the production <see cref="ApplicationLifetime"/> over the
/// production session (coordinator, executor, runner, persistence and the background owners from
/// <see cref="ComposedSessionWorld"/>) and the production presentation gate, with lifetime parts in
/// the shape the shell supplies and fakes only at the leaves. Where the earlier proofs drove the
/// lifetime with a substitute executor and the session with a hand-made exit policy, these drive
/// both together, through the one budget.
/// </summary>
public sealed class ApplicationExitCompositionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExitDuringAHeldTranscriptionIsCleanThroughTheWholeComposition()
    {
        // THE LIFETIME'S EXIT POLICY REACHES THE ENGINE THROUGH THE PRODUCTION SESSION. A release is
        // inside the final transcription when the exit begins: admission closes before the first
        // await (a press is refused as Stopping), the drain finishes at once, the exit policy cancels the
        // finalisation on the executor's own token, the session's shutdown waits for the recovered
        // ending, its teardown disposes the capture, and only then the lifetime disposes the
        // dependencies in order, completes the run and reports clean - no host termination.
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock);
        var exit = Exit.Compose(world, clock);
        await world.PressAsync();
        world.Engine.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Engine.Entered.Task.WaitAsync(Patience);

        var leaving = exit.Lifetime.ExitAsync();
        Assert.True(exit.Admission.Closed, "the presentation gate closed before the first await");
        Assert.Equal(SessionCommandDisposition.Stopping, (await world.Coordinator.SubmitAsync(PushToTalkSignal.Pressed).WaitAsync(Patience)).Disposition);

        var report = await leaving.WaitAsync(Patience);
        Assert.True(world.Engine.Token!.Value.IsCancellationRequested, "the exit policy reached the engine on the production token");
        Assert.NotEqual(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        Assert.Equal(ExitOutcome.Clean, report.Outcome);
        Assert.True(report.Session!.Clean);
        Assert.True(report.RunCompleted);
        Assert.False(report.Retained);
        Assert.False(report.Escalated);
        Assert.Null(exit.Terminator.Report);
        Assert.Equal(1, world.TearDowns);
        Assert.True(world.Capture.Disposed);
        Assert.Empty(world.Delivery.Requests);
        Assert.Equal(["polish warm-up", "live preview", "watchdog", "auto-stop", "history store", "recovery store", "run completion", "run-state store"], exit.Ran);
        Assert.Contains(AppEventCode.DictationSessionRecovered, world.Log.Events);
        Assert.Contains(AppEventCode.ApplicationCleanShutdown, world.Log.Events);
    }

    [Fact]
    public async Task ExitDuringAHeldDeliveryRetainsEverythingAndEndsTheHost()
    {
        // A DELIVERY INSIDE ACCESSIBILITY WORK DOES NOT STOP WHEN ASKED. The route holds through the
        // exit's cancel; the command outlives the whole budget; the session's report names it; the
        // lifetime disposes none of what the command uses - not the capture, not the owners, not the
        // stores - completes no run, and ends the host with the report. When the route finally
        // answers, the command ends on its own terms.
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock);
        var exit = Exit.Compose(world, clock);
        await world.PressAsync();
        world.Delivery.Hold = true;
        var release = world.Coordinator.SubmitAsync(PushToTalkSignal.Released);
        await world.Delivery.Entered.Task.WaitAsync(Patience);

        var registered = clock.Registered;
        var leaving = exit.Lifetime.ExitAsync();
        // The lifetime's watchdog timer, then the coordinator's wait for the command.
        await clock.WhenRegistered(registered + 2).WaitAsync(Patience);
        clock.Advance(ApplicationLifetime.DefaultBudget);

        var report = await leaving.WaitAsync(Patience);
        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.Equal(ShutdownOutcome.Unclean, report.Session!.Outcome);
        Assert.True(report.Session.CommandOutstanding);
        Assert.Null(report.Session.Teardown);
        Assert.True(report.Retained);
        Assert.True(report.Escalated);
        Assert.False(report.RunCompleted);
        Assert.Same(report, exit.Terminator.Report);
        Assert.Equal(0, world.TearDowns);
        Assert.False(world.Capture.Disposed);
        Assert.Equal(["polish warm-up"], exit.Ran);
        Assert.Contains(AppEventCode.ApplicationShutdownUnclean, world.Log.Events);
        Assert.Contains(AppEventCode.ApplicationExitEscalated, world.Log.Events);
        Assert.DoesNotContain(AppEventCode.ApplicationCleanShutdown, world.Log.Events);

        world.Delivery.AllowExit.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await release.WaitAsync(Patience)).Disposition);
        Assert.Single(world.Delivery.Requests);
    }

    [Fact]
    public async Task ALeaseHeldAcrossTheExitKeepsTheSessionFromBeingAskedToShutDown()
    {
        // THE QUICK ADD'S SHAPE, END TO END. A shell operation holds a presentation lease - the
        // adapter read the session's teardown would dispose under - and does not return inside the
        // budget: the drain is outstanding, the idle session is not asked to shut down, nothing of it
        // is disposed, and the host is ended with the drain named.
        var clock = new Deterministic.ManualClock();
        var world = ComposedSessionWorld.Create("hello world", clock);
        var exit = Exit.Compose(world, clock);
        Assert.True(exit.Admission.TryEnter(out var lease));

        var registered = clock.Registered;
        var leaving = exit.Lifetime.ExitAsync();
        Assert.True(lease!.Closing.IsCancellationRequested, "the drain told the operation to stop");
        // The lifetime's watchdog timer, then the drain's bounded join.
        await clock.WhenRegistered(registered + 2).WaitAsync(Patience);
        clock.Advance(ApplicationLifetime.DefaultBudget);

        var report = await leaving.WaitAsync(Patience);
        Assert.Equal(["presentation drain"], report.Outstanding);
        Assert.Null(report.Session);
        Assert.True(report.Retained);
        Assert.True(report.Escalated);
        Assert.Equal(0, world.TearDowns);
        Assert.False(world.Capture.Disposed);
        // The quiescence joins are still issued and observed - the warm-up finished at once - and
        // nothing is disposed behind them.
        Assert.Equal(["polish warm-up"], exit.Ran);
        Assert.Same(report, exit.Terminator.Report);
        lease.Dispose();
    }

    /// <summary>The production lifetime over the composed session, with parts in the shell's shape.</summary>
    private sealed class Exit
    {
        public required ApplicationLifetime Lifetime { get; init; }
        public required PresentationAdmission Admission { get; init; }
        public required RecordingTerminator Terminator { get; init; }
        public List<string> Ran { get; } = [];

        public static Exit Compose(ComposedSessionWorld world, Deterministic.ManualClock clock)
        {
            var admission = new PresentationAdmission();
            var terminator = new RecordingTerminator();
            Exit? exit = null;
            LifetimeStep Disposal(string name, Func<ValueTask> dispose) =>
                new(name, async () =>
                {
                    exit!.Ran.Add(name);
                    await dispose();
                });
            var parts = new LifetimeParts(
                CloseAdmission: world.Coordinator.Close,
                DrainPresentation: admission.CloseAsync,
                ShellClosing: () => { },
                AbortPolishRuntime: () => { },
                CancelProcessing: world.Coordinator.CancelProcessing,
                ReleaseInputs: [],
                ShutDownSession: world.Coordinator.ShutdownAsync,
                Quiesce: [LifetimeStep.Of("polish warm-up", () => exit!.Ran.Add("polish warm-up"))],
                DisposeSessionDependencies:
                [
                    Disposal("live preview", world.Runtime.Preview.DisposeAsync),
                    Disposal("watchdog", world.Runtime.Watchdog.DisposeAsync),
                    Disposal("auto-stop", world.Runtime.AutoStop.DisposeAsync),
                    LifetimeStep.Of("history store", () => exit!.Ran.Add("history store")),
                    LifetimeStep.Of("recovery store", () => exit!.Ran.Add("recovery store")),
                ],
                DisposeShell: [],
                CompleteRun: (_, _) =>
                {
                    exit!.Ran.Add("run completion");
                    return Task.FromResult(true);
                },
                CloseRunState: () => exit!.Ran.Add("run-state store"),
                DisposeLogger: () => Task.CompletedTask);
            exit = new Exit
            {
                Lifetime = new ApplicationLifetime(parts, world.Log, clock, terminator),
                Admission = admission,
                Terminator = terminator,
            };
            return exit;
        }
    }

    private sealed class RecordingTerminator : IHostTerminator
    {
        public ExitReport? Report { get; private set; }

        public void Terminate(ExitReport report) => Report = report;
    }
}
