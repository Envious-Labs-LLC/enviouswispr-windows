using System.Diagnostics;
using EnviousWispr.App.Composition;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Sessions;
using EnviousWispr.Pipeline;

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
        // ONE BUDGET, BEGINNING BEFORE THE FIRST AWAIT. The settings drain takes twelve of the twenty
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
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(12));
        world.Drain.SetResult();
        await clock.WhenRegistered(2).WaitAsync(Patience);
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

        var exit = world.Lifetime.ExitAsync();
        await clock.WhenRegistered(1).WaitAsync(Patience);
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
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency", "second dependency", "shell", "last"], world.Ran);
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
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency", "second dependency", "shell", "last"], faulted.Ran);
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
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency", "second dependency", "shell", "last"], world.Ran);
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
        await clock.WhenRegistered(1).WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(20));

        var report = await exit.WaitAsync(Patience);
        Assert.Equal(["dependency"], report.Outstanding);
        Assert.Equal(["inputs", "warm-up", "heartbeat", "dependency"], world.Ran);
        Assert.Equal(0, world.CompleteRunCalls);
        Assert.True(report.Escalated);
        world.Dependency.SetResult();
    }

    [Fact]
    public async Task TerminalExitEscalationEndsTheHostProcess()
    {
        // A CHILD PROCESS LEAVES THROUGH THE PRODUCTION LIFETIME with a disposal that never finishes and
        // a one-second budget. The process is gone inside a few seconds with the escalation's exit
        // code, its log said so, the report named the step, and the file the hung step would have
        // written on finishing was never written: its late effect did not land.
        var probe = Path.Combine(AppContext.BaseDirectory, "EnviousWispr.ExitProbe.exe");
        Assert.True(File.Exists(probe), $"the exit probe was not built beside the tests: {probe}");
        var marker = Path.Combine(Path.GetTempPath(), $"EnviousWispr-exit-probe-{Guid.NewGuid():N}.txt");

        var hung = await RunProbeAsync(probe, $"--budget-ms 1000 --hang dispose --marker \"{marker}\"");
        Assert.Equal(70, hung.ExitCode);
        Assert.True(hung.Elapsed < TimeSpan.FromSeconds(8), $"the escalation took {hung.Elapsed}");
        Assert.Contains("log: ApplicationExitEscalated", hung.Output, StringComparison.Ordinal);
        Assert.Contains("terminating: outstanding=[dispose]", hung.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("report:", hung.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(marker), "the hung step's late effect landed");

        var clean = await RunProbeAsync(probe, "--budget-ms 1000 --hang none");
        Assert.Equal(0, clean.ExitCode);
        Assert.Contains("report: outcome=Clean outstanding=[] failed=[] escalated=False runCompleted=True", clean.Output, StringComparison.Ordinal);
        Assert.Contains("log: ApplicationCleanShutdown", clean.Output, StringComparison.Ordinal);
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
        public int CompleteRunCalls { get; private set; }
        public int LoggerDisposals { get; private set; }
        public bool LoggerDisposed => LoggerDisposals > 0;
        public bool CompleteRunAnswer { get; set; } = true;
        public bool ShellThrows { get; set; }
        /// <summary>When set, the settings drain does not finish until it is completed.</summary>
        public TaskCompletionSource? Drain { get; set; }
        /// <summary>When set, the polish warm-up does not finish until it is completed.</summary>
        public TaskCompletionSource? Warmup { get; set; }
        /// <summary>When set, the first dependency's disposal does not finish until it is completed.</summary>
        public TaskCompletionSource? Dependency { get; set; }

        public static World Create(TimeProvider clock, DictationSessionCoordinator? coordinator = null)
        {
            var log = new RecordingLogger();
            var terminator = new RecordingTerminator();
            World? world = null;
            var parts = new LifetimeParts(
                CloseAdmission: () =>
                {
                    world!.AdmissionClosedCount++;
                    coordinator?.Close();
                },
                DrainSettings: () =>
                {
                    world!.DrainCalls++;
                    return world.Drain?.Task ?? Task.CompletedTask;
                },
                ShellClosing: () => world!.ShellClosingCalls++,
                CancelProcessing: () => coordinator?.CancelProcessing(),
                ReleaseInputs: [Step("inputs", () => world!)],
                ShutDownSession: coordinator is null ? null : budget => coordinator.ShutdownAsync(budget),
                Quiesce:
                [
                    new LifetimeStep("polish warm-up", () =>
                    {
                        world!.Ran.Add("warm-up");
                        return world.Warmup?.Task ?? Task.CompletedTask;
                    }),
                    Step("heartbeat", () => world!),
                ],
                DisposeSessionDependencies:
                [
                    new LifetimeStep("dependency", () =>
                    {
                        world!.Ran.Add("dependency");
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
                CompleteRun: () =>
                {
                    world!.CompleteRunCalls++;
                    return Task.FromResult(world.CompleteRunAnswer);
                },
                DisposeLast: [Step("last", () => world!)],
                DisposeLogger: () =>
                {
                    world!.LoggerDisposals++;
                    return Task.CompletedTask;
                });
            world = new World
            {
                Lifetime = new ApplicationLifetime(parts, log, clock, terminator),
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
}
