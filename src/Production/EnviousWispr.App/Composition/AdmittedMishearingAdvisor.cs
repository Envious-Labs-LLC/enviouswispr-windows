using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;

namespace EnviousWispr.App.Composition;

/// <summary>
/// App-owned <see cref="IMishearingAdvisor"/> adapter that resolves the live polish provider at call
/// time, admits work under <see cref="WindowPresentationSession"/>, links closing cancellation, and
/// acquires the local runtime resource lease when needed.
/// </summary>
internal sealed class AdmittedMishearingAdvisor(
    Func<IPolishProvider?> getProvider,
    Func<bool> usesLocalRuntime,
    Func<IPolishProvider, RuntimeResourceKind> resolveLocalResource,
    IRuntimeResourceAdmission resourceArbiter,
    WindowPresentationSession presentation) : IMishearingAdvisor
{
    private readonly Func<IPolishProvider?> _getProvider = getProvider ?? throw new ArgumentNullException(nameof(getProvider));
    private readonly Func<bool> _usesLocalRuntime = usesLocalRuntime ?? throw new ArgumentNullException(nameof(usesLocalRuntime));
    private readonly Func<IPolishProvider, RuntimeResourceKind> _resolveLocalResource = resolveLocalResource ?? throw new ArgumentNullException(nameof(resolveLocalResource));
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

            if (_getProvider() is not IMishearingAdvisor advisor)
            {
                return MishearingAdvice.None(MishearingAdviceStatus.NotSupported);
            }

            IAsyncDisposable? resourceLease = null;
            if (_usesLocalRuntime() && _getProvider() is { } localProvider)
            {
                RuntimeResourceAcquireResult acquired;
                try
                {
                    acquired = await _resourceArbiter.AcquireAsync(
                        _resolveLocalResource(localProvider),
                        RuntimeWorkloadKind.LocalPolish,
                        TimeSpan.FromSeconds(2),
                        linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    throw new OperationCanceledException(lease.Closing);
                }

                if (!acquired.Succeeded || acquired.Lease is null)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    return MishearingAdvice.None(MishearingAdviceStatus.Failed);
                }

                resourceLease = acquired.Lease;
            }

            try
            {
                return await advisor.SuggestAsync(spokenForm, existing, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
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
