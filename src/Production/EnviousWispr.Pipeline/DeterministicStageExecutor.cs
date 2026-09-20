using System.Diagnostics;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

internal sealed class DeterministicStageExecutor
{
    private readonly object _gate = new();
    private readonly Dictionary<IDeterministicTextStep, Task<DeterministicTextContext>> _invocations =
        new(ReferenceEqualityComparer.Instance);

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
        var stopwatch = Stopwatch.StartNew();
        CancellationTokenSource deadline;
        Task<DeterministicTextContext> invocation;
        lock (_gate)
        {
            if (_invocations.TryGetValue(step, out var previous) && !previous.IsCompleted)
            {
                return new Result(
                    input,
                    new DeterministicStageReceipt(
                        step.Stage, DeterministicStageStatus.Busy, false, stopwatch.ElapsedMilliseconds),
                    true);
            }

            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(step.Timeout);
            invocation = Task.Run(() => step.Process(input, deadline.Token), deadline.Token);
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
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            context = input;
            status = DeterministicStageStatus.TimedOut;
        }
        catch (TimeoutException)
        {
            context = input;
            status = DeterministicStageStatus.TimedOut;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or StackOverflowException or OutOfMemoryException))
        {
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
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // THE WHOLE ATTEMPT COUNTS. Both callers include validation and fallback handling in the
        // elapsed time, so a failed result cannot exclude the work needed to recover from it.
        stopwatch.Stop();
        return new Result(
            context,
            new DeterministicStageReceipt(step.Stage, status, changed, stopwatch.ElapsedMilliseconds),
            status != DeterministicStageStatus.Completed);
    }
}
