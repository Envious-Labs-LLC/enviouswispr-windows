using EnviousWispr.Core.Learning;

namespace EnviousWispr.PostProcessing.Learning;

/// <summary>
/// Deterministic reliability-floor learned-word checker: approves known observed-misspelling
/// candidates extracted from auto-learned words when no local contextual model is active.
/// </summary>
public sealed class DeterministicLearnedWordChecker : ILearnedWordChecker
{
    public string ArmName => "deterministic";

    public bool ScoresAreComparable => false;

    public Task<LearnedWordCheckResult?> DecideAsync(
        IReadOnlyList<LearnedWordCheckQuestion> questions,
        string? language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<LearnedWordCheckResult?>(cancellationToken);
        }

        var decisions = questions
            .Select(q => new LearnedWordCheckDecision(q.Id, Approved: true))
            .ToArray();
        return Task.FromResult<LearnedWordCheckResult?>(new LearnedWordCheckResult(
            decisions,
            ProviderId: null,
            UsedFallback: false));
    }
}
