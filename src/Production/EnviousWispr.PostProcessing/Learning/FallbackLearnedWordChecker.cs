using EnviousWispr.Core.Learning;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Prefers the primary on-device contextual checker (local EG-1) when warm and ready,
/// falling back cleanly to the deterministic checker whenever the primary returns null or faults.
/// </summary>
public sealed class FallbackLearnedWordChecker : ILearnedWordChecker
{
    private readonly Func<ILearnedWordChecker?> _primaryAccessor;
    private readonly ILearnedWordChecker _fallback;

    public FallbackLearnedWordChecker(
        Func<ILearnedWordChecker?> primaryAccessor,
        ILearnedWordChecker fallback)
    {
        _primaryAccessor = primaryAccessor ?? throw new ArgumentNullException(nameof(primaryAccessor));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public string ArmName => _primaryAccessor()?.ArmName ?? _fallback.ArmName;

    public bool ScoresAreComparable => _primaryAccessor()?.ScoresAreComparable ?? _fallback.ScoresAreComparable;

    public async Task<LearnedWordCheckResult?> DecideAsync(
        IReadOnlyList<LearnedWordCheckQuestion> questions,
        string? language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
        {
            return new LearnedWordCheckResult([], ProviderId: null, UsedFallback: false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_primaryAccessor() is { } primary)
        {
            try
            {
                var result = await primary.DecideAsync(questions, language, cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
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

        var fallbackResult = await _fallback.DecideAsync(questions, language, cancellationToken)
            .ConfigureAwait(false);
        return fallbackResult is null
            ? null
            : fallbackResult with { UsedFallback = true, ProviderId = null };
    }
}
