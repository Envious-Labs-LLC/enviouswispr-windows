using EnviousWispr.Core.Learning;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Combines an on-device LLM judge (e.g. local EG-1) with a deterministic rules fallback
/// whenever the primary judge is unavailable, deferred, timed out, or returns malformed output.
/// Explicit cancellation propagates without falling back.
/// </summary>
public sealed class FallbackCorrectionJudge : ICorrectionJudge
{
    private readonly Func<ICorrectionJudge?> _primaryAccessor;
    private readonly ICorrectionJudge _fallback;

    public FallbackCorrectionJudge(
        Func<ICorrectionJudge?> primaryAccessor,
        ICorrectionJudge fallback)
    {
        _primaryAccessor = primaryAccessor ?? throw new ArgumentNullException(nameof(primaryAccessor));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async ValueTask<CorrectionJudgeCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_primaryAccessor() is { } primary)
        {
            try
            {
                var caps = await primary.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
                if (caps.CanRunOnThisDevice)
                {
                    return caps;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
            }
        }

        return await _fallback.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CorrectionJudgeOutcome> JudgeAsync(
        CorrectionJudgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return new CorrectionJudgeOutcome.Bypass(CorrectionJudgeBypass.Cancelled);
        }

        if (_primaryAccessor() is { } primary)
        {
            try
            {
                var outcome = await primary.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
                if (outcome is CorrectionJudgeOutcome.Verdict)
                {
                    return outcome;
                }

                if (outcome is CorrectionJudgeOutcome.Bypass(CorrectionJudgeBypass.Cancelled))
                {
                    return outcome;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CorrectionJudgeOutcome.Bypass(CorrectionJudgeBypass.Cancelled);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
            {
            }
        }

        return await _fallback.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
