using System.Threading.Channels;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Sessions;

namespace EnviousWispr.Pipeline;

/// <summary>What became of a submitted push-to-talk signal.</summary>
public enum SessionCommandDisposition
{
    /// <summary>The executor ran the command; <see cref="SessionCommandResult.Session"/> is its answer.</summary>
    Applied,

    /// <summary>A terminal signal arrived while an identical one was already waiting; the first one carries it.</summary>
    Ignored,

    /// <summary>A press arrived while the session gate was held or a command was pending; nothing was recorded.</summary>
    Busy,

    /// <summary>Admission had closed for shutdown before the command could run.</summary>
    Stopping,

    /// <summary>The executor threw; the exception was observed and logged by whoever owns the executor.</summary>
    Failed,
}

/// <param name="Disposition">What happened to the command.</param>
/// <param name="Session">The session the executor reported, when it reported one.</param>
/// <param name="WasQueued">
/// True when the command had to wait for an earlier command or an outside holder of the session gate
/// before it ran. This is the evidence that the window the queue exists for was actually entered.
/// </param>
public sealed record SessionCommandResult(
    SessionCommandDisposition Disposition,
    DictationSessionSnapshot? Session = null,
    bool WasQueued = false);

/// <summary>What a session command is: a key, Windows interrupting, or the recording's own limit.</summary>
public enum SessionCommandKind
{
    /// <summary>A push-to-talk signal from the hook, the auto-stop, or the shell.</summary>
    PushToTalk,

    /// <summary>Windows is locking or suspending: whatever is recording is preserved, whatever else is reset.</summary>
    Interruption,

    /// <summary>The recording has run for as long as it is allowed; if it is still the one recording, it is aborted.</summary>
    Timeout,
}

/// <param name="Kind">What the command is.</param>
/// <param name="Signal">For a push-to-talk command: the signal. Ignored for the other kinds.</param>
/// <param name="StartContext">For a press: what it was about, captured at admission. Null otherwise.</param>
/// <param name="Transition">For an interruption: which one.</param>
/// <param name="TimedOutSession">For a timeout: the recording that was armed.</param>
/// <param name="ForSession">
/// For a terminal that a loop posted on a recording's behalf - the auto-stop's release - the recording
/// it was for. Validated when the command runs, not when it was queued: a release a retired loop
/// posts late is for a recording that has ended, and must not end the one after it.
/// </param>
public sealed record SessionCommand(
    SessionCommandKind Kind,
    PushToTalkSignal Signal,
    RecordingStartContext? StartContext = null,
    SystemLifecycleTransition? Transition = null,
    DictationSessionId? TimedOutSession = null,
    DictationSessionId? ForSession = null)
{
    public SessionCommand(PushToTalkSignal signal, RecordingStartContext? startContext = null)
        : this(SessionCommandKind.PushToTalk, signal, startContext)
    {
    }

    /// <summary>A press takes the gate at admission; nothing else does.</summary>
    public bool IsPress => Kind == SessionCommandKind.PushToTalk && Signal == PushToTalkSignal.Pressed;

    /// <summary>The recording a terminal is for - a loop's release or a timeout names one; a key's terminal is for whatever is in flight.</summary>
    public DictationSessionId? TerminalIdentity => ForSession ?? TimedOutSession;

    /// <summary>
    /// A command that ends the recording in flight. One may wait at a time; a second is ignored,
    /// because the recording it would end is already ending. An interruption is not one: it runs
    /// whatever came before it, since Windows locking is a fact whether or not a release was queued.
    /// </summary>
    public bool IsTerminal => Kind switch
    {
        SessionCommandKind.PushToTalk => Signal != PushToTalkSignal.Pressed,
        SessionCommandKind.Timeout => true,
        _ => false,
    };
}

