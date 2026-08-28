namespace EnviousWispr.Core.Settings;

/// <summary>Why an ask for suggestions produced nothing.</summary>
public enum MishearingAdviceStatus
{
    /// <summary>The model answered and there is at least one candidate.</summary>
    Suggested,

    /// <summary>The model answered and nothing in the reply was usable.</summary>
    NothingUsable,

    /// <summary>The chosen polish option cannot answer questions like this one.</summary>
    NotSupported,

    /// <summary>The ask was made and did not come back. Network, key, model, or timeout.</summary>
    Failed,
}

/// <summary>
/// What came back from asking a model for likely mishearings.
/// </summary>
/// <remarks>
/// FOUR OUTCOMES, NOT TWO, BECAUSE THE USER HAS A DIFFERENT NEXT MOVE IN EACH. An empty list with no
/// reason is the shape that makes a feature feel broken: the user cannot tell whether the model had
/// no ideas, whether their key is wrong, or whether the option they picked simply cannot do this.
/// Those need three different sentences on screen, so they are three different values here.
/// </remarks>
public sealed record MishearingAdvice(
    MishearingAdviceStatus Status,
    IReadOnlyList<string> Suggestions)
{
    /// <summary>Nothing to offer, and the reason why.</summary>
    public static MishearingAdvice None(MishearingAdviceStatus status) => new(status, []);
}

/// <summary>
/// Asks the user's chosen model what a word is likely to be misheard as.
/// </summary>
/// <remarks>
/// SEPARATE FROM POLISHING ON PURPOSE. A polish provider's whole contract is "give this text back
/// cleaned up", and its prompt tells the model to treat instructions as content. Reusing it for a
/// question returns a tidier question. Providers that can hold an open-ended conversation implement
/// this as well; the ones that cannot say so, and the user is told which.
/// </remarks>
public interface IMishearingAdvisor
{
    /// <summary>
    /// Likely mis-transcriptions of a word.
    /// </summary>
    /// <param name="spokenForm">The word being taught.</param>
    /// <param name="existing">Aliases the user already has, so nothing is offered twice.</param>
    /// <param name="cancellationToken">Cancels a slow ask.</param>
    /// <remarks>
    /// NEVER THROWS FOR AN ORDINARY FAILURE. A missing key, a refused connection and a timeout are
    /// all expected on this path and all mean the same thing to the user - the suggestion did not
    /// arrive - so they arrive as <see cref="MishearingAdviceStatus.Failed"/> rather than as an
    /// exception the caller has to remember to catch on a button click.
    /// </remarks>
    Task<MishearingAdvice> SuggestAsync(
        string spokenForm,
        IReadOnlyList<string> existing,
        CancellationToken cancellationToken = default);
}
