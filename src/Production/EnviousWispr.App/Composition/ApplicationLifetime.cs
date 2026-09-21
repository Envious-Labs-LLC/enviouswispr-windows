using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Pipeline;

namespace EnviousWispr.App.Composition;

/// <summary>One thing the shell does on the way out, named so the report can say which one did not finish.</summary>
/// <param name="Name">What the step is, for the report and the log.</param>
/// <param name="Run">The step; a step that throws is recorded as failed and the exit carries on.</param>
public sealed record LifetimeStep(string Name, Func<Task> Run)
{
    /// <summary>A synchronous step.</summary>
    public static LifetimeStep Of(string name, Action run) => new(name, () =>
    {
        run();
        return Task.CompletedTask;
    });
}

/// <summary>The shell, as the lifetime sees it: the steps of leaving, in the order they run, each named.</summary>
/// <remarks>
/// THE STEPS ARE HANDED IN, NOT REACHED FOR. The lifetime knows the order and the budget; what a step
/// touches - a WinUI window, a tray icon, a hook - is the shell's, and stays behind these delegates so
/// the same lifetime runs in a test against fakes and in a probe process against nothing at all.
/// </remarks>
/// <param name="CloseAdmission">Closes the session's admission; synchronous, run before the first await.</param>
/// <param name="DrainSettings">Finishes the settings write in flight, so a choice just made is not lost.</param>
/// <param name="ShellClosing">What the shell does once the settings are safe and before anything is torn down: its windows, its own log line.</param>
/// <param name="CancelProcessing">The shell's exit policy for a transcription in flight, made before the session is asked to shut down.</param>
/// <param name="ReleaseInputs">The input sources, unsubscribed and disposed first so nothing new arrives.</param>
/// <param name="ShutDownSession">The session's own shutdown under the budget it is handed (step 8); null when the shell owns no session.</param>
/// <param name="Quiesce">Work the shell started that must be over before anything it uses is disposed: the polish warm-up, the heartbeat.</param>
/// <param name="DisposeSessionDependencies">What a session uses: run only behind a quiescent session and finished quiescence steps.</param>
/// <param name="DisposeShell">The shell's own services, run only when nothing is outstanding.</param>
/// <param name="CompleteRun">Writes the run's clean ending; asked only when the exit is clean. False when the store refused.</param>
/// <param name="DisposeLast">The single-instance lock and the run-state store: after the run is completed, and only when nothing is outstanding.</param>
/// <param name="DisposeLogger">The log, flushed and closed as the last act whatever the outcome; best effort.</param>
public sealed record LifetimeParts(
    Action CloseAdmission,
    Func<Task> DrainSettings,
    Action ShellClosing,
    Action CancelProcessing,
    IReadOnlyList<LifetimeStep> ReleaseInputs,
    Func<TimeSpan, Task<ShutdownReport>>? ShutDownSession,
    IReadOnlyList<LifetimeStep> Quiesce,
    IReadOnlyList<LifetimeStep> DisposeSessionDependencies,
    IReadOnlyList<LifetimeStep> DisposeShell,
    Func<Task<bool>> CompleteRun,
    IReadOnlyList<LifetimeStep> DisposeLast,
    Func<Task> DisposeLogger);

/// <summary>How the exit ended.</summary>
public enum ExitOutcome
{
    /// <summary>Every step finished inside the budget, the session was quiescent, nothing failed, and the run was completed.</summary>
    Clean,

    /// <summary>Something did not finish, failed, or the session was not quiescent; what was in use was kept.</summary>
    Unclean,
}

/// <summary>What the exit established.</summary>
/// <param name="Outcome">Clean or unclean.</param>
/// <param name="Outstanding">The steps still running when their share of the budget ran out. They are still running.</param>
/// <param name="Failed">The steps that threw.</param>
/// <param name="Session">The session's own report, or null when the shell owned no session.</param>
/// <param name="Retained">Whether the session's dependencies were kept rather than disposed.</param>
/// <param name="RunCompleted">Whether the run's clean ending was written.</param>
/// <param name="Escalated">Whether the host was told to end because something was still running.</param>
public sealed record ExitReport(
    ExitOutcome Outcome,
    IReadOnlyList<string> Outstanding,
    IReadOnlyList<string> Failed,
    ShutdownReport? Session,
    bool Retained,
    bool RunCompleted,
    bool Escalated)
{
    public bool Clean => Outcome == ExitOutcome.Clean;
}

/// <summary>Ends the host process when the exit could not end it cleanly.</summary>
public interface IHostTerminator
{
    void Terminate(ExitReport report);
}

