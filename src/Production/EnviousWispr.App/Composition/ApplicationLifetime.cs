using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Reliability;
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
/// <param name="DrainPresentation">Closes the presentation's gate, stops and joins the work inside it, and finishes the settings write in flight, so a choice just made is not lost and nothing of the window's is still inside what the exit disposes.</param>
/// <param name="ShellClosing">What the shell does once the settings are safe and before anything is torn down: its windows, its own log line.</param>
/// <param name="AbortPolishRuntime">
/// The shell's exit policy for the local polish runtime, run right after the shell closes and before
/// the finalisation is cancelled or the session asked to shut down: the runtime's process is ended by
/// force, synchronously - killed with its tree and its handles disposed before this returns - so a
/// polish in flight fails at once and the finalisation goes on with the unpolished words rather than
/// holding the budget for an answer that is not coming. The provider's own disposal, later, finds
/// nothing to stop. Nothing when no local runtime was started.
/// </param>
/// <param name="CancelProcessing">The shell's exit policy for a transcription in flight, made before the session is asked to shut down.</param>
/// <param name="ReleaseInputs">The input sources, unsubscribed and disposed first so nothing new arrives.</param>
/// <param name="ShutDownSession">The session's own shutdown under the budget it is handed (step 8); null when the shell owns no session.</param>
/// <param name="Quiesce">Work the shell started that must be over before anything it uses is disposed: the polish warm-up, the heartbeat.</param>
/// <param name="DisposeSessionDependencies">What a session uses: run only behind a quiescent session and finished quiescence steps.</param>
/// <param name="DisposeShell">The shell's own services, run only when nothing is outstanding. The single-instance lock is not among them: it is held to the process's end, so no other launch writes the record before this one has.</param>
/// <param name="CompleteRun">
/// Writes the run's clean ending: the one publication the next launch trusts. Asked only when everything
/// before it finished and nothing failed, under what is left of the budget, with a token cancelled when
/// that runs out - a write not begun by then is never begun - and under a fence the write commits
/// through and the exit abandons through, so the record is never replaced after the exit stopped
/// waiting. False when the store refused.
/// </param>
/// <param name="CloseRunState">Closes the run-state store: only once nothing that writes to it is outstanding. Best effort, reported if it throws.</param>
/// <param name="DisposeLogger">The log, flushed and closed as the last act whatever the outcome; best effort.</param>
public sealed record LifetimeParts(
    Action CloseAdmission,
    Func<Task> DrainPresentation,
    Action ShellClosing,
    Action AbortPolishRuntime,
    Action CancelProcessing,
    IReadOnlyList<LifetimeStep> ReleaseInputs,
    Func<TimeSpan, Task<ShutdownReport>>? ShutDownSession,
    IReadOnlyList<LifetimeStep> Quiesce,
    IReadOnlyList<LifetimeStep> DisposeSessionDependencies,
    IReadOnlyList<LifetimeStep> DisposeShell,
    Func<PublicationFence, CancellationToken, Task<bool>> CompleteRun,
    Action CloseRunState,
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
/// is left of it, the session's dependencies disposed only once nothing uses them, the run's clean
/// ending written only by an exit that earned it, and the host ended by force when something would
/// not finish - by the exit itself when it can conclude, by a watchdog on its own thread when it
/// cannot.
/// </summary>
/// <remarks>
/// ARBITRARY IN-PROCESS WORK CANNOT BE JOINED BY FORCE. A step that will not finish is not made to;
/// it is named in the report, everything it might be using is kept rather than disposed under it,
/// nothing later in the order that depends on quiescence runs, and the host is told to end - the
/// process ends with the work still owned, which is the honest outcome, and the next launch reads a
/// run that was interrupted. A timeout is never called clean.
///
/// THE BUDGET BEGINS BEFORE THE FIRST AWAIT, at the exit's preparation, and includes the settings
/// drain, the session's shutdown, the polish warm-up, the heartbeat, the disposals, the run's
/// completion and the log: the twenty seconds the shell can promise to be gone in. Each step is given
/// what is left when it begins; a step given nothing is still issued and observed, and reported
/// outstanding if it did not finish.
///
/// THE WATCHDOG STANDS BEHIND THE BUDGET ON ITS OWN THREAD. A join can only bound a step that
/// returned a task; a step that blocks the thread it was called on - a native disposal, a synchronous
/// write - blocks the exit with it, and its continuations never reach the terminator. So the clock is
/// asked, at the first step, for a timer two seconds behind the budget: an exit that concludes on its
/// own never meets it, and one that cannot is ended by it, the step it was inside named. The log is
/// attempted from beside it and not waited for - the blocked thread may be inside the log.
///
/// THE PUBLICATION IS THE LAST OF THE RUN'S WORK, AND IT IS FENCED. Everything the run had to finish -
/// the session, the quiescence, every disposal - runs before the run's clean ending is written. The
/// write is under the remainder with a token cancelled when it runs out, so a write not begun by the
/// deadline never begins; and it commits through a fence the exit abandons through, so a write the
/// exit stopped waiting for never replaces the record - and if it committed first, the exit learns
/// that and reports the run completed, while still reporting the writer outstanding: committed is not
/// finished, and a store closed under a writer's tail faults it. After it only the two handles that
/// wrote it are closed: the store, only once nothing that writes to it is outstanding, and the log,
/// always, both reported if they do not. Diagnostic closure is not the run's work: a log that will not close unmakes the
/// report's clean verdict and ends the host, but the completion already committed stands, because
/// everything the run had to finish had finished.
///
/// PREPARED ONCE, EXITED ONCE. Every path out of the app - the tray, the window, an update, a system
/// ending - reaches the same two cached tasks; a second caller shares the first's completion and no
/// step runs twice.
/// </remarks>
public sealed class ApplicationLifetime
{
    /// <summary>What the shell can promise to be gone in.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(20);

    /// <summary>How far behind the budget the watchdog stands: an exit that concludes on its last tick is never pre-empted.</summary>
    public static readonly TimeSpan WatchGrace = TimeSpan.FromSeconds(2);

    private readonly LifetimeParts _parts;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _clock;
    private readonly IHostTerminator _terminator;
    private readonly TimeSpan _budgetLength;
    private readonly object _lock = new();
    private StopBudget? _budget;
    private ITimer? _watch;
    private Task? _preparation;
    private Task<ExitReport>? _exit;
    private List<string> _preparationOutstanding = [];
    private List<string> _preparationFailed = [];
    private string? _current;
    private int _terminated;
    private int _concluded;

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

    /// <summary>The budget and, with it, the watchdog: both begin at the first call, before anything is awaited.</summary>
    private StopBudget Budget()
    {
        lock (_lock)
        {
            if (_budget is null)
            {
                _budget = new StopBudget(_budgetLength, _clock);
                _watch = _clock.CreateTimer(
                    static state => ((ApplicationLifetime)state!).OnDeadline(),
                    this,
                    _budgetLength + WatchGrace,
                    Timeout.InfiniteTimeSpan);
            }

            return _budget;
        }
    }

    private async Task PrepareCoreAsync()
    {
        // CLOSED BEFORE THE FIRST AWAIT, and the budget with it: a key that lands while the
        // presentation drains is refused, and the drain is the first thing the twenty seconds pay for.
        var budget = Budget();
        var outstanding = new List<string>();
        var failed = new List<string>();
        Try("admission", _parts.CloseAdmission, failed);
        await RunAsync(new LifetimeStep("presentation drain", _parts.DrainPresentation), budget, outstanding, failed);
        Try("shell closing", _parts.ShellClosing, failed);
        Try("polish runtime abort", _parts.AbortPolishRuntime, failed);
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

        Try("exit policy", _parts.CancelProcessing, failed);
        foreach (var step in _parts.ReleaseInputs)
        {
            await RunAsync(step, budget, outstanding, failed);
        }

        // THE SESSION SHUTS ITSELF DOWN under what is left; its report says whether anything still
        // uses it. A shell without a session has nothing here.
        ShutdownReport? session = null;
        if (_parts.ShutDownSession is { } shutDown)
        {
            Volatile.Write(ref _current, "session shutdown");
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

        if (!retained)
        {
            // A DISPOSAL THAT DOES NOT FINISH STOPS THE DISPOSALS: what it holds is kept, and so is
            // everything after it in the order, since the order is the dependency order.
            await DisposeAsync(_parts.DisposeSessionDependencies, budget, outstanding, failed);
            await DisposeAsync(_parts.DisposeShell, budget, outstanding, failed);
        }

        // ONLY A QUIESCENT, FINISHED, FAULTLESS EXIT PUBLISHES A CLEAN RUN, and only inside the budget:
        // the run's ending is what the next launch trusts, and a timeout or a fault on the way out is
        // an interrupted run.
        var runCompleted = false;
        if (!retained && outstanding.Count == 0 && failed.Count == 0 && session is not { Clean: false })
        {
            runCompleted = await CompleteRunAsync(budget, outstanding, failed);
        }

        // THE STORE IS CLOSED ONLY ONCE NOTHING THAT WRITES TO IT IS OUTSTANDING: a heartbeat still
        // joining, a completion still committing, a session command still to write its last edge -
        // each is inside the store, and a store disposed under it faults the write. Kept otherwise.
        if (!retained && outstanding.Count == 0)
        {
            Try("run-state store", _parts.CloseRunState, failed);
        }

        // WHAT IS SAID BEFORE THE LOG CLOSES: whether the run's ending was written - the line mirrors
        // the record, so a run completed by a writer the exit then stopped waiting for is still said
        // to have completed, with the escalation beside it - and the escalation if it is already
        // known. The log's own closing is the one step that can still go wrong after this, and it is
        // reported through the terminator and the returned report rather than the log.
        if (runCompleted)
        {
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.ApplicationCleanShutdown));
        }
        else
        {
            _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.UnhandledFailure, AppFailureCategory.Recovery));
        }

        if (retained || outstanding.Count > 0)
        {
            _logger.Write(new AppLogEntry(
                _clock.GetUtcNow(),
                AppEventCode.ApplicationExitEscalated,
                AppFailureCategory.SystemLifecycle));
        }

        await RunAsync(new LifetimeStep("log", _parts.DisposeLogger), budget, outstanding, failed, record: false);

        // THE VERDICT IS TAKEN LAST, after every step including the log's closing: a log that did not
        // close is outstanding like any other step, ends the host, and unmakes a clean report - the
        // run's ending already written stands, because everything the run had to finish had finished.
        var escalated = retained || outstanding.Count > 0;
        var clean = runCompleted && outstanding.Count == 0 && failed.Count == 0;
        var report = new ExitReport(
            clean ? ExitOutcome.Clean : ExitOutcome.Unclean,
            outstanding,
            failed,
            session,
            retained,
            runCompleted,
            escalated);
        Conclude();
        if (escalated)
        {
            TerminateOnce(report);
        }

        return report;
    }

    /// <summary>The run's completion: joined under the remainder, bounded by a token that runs out with it, and fenced so a commit and an abandonment can never both happen.</summary>
    private async Task<bool> CompleteRunAsync(StopBudget budget, List<string> outstanding, List<string> failed)
    {
        Volatile.Write(ref _current, "run completion");
        var remaining = budget.Left;
        var fence = new PublicationFence();
        using var patience = new CancellationTokenSource(remaining, _clock);
        Task<bool> write;
        try
        {
            write = _parts.CompleteRun(fence, patience.Token);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            failed.Add("run completion");
            RecordFailure();
            return false;
        }

        try
        {
            return await write.WaitAsync(remaining, _clock);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // NOT FINISHED INSIDE THE BUDGET. The fence decides what the next launch reads: abandoned
            // here before the store's commit, the record is never replaced; committed first, the
            // record already says clean and the exit says the run completed. EITHER WAY THE WRITER IS
            // STILL RUNNING - after a commit it has its gate to let go of and its temporary file to
            // remove - so the completion is outstanding, the store is kept for it, and the host is
            // ended: the publication and the writer's finishing are two facts, reported as two.
            outstanding.Add("run completion");
            _ = write.ContinueWith(
                static task => _ = task.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return !fence.TryAbandon();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            failed.Add("run completion");
            RecordFailure();
            return false;
        }
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
        Volatile.Write(ref _current, step.Name);
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
        Volatile.Write(ref _current, name);
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

    /// <summary>The watchdog's turn: the exit has not concluded two seconds past its budget, so it cannot; the host is ended from here.</summary>
    private void OnDeadline()
    {
        if (Volatile.Read(ref _concluded) != 0)
        {
            return;
        }

        var inside = Volatile.Read(ref _current) ?? "exit";
        var report = new ExitReport(
            ExitOutcome.Unclean,
            [inside],
            [],
            Session: null,
            Retained: true,
            RunCompleted: false,
            Escalated: true);
        // THE LOG IS ATTEMPTED, NOT WAITED FOR: the thread that blocked may be inside the log itself,
        // and a wait on it here would be the very hang this exists to end.
        _ = Task.Run(() => _logger.Write(new AppLogEntry(
            _clock.GetUtcNow(),
            AppEventCode.ApplicationExitEscalated,
            AppFailureCategory.SystemLifecycle)));
        TerminateOnce(report);
    }

    private void Conclude()
    {
        Volatile.Write(ref _concluded, 1);
        ITimer? watch;
        lock (_lock)
        {
            watch = _watch;
            _watch = null;
        }

        watch?.Dispose();
    }

    private void TerminateOnce(ExitReport report)
    {
        if (Interlocked.Exchange(ref _terminated, 1) == 0)
        {
            _terminator.Terminate(report);
        }
    }

    private void RecordFailure() =>
        _logger.Write(new AppLogEntry(_clock.GetUtcNow(), AppEventCode.UnhandledFailure, AppFailureCategory.Recovery));
}
