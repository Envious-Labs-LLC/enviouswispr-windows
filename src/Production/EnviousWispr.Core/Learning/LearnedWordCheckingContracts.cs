namespace EnviousWispr.Core.Learning;

/// <summary>One auto-learned dictionary word with the spoken misspellings that may trigger it.</summary>
public sealed record LearnedWord(
    string Canonical,
    IReadOnlyList<string> ObservedMisspellings);

/// <summary>
/// One occurrence of an observed misspelling in post-processed text, paired with bounded context
/// for runtime contextual verification before applying <see cref="Word"/>.
/// Mirrors macOS <c>LearnedWordCheckQuestion</c>.
/// </summary>
public sealed class LearnedWordCheckQuestion
{
    public LearnedWordCheckQuestion(
        int id,
        string sentence,
        int rangeStart,
        int rangeLength,
        int contextStart,
        int contextLength,
        string word)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        ArgumentException.ThrowIfNullOrEmpty(word);
        if (id < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Question id must be >= 1.");
        }

        if (contextStart < 0 ||
            contextLength < 0 ||
            contextStart > sentence.Length ||
            contextStart + contextLength > sentence.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(contextStart), "Context window is out of bounds.");
        }

        if (rangeLength <= 0 ||
            rangeStart < contextStart ||
            rangeStart + rangeLength > contextStart + contextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStart), "Candidate range must sit inside context window.");
        }

        Id = id;
        Sentence = sentence;
        RangeStart = rangeStart;
        RangeLength = rangeLength;
        ContextStart = contextStart;
        ContextLength = contextLength;
        Word = word;
    }

    public int Id { get; }

    public string Sentence { get; }

    public int RangeStart { get; }

    public int RangeLength { get; }

    public int ContextStart { get; }

    public int ContextLength { get; }

    public string Word { get; }

    public string OriginalSpan => Sentence.Substring(RangeStart, RangeLength);

    public string ContextText => Sentence.Substring(ContextStart, ContextLength);

    public string ContextRewritten
    {
        get
        {
            var localOffset = RangeStart - ContextStart;
            return string.Concat(
                Sentence.AsSpan(ContextStart, localOffset),
                Word.AsSpan(),
                Sentence.AsSpan(RangeStart + RangeLength, (ContextStart + ContextLength) - (RangeStart + RangeLength)));
        }
    }
}

/// <summary>Decision for one <see cref="LearnedWordCheckQuestion"/>.</summary>
public sealed record LearnedWordCheckDecision(
    int QuestionId,
    bool Approved,
    double? Score = null);

/// <summary>Outcome of evaluating a batch of learned-word questions by a checker arm.</summary>
public sealed record LearnedWordCheckResult(
    IReadOnlyList<LearnedWordCheckDecision> Decisions,
    string? ProviderId = null,
    bool UsedFallback = false);

/// <summary>Runtime contextual checker deciding whether flagged learned aliases fit the sentence.</summary>
public interface ILearnedWordChecker
{
    string ArmName { get; }

    bool ScoresAreComparable { get; }

    Task<LearnedWordCheckResult?> DecideAsync(
        IReadOnlyList<LearnedWordCheckQuestion> questions,
        string? language,
        CancellationToken cancellationToken = default);
}
