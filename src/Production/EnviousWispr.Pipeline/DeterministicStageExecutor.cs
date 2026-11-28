using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

internal sealed class DeterministicStageExecutor
{
    private readonly object _gate = new();
    private readonly Dictionary<IDeterministicTextStep, Task<DeterministicTextContext>> _invocations =
        new(ReferenceEqualityComparer.Instance);
    private readonly TimeProvider _timeProvider;
    private readonly TaskScheduler _taskScheduler;

    internal DeterministicStageExecutor(
        TimeProvider? timeProvider = null,
        TaskScheduler? taskScheduler = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _taskScheduler = taskScheduler ?? TaskScheduler.Default;
    }

    internal readonly record struct Result(
        DeterministicTextContext Context,
        DeterministicStageReceipt Receipt,
        bool IsDegraded);

    /// <summary>
    /// The invocation of a stage that is still running after its request moved on, or null. A test
    /// that wants to prove the stage becomes available again has to wait for the real task, because a
    /// signal raised from inside the step fires before the task itself has reached a terminal state.
    /// </summary>
    internal Task<DeterministicTextContext>? OutstandingInvocation(IDeterministicTextStep step)
    {
        lock (_gate)
        {
            return _invocations.TryGetValue(step, out var invocation) ? invocation : null;
        }
    }

    internal async Task<Result> ExecuteAsync(
        IDeterministicTextStep step,
        DeterministicTextContext input,
        Func<DeterministicTextContext, DeterministicTextContext, bool> hasChanged,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = input;
        var status = DeterministicStageStatus.Completed;
        var changed = false;
        var startedAt = _timeProvider.GetTimestamp();
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? deadline = null;
        Task<DeterministicTextContext> invocation;
        lock (_gate)
        {
            if (_invocations.TryGetValue(step, out var previous) && !previous.IsCompleted)
            {
                return new Result(
                    input,
                    new DeterministicStageReceipt(
                        step.Stage, DeterministicStageStatus.Busy, false, ElapsedMilliseconds(startedAt)),
                    true);
            }

            try
            {
                timeout = new CancellationTokenSource(step.GetTimeout(input), _timeProvider);
                deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var token = deadline.Token;
                invocation = Task.Factory.StartNew(
                    () => step.Process(input, token),
                    token,
                    TaskCreationOptions.DenyChildAttach,
                    _taskScheduler);
            }
            catch
            {
                // Nothing was scheduled, so nothing will ever run the disposal below.
                deadline?.Dispose();
                timeout?.Dispose();
                throw;
            }

            _invocations[step] = invocation;
        }

        try
        {
            context = await invocation.WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            if (context is null || context.Text is null)
            {
                throw new InvalidOperationException("A deterministic text stage returned no context.");
            }

            changed = hasChanged(input, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve the caller's token even when the step throws with the linked token.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            context = input;
            status = DeterministicStageStatus.TimedOut;
        }
        catch (TimeoutException)
        {
            context = input;
            status = DeterministicStageStatus.TimedOut;
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            // A CANCELLATION NOBODY ASKED FOR IS A FAILURE. The two catches above have already claimed
            // the caller's and the deadline's; an OperationCanceledException still standing here came
            // from inside the step for its own reasons, and the contract for a stage that stops short
            // is the last valid text, not an aborted pipeline.
            context = input;
            status = DeterministicStageStatus.Failed;
        }
        finally
        {
            // Keep the token alive until the worker exits, including after caller cancellation.
            // Observe late faults and remove only this invocation: a newer one may already exist.
            _ = invocation.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lock (_gate)
                {
                    if (_invocations.TryGetValue(step, out var current) && ReferenceEquals(current, completed))
                    {
                        _invocations.Remove(step);
                    }
                }

                deadline.Dispose();
                timeout.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // THE WHOLE ATTEMPT COUNTS. Both callers include validation and fallback handling in the
        // elapsed time, so a failed result cannot exclude the work needed to recover from it.
        return new Result(
            context,
            new DeterministicStageReceipt(step.Stage, status, changed, ElapsedMilliseconds(startedAt)),
            status != DeterministicStageStatus.Completed);
    }

    private long ElapsedMilliseconds(long startedAt) =>
        (long)_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
}