/// <summary>The body of one session command, run by the coordinator one at a time.</summary>
public interface ISessionCommandExecutor
{
    Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken);

    /// <summary>
    /// A queued interruption has waited longer than it may behind the command that was running when
    /// Windows locked. Runs outside the session, while that command is still running, as the shell's
    /// five-second wait used to report from outside the gate; the command itself will not run.
    /// </summary>
    Task ExpireAsync(SessionCommand command) => Task.CompletedTask;

    /// <summary>Whether a finalisation is in flight under its processing deadline.</summary>
    bool IsProcessing => false;

    /// <summary>
    /// Cancels the finalisation in flight, if any, now - not when the next command reaches the front
    /// of the queue. Windows locking and the shell's exit both need the transcription they are waiting
    /// behind to stop first.
    /// </summary>
    void CancelProcessing()
    {
    }

    /// <summary>Admission has closed for good: a delivery not yet issued is not issued from now on.</summary>
    void Close()
    {
    }

    /// <summary>
    /// The session's teardown, run only once nothing is using the session: the background work stopped
    /// under the deadline, each owner saying whether it finished, then whatever the executor's port
    /// disposes.
    /// </summary>
    Task<SessionTeardownReport> TearDownAsync(TimeSpan deadline) => Task.FromResult(SessionTeardownReport.Nothing);
}

/// <summary>
/// Accepts push-to-talk signals and runs them one at a time, keeping a release or a cancel that arrives
/// while the previous command is still running instead of throwing it away.
/// </summary>
/// <remarks>
/// THE ZERO-TIMEOUT GATE IT REPLACES DROPPED THE KEY-UP. A press holds the session gate for as long as
/// starting the recording takes, and starting a recording includes opening the microphone and starting
/// live preview, which on a busy machine is longer than a quick tap. The release arrived, found the gate
/// held, and was discarded without a word - so the recording ran on until the next press, and the person
/// who tapped the key saw nothing happen. Ref #86, and finding 1 on #148.
///
/// A PRESS AND A RELEASE ARE ADMITTED DIFFERENTLY, ON PURPOSE. A release or cancel is queued and runs when
/// the gate is next free, because the recording it ends already exists and must end. A press takes the
/// gate synchronously at admission or is refused as <see cref="SessionCommandDisposition.Busy"/>: a press
/// that ran later - after an update check that held the gate through a download, after lock recovery -
/// would open a microphone nobody was still asking for. That is the same answer the old zero-timeout
/// probe gave a press, now said out loud. The refusal happens before the first await, so a caller that
/// fires and forgets still gets the decision made at the moment it called.
///
/// ONE CONSUMER, SO A COMMAND RUNS AFTER THE ONE BEFORE IT FINISHES. A queued release therefore runs
/// after the press it belongs to has fully started, which is what the state machine expects. Two terminal
/// signals in a row are one: the second is <see cref="SessionCommandDisposition.Ignored"/>. The queue is
/// never longer than a press and the terminal that ends it.
///
/// THE SESSION IS OWNED HERE. The watchdog's timeout and Windows locking or suspending are commands on
/// this queue; the update check holds the session through <see cref="TryHold"/>; shutdown is
/// <see cref="ShutdownAsync"/>, which runs the executor's teardown after the last command. Nothing
/// outside this class takes the gate.
///
/// AN INTERRUPTION HAS FIVE SECONDS TO REACH THE FRONT. The shell used to wait that long for its gate
/// and then report recovery as pending, while whatever held the gate carried on. The queue keeps that:
/// an interruption admitted behind a running command expires if the command has not finished in
/// <see cref="InterruptionPatience"/>, the executor is told so at that moment - outside the session,
/// as the shell reported it - and the command is skipped when its turn comes.
/// </remarks>
public sealed class DictationSessionCoordinator : IAsyncDisposable
{
    /// <summary>How long an interruption may wait behind the command that was running when Windows locked.</summary>
    public static readonly TimeSpan InterruptionPatience = TimeSpan.FromSeconds(5);

    private readonly ISessionCommandExecutor _executor;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly TimeProvider _clock;
    private int _holds;
    private bool _gateDisposed;
    private readonly Func<RecordingStartContext>? _captureStartContext;
    private readonly Channel<QueuedCommand> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _admission = new();
    private readonly List<Task> _expiries = [];
    private bool _expiryFaulted;
    private readonly Task _consumer;
    private int _pendingOrRunning;
    private int _gateWaitsEntered;
    /// <summary>The recordings the pending terminals are for: null for a key's or a timeout's, which is for whatever is in flight.</summary>
    private readonly List<DictationSessionId?> _pendingTerminals = [];
    /// <summary>The recording in flight as the commands' results reported it: set by a press that started one, cleared by the terminal that ended it.</summary>
    private DictationSessionId? _recording;
    private bool _closed;
    private TaskCompletionSource? _noHolds;
    private Task<ShutdownReport>? _shutdown;

