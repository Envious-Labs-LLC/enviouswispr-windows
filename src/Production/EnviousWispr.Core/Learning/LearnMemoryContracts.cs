namespace EnviousWispr.Core.Learning;

public enum LearnUserNoResult
{
    Watching,
    Blocked,
}

public readonly record struct LearnMemoryMutation<T>(bool Committed, T? Value);

public static class LearnMemoryMutation
{
    public static LearnMemoryMutation<T> Commit<T>(T value) => new(true, value);

    public static LearnMemoryMutation<T> NotRemembered<T>() => new(false, default);
}

public sealed record LearnMemoryEntry(
    int JudgeRefusals,
    int UserNos,
    bool Blocked,
    DateTimeOffset LastSeen);

public interface ILearnMemoryStore
{
    event Action? Changed;

    bool IsRepeat(string pairKey);

    bool IsBlocked(string pairKey);

    IReadOnlySet<string> BlockedKeys { get; }

    int BlockedCount { get; }

    bool HasStoredMemory { get; }

    LearnMemoryMutation<bool> RecordJudgeRefusal(string pairKey);

    LearnMemoryMutation<LearnUserNoResult> RecordUndo(string pairKey);

    bool ForgetAll();

    IReadOnlyDictionary<string, LearnMemoryEntry> ExportEntries();

    bool ImportEntries(IReadOnlyDictionary<string, LearnMemoryEntry> entries);
}
