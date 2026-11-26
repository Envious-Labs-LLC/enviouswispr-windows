namespace EnviousWispr.Core.Learning;

public abstract record LearnTargetState
{
    private LearnTargetState() { }

    public sealed record ExistingWord(string WordId) : LearnTargetState;

    public sealed record NewWord : LearnTargetState;

    public static readonly NewWord New = new();
}

public enum LearnedCorrectionPillKind
{
    Added,
    Updated,
}

public enum LearnedCorrectionPillPhase
{
    Learned,
    UndoPending,
    Undone,
    UndoError,
}

public sealed record LearnedCorrectionPillModel(
    Guid PillId,
    string WordId,
    string Canonical,
    LearnedCorrectionPillKind Kind,
    LearnedCorrectionPillPhase Phase = LearnedCorrectionPillPhase.Learned);

public enum LearnVia
{
    Judge,
    Repeat,
}

public abstract record LearnedCorrectionOutcome
{
    private LearnedCorrectionOutcome() { }

    public sealed record Learned(LearnedCorrectionPillModel Pill) : LearnedCorrectionOutcome;

    public sealed record AlreadyCovered : LearnedCorrectionOutcome;

    public sealed record Blocked : LearnedCorrectionOutcome;

    public sealed record NoLongerWanted : LearnedCorrectionOutcome;

    public sealed record Refused(string Reason) : LearnedCorrectionOutcome;
}

public enum LearnedCorrectionUndoOutcome
{
    Undone,
    AlreadyChanged,
    Failed,
    Stale,
}

public interface ILearnedCorrectionPresenter
{
    void Show(LearnedCorrectionPillModel pill);

    void ShowUndone(Guid pillId);

    void ShowUndoError(Guid pillId);

    void ShowSaveError(string canonical);

    void Close(Guid pillId);
}

public interface ILearnedCorrectionCoordinator
{
    Task<LearnedCorrectionOutcome> LearnAsync(
        string original,
        string corrected,
        LearnTargetState expectedTarget,
        LearnVia via,
        Func<bool>? stillWanted = null,
        CancellationToken cancellationToken = default);
}
