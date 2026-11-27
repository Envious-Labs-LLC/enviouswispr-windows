using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;

namespace EnviousWispr.App.Composition;

/// <summary>
/// App-owned <see cref="IMishearingAdvisor"/> adapter that resolves the live polish setup at call
/// time, admits work under <see cref="WindowPresentationSession"/>, links closing cancellation, and
/// acquires the local runtime resource lease when needed.
/// </summary>
internal sealed class AdmittedMishearingAdvisor(
    Func<PolishSetup?> getPolishSetup,
    IRuntimeResourceAdmission resourceArbiter,
    WindowPresentationSession presentation) : IMishearingAdvisor
{
    private readonly Func<PolishSetup?> _getPolishSetup = getPolishSetup ?? throw new ArgumentNullException(nameof(getPolishSetup));
    private readonly IRuntimeResourceAdmission _resourceArbiter = resourceArbiter ?? throw new ArgumentNullException(nameof(resourceArbiter));
    private readonly WindowPresentationSession _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));

    public async Task<MishearingAdvice> SuggestAsync(
        string spokenForm,
        IReadOnlyList<string> existing,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_presentation.TryEnter(out var lease))
        {
            throw new OperationCanceledException(_presentation.Closing ? CancellationToken.None : cancellationToken);
        }

        using (lease)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Closing);

            IAsyncDisposable? resourceLease = null;
            try
            {
                if (_getPolishSetup() is not { Provider: IMishearingAdvisor advisor } setup)
                {
                    return MishearingAdvice.None(MishearingAdviceStatus.NotSupported);
                }

                if (setup.UsesLocalRuntime)
                {
                    var acquired = await _resourceArbiter.AcquireAsync(
                        setup.Resource,
                        RuntimeWorkloadKind.LocalPolish,
                        TimeSpan.FromSeconds(2),
                        linked.Token).ConfigureAwait(false);

                    if (!acquired.Succeeded || acquired.Lease is null)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        return MishearingAdvice.None(MishearingAdviceStatus.Failed);
                    }

                    resourceLease = acquired.Lease;
                }

                var advice = await advisor.SuggestAsync(spokenForm, existing, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                return advice;
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                linked.Token.ThrowIfCancellationRequested();
                return MishearingAdvice.None(MishearingAdviceStatus.Failed);
            }
            finally
            {
                if (resourceLease is not null)
                {
                    await resourceLease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