    /// <param name="executor">Runs one command at a time.</param>
    /// <param name="captureStartContext">
    /// Called synchronously when a press is admitted, before any await, so the target and delivery
    /// choice belong to the instant of the press rather than to whenever the consumer gets to it.
    /// </param>
    /// <param name="clock">Stamps admissions; an interruption that waited too long behind a running command stands down.</param>
    public DictationSessionCoordinator(
        ISessionCommandExecutor executor,
        Func<RecordingStartContext>? captureStartContext = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
        _clock = clock ?? TimeProvider.System;
        _captureStartContext = captureStartContext;
        _queue = Channel.CreateUnbounded<QueuedCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _consumer = ConsumeAsync();
    }

    /// <summary>How many commands are waiting or running. Exposed for tests and shutdown accounting.</summary>
    public int PendingCount => Volatile.Read(ref _pendingOrRunning);

    /// <summary>Whether nothing holds the session: no command running, no hold taken.</summary>
    internal bool IsIdle => !_gateDisposed && _sessionGate.CurrentCount == 1;

    /// <summary>
    /// Takes the session for something that is not a dictation - an update check that must not run
    /// under a recording - and refuses if anything is pending, running, or already holding it. A
    /// press admitted while the hold is held is <see cref="SessionCommandDisposition.Busy"/>; a
    /// terminal or an interruption waits for the hold to be released, as it waits for a command.
    /// </summary>
    public IDisposable? TryHold()
    {
        lock (_admission)
        {
            if (_closed || _pendingOrRunning > 0 || !_sessionGate.Wait(0))
            {
                return null;
            }

            _holds++;
            return new Hold(this);
        }
    }

