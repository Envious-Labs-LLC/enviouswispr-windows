using System.Diagnostics;
using EnviousWispr.App.Composition;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Reliability;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The application's exit, as the shell composes it: one budget from the first step to the last,
/// what a session uses disposed only once nothing uses it, the run marked clean only by a quiescent
/// exit, and the host ended by force when something would not finish.
/// </summary>
public sealed class ApplicationLifetimeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExitBudgetIncludesSettingsAndWarmup()
    {
        // ONE BUDGET, BEGINNING BEFORE THE FIRST AWAIT. The presentation drain takes twelve of the twenty
        // seconds; the polish warm-up is given the eight that are left and does not finish inside
        // them; nothing after it is disposed, the run is not completed, the exit is unclean and the
        // host is told to end - twenty seconds after the exit was asked for, not twenty plus twenty.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.Drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Warmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = clock.GetTimestamp();

        var exit = world.Lifetime.ExitAsync();
        Assert.True(world.AdmissionClosed, "admission closed before the first await");
        await world.WhenJoined("presentation drain").WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(12));
        world.Drain.SetResult();
        await world.WhenJoined("polish warm-up").WaitAsync(Patience);
        // THE WARM-UP'S OWN JOIN IS DUE IN EIGHT SECONDS: the twelve the drain took are gone from it.
        Assert.Equal(TimeSpan.FromSeconds(8), clock.NextDue);
        clock.Advance(TimeSpan.FromSeconds(8));

        var report = await exit.WaitAsync(Patience);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.GetElapsedTime(started));
        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.Equal(["polish warm-up"], report.Outstanding);
        Assert.Empty(report.Failed);
        Assert.True(report.Retained);
        Assert.False(report.RunCompleted);
        Assert.True(report.Escalated);
        Assert.Same(report, world.Terminator.Report);
        // THE HEARTBEAT, GIVEN NOTHING, WAS STILL ISSUED AND OBSERVED: it finished at once, so it is
        // not outstanding. The disposals were not run: what the warm-up uses is kept.
        Assert.Equal(["inputs", "warm-up", "heartbeat"], world.Ran);
        Assert.Equal(0, world.CompleteRunCalls);
        Assert.Contains(AppEventCode.ApplicationExitEscalated, world.Log.Events);
        Assert.DoesNotContain(AppEventCode.ApplicationCleanShutdown, world.Log.Events);
        Assert.True(world.LoggerDisposed, "the log is closed last, whatever the outcome");
        world.Warmup.SetResult();
    }

    [Fact]
    public async Task BlockedCleanupDoesNotDisposeActiveDependencies()
    {
        // THE SESSION'S SHUTDOWN IS THE PRODUCTION COORDINATOR'S, with a command that does not finish
        // inside the budget. Its report says the session is not quiescent; the lifetime disposes
        // none of what the command uses - not the engines, not the stores, not the shell services
        // that follow them - completes no run, and tells the host to end with the command still owned.
        var clock = new Deterministic.ManualClock();
        var executor = new HeldExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor, clock: clock);
        var world = World.Create(clock, coordinator);
        var press = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await executor.Entered.Task.WaitAsync(Patience);

        var registered = clock.Registered;
        var exit = world.Lifetime.ExitAsync();
        // The watchdog's timer is registered at the first step; the coordinator's wait for the
        // command is the one after it.
        await clock.WhenRegistered(registered + 2).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));

        var report = await exit.WaitAsync(Patience);
        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.NotNull(report.Session);
        Assert.Equal(ShutdownOutcome.Unclean, report.Session.Outcome);
        Assert.True(report.Session.CommandOutstanding);
        Assert.True(report.Retained);
        Assert.Empty(report.Outstanding);
        Assert.Equal(["inputs", "warm-up", "heartbeat"], world.Ran);
        Assert.Equal(0, world.CompleteRunCalls);
        Assert.True(report.Escalated);
        Assert.Contains(AppEventCode.ApplicationShutdownUnclean, world.Log.Events);
        Assert.Equal(0, executor.TearDowns);

        executor.Release.SetResult();
        Assert.Equal(SessionCommandDisposition.Applied, (await press.WaitAsync(Patience)).Disposition);
    }

    [Fact]
    public async Task OnlyQuiescentShutdownMarksRunClean()
    {
        // CLEAN IS EARNED. With the production coordinator idle, every step finishing and nothing
        // failing, the run is completed and the exit is clean; with the same coordinator and one
        // shell step throwing, the dependencies are still disposed (nothing uses them) but the run
        // is not completed and the exit is unclean - without the host being told to end, since
        // nothing is still running.
        var clock = new Deterministic.ManualClock();
        var executor = new HeldExecutor();
        await using var coordinator = new DictationSessionCoordinator(executor, clock: clock);
        var world = World.Create(clock, coordinator);

        var report = await world.Lifetime.ExitAsync().WaitAsync(Patience);

        Assert.Equal(ExitOutcome.Clean, report.Outcome);
        Assert.True(report.Clean);
        Assert.NotNull(report.Session);
        Assert.True(report.Session.Clean);
        Assert.False(report.Retained);
        Assert.Equal(1, world.CompleteRunCalls);
        Assert.True(report.RunCompleted);
        Assert.False(report.Escalated);
        Assert.Null(world.Terminator.Report);
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency", "second dependency", "shell", "run-state store"], world.Ran);
        Assert.Equal(1, executor.TearDowns);
        Assert.Contains(AppEventCode.ApplicationCleanShutdown, world.Log.Events);
        Assert.True(world.LoggerDisposed);

        var faulted = World.Create(clock, new DictationSessionCoordinator(new HeldExecutor(), clock: clock));
        faulted.ShellThrows = true;
        var faultedReport = await faulted.Lifetime.ExitAsync().WaitAsync(Patience);
        Assert.Equal(ExitOutcome.Unclean, faultedReport.Outcome);
        Assert.Equal(["shell"], faultedReport.Failed);
        Assert.Equal(0, faulted.CompleteRunCalls);
        Assert.False(faultedReport.RunCompleted);
        Assert.False(faultedReport.Escalated);
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency", "second dependency", "shell", "run-state store"], faulted.Ran);
        Assert.Contains(AppEventCode.UnhandledFailure, faulted.Log.Events);
        Assert.DoesNotContain(AppEventCode.ApplicationCleanShutdown, faulted.Log.Events);
    }

    [Fact]
    public async Task ARunTheStoreRefusesToCompleteIsNotClean()
    {
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.CompleteRunAnswer = false;

        var report = await world.Lifetime.ExitAsync().WaitAsync(Patience);

        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.Equal(1, world.CompleteRunCalls);
        Assert.False(report.RunCompleted);
        Assert.False(report.Escalated);
        Assert.DoesNotContain(AppEventCode.ApplicationCleanShutdown, world.Log.Events);
    }

    [Fact]
    public async Task ThePolishRuntimeIsAbortedAfterTheShellClosesAndBeforeTheExitPolicy()
    {
        // THE ABORT IS THE LIFETIME'S STEP, IN ITS ORDER: admission closed, the presentation drained,
        // the shell closed, then the local polish runtime ended by force - before the finalisation is
        // cancelled and before the session is asked to shut down, so a polish in flight fails at
        // once rather than holding the budget. One that throws is a failed step, named; the exit
        // goes on.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.PolishAbortThrows = true;

        var report = await world.Lifetime.ExitAsync().WaitAsync(Patience);

        Assert.Equal(["admission", "presentation drain", "shell closing", "polish runtime abort", "exit policy"], world.Prepared);
        Assert.Equal(1, world.PolishAborts);
        Assert.Equal(["polish runtime abort"], report.Failed);
        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.False(report.Retained);
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency", "second dependency", "shell", "run-state store"], world.Ran);
    }

    [Fact]
    public async Task RepeatedDisposeDoesNotRepeatEffects()
    {
        // EVERY PATH OUT REACHES THE SAME TWO TASKS. Prepared twice and exited three times, from three
        // callers at once and once more after the fact: admission closed once, the settings drained
        // once, every step ran once, the run completed once, and every caller holds the same report.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.Drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstPreparation = world.Lifetime.PrepareAsync();
        var secondPreparation = world.Lifetime.PrepareAsync();
        var firstExit = world.Lifetime.ExitAsync();
        var secondExit = world.Lifetime.ExitAsync();
        Assert.Same(firstPreparation, secondPreparation);
        Assert.Same(firstExit, secondExit);
        Assert.Equal(1, world.AdmissionClosedCount);
        Assert.Equal(1, world.DrainCalls);
        world.Drain.SetResult();

        var report = await firstExit.WaitAsync(Patience);
        var again = await world.Lifetime.ExitAsync().WaitAsync(Patience);
        Assert.Same(report, again);
        Assert.Equal(1, world.DrainCalls);
        Assert.Equal(1, world.ShellClosingCalls);
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency", "second dependency", "shell", "run-state store"], world.Ran);
        Assert.Equal(1, world.CompleteRunCalls);
        Assert.Equal(1, world.LoggerDisposals);
        Assert.Equal(1, world.Log.Events.Count(code => code == AppEventCode.ApplicationCleanShutdown));
    }

    [Fact]
    public async Task ADisposalThatDoesNotFinishStopsTheDisposalsAfterIt()
    {
        // THE ORDER IS THE DEPENDENCY ORDER. The first dependency's disposal does not finish inside
        // what is left; the ones after it, and the shell's, and the last, are not run - what a stuck
        // disposal might still be inside is kept - and the host is told to end.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.Dependency = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var exit = world.Lifetime.ExitAsync();
        await world.WhenJoined("dependency").WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));

        var report = await exit.WaitAsync(Patience);
        Assert.Equal(["dependency"], report.Outstanding);
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency"], world.Ran);
        Assert.Equal(0, world.CompleteRunCalls);
        Assert.True(report.Escalated);
        world.Dependency.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunCompletionThatDoesNotLandInsideTheBudgetLeavesTheRunInterrupted(bool releasedLate)
    {
        // THE PUBLICATION IS FENCED. The production run-state store on a file, behind a hold: the
        // exit reaches the run's completion with its whole budget and the write does not begin inside
        // it. The report says the completion is outstanding, the exit is unclean and the host is told
        // to end. Released late, the write finds its token run out and never lands: the next launch
        // reads an interrupted run, not a clean one. Never released, the same.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        var store = new JsonApplicationRunStateStore(path);
        var run = await store.BeginRunAsync(DateTimeOffset.UtcNow);
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? write = null;
        world.CompleteRun = async (fence, cancellation) =>
        {
            await release.Task;
            write = store.CompleteRunAsync(run.RunId, DateTimeOffset.UtcNow, fence, cancellation);
            return await write;
        };

        var exit = world.Lifetime.ExitAsync();
        // The completion's token is registered before the step is entered; its join is the next.
        await world.WhenJoined("run completion").WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));

        var report = await exit.WaitAsync(Patience);
        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.Equal(["run completion"], report.Outstanding);
        Assert.False(report.RunCompleted);
        Assert.True(report.Escalated);
        Assert.Same(report, world.Terminator.Report);
        Assert.DoesNotContain(AppEventCode.ApplicationCleanShutdown, world.Log.Events);

        if (releasedLate)
        {
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                while (write is null)
                {
                    await Task.Yield();
                }

                await write.WaitAsync(Patience);
            });
        }

        store.Dispose();
        using var nextLaunch = new JsonApplicationRunStateStore(path);
        var next = await nextLaunch.BeginRunAsync(DateTimeOffset.UtcNow);
        Assert.Equal(RunStateLoadStatus.PreviousRunInterrupted, next.Status);
    }

    [Fact]
    public async Task ARunCompletionInsideTheBudgetIsReadAsCleanByTheNextLaunch()
    {
        // THE CONTROL: the same production store, the completion landing inside the budget, the next
        // launch reads a clean run.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        var store = new JsonApplicationRunStateStore(path);
        var run = await store.BeginRunAsync(DateTimeOffset.UtcNow);
        var world = World.Create(new Deterministic.ManualClock());
        world.CompleteRun = (fence, cancellation) => store.CompleteRunAsync(run.RunId, DateTimeOffset.UtcNow, fence, cancellation);

        var report = await world.Lifetime.ExitAsync().WaitAsync(Patience);

        Assert.True(report.Clean);
        Assert.True(report.RunCompleted);
        store.Dispose();
        using var nextLaunch = new JsonApplicationRunStateStore(path);
        Assert.Equal(RunStateLoadStatus.Started, (await nextLaunch.BeginRunAsync(DateTimeOffset.UtcNow)).Status);
    }

    [Fact]
    public async Task ARunCompletionSuspendedAtItsCommitWhenTheExitAbandonsItIsNeverPublished()
    {
        // THE FENCE, AT THE INSTANT THAT MATTERS. The production store has written its temporary
        // file and is about to replace the record when it is held; the exit's budget runs out and
        // the exit abandons the publication; released, the store finds the fence closed, does not
        // replace the record, deletes its temporary file and answers false. The next launch reads an
        // interrupted run - not the clean one a bounded wait alone would have let land.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        var store = new JsonApplicationRunStateStore(path);
        var run = await store.BeginRunAsync(DateTimeOffset.UtcNow);
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new ManualResetEventSlim();
        Task<bool>? write = null;
        world.CompleteRun = (fence, cancellation) =>
        {
            fence.BeforeCommit = () =>
            {
                committing.SetResult();
                if (!proceed.Wait(Patience))
                {
                    throw new TimeoutException("the commit was never released");
                }
            };
            write = store.CompleteRunAsync(run.RunId, DateTimeOffset.UtcNow, fence, cancellation);
            return write;
        };

        var exit = world.Lifetime.ExitAsync();
        await committing.Task.WaitAsync(Patience);
        await world.WhenJoined("run completion").WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));
        var report = await exit.WaitAsync(Patience);
        Assert.Equal(["run completion"], report.Outstanding);
        Assert.False(report.RunCompleted);
        Assert.True(report.Escalated);

        proceed.Set();
        Assert.False(await write!.WaitAsync(Patience), "the store replaced the record after the exit abandoned it");
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        store.Dispose();
        using var nextLaunch = new JsonApplicationRunStateStore(path);
        Assert.Equal(RunStateLoadStatus.PreviousRunInterrupted, (await nextLaunch.BeginRunAsync(DateTimeOffset.UtcNow)).Status);
    }

    [Fact]
    public async Task ARunCompletionCommittedBeforeTheExitStoppedWaitingIsReportedCompletedAndItsWriterKept()
    {
        // COMMITTED IS NOT FINISHED. The production store has replaced the record (the move took the
        // temporary file with it) and the fence has let go; the writer is held there - its gate still
        // taken, its cleanup and its answer still to come - when the budget runs out. The exit's abandonment is refused, so it reports the run
        // completed; but the writer is still inside the store, so the completion is outstanding, the
        // store is kept (its production disposal is wired and does not run), the exit is unclean and
        // the host is told to end. Released, the writer lets go of a gate that still exists and
        // answers true, leaving nothing temporary behind; the next launch reads a clean run.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        var store = new JsonApplicationRunStateStore(path);
        var run = await store.BeginRunAsync(DateTimeOffset.UtcNow);
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.CloseRunState = store.Dispose;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new ManualResetEventSlim();
        Task<bool>? write = null;
        world.CompleteRun = (fence, cancellation) =>
        {
            fence.AfterCommit = () =>
            {
                committed.SetResult();
                if (!proceed.Wait(Patience))
                {
                    throw new TimeoutException("the writer was never released");
                }
            };
            write = store.CompleteRunAsync(run.RunId, DateTimeOffset.UtcNow, fence, cancellation);
            return write;
        };

        var exit = world.Lifetime.ExitAsync();
        await world.WhenJoined("run completion").WaitAsync(Patience);
        await committed.Task.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));
        var report = await exit.WaitAsync(Patience);

        Assert.True(report.RunCompleted);
        Assert.Equal(["run completion"], report.Outstanding);
        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.True(report.Escalated);
        Assert.DoesNotContain("run-state store", world.Ran);
        Assert.Contains(AppEventCode.ApplicationCleanShutdown, world.Log.Events);

        proceed.Set();
        Assert.True(await write!.WaitAsync(Patience), "the writer faulted after the commit: its store was closed under it");
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        store.Dispose();
        using var nextLaunch = new JsonApplicationRunStateStore(path);
        Assert.Equal(RunStateLoadStatus.Started, (await nextLaunch.BeginRunAsync(DateTimeOffset.UtcNow)).Status);
    }

    [Fact]
    public async Task TheRunStateStoreIsKeptWhileAnythingThatWritesToItIsOutstanding()
    {
        // THE STORE IS ONE OF THE THINGS A LATE STEP STILL USES. The heartbeat's join does not finish
        // inside the budget; the production store is not closed - the report names the heartbeat,
        // and a session's late edge still lands in the store afterwards. Released, the heartbeat
        // finds its store intact too.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "run-state.json");
        using var store = new JsonApplicationRunStateStore(path);
        var run = await store.BeginRunAsync(DateTimeOffset.UtcNow);
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.Heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.CloseRunState = store.Dispose;

        var exit = world.Lifetime.ExitAsync();
        await world.WhenJoined("heartbeat").WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));
        var report = await exit.WaitAsync(Patience);

        Assert.Equal(["heartbeat"], report.Outstanding);
        Assert.DoesNotContain("run-state store", world.Ran);
        Assert.True(await store.SetDictationActiveAsync(run.RunId, false, DateTimeOffset.UtcNow), "the late edge could not be written: the store was closed under it");
        Assert.True(await store.HeartbeatAsync(run.RunId, DateTimeOffset.UtcNow));
        world.Heartbeat.SetResult();
    }

    [Fact]
    public async Task ALogThatDoesNotCloseInsideTheBudgetIsOutstandingAndEndsTheHost()
    {
        // THE VERDICT IS TAKEN AFTER THE LOG CLOSES. Everything finished and the run was completed;
        // the log's closing does not finish inside what is left. The report is unclean with the log
        // outstanding, and the host is told to end - the completion already written stands, because
        // everything the run had to finish had finished.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        world.Logger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var exit = world.Lifetime.ExitAsync();
        await world.WhenJoined("log").WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));

        var report = await exit.WaitAsync(Patience);
        Assert.Equal(ExitOutcome.Unclean, report.Outcome);
        Assert.Equal(["log"], report.Outstanding);
        Assert.True(report.RunCompleted);
        Assert.True(report.Escalated);
        Assert.Same(report, world.Terminator.Report);
        world.Logger.SetResult();
    }

    [Fact]
    public async Task AStepThatBlocksItsThreadIsEndedByTheWatchdog()
    {
        // NO JOIN CAN BOUND A STEP THAT NEVER RETURNS A TASK. The shell's closing blocks the thread it
        // was called on; the exit cannot conclude, so two seconds past the budget the watchdog, on the
        // clock's own thread, tells the host to end, naming the step it was inside - and nothing was
        // disposed, since nothing after the block ever ran.
        var clock = new Deterministic.ManualClock();
        var world = World.Create(clock);
        var blocked = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.ShellClosingBlocks = () =>
        {
            entered.SetResult();
            blocked.Wait(Patience);
        };

        var exit = Task.Run(() => world.Lifetime.ExitAsync());
        await entered.Task.WaitAsync(Patience);
        clock.Advance(ApplicationLifetime.DefaultBudget);
        Assert.Null(world.Terminator.Report);
        clock.Advance(ApplicationLifetime.WatchGrace);

        var report = world.Terminator.Report;
        Assert.NotNull(report);
        Assert.Equal(["shell closing"], report.Outstanding);
        Assert.True(report.Escalated);
        Assert.True(report.Retained);
        Assert.Empty(world.Ran);
        Assert.Equal(0, world.CompleteRunCalls);

        blocked.Set();
        Assert.Same(report, world.Terminator.Report);
        await exit.WaitAsync(Patience);
    }

    [Fact]
    public async Task TerminalExitEscalationEndsTheHostProcess()
    {
        // A CHILD PROCESS LEAVES THROUGH THE PRODUCTION LIFETIME with a disposal that finishes three
        // seconds late and a one-second budget. The process is gone inside a few seconds with the
        // escalation's exit code, its log said so, the report named the step, and the file the step
        // writes on finishing was never written: the process ended before its late effect could land.
        // The control run, the same step finishing inside the budget, leaves the file - so its absence
        // above is the escalation's doing.
        var probe = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.ExitProbe.exe");
        Assert.True(File.Exists(probe), $"the exit probe was not built beside the tests: {probe}");
        var marker = Path.Combine(Path.GetTempPath(), $"EnviousWispr-exit-probe-{Guid.NewGuid():N}.txt");

        var hung = await RunProbeAsync(probe, $"--budget-ms 1000 --hang dispose --hang-ms 3000 --marker \"{marker}\"");
        Assert.Equal(70, hung.ExitCode);
        Assert.True(hung.Elapsed < TimeSpan.FromSeconds(2.5), $"the escalation took {hung.Elapsed}");
        Assert.Contains("log: ApplicationExitEscalated", hung.Output, StringComparison.Ordinal);
        Assert.Contains("terminating: outstanding=[dispose]", hung.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("report:", hung.Output, StringComparison.Ordinal);
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.False(File.Exists(marker), "the hung step's late effect landed");

        var control = await RunProbeAsync(probe, $"--budget-ms 1000 --hang dispose --hang-ms 200 --marker \"{marker}\"");
        Assert.Equal(0, control.ExitCode);
        Assert.True(File.Exists(marker), "the control run's step did not write its file");
        File.Delete(marker);
        Assert.Contains("report: outcome=Clean outstanding=[] failed=[] escalated=False runCompleted=True", control.Output, StringComparison.Ordinal);
        Assert.Contains("log: ApplicationCleanShutdown", control.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepThatBlocksItsThreadIsEndedByTheWatchdogInTheHostProcess()
    {
        // THE WATCHDOG, IN A REAL PROCESS: the shell's closing blocks its thread before any task is
        // returned; no join is ever reached. Two seconds past the one-second budget the watchdog ends
        // the process from the clock's thread with the escalation's code, naming the step.
        var probe = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.ExitProbe.exe");
        var blocked = await RunProbeAsync(probe, "--budget-ms 1000 --block closing");
        Assert.Equal(70, blocked.ExitCode);
        Assert.InRange(blocked.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(8));
        Assert.Contains("terminating: outstanding=[shell closing]", blocked.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("report:", blocked.Output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, TimeSpan Elapsed)> RunProbeAsync(string probe, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(probe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(patience.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the probe did not end inside thirty seconds: " + await output);
        }

        stopwatch.Stop();
        return (process.ExitCode, await output + await error, stopwatch.Elapsed);
    }

    /// <summary>The shell's steps as fakes, each recording that it ran, some holdable, one failing on request.</summary>
    private sealed class World
    {
        public required ApplicationLifetime Lifetime { get; init; }
        public required RecordingLogger Log { get; init; }
        public required RecordingTerminator Terminator { get; init; }
        public List<string> Ran { get; } = [];
        public bool AdmissionClosed => AdmissionClosedCount > 0;
        public int AdmissionClosedCount { get; private set; }
        public int DrainCalls { get; private set; }
        public int ShellClosingCalls { get; private set; }
        public int PolishAborts { get; private set; }
        /// <summary>When set, the polish runtime's abort throws.</summary>
        public bool PolishAbortThrows { get; set; }
        /// <summary>The preparation's steps and the exit policy, in the order they were called.</summary>
        public List<string> Prepared { get; } = [];
        public int CompleteRunCalls { get; private set; }
        public int LoggerDisposals { get; private set; }
        public bool LoggerDisposed => LoggerDisposals > 0;
        public bool CompleteRunAnswer { get; set; } = true;
        /// <summary>When set, the run's completion: the production store behind a hold, say.</summary>
        public Func<PublicationFence, CancellationToken, Task<bool>>? CompleteRun { get; set; }
        /// <summary>When set, what closing the run-state store does: the production store's disposal, say.</summary>
        public Action? CloseRunState { get; set; }
        public bool ShellThrows { get; set; }
        /// <summary>When set, the heartbeat's join does not finish until it is completed.</summary>
        public TaskCompletionSource? Heartbeat { get; set; }
        /// <summary>How many timers the clock had registered when each holdable step was entered, once it has been.</summary>
        private readonly Dictionary<string, TaskCompletionSource<int>> _entries = [];
        public required Deterministic.ManualClock Clock { get; init; }

        /// <summary>Completes once the step has been entered and the clock has registered the next timer after that: the step's own join.</summary>
        public async Task WhenJoined(string step)
        {
            var registeredAtEntry = await Entry(step).Task;
            await Clock.WhenRegistered(registeredAtEntry + 1);
        }

        internal void Entered(string step) => Entry(step).TrySetResult(Clock.Registered);

        private TaskCompletionSource<int> Entry(string step)
        {
            lock (_entries)
            {
                if (!_entries.TryGetValue(step, out var entry))
                {
                    entry = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _entries[step] = entry;
                }

                return entry;
            }
        }
        /// <summary>When set, the log does not close until it is completed.</summary>
        public TaskCompletionSource? Logger { get; set; }
        /// <summary>When set, the shell's closing blocks the thread it is called on for as long as this does.</summary>
        public Action? ShellClosingBlocks { get; set; }
        /// <summary>When set, the presentation drain does not finish until it is completed.</summary>
        public TaskCompletionSource? Drain { get; set; }
        /// <summary>When set, the polish warm-up does not finish until it is completed.</summary>
        public TaskCompletionSource? Warmup { get; set; }
        /// <summary>When set, the first dependency's disposal does not finish until it is completed.</summary>
        public TaskCompletionSource? Dependency { get; set; }

        public static World Create(Deterministic.ManualClock clock, DictationSessionCoordinator? coordinator = null)
        {
            var log = new RecordingLogger();
            var terminator = new RecordingTerminator();
            World? world = null;
            var parts = new LifetimeParts(
                CloseAdmission: () =>
                {
                    world!.AdmissionClosedCount++;
                    world.Prepared.Add("admission");
                    coordinator?.Close();
                },
                DrainPresentation: () =>
                {
                    world!.DrainCalls++;
                    world.Prepared.Add("presentation drain");
                    world.Entered("presentation drain");
                    return world.Drain?.Task ?? Task.CompletedTask;
                },
                ShellClosing: () =>
                {
                    world!.ShellClosingCalls++;
                    world.Prepared.Add("shell closing");
                    world.ShellClosingBlocks?.Invoke();
                },
                AbortPolishRuntime: () =>
                {
                    world!.PolishAborts++;
                    world.Prepared.Add("polish runtime abort");
                    if (world.PolishAbortThrows)
                    {
                        throw new InvalidOperationException("the runtime's process could not be ended");
                    }
                },
                CancelProcessing: () =>
                {
                    world!.Prepared.Add("exit policy");
                    coordinator?.CancelProcessing();
                },
                ReleaseInputs: [Step("inputs", () => world!)],
                ShutDownSession: coordinator is null ? null : budget => coordinator.ShutdownAsync(budget),
                Quiesce:
                [
                    new LifetimeStep("polish warm-up", () =>
                    {
                        world!.Ran.Add("warm-up");
                        world.Entered("polish warm-up");
                        return world.Warmup?.Task ?? Task.CompletedTask;
                    }),
                    new LifetimeStep("heartbeat", () =>
                    {
                        world!.Ran.Add("heartbeat");
                        world.Entered("heartbeat");
                        return world.Heartbeat?.Task ?? Task.CompletedTask;
                    }),
                ],
                DisposeSessionDependencies:
                [
                    new LifetimeStep("dependency", () =>
                    {
                        world!.Ran.Add("dependency");
                        world.Entered("dependency");
                        return world.Dependency?.Task ?? Task.CompletedTask;
                    }),
                    Step("second dependency", () => world!),
                ],
                DisposeShell:
                [
                    new LifetimeStep("shell", () =>
                    {
                        world!.Ran.Add("shell");
                        return world.ShellThrows ? throw new InvalidOperationException("the tray icon refused") : Task.CompletedTask;
                    }),
                ],
                CompleteRun: (fence, cancellation) =>
                {
                    world!.CompleteRunCalls++;
                    world.Entered("run completion");
                    return world.CompleteRun is { } complete
                        ? complete(fence, cancellation)
                        : Task.FromResult(fence.TryCommit(() => { }) && world.CompleteRunAnswer);
                },
                CloseRunState: () =>
                {
                    world!.Ran.Add("run-state store");
                    world.CloseRunState?.Invoke();
                },
                DisposeLogger: () =>
                {
                    world!.LoggerDisposals++;
                    world.Entered("log");
                    return world.Logger?.Task ?? Task.CompletedTask;
                });
            world = new World
            {
                Lifetime = new ApplicationLifetime(parts, log, clock, terminator),
                Clock = clock,
                Log = log,
                Terminator = terminator,
            };
            return world;
        }

        private static LifetimeStep Step(string name, Func<World> world) =>
            LifetimeStep.Of(name, () => world().Ran.Add(name));
    }

    /// <summary>A command the executor holds until released; the coordinator around it is the production one.</summary>
    private sealed class HeldExecutor : ISessionCommandExecutor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TearDowns { get; private set; }

        public async Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new SessionCommandResult(SessionCommandDisposition.Applied);
        }

        public Task<SessionTeardownReport> TearDownAsync(TimeSpan deadline)
        {
            TearDowns++;
            return Task.FromResult(SessionTeardownReport.Nothing);
        }
    }

    private sealed class RecordingTerminator : IHostTerminator
    {
        public ExitReport? Report { get; private set; }

        public void Terminate(ExitReport report) => Report = report;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<AppEventCode> Events { get; } = [];

        public void Write(AppLogEntry entry) => Events.Add(entry.Event);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"EnviousWispr-lifetime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
