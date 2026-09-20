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
/// <param name="SubmittedAt">The clock's timestamp at admission; an interruption that waited too long stands down.</param>
public sealed record SessionCommand(
    SessionCommandKind Kind,
    PushToTalkSignal Signal,
    RecordingStartContext? StartContext = null,
    SystemLifecycleTransition? Transition = null,
    DictationSessionId? TimedOutSession = null,
    long SubmittedAt = 0)
{
    public SessionCommand(PushToTalkSignal signal, RecordingStartContext? startContext = null)
        : this(SessionCommandKind.PushToTalk, signal, startContext)
    {
    }

    /// <summary>A press takes the gate at admission; nothing else does.</summary>
    public bool IsPress => Kind == SessionCommandKind.PushToTalk && Signal == PushToTalkSignal.Pressed;

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

/// <summary>The body of one push-to-talk transition, run by the coordinator one at a time.</summary>
public interface ISessionCommandExecutor
{
    Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken);
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
/// THE SESSION GATE IS STILL OWNED BY THE SHELL, AND SHARED. The watchdog, the lock/suspend recovery, the
/// update check and shutdown all serialise against it, and they keep doing so. Moving those flows behind
/// this queue is a later step (#148, step 11); until then the gate is injected rather than created here.
/// </remarks>
public sealed class DictationSessionCoordinator : IAsyncDisposable
{
    private readonly ISessionCommandExecutor _executor;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly Func<RecordingStartContext>? _captureStartContext;
    private readonly Channel<QueuedCommand> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _admission = new();
    private readonly Task _consumer;
    private int _pendingOrRunning;
    private int _gateWaitsEntered;
    private bool _terminalPending;
    private bool _closed;

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
    internal bool IsIdle => _sessionGate.CurrentCount == 1;

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

            return new Hold(_sessionGate);
        }
    }

    /// <summary>
    /// Windows is locking or suspending. Admitted whatever else is queued - the fact is a fact - and
    /// run after it; an interruption that then waited longer than <paramref name="patience"/> stands
    /// down when it runs, reporting that recovery is still pending, which is what the shell's
    /// five-second wait for the session gate used to do.
    /// </summary>
    public Task<SessionCommandResult> InterruptAsync(SystemLifecycleTransition transition) =>
        Submit(new SessionCommand(
            SessionCommandKind.Interruption,
            PushToTalkSignal.Cancelled,
            Transition: transition,
            SubmittedAt: _clock.GetTimestamp()));

    /// <summary>
    /// The recording armed as <paramref name="sessionId"/> has run for as long as it is allowed. A
    /// terminal like a release: if one is already waiting, the recording is ending anyway and this is
    /// ignored; when it runs, the executor checks the recording is still the one that was armed.
    /// </summary>
    public Task<SessionCommandResult> TimeOutAsync(DictationSessionId sessionId) =>
        Submit(new SessionCommand(SessionCommandKind.Timeout, PushToTalkSignal.Cancelled, TimedOutSession: sessionId));

    /// <summary>Refuses everything from now on, without waiting; the shell calls this when leaving.</summary>
    public void Close()
    {
        lock (_admission)
        {
            _closed = true;
            _queue.Writer.TryComplete();
        }
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
    public Task<SessionCommandResult> SubmitAsync(PushToTalkSignal signal)
    {
        if (signal == PushToTalkSignal.QuickAdd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signal),
                signal,
                "Quick add is not a dictation session command.");
        }

        return Submit(new SessionCommand(signal));
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
            else if (command.IsTerminal && _terminalPending)
            {
                return Task.FromResult(new SessionCommandResult(SessionCommandDisposition.Ignored));
            }
            else if (command.IsTerminal)
            {
                _terminalPending = true;
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
        }

        return queued.Completion.Task;
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
        var finished = await Task.WhenAny(_consumer, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, _consumer);
    }

    public async ValueTask DisposeAsync()
    {
        // THE CANCELLATION SOURCE AND THE GATE OUTLIVE A CONSUMER THAT WOULD NOT STOP. Disposing them
        // under a running consumer turns the next token read or gate release into an
        // ObjectDisposedException inside the loop; a stranded pair is a few bytes, and an unclean stop
        // has already been reported by StopAsync.
        if (await StopAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            _stopping.Dispose();
            _sessionGate.Dispose();
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
            if (_stopping.IsCancellationRequested)
            {
                result = new SessionCommandResult(SessionCommandDisposition.Stopping);
            }
            else
            {
                var waited = false;
                if (!holdingGate)
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

                var executed = await _executor
                    .ExecuteAsync(queued.Command, _stopping.Token)
                    .ConfigureAwait(false);
                result = executed with { WasQueued = executed.WasQueued || waited || queued.WaitedInQueue };
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
                _terminalPending = false;
            }
        }
    }

    /// <summary>The session, held by something that is not a dictation; disposing gives it back.</summary>
    private sealed class Hold(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
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

        public TaskCompletionSource<SessionCommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
