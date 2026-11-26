using System.Diagnostics;
using EnviousWispr.Core.Learning;
using EnviousWispr.Core.Settings;
using EnviousWispr.PostProcessing.Learning;

namespace EnviousWispr.Pipeline;

public sealed record LearnedWordCheckOutcome(
    string Text,
    int Flagged,
    int Approved,
    int Applied,
    int Contested,
    long ElapsedMilliseconds,
    string? ProviderId,
    bool UsedFallback);

/// <summary>
/// Runtime step that verifies occurrences of auto-learned aliases in contextual dictation output
/// under a bounded deadline, preserving unmodified text on cancellation, timeout, or checker absence.
/// Mirrors macOS <c>LearnedWordCheckStep</c>.
/// </summary>
public sealed class LearnedWordCheckStep
{
    public static readonly TimeSpan DefaultAnswerDeadline = TimeSpan.FromMilliseconds(1200);

    private readonly Func<ILearnedWordChecker?> _checkerAccessor;
    private readonly TimeSpan _answerDeadline;

    public LearnedWordCheckStep(
        Func<ILearnedWordChecker?> checkerAccessor,
        TimeSpan? answerDeadline = null)
    {
        _checkerAccessor = checkerAccessor ?? throw new ArgumentNullException(nameof(checkerAccessor));
        _answerDeadline = answerDeadline is { } d && d > TimeSpan.Zero ? d : DefaultAnswerDeadline;
    }

    public async Task<LearnedWordCheckOutcome> ProcessAsync(
        string text,
        IReadOnlyList<CustomWordEntry> customWords,
        string? language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(customWords);

        if (text.Length == 0 || customWords.Count == 0)
        {
            return new LearnedWordCheckOutcome(text, 0, 0, 0, 0, 0, null, false);
        }

        var timer = Stopwatch.StartNew();
        var learned = LearnedWordCandidates.ExtractLearnedWords(customWords);
        if (learned.Count == 0)
        {
            return new LearnedWordCheckOutcome(text, 0, 0, 0, 0, 0, null, false);
        }

        var searchBudget = _answerDeadline - timer.Elapsed;
        var questions = LearnedWordCandidates.Search(
            text,
            learned,
            searchBudget: searchBudget > TimeSpan.Zero ? searchBudget : TimeSpan.Zero);
        if (questions.Count == 0)
        {
            return new LearnedWordCheckOutcome(text, 0, 0, 0, 0, timer.ElapsedMilliseconds, null, false);
        }

        var checker = _checkerAccessor();
        if (checker is null)
        {
            return new LearnedWordCheckOutcome(text, questions.Count, 0, 0, 0, timer.ElapsedMilliseconds, null, false);
        }

        var remaining = _answerDeadline - timer.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return new LearnedWordCheckOutcome(
                text,
                questions.Count,
                0,
                0,
                0,
                timer.ElapsedMilliseconds,
                ProviderId: null,
                UsedFallback: true);
        }

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var decideTask = checker.DecideAsync(questions, language, deadlineCts.Token);
        var delayTask = Task.Delay(remaining, CancellationToken.None);
        var finished = await Task.WhenAny(decideTask, delayTask).ConfigureAwait(false);
        if (!ReferenceEquals(finished, decideTask))
        {
            deadlineCts.Cancel();
            timer.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            return new LearnedWordCheckOutcome(
                text,
                questions.Count,
                0,
                0,
                0,
                timer.ElapsedMilliseconds,
                ProviderId: null,
                UsedFallback: true);
        }

        LearnedWordCheckResult? result;
        try
        {
            result = await decideTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            timer.Stop();
            return new LearnedWordCheckOutcome(
                text,
                questions.Count,
                0,
                0,
                0,
                timer.ElapsedMilliseconds,
                ProviderId: null,
                UsedFallback: true);
        }

        timer.Stop();
        if (result is null || result.Decisions.Count == 0)
        {
            return new LearnedWordCheckOutcome(
                text,
                questions.Count,
                0,
                0,
                0,
                timer.ElapsedMilliseconds,
                result?.ProviderId,
                result?.UsedFallback ?? false);
        }

        var approvedCount = result.Decisions.Count(d => d.Approved);
        var applied = LearnedWordSpanApplier.Apply(
            text,
            questions,
            result.Decisions,
            checker.ScoresAreComparable);
        return new LearnedWordCheckOutcome(
            applied.Text,
            questions.Count,
            approvedCount,
            applied.Applied,
            applied.Contested,
            timer.ElapsedMilliseconds,
            result.ProviderId,
            result.UsedFallback);
    }
}