    /// <summary>A hold is given back under the admission lock; a gate already disposed is not released.</summary>
    private void ReleaseHold()
    {
        lock (_admission)
        {
            _holds--;
            if (!_gateDisposed)
            {
                _sessionGate.Release();
            }

            if (_holds == 0)
            {
                _noHolds?.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Windows is locking or suspending. Admitted whatever else is queued - the fact is a fact - and
    /// run after it. If it has not reached the session in <see cref="InterruptionPatience"/>, the
    /// executor is told at that moment that recovery is pending - what the shell's five-second wait
    /// for its gate used to report - and the interruption is skipped when its turn comes.
    /// </summary>
    public Task<SessionCommandResult> InterruptAsync(SystemLifecycleTransition transition)
    {
        // THE DEADLINE IS CANCELLED HERE, NOW, not when the interruption reaches the front of the queue:
        // a finalisation in flight is what the queue is waiting behind, and cancelling it is how the
        // interruption gets its turn inside the five seconds it allows itself.
        _executor.CancelProcessing();
        return Submit(new SessionCommand(SessionCommandKind.Interruption, PushToTalkSignal.Cancelled, Transition: transition));
    }

    /// <summary>Whether a finalisation is in flight: a transcription or a delivery under its deadline.</summary>
    public bool IsProcessing => _executor.IsProcessing;

    /// <summary>Cancels the finalisation in flight, if any. The shell's exit calls this before it closes admission.</summary>
    public void CancelProcessing() => _executor.CancelProcessing();

    /// <summary>
    /// The recording armed as <paramref name="sessionId"/> has run for as long as it is allowed. A
    /// terminal like a release: if one is already waiting, the recording is ending anyway and this is
    /// ignored; when it runs, the executor checks the recording is still the one that was armed.
    /// </summary>
    public Task<SessionCommandResult> TimeOutAsync(DictationSessionId sessionId) =>
        Submit(new SessionCommand(SessionCommandKind.Timeout, PushToTalkSignal.Cancelled, TimedOutSession: sessionId));

    /// <summary>
    /// Refuses everything from now on, without waiting: nothing more is admitted, and a command already
    /// queued but not yet started is refused when its turn comes. The command running now finishes on
    /// its own terms. The shell calls this before its first await when leaving.
    /// </summary>
    public void Close()
    {
        lock (_admission)
        {
            _closed = true;
            _queue.Writer.TryComplete();
        }

        _executor.Close();
        _stopping.Cancel();
    }

    /// <summary>
    /// Shutdown: admission closes at once and the finalisation in flight is cancelled; the command
    /// running now, every expiry notification and every hold are then given <paramref name="budget"/>
    /// to finish; and only once nothing is using the session does the executor's teardown run under it,
    /// with what is left of the budget. What did not finish is named in the report, and nothing is torn
    /// down beside it. A second call shares the first's completion.
    /// </summary>
    /// <remarks>
    /// CANCELLATION IS NOT QUIESCENCE. A command asked to stop is still running until it says it has
    /// stopped; a hold is still held until it is given back. The old protocol ran the teardown beside a
    /// command that outlived its waits and made every command answer for a session gone from under it;
    /// this one waits, and if the wait is not enough, says so and leaves the command what it holds.
    /// </remarks>
    public Task<ShutdownReport> ShutdownAsync(TimeSpan budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        lock (_admission)
        {
            return _shutdown ??= ShutdownCoreAsync(budget);
        }
    }

    private async Task<ShutdownReport> ShutdownCoreAsync(TimeSpan budget)
    {
        var started = _clock.GetTimestamp();
        TimeSpan Remaining()
        {
            var remaining = budget - _clock.GetElapsedTime(started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        // CLOSED SYNCHRONOUSLY, before the first await: a press that lands after this call was made
        // is refused, whatever the caller does next. The finalisation in flight is NOT cancelled
        // here: whether to wait for a transcription or cut it short is the shell's exit policy, and
        // the shell cancels before it asks for the shutdown when it means to.
        Close();

        var workFinished = await OutstandingWorkFinishedAsync(Remaining()).ConfigureAwait(false);
        var holdsReleased = await HoldsReleasedAsync(Remaining()).ConfigureAwait(false);
        int holds;
        lock (_admission)
        {
            holds = _holds;
        }

        // A NOTIFICATION THAT THREW IS OVER, NOT OUTSTANDING: it is reported, because the shell's
        // status line faulted, but nothing of it is still running, and the teardown may proceed.
        var commandOutstanding = !_consumer.IsCompleted;
        var expiriesOutstanding = (!workFinished && !commandOutstanding) || Volatile.Read(ref _expiryFaulted);
        if (!workFinished || !holdsReleased)
        {
            return new ShutdownReport(ShutdownOutcome.Unclean, commandOutstanding, expiriesOutstanding, holds, Teardown: null);
        }

        // THE SESSION IS TAKEN FOR THE TEARDOWN. With no command and no hold left it is free; if it is
        // not, something this accounting did not see is using it, and the teardown does not run.
        if (!_sessionGate.Wait(0))
        {
            return new ShutdownReport(ShutdownOutcome.Unclean, CommandOutstanding: true, expiriesOutstanding, holds, Teardown: null);
        }

        try
        {
            var deadline = Remaining();
            var teardown = await _executor
                .TearDownAsync(deadline > TimeSpan.Zero ? deadline : TimeSpan.FromMilliseconds(1))
                .ConfigureAwait(false);
            return new ShutdownReport(ShutdownOutcome.Quiescent, CommandOutstanding: false, expiriesOutstanding, holds, teardown);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>Waits up to the timeout for every hold to be given back; true when none is out.</summary>
    private async Task<bool> HoldsReleasedAsync(TimeSpan timeout)
    {
        TaskCompletionSource waiter;
        lock (_admission)
        {
            if (_holds == 0)
            {
                return true;
            }

            waiter = _noHolds ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout, _clock)).ConfigureAwait(false);
        return ReferenceEquals(finished, waiter.Task);
    }

    /// <summary>
    /// How many times the consumer has actually parked on the session gate - counted only once the
    /// asynchronous wait exists, never on the fast path, so a test that reads one here knows a waiter
    /// is registered and not merely about to be.
    /// </summary>
    internal int GateWaitsEntered => Volatile.Read(ref _gateWaitsEntered);

    /// <summary>
    /// Admits the signal synchronously and returns a task that completes once it has run or been refused.
    /// </summary>
    public Task<SessionCommandResult> SubmitAsync(PushToTalkSignal signal) => SubmitAsync(signal, forSession: null);

    /// <summary>
    /// Admits the signal on a recording's behalf: a terminal posted by a loop that was watching that
    /// recording, ignored when it runs if that recording is no longer the one in flight.
    /// </summary>
    public Task<SessionCommandResult> SubmitAsync(PushToTalkSignal signal, DictationSessionId? forSession)
    {
        if (signal == PushToTalkSignal.QuickAdd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signal),
                signal,
                "Quick add is not a dictation session command.");
        }

        return Submit(new SessionCommand(signal) with { ForSession = forSession });
    }

    private Task<SessionCommandResult> Submit(SessionCommand command)
    {
        QueuedCommand queued;
        lock (_admission)
        {
            if (_closed)
            {
                return Task.FromResult(new SessionCommandResult(SessionCommandDisposition.Stopping));
            }

            var gateReserved = false;
            var startContext = command.StartContext;
            if (command.IsPress)
            {
                // The counter covers commands this queue knows about; the gate covers everybody else.
                // Both have to be free for a press, and the gate is taken here, now, so that nothing
                // can slip in between the decision and the start.
                if (_pendingOrRunning > 0 || !_sessionGate.Wait(0))
                {
                    return Task.FromResult(new SessionCommandResult(SessionCommandDisposition.Busy));
                }

                gateReserved = true;
                // STILL INSIDE THE CALLER'S FRAME. The hook reached the target capture synchronously
                // before this queue existed; capturing here keeps that true. A capture that throws hands
                // the gate back first: nothing has been queued yet, so nothing else ever would.
                try
                {
                    startContext = _captureStartContext?.Invoke();
                }
                catch
                {
                    _sessionGate.Release();
                    throw;
                }
            }
            else if (command.IsTerminal && IsDuplicateTerminal(command))
            {
                return Task.FromResult(new SessionCommandResult(SessionCommandDisposition.Ignored));
            }
            else if (command.IsTerminal)
            {
                _pendingTerminals.Add(command.TerminalIdentity);
            }

            // A terminal admitted while anything is ahead of it has, by definition, waited in the queue.
            // A press is admitted only when nothing is ahead, so it never has.
            queued = new QueuedCommand(command with { StartContext = startContext }, gateReserved)
            {
                WaitedInQueue = _pendingOrRunning > 0,
            };
            if (!_queue.Writer.TryWrite(queued))
            {
                // The writer is completed only by StopAsync, under this same lock and after _closed is
                // set, so an unbounded channel cannot refuse here. Loud rather than silent if that
                // ordering ever changes.
                if (gateReserved)
                {
                    _sessionGate.Release();
                }

                throw new InvalidOperationException("The session command queue refused a write while open.");
            }

            _pendingOrRunning++;

            if (command.Kind == SessionCommandKind.Interruption)
            {
                // OWNED WORK, NOT FIRE-AND-FORGET, AND OWNED FROM THE SAME LOCK THAT ADMITTED IT: the
                // stop's snapshot is taken under this lock after admission has closed, so an expiry
                // registered here is either in that snapshot or was refused before it existed. The
                // stop waits for every one it started, so a notification that finishes inside the
                // shutdown's patience lands before the shell tears down what it touches; one that
                // does not is reported as an unclean stop.
                _expiries.RemoveAll(expiry => expiry.IsCompleted);
                _expiries.Add(ExpireIfLateAsync(queued));
            }
        }

        return queued.Completion.Task;
    }

    /// <summary>
    /// The five seconds an interruption may wait. If the command has not started by then - whether it
    /// is behind a running command or parked behind a hold - it is marked expired, under the admission
    /// lock, so it either starts or expires, never both, and the executor is told, outside the
    /// session, while whatever is ahead of it carries on. Nothing is told after admission has closed.
    /// </summary>
    private async Task ExpireIfLateAsync(QueuedCommand queued)
    {
        try
        {
            await Task.Delay(InterruptionPatience, _clock, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: the command is refused when its turn comes, and nothing is reported.
            return;
        }

        lock (_admission)
        {
            if (queued.Started || _closed)
            {
                return;
            }

            queued.Expired = true;
        }

        try
        {
            await _executor.ExpireAsync(queued.Command).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // OBSERVED, NOT LOST. A notification that threw is a fault in the shell's status line, not
            // in the session; it is remembered and reported by the stop as an unclean one.
            Volatile.Write(ref _expiryFaulted, true);
        }
    }

    /// <summary>
    /// Closes admission, refuses everything still waiting, and waits for the running command to finish.
    /// </summary>
    /// <returns>True when the consumer reached quiescence within the timeout.</returns>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Close();

        // THE STOPPING TOKEN CANCELS A WAIT ON THE GATE, NOT THE COMMAND ALREADY RUNNING. A command in
        // flight owns a microphone or a transcription and finishes on its own terms; what must not
        // happen is a consumer parked on the shared gate forever after the shell has moved on.
        await _stopping.CancelAsync().ConfigureAwait(false);
        return await OutstandingWorkFinishedAsync(timeout).ConfigureAwait(false) && !Volatile.Read(ref _expiryFaulted);
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the consumer and every expiry notification it owns.
    /// THE EXPIRIES ARE JOINED WITH THE CONSUMER: each is either cancelled by the stopping token or
    /// already notifying, and a notification that finishes inside the wait is reported before anything
    /// it touches is torn down. False when the work outlived the wait - the caller then proceeds beside
    /// it and says so - or a notification threw.
    /// </summary>
    private async Task<bool> OutstandingWorkFinishedAsync(TimeSpan timeout)
    {
        Task[] expiries;
        lock (_admission)
        {
            expiries = [.. _expiries];
        }

        var work = Task.WhenAll([_consumer, .. expiries]);
        var finished = await Task.WhenAny(work, Task.Delay(timeout, _clock)).ConfigureAwait(false);
        return ReferenceEquals(finished, work);
    }

    public async ValueTask DisposeAsync()
    {
        // THE CANCELLATION SOURCE AND THE GATE OUTLIVE A CONSUMER THAT WOULD NOT STOP, AND THE GATE
        // OUTLIVES A HOLD STILL OUT. Disposing them under a running consumer turns the next token read
        // or gate release into an ObjectDisposedException inside the loop, and disposing the gate under
        // an update check still downloading would throw when that check gave its hold back. A stranded
        // pair is a few bytes, and an unclean stop has already been reported by StopAsync.
        if (await StopAsync(TimeSpan.Zero).ConfigureAwait(false) && !Volatile.Read(ref _expiryFaulted))
        {
            _stopping.Dispose();
            lock (_admission)
            {
                if (_holds == 0)
                {
                    _gateDisposed = true;
                    _sessionGate.Dispose();
                }
            }
        }
    }

    private async Task ConsumeAsync()
    {
        await Task.Yield();
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var queued))
            {
                await RunAsync(queued).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(QueuedCommand queued)
    {
        SessionCommandResult result;
        var holdingGate = queued.GateReserved;
        try
        {
            var waited = false;
            if (!holdingGate && !_stopping.IsCancellationRequested)
            {
                if (!_sessionGate.Wait(0))
                {
                    // THE WAITER EXISTS BEFORE IT IS COUNTED. A test that reads the count and then
                    // releases the gate must find a registered waiter, not a consumer about to
                    // probe again and succeed on its own.
                    var pending = _sessionGate.WaitAsync(_stopping.Token);
                    Interlocked.Increment(ref _gateWaitsEntered);
                    await pending.ConfigureAwait(false);
                    waited = true;
                }

                holdingGate = true;
            }

            // COMMITTED UNDER ONE LOCK, WITH THE GATE OWNED: still open, not expired, and only now
            // started. A wait on the gate that ended just before admission closed does not run; an
            // interruption that expired while parked behind a hold does not run; one that starts
            // here can no longer expire.
            bool committed;
            lock (_admission)
            {
                committed = holdingGate && !_closed && !queued.Expired;
                queued.Started = committed;
            }

            if (committed)
            {
                var executed = await _executor
                    .ExecuteAsync(queued.Command, _stopping.Token)
                    .ConfigureAwait(false);
                result = executed with { WasQueued = executed.WasQueued || waited || queued.WaitedInQueue };
            }
            else if (queued.Expired)
            {
                result = new SessionCommandResult(SessionCommandDisposition.Ignored, WasQueued: true);
            }
            else
            {
                result = new SessionCommandResult(SessionCommandDisposition.Stopping);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            result = new SessionCommandResult(SessionCommandDisposition.Stopping);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // THE EXECUTOR OWNS ITS OWN RECOVERY; this is the last line of defence for a consumer loop
            // that must outlive any single command. Admission is reopened BEFORE the submitter is told,
            // so a retry that runs on the fault's continuation finds the queue open rather than Busy.
            Undo(queued.Command);
            ReleaseGate(ref holdingGate);
            queued.Completion.TrySetException(exception);
            return;
        }

        // THE COUNT COMES DOWN BEFORE THE GATE GOES UP. A terminal admitted in the gap between the two
        // would otherwise be marked as having waited behind a press whose work and gate ownership had
        // both already ended, and the journey that reads that mark would certify an overlap that never
        // happened. Undercounting in the other direction only makes that journey say "not proven".
        NoteRecording(queued.Command, result);
        Undo(queued.Command);
        ReleaseGate(ref holdingGate);
        queued.Completion.TrySetResult(result);
    }

    private void ReleaseGate(ref bool holdingGate)
    {
        if (holdingGate)
        {
            holdingGate = false;
            _sessionGate.Release();
        }
    }

    private void Undo(SessionCommand command)
    {
        lock (_admission)
        {
            _pendingOrRunning--;
            if (command.IsTerminal)
            {
                _pendingTerminals.Remove(command.TerminalIdentity);
            }
        }
    }

    /// <summary>
    /// Whether a terminal is already waiting to end the same recording. A key's or a timeout's terminal
    /// is for whatever is in flight and stands in for any; one a loop posted for a named recording
    /// stands in for that recording only - for a key's terminal when that recording is the one in
    /// flight, never when it has ended. A release posted late for a take that is over must not swallow
    /// the key that ends the take after it.
    /// </summary>
    private bool IsDuplicateTerminal(SessionCommand command) =>
        _pendingTerminals.Contains(null) ||
        (command.TerminalIdentity is { } named
            ? _pendingTerminals.Contains(named)
            : _recording is { } recording && _pendingTerminals.Contains(recording));

    /// <summary>What a command's result says about the recording in flight, kept for the terminal coalescing above.</summary>
    private void NoteRecording(SessionCommand command, SessionCommandResult result)
    {
        lock (_admission)
        {
            if (command.IsPress)
            {
                if (result.Disposition == SessionCommandDisposition.Applied && result.Session is { } session)
                {
                    _recording = session.Id;
                }
            }
            else if (result.Disposition is SessionCommandDisposition.Applied or SessionCommandDisposition.Failed)
            {
                // A terminal or an interruption that ran ended the take, one way or another; one that
                // was ignored - for a recording that had already ended - says nothing about this one.
                _recording = null;
            }
        }
    }

    /// <summary>The session, held by something that is not a dictation; disposing gives it back.</summary>
    private sealed class Hold(DictationSessionCoordinator owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseHold();
            }
        }
    }

    private sealed class QueuedCommand(SessionCommand command, bool gateReserved)
    {
        public SessionCommand Command { get; } = command;

        /// <summary>A press reserved the gate at admission; the consumer inherits it rather than waiting again.</summary>
        public bool GateReserved { get; } = gateReserved;

        /// <summary>A terminal admitted behind another command: it waited in the queue, not only on the gate.</summary>
        public bool WaitedInQueue { get; init; }

        /// <summary>Set under the admission lock when the consumer takes the command up.</summary>
        public bool Started { get; set; }

        /// <summary>Set under the admission lock when an interruption waited too long; it will not run.</summary>
        public bool Expired { get; set; }

        public TaskCompletionSource<SessionCommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