/// <summary>
/// The application's exit: one budget from the first step to the last, every step joined under what
/// is left of it, the session's dependencies disposed only once nothing uses them, and the host ended
/// by force when something would not finish.
/// </summary>
/// <remarks>
/// ARBITRARY IN-PROCESS WORK CANNOT BE JOINED BY FORCE. A step that will not finish is not made to;
/// it is named in the report, everything it might be using is kept rather than disposed under it,
/// nothing later in the order that depends on quiescence runs, and the host is told to end - the
/// process ends with the work still owned, which is the honest outcome, and the next launch reads a
/// run that was interrupted. A timeout is never called clean.
///
/// THE BUDGET BEGINS BEFORE THE FIRST AWAIT, at the exit's preparation, and includes the settings
/// drain, the session's shutdown, the polish warm-up, the heartbeat, the persistence and the log: the
/// twenty seconds the shell can promise to be gone in. Each step is given what is left when it begins;
/// a step given nothing is still issued and observed, and reported outstanding if it did not finish.
///
/// PREPARED ONCE, EXITED ONCE. Every path out of the app - the tray, the window, an update, a system
/// ending - reaches the same two cached tasks; a second caller shares the first's completion and no
/// step runs twice.
/// </remarks>
public sealed class ApplicationLifetime
{
    /// <summary>What the shell can promise to be gone in.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(20);

    private readonly LifetimeParts _parts;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly IHostTerminator _terminator;
    private readonly TimeSpan _budgetLength;
    private readonly object _lock = new();
    private StopBudget? _budget;
    private Task? _preparation;
    private Task<ExitReport>? _exit;
    private List<string> _preparationOutstanding = [];
    private List<string> _preparationFailed = [];

    public ApplicationLifetime(
        LifetimeParts parts,
        IAppLogger logger,
        TimeProvider clock,
        IHostTerminator terminator,
        TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(terminator);
        _parts = parts;
        _logger = logger;
        _clock = clock;
        _terminator = terminator;
        _budgetLength = budget ?? DefaultBudget;
        ArgumentOutOfRangeException.ThrowIfLessThan(_budgetLength, TimeSpan.Zero);
    }

    /// <summary>Whether leaving has begun: admission is closed and the budget is running.</summary>
    public bool Leaving
    {
        get
        {
            lock (_lock)
            {
                return _budget is not null;
            }
        }
    }

    /// <summary>
    /// The first half of leaving: admission closes before the first await and the budget starts;
    /// the settings write finishes; the shell closes its windows. Shared by every caller.
    /// </summary>
    public Task PrepareAsync()
    {
        lock (_lock)
        {
            return _preparation ??= PrepareCoreAsync();
        }
    }

    /// <summary>
    /// The whole of leaving, prepared first if it was not already: the exit under the budget, and the
    /// host ended by force if something would not finish. Shared by every caller.
    /// </summary>
    public Task<ExitReport> ExitAsync()
    {
        lock (_lock)
        {
            return _exit ??= ExitCoreAsync();
        }
    }

    private StopBudget Budget()
    {
        lock (_lock)
        {
            return _budget ??= new StopBudget(_budgetLength, _clock);
        }
    }

    private async Task PrepareCoreAsync()
    {
        // CLOSED BEFORE THE FIRST AWAIT, and the budget with it: a key that lands while the settings
        // drain waits is refused, and the drain is the first thing the twenty seconds pay for.
        var budget = Budget();
        _parts.CloseAdmission();
        var outstanding = new List<string>();
        var failed = new List<string>();
        await RunAsync(new LifetimeStep("settings drain", _parts.DrainSettings), budget, outstanding, failed);
        Try("shell closing", _parts.ShellClosing, failed);
        lock (_lock)
        {
            _preparationOutstanding = outstanding;
            _preparationFailed = failed;
        }
    }

