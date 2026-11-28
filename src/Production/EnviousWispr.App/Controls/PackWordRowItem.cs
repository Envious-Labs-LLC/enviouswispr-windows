using EnviousWispr.Core.Settings;

namespace EnviousWispr.App.Controls;

/// <summary>Callbacks supplied by the pack editor view to each word row.</summary>
public sealed class PackWordRowCallbacks
{
    public required Func<string, bool, Task<bool>> SetWordEnabledAsync { get; init; }

    public required Func<string, IReadOnlyList<string>, Task<bool>> SetWordAliasesAsync { get; init; }

    public required Func<string, Task<bool>> RestoreWordAsync { get; init; }

    public required Func<string, string> GetDraft { get; init; }

    public required Action<string, string> SetDraft { get; init; }
}

/// <summary>
/// Row model bound to each virtualized item in the Vocabulary Pack editor list.
/// Updates its <see cref="State"/> in place so single-word edits keep scroll position and keyboard focus intact.
/// </summary>
public sealed class PackWordRowItem
{
    public PackWordRowItem(VocabularyPackWordState state, PackWordRowCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(callbacks);
        State = state;
        Callbacks = callbacks;
    }

    public VocabularyPackWordState State { get; private set; }

    public string Canonical => State.Canonical;

    public PackWordRowCallbacks Callbacks { get; }

    public event Action? StateUpdated;

    public void UpdateState(VocabularyPackWordState nextState)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        if (State.Equals(nextState))
        {
            return;
        }

        State = nextState;
        StateUpdated?.Invoke();
    }

    public override string ToString() => State.ToString();
}
