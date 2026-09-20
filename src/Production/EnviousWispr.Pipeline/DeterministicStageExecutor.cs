using System.Diagnostics;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.Pipeline;

internal static class DeterministicStageExecutor
{
    internal readonly record struct Result(
        DeterministicTextContext Context,
        DeterministicStageReceipt Receipt,
        bool IsDegraded);

    internal static async Task<Result> ExecuteAsync(
        IDeterministicTextStep step,
        DeterministicTextContext input,
        Func<DeterministicTextContext, DeterministicTextContext, bool> hasChanged,
        CancellationToken cancellationToken)
    {
        var context = input;
        var status = DeterministicStageStatus.Completed;
        var changed = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            context = await Task.Run(() => step.Process(input), cancellationToken)
                .WaitAsync(step.Timeout, cancellationToken)
                .ConfigureAwait(false);
            if (context is null || context.Text is null)
            {
                throw new InvalidOperationException("A deterministic text stage returned no context.");
            }

            changed = hasChanged(input, context);
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

        // THE WHOLE ATTEMPT COUNTS. Both callers include validation and fallback handling in the
        // elapsed time, so a failed result cannot exclude the work needed to recover from it.
        stopwatch.Stop();
        return new Result(
            context,
            new DeterministicStageReceipt(step.Stage, status, changed, stopwatch.ElapsedMilliseconds),
            status != DeterministicStageStatus.Completed);
    }
}
