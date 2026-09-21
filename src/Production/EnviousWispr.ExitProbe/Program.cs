// THE EXIT PROBE: a host process that leaves through the production ApplicationLifetime.
//
// What the shell cannot prove in-process - that a step which will not finish ends in the host being
// told to end, inside the budget, with the step's late effects never landing; and that a step which
// blocks its thread is ended by the watchdog from another - a child process can. The test runs this
// with a budget and a step to hang or block, and watches the process: its exit code is the
// terminator's, its stdout is the report, and the marker file the hung step writes on finishing is
// present or absent.
//
// `--budget-ms N` the exit budget; `--hang <step>` one of drain, quiesce, dispose, none - the step
// returns a task that finishes after `--hang-ms N` (default: never) and then writes `--marker <path>`;
// `--block <step>` one of closing, dispose, none - the step blocks the thread it was called on, for
// ever, before returning anything.
using EnviousWispr.App.Composition;
using EnviousWispr.Core.Diagnostics;

var budgetMilliseconds = ReadIntegerArgument(args, "--budget-ms", defaultValue: 1_000);
var hang = ReadArgument(args, "--hang") ?? "none";
var block = ReadArgument(args, "--block") ?? "none";
var marker = ReadArgument(args, "--marker");
var hangMilliseconds = ReadIntegerArgument(args, "--hang-ms", defaultValue: -1);

Func<Task> Hanging(string name) => async () =>
{
    Console.WriteLine($"step {name}: entered");
    await Task.Delay(hangMilliseconds);
    if (marker is not null)
    {
        File.WriteAllText(marker, name);
    }

    Console.WriteLine($"step {name}: finished");
};

Func<Task> Blocking(string name) => () =>
{
    Console.WriteLine($"step {name}: blocking its thread");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return Task.CompletedTask;
};

Task Finished() => Task.CompletedTask;

var parts = new LifetimeParts(
    CloseAdmission: () => Console.WriteLine("admission closed"),
    DrainSettings: hang == "drain" ? Hanging("drain") : Finished,
    ShellClosing: () =>
    {
        Console.WriteLine("shell closing");
        if (block == "closing")
        {
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
    },
    CancelProcessing: () => { },
    ReleaseInputs: [new LifetimeStep("inputs", Finished)],
    ShutDownSession: null,
    Quiesce: [new LifetimeStep("quiesce", hang == "quiesce" ? Hanging("quiesce") : Finished)],
    DisposeSessionDependencies:
    [
        new LifetimeStep("dispose", hang == "dispose" ? Hanging("dispose") : block == "dispose" ? Blocking("dispose") : Finished),
    ],
    DisposeShell: [new LifetimeStep("shell", Finished)],
    DisposeLast: [new LifetimeStep("last", Finished)],
    CompleteRun: _ => Task.FromResult(true),
    CloseRunState: () => { },
    DisposeLogger: Finished);

var lifetime = new ApplicationLifetime(
    parts,
    new ConsoleLogger(),
    TimeProvider.System,
    new ExitingTerminator(),
    TimeSpan.FromMilliseconds(budgetMilliseconds));

var report = await lifetime.ExitAsync();
Console.WriteLine($"report: outcome={report.Outcome} outstanding=[{string.Join(",", report.Outstanding)}] failed=[{string.Join(",", report.Failed)}] escalated={report.Escalated} runCompleted={report.RunCompleted}");
return report.Clean ? 0 : 1;

static string? ReadArgument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int ReadIntegerArgument(string[] args, string name, int defaultValue) =>
    int.TryParse(ReadArgument(args, name), out var value) ? value : defaultValue;

/// <summary>The production terminator's shape: the process ends here, with the escalation's code, and nothing after runs.</summary>
internal sealed class ExitingTerminator : IHostTerminator
{
    public const int ExitCode = 70;

    public void Terminate(ExitReport report)
    {
        Console.WriteLine($"terminating: outstanding=[{string.Join(",", report.Outstanding)}]");
        Console.Out.Flush();
        Environment.Exit(ExitCode);
    }
}

internal sealed class ConsoleLogger : IAppLogger
{
    public void Write(AppLogEntry entry) => Console.WriteLine($"log: {entry.Event}");
}
