using System.Threading.Channels;
using EnviousWispr.Core.Input;
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

public sealed record SessionCommand(PushToTalkSignal Signal);

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
    private readonly SemaphoreSlim _sessionGate;
    private readonly Channel<QueuedCommand> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _admission = new();
    private readonly Task _consumer;
    private int _pendingOrRunning;
    private int _gateWaitsEntered;
    private bool _terminalPending;
    private bool _closed;

    public DictationSessionCoordinator(ISessionCommandExecutor executor, SemaphoreSlim sessionGate)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sessionGate);
        _executor = executor;
        _sessionGate = sessionGate;
        _queue = Channel.CreateUnbounded<QueuedCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _consumer = ConsumeAsync();
    }

    /// <summary>How many commands are waiting or running. Exposed for tests and shutdown accounting.</summary>
    public int PendingCount => Volatile.Read(ref _pendingOrRunning);

    /// <summary>
    /// How many times the consumer has parked on the session gate. A test that wants to prove a stop
    /// releases a parked consumer needs to know the consumer was parked first.
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

        QueuedCommand queued;
        lock (_admission)
        {
            if (_closed)
            {
                return Task.FromResult(new SessionCommandResult(SessionCommandDisposition.Stopping));
            }

            var gateReserved = false;
            if (signal == PushToTalkSignal.Pressed)
            {
                // The counter covers commands this queue knows about; the gate covers everybody else.
                // Both have to be free for a press, and the gate is taken here, now, so that nothing
                // can slip in between the decision and the start.
                if (_pendingOrRunning > 0 || !_sessionGate.Wait(0))
                {
                    return Task.FromResult(new SessionCommandResult(SessionCommandDisposition.Busy));
                }

                gateReserved = true;
            }
            else if (_terminalPending)
            {
                return Task.FromResult(new SessionCommandResult(SessionCommandDisposition.Ignored));
            }
            else
            {
                _terminalPending = true;
            }

            // A terminal admitted while anything is ahead of it has, by definition, waited in the queue.
            // A press is admitted only when nothing is ahead, so it never has.
            queued = new QueuedCommand(new SessionCommand(signal), gateReserved)
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
        lock (_admission)
        {
            _closed = true;
            _queue.Writer.TryComplete();
        }

        // THE STOPPING TOKEN CANCELS A WAIT ON THE GATE, NOT THE COMMAND ALREADY RUNNING. A command in
        // flight owns a microphone or a transcription and finishes on its own terms; what must not
        // happen is a consumer parked on the shared gate forever after the shell has moved on.
        await _stopping.CancelAsync().ConfigureAwait(false);
        var finished = await Task.WhenAny(_consumer, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, _consumer);
    }

    public async ValueTask DisposeAsync()
    {
        // THE CANCELLATION SOURCE OUTLIVES A CONSUMER THAT WOULD NOT STOP. Disposing it under a running
        // consumer turns the next token read into an ObjectDisposedException inside the loop; a
        // stranded source is a few bytes, and an unclean stop has already been reported by StopAsync.
        if (await StopAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            _stopping.Dispose();
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
                    Interlocked.Increment(ref _gateWaitsEntered);
                    waited = !_sessionGate.Wait(0);
                    if (waited)
                    {
                        await _sessionGate.WaitAsync(_stopping.Token).ConfigureAwait(false);
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
            ReleaseGate(ref holdingGate);
            Undo(queued.Command);
            queued.Completion.TrySetException(exception);
            return;
        }
        finally
        {
            ReleaseGate(ref holdingGate);
        }

        Undo(queued.Command);
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
            if (command.Signal != PushToTalkSignal.Pressed)
            {
                _terminalPending = false;
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