    private async Task<ExitReport> ExitCoreAsync()
    {
        await PrepareAsync();
        var budget = Budget();
        List<string> outstanding;
        List<string> failed;
        lock (_lock)
        {
            outstanding = [.. _preparationOutstanding];
            failed = [.. _preparationFailed];
        }

        _parts.CancelProcessing();
        foreach (var step in _parts.ReleaseInputs)
        {
            await RunAsync(step, budget, outstanding, failed);
        }

        // THE SESSION SHUTS ITSELF DOWN under what is left; its report says whether anything still
        // uses it. A shell without a session has nothing here.
        ShutdownReport? session = null;
        if (_parts.ShutDownSession is { } shutDown)
        {
            try
            {
                session = await shutDown(budget.Left);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                failed.Add("session shutdown");
                RecordFailure();
            }
        }

        foreach (var step in _parts.Quiesce)
        {
            await RunAsync(step, budget, outstanding, failed);
        }

        // NOTHING IS DISPOSED UNDER SOMETHING STILL RUNNING. A step outstanding is still inside
        // whatever it uses; a session not quiescent has a command or a loop inside the engines and
        // the stores. Either keeps everything from here on, and the exit is unclean.
        var sessionQuiescent = _parts.ShutDownSession is null || session is { SessionQuiescent: true };
        var retained = outstanding.Count > 0 || !sessionQuiescent;
        if (!sessionQuiescent)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.ApplicationShutdownUnclean,
                AppFailureCategory.SystemLifecycle));
        }

        var runCompleted = false;
        if (!retained)
        {
            // A DISPOSAL THAT DOES NOT FINISH STOPS THE DISPOSALS: what it holds is kept, and so is
            // everything after it in the order, since the order is the dependency order.
            await DisposeAsync(_parts.DisposeSessionDependencies, budget, outstanding, failed);
            await DisposeAsync(_parts.DisposeShell, budget, outstanding, failed);
        }

        // ONLY A QUIESCENT, FINISHED, FAULTLESS EXIT IS WRITTEN AS CLEAN: the run's ending is what the
        // next launch trusts, and a timeout or a fault on the way out is an interrupted run.
        var clean = !retained && outstanding.Count == 0 && failed.Count == 0 && session is not { Clean: false };
        if (clean)
        {
            try
            {
                runCompleted = await _parts.CompleteRun();
                clean &= runCompleted;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
                failed.Add("run completion");
                RecordFailure();
                clean = false;
            }
        }

        if (clean)
        {
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.ApplicationCleanShutdown));
        }
        else
        {
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.UnhandledFailure, AppFailureCategory.Recovery));
        }

        if (!retained && outstanding.Count == 0)
        {
            await DisposeAsync(_parts.DisposeLast, budget, outstanding, failed);
        }

        // THE HOST IS ENDED BY FORCE ONLY WHEN SOMETHING WOULD NOT FINISH - a step of the shell's, or
        // the session's own work, which is still running and may be holding a thread or a worker the
        // process would otherwise wait on. The log says so before it is closed, and the terminator is
        // the last thing called.
        var escalated = retained || outstanding.Count > 0;
        if (escalated)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.ApplicationExitEscalated,
                AppFailureCategory.SystemLifecycle));
        }

        await RunAsync(new LifetimeStep("log", _parts.DisposeLogger), budget, outstanding, failed, record: false);

        var report = new ExitReport(
            clean ? ExitOutcome.Clean : ExitOutcome.Unclean,
            outstanding,
            failed,
            session,
            retained,
            runCompleted,
            escalated);
        if (escalated)
        {
            _terminator.Terminate(report);
        }

        return report;
    }

    /// <summary>Disposals in dependency order: the first that does not finish ends the run of them.</summary>
    private async Task DisposeAsync(IReadOnlyList<LifetimeStep> steps, StopBudget budget, List<string> outstanding, List<string> failed)
    {
        foreach (var step in steps)
        {
            if (outstanding.Count > 0)
            {
                return;
            }

            await RunAsync(step, budget, outstanding, failed);
        }
    }

    /// <summary>Runs one step under what is left of the budget; outstanding or failed, it is named and the exit carries on.</summary>
    private async Task RunAsync(LifetimeStep step, StopBudget budget, List<string> outstanding, List<string> failed, bool record = true)
    {
        try
        {
            var work = step.Run();
            if (await BoundedJoin.JoinAsync(work, budget.Left, _clock) == StopOutcome.StillRunning)
            {
                outstanding.Add(step.Name);
                // A LATE ENDING IS OBSERVED, NOT ACTED ON: its fault is taken off the task so nothing
                // surfaces it later, and nothing else happens - the exit did not wait for the step, so
                // the step's late effects reach a process that has already decided.
                _ = work.ContinueWith(
                    static task => _ = task.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Thrown on the way in or faulted inside the join (a cancelled ending counts as finished).
            failed.Add(step.Name);
            if (record)
            {
                RecordFailure();
            }
        }
    }

    private void Try(string name, Action action, List<string> failed)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            failed.Add(name);
            RecordFailure();
        }
    }

    private void RecordFailure() =>
        _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.UnhandledFailure, AppFailureCategory.Recovery));
}
